//! Taskbar docking, following the approach of the open-source
//! TrafficMonitor (CTaskBarDlg and its two implementations),
//! which was read and mirrored deliberately:
//!
//! * Classical taskbar (Windows 10 and earlier): the parent is
//!   `ReBarWindow32` inside `Shell_TrayWnd` — a direct child of
//!   `Shell_TrayWnd` would be covered by the rebar. Room is made
//!   by shrinking the task-list window (`MSTaskSwWClass`) by the
//!   dock's width; its original size is restored when the dock
//!   hides or the app exits.
//! * Windows 11 taskbar (no rebar): the parent is `Shell_TrayWnd`
//!   itself; the dock sits at `TrayNotifyWnd.left - width + 2`,
//!   vertically centered on the Start button's height.
//!
//! In both cases the window is placed with `MoveWindow` in
//! parent-client coordinates, repositioned when the tray layout
//! signature changes, and only reported `active` (which makes
//! the GUI skip the floating strip) after its on-screen rect
//! verifiably intersects the taskbar while visible — a dock that
//! cannot be seen never suppresses the fallback strip. Every
//! step is appended to `dock.log` next to the rules file, so a
//! failure on a real machine leaves evidence instead of silence.

use crate::StripAction;
use std::cell::RefCell;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use windows::Win32::Foundation::{HWND, RECT};

pub struct DockShared {
    pub down: Mutex<String>,
    pub up: Mutex<String>,
    pub hover: Mutex<String>,
    pub visible: AtomicBool,
    pub active: AtomicBool,
    pub use_bits: AtomicBool,
    pub action: Arc<Mutex<Option<StripAction>>>,
}

impl DockShared {
    pub fn new(action: Arc<Mutex<Option<StripAction>>>) -> Self {
        Self {
            down: Mutex::new(String::new()),
            up: Mutex::new(String::new()),
            hover: Mutex::new(String::new()),
            visible: AtomicBool::new(true),
            active: AtomicBool::new(false),
            use_bits: AtomicBool::new(false),
            action,
        }
    }

    fn set_action(&self, a: StripAction) {
        if let Ok(mut slot) = self.action.lock() {
            *slot = Some(a);
        }
    }
}

const MENU_OPEN: u32 = 1;
const MENU_BITS: u32 = 2;
const MENU_HIDE: u32 = 3;
const MENU_EXIT: u32 = 4;

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

/// Append one line to dock.log in the app's data directory.
fn log_line(msg: &str) {
    let dir = fluxlimiter::rules::user_data_dir();
    let _ = std::fs::create_dir_all(&dir);
    let secs = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);
    if let Ok(mut f) = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(dir.join("dock.log"))
    {
        use std::io::Write;
        let _ = writeln!(f, "[{secs}] {msg}");
    }
}

#[derive(Clone, Copy, PartialEq)]
enum Mode {
    None,
    Classical,
    Win11,
}

/// Runtime docking state; only ever touched on the dock thread.
struct DockRt {
    mode: Mode,
    parent: HWND,
    tasklist: HWND,
    /// Task-list rect (parent-client coords) before we shrank it.
    tasklist_orig: Option<RECT>,
    tasklist_shrunk: bool,
    docked: bool,
    /// (notify_left, notify_width, tasklist_width) last positioned for.
    last_sig: (i32, i32, i32),
    verified_ticks: u32,
    logged_waiting: bool,
}

impl DockRt {
    fn new() -> Self {
        Self {
            mode: Mode::None,
            parent: HWND::default(),
            tasklist: HWND::default(),
            tasklist_orig: None,
            tasklist_shrunk: false,
            docked: false,
            last_sig: (0, 0, 0),
            verified_ticks: 0,
            logged_waiting: false,
        }
    }
}

thread_local! {
    static RT: RefCell<DockRt> = RefCell::new(DockRt::new());
}

pub fn spawn(shared: Arc<DockShared>) {
    std::thread::spawn(move || unsafe { run(shared) });
}

unsafe fn run(shared: Arc<DockShared>) {
    use windows::core::{w, PCWSTR};
    use windows::Win32::Foundation::{LPARAM, LRESULT, WPARAM};
    use windows::Win32::System::LibraryLoader::GetModuleHandleW;
    use windows::Win32::UI::WindowsAndMessaging::{
        CreateWindowExW, DispatchMessageW, GetMessageW, LoadCursorW, RegisterClassExW,
        SetTimer, TranslateMessage, IDC_ARROW, MSG, WINDOW_EX_STYLE, WNDCLASSEXW, WS_POPUP,
    };

    // Stash the shared state where the window procedure can reach
    // it (the dock lives for the whole process lifetime).
    let state: &'static DockShared = &*Arc::into_raw(shared);
    let hinstance = GetModuleHandleW(None).unwrap_or_default();
    let class_name = w!("FluxLimiterDock");
    let wc = WNDCLASSEXW {
        cbSize: std::mem::size_of::<WNDCLASSEXW>() as u32,
        lpfnWndProc: Some(wnd_proc),
        hInstance: hinstance.into(),
        lpszClassName: class_name,
        hCursor: LoadCursorW(None, IDC_ARROW).unwrap_or_default(),
        ..Default::default()
    };
    if RegisterClassExW(&wc) == 0 {
        log_line("RegisterClassExW failed");
        return;
    }

    extern "system" fn wnd_proc(
        hwnd: HWND,
        msg: u32,
        wparam: WPARAM,
        lparam: LPARAM,
    ) -> LRESULT {
        unsafe { dock_wnd_proc(hwnd, msg, wparam, lparam) }
    }

    // Created hidden as a popup; the timer cycle parents it into
    // the taskbar (TrafficMonitor parents in OnInitDialog; here
    // the first timer tick, ~500 ms in, does the same job).
    let hwnd = CreateWindowExW(
        WINDOW_EX_STYLE::default(),
        class_name,
        PCWSTR::null(),
        WS_POPUP,
        -100,
        -100,
        10,
        10,
        HWND::default(),
        None,
        hinstance,
        Some(state as *const DockShared as *const std::ffi::c_void),
    );
    let Ok(hwnd) = hwnd else {
        log_line("CreateWindowExW failed");
        return;
    };
    SetTimer(hwnd, 1, 500, None);
    log_line("dock thread started");

    let mut msg = MSG::default();
    loop {
        let r = GetMessageW(&mut msg, HWND::default(), 0, 0);
        if r.0 <= 0 {
            break;
        }
        let _ = TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    state.active.store(false, Ordering::Relaxed);
    log_line("dock thread exiting");
}

unsafe fn shared_of(hwnd: HWND) -> Option<&'static DockShared> {
    use windows::Win32::UI::WindowsAndMessaging::{GetWindowLongPtrW, GWLP_USERDATA};
    let p = GetWindowLongPtrW(hwnd, GWLP_USERDATA);
    if p == 0 {
        None
    } else {
        Some(&*(p as *const DockShared))
    }
}

unsafe fn win_rect(hwnd: HWND) -> Option<RECT> {
    use windows::Win32::UI::WindowsAndMessaging::GetWindowRect;
    let mut r = RECT::default();
    GetWindowRect(hwnd, &mut r).ok().map(|_| r)
}

/// Windows 11 or later, decided by the OS build number from the
/// registry (>= 22000), the same way TrafficMonitor picks its
/// implementation by Windows version. Probing the tray for a
/// `WorkerW` child instead is wrong: Windows 11's tray can
/// contain one too, which misroutes to the classical path (whose
/// task-list window does not exist there) and docking fails.
unsafe fn is_windows_11() -> bool {
    use windows::core::PCWSTR;
    use windows::Win32::System::Registry::{
        RegGetValueW, HKEY_LOCAL_MACHINE, RRF_RT_REG_SZ,
    };
    let subkey = wide("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion");
    let value = wide("CurrentBuildNumber");
    let mut buf = [0u16; 32];
    let mut size = (buf.len() * 2) as u32;
    let status = RegGetValueW(
        HKEY_LOCAL_MACHINE,
        PCWSTR(subkey.as_ptr()),
        PCWSTR(value.as_ptr()),
        RRF_RT_REG_SZ,
        None,
        Some(buf.as_mut_ptr() as *mut std::ffi::c_void),
        Some(&mut size),
    );
    if status.0 != 0 {
        log_line("CurrentBuildNumber read failed; assuming Windows 11");
        return true;
    }
    let len = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
    let text = String::from_utf16_lossy(&buf[..len]);
    let build: u32 = text.trim().parse().unwrap_or(0);
    log_line(&format!("OS build {build}"));
    build >= 22000
}

unsafe fn find_child(parent: HWND, class: &str) -> Option<HWND> {
    use windows::Win32::UI::WindowsAndMessaging::FindWindowExW;
    let cls = wide(class);
    FindWindowExW(
        parent,
        HWND::default(),
        windows::core::PCWSTR(cls.as_ptr()),
        None,
    )
    .ok()
    .filter(|h| !h.0.is_null())
}

/// Attempt the full docking once. Logs every step; false keeps
/// the caller retrying on later ticks (Explorer may still be
/// starting) without ever marking the dock active.
unsafe fn try_dock(hwnd: HWND) -> bool {
    use windows::Win32::UI::WindowsAndMessaging::{FindWindowW, SetParent};
    let tray = {
        let cls = wide("Shell_TrayWnd");
        FindWindowW(windows::core::PCWSTR(cls.as_ptr()), None).ok()
    };
    let Some(tray) = tray.filter(|h| !h.0.is_null()) else {
        log_line("Shell_TrayWnd not found");
        return false;
    };
    let Some(tray_rect) = win_rect(tray) else {
        log_line("tray rect failed");
        return false;
    };
    let bar_w = tray_rect.right - tray_rect.left;
    let bar_h = tray_rect.bottom - tray_rect.top;
    if bar_w <= bar_h {
        log_line(&format!("vertical taskbar ({bar_w}x{bar_h}); not docking"));
        return false;
    }
    let Some(notify) = find_child(tray, "TrayNotifyWnd") else {
        log_line("TrayNotifyWnd not found");
        return false;
    };

    // Parent choice by OS version, TrafficMonitor-style: the
    // rebar on the classical taskbar, the tray itself on
    // Windows 11.
    let (mode, parent) = if is_windows_11() {
        (Mode::Win11, tray)
    } else {
        match find_child(tray, "ReBarWindow32") {
            Some(bar) => (Mode::Classical, bar),
            None => {
                log_line("classical taskbar but ReBarWindow32 not found");
                return false;
            }
        }
    };
    if SetParent(hwnd, parent).is_err() {
        log_line("SetParent failed");
        return false;
    }
    // Popup -> child, now that it has a parent.
    use windows::Win32::UI::WindowsAndMessaging::{
        GetWindowLongPtrW, SetWindowLongPtrW, GWL_STYLE, WS_CHILD, WS_POPUP,
    };
    let style = GetWindowLongPtrW(hwnd, GWL_STYLE);
    let child_style = (style & !(WS_POPUP.0 as isize)) | WS_CHILD.0 as isize;
    SetWindowLongPtrW(hwnd, GWL_STYLE, child_style);

    let mut tasklist = HWND::default();
    if mode == Mode::Classical {
        tasklist = find_child(parent, "MSTaskSwWClass")
            .or_else(|| find_child(parent, "MSTaskListWClass"))
            .unwrap_or_default();
        if tasklist.0.is_null() {
            log_line("classical taskbar but no task-list window found");
            return false;
        }
    }
    RT.with(|rt| {
        let mut rt = rt.borrow_mut();
        rt.mode = mode;
        rt.parent = parent;
        rt.tasklist = tasklist;
        rt.docked = true;
        rt.last_sig = (0, 0, 0);
        rt.verified_ticks = 0;
    });
    log_line(&format!(
        "docked: mode={} bar={bar_w}x{bar_h}",
        if mode == Mode::Classical { "classical" } else { "win11" }
    ));
    position(hwnd, true);
    true
}

/// (Re)position the dock inside its parent, TrafficMonitor
/// geometry. `force` repositions even when the tray signature
/// looks unchanged.
unsafe fn position(hwnd: HWND, force: bool) {
    use windows::Win32::UI::WindowsAndMessaging::{GetParent, MoveWindow, ShowWindow, SW_SHOW};
    let Some(tray) = ({
        let cls = wide("Shell_TrayWnd");
        use windows::Win32::UI::WindowsAndMessaging::FindWindowW;
        FindWindowW(windows::core::PCWSTR(cls.as_ptr()), None)
            .ok()
            .filter(|h| !h.0.is_null())
    }) else {
        return;
    };
    let Some(tray_rect) = win_rect(tray) else { return };
    let Some(notify) = find_child(tray, "TrayNotifyWnd") else {
        return;
    };
    let Some(notify_rect) = win_rect(notify) else {
        return;
    };
    let bar_h = tray_rect.bottom - tray_rect.top;
    let width = (bar_h * 3).clamp(110, 170);

    // Layout signature: notification area geometry plus (in
    // classical mode) the task-list width, whose changes mean
    // Explorer re-laid out the bar and our space may be gone.
    let tasklist_w = RT.with(|rt| {
        let rt = rt.borrow();
        if rt.mode == Mode::Classical {
            win_rect(rt.tasklist).map(|r| r.right - r.left).unwrap_or(0)
        } else {
            0
        }
    });
    let sig = (
        notify_rect.left,
        notify_rect.right - notify_rect.left,
        tasklist_w,
    );
    let need = RT.with(|rt| {
        let rt = rt.borrow();
        force || rt.last_sig != sig || !rt.docked
    });
    if !need {
        return;
    }

    let parent = GetParent(hwnd).unwrap_or_default();
    let Some(parent_rect) = win_rect(parent) else {
        return;
    };
    let (x, y, height) = RT.with(|rt| {
        let mut rt = rt.borrow_mut();
        match rt.mode {
            Mode::Classical => {
                let Some(min_rect) = win_rect(rt.tasklist) else {
                    return (0, 0, 0);
                };
                let left_space = min_rect.left - parent_rect.left;
                let top_space = min_rect.top - parent_rect.top;
                // Once shrunk, the live rect is narrower than the
                // original; all math uses the ORIGINAL width.
                let (min_w, min_h) = if rt.tasklist_shrunk {
                    rt.tasklist_orig
                        .map(|o| (o.right - o.left, o.bottom - o.top))
                        .unwrap_or((min_rect.right - min_rect.left, min_rect.bottom - min_rect.top))
                } else {
                    (min_rect.right - min_rect.left, min_rect.bottom - min_rect.top)
                };
                if rt.tasklist_orig.is_none() {
                    rt.tasklist_orig = Some(RECT {
                        left: left_space,
                        top: top_space,
                        right: left_space + min_w,
                        bottom: top_space + min_h,
                    });
                }
                if rt.tasklist_shrunk && min_rect.right - min_rect.left >= min_w {
                    // Explorer re-laid out the bar and gave the
                    // task list its full width back: our reserved
                    // space is gone, reserve it again below.
                    rt.tasklist_shrunk = false;
                }
                if !rt.tasklist_shrunk {
                    // Make room: shrink the task list by our width
                    // (TrafficMonitor's classical AdjustTaskbarWndPos).
                    let _ = MoveWindow(
                        rt.tasklist,
                        left_space,
                        top_space,
                        (min_w - width).max(0),
                        min_h,
                        true,
                    );
                    rt.tasklist_shrunk = true;
                }
                // Right after the (shrunk) task list, i.e. just
                // left of the notification area.
                (left_space + min_w - width + 2, 0, parent_rect.bottom - parent_rect.top)
            }
            _ => {
                // Windows 11: next to the notification area,
                // vertically centered on the Start button height.
                let start_h = find_child(tray, "Start")
                    .and_then(|h| win_rect(h))
                    .map(|r| r.bottom - r.top)
                    .unwrap_or(bar_h);
                let h = start_h;
                let x = (notify_rect.left - parent_rect.left) - width + 2;
                let y = (start_h - h) / 2 + (bar_h - start_h);
                (x, y, h)
            }
        }
    });
    if height == 0 {
        return;
    }
    let _ = MoveWindow(hwnd, x, y, width, height, true);
    let _ = ShowWindow(hwnd, SW_SHOW);
    RT.with(|rt| {
        rt.borrow_mut().last_sig = sig;
    });
}

/// Restore the task list's original size (classical mode) —
/// called when the dock hides or is destroyed.
unsafe fn restore_tasklist() {
    use windows::Win32::UI::WindowsAndMessaging::MoveWindow;
    RT.with(|rt| {
        let mut rt = rt.borrow_mut();
        if rt.mode == Mode::Classical && rt.tasklist_shrunk {
            if let Some(o) = rt.tasklist_orig {
                let _ = MoveWindow(
                    rt.tasklist,
                    o.left,
                    o.top,
                    o.right - o.left,
                    o.bottom - o.top,
                    true,
                );
            }
            rt.tasklist_shrunk = false;
        }
    });
}

/// Per-tick maintenance: dock if needed, follow tray changes,
/// and only mark `active` once the window is verifiably on the
/// taskbar (visible + intersecting it).
unsafe fn adjust(hwnd: HWND, shared: &DockShared) {
    use windows::Win32::UI::WindowsAndMessaging::{
        BringWindowToTop, IsWindowVisible, ShowWindow, SW_HIDE,
    };
    if !shared.visible.load(Ordering::Relaxed) {
        if shared.active.swap(false, Ordering::Relaxed) {
            restore_tasklist();
            RT.with(|rt| rt.borrow_mut().docked = false);
        }
        let _ = ShowWindow(hwnd, SW_HIDE);
        return;
    }
    let docked = RT.with(|rt| rt.borrow().docked);
    if !docked {
        try_dock(hwnd);
        return;
    }
    // Explorer may have restarted: parent gone means re-dock.
    use windows::Win32::UI::WindowsAndMessaging::GetParent;
    let parent_ok = RT.with(|rt| {
        let rt = rt.borrow();
        GetParent(hwnd).map(|p| p == rt.parent).unwrap_or(false)
    });
    if !parent_ok {
        restore_tasklist();
        RT.with(|rt| {
            let mut rt = rt.borrow_mut();
            rt.docked = false;
            rt.mode = Mode::None;
        });
        shared.active.store(false, Ordering::Relaxed);
        log_line("parent lost (Explorer restart?); will re-dock");
        return;
    }
    position(hwnd, false);
    let _ = BringWindowToTop(hwnd);

    // Verification gate: believe "active" only when the window
    // really is on the bar.
    let on_bar = (|| {
        let mine = win_rect(hwnd)?;
        let cls = wide("Shell_TrayWnd");
        let tray = windows::Win32::UI::WindowsAndMessaging::FindWindowW(
            windows::core::PCWSTR(cls.as_ptr()),
            None,
        )
        .ok()?;
        let t = win_rect(tray)?;
        let intersects = mine.left < t.right
            && mine.right > t.left
            && mine.top < t.bottom
            && mine.bottom > t.top
            && mine.right - mine.left >= 50;
        Some(intersects && IsWindowVisible(hwnd).as_bool())
    })()
    .unwrap_or(false);
    RT.with(|rt| {
        let mut rt = rt.borrow_mut();
        if on_bar {
            rt.verified_ticks = rt.verified_ticks.saturating_add(1);
            if rt.verified_ticks >= 2 && !shared.active.load(Ordering::Relaxed) {
                shared.active.store(true, Ordering::Relaxed);
                log_line("dock verified visible on the taskbar");
            }
        } else {
            rt.verified_ticks = 0;
            if !rt.logged_waiting {
                rt.logged_waiting = true;
                log_line("docked but not yet visible on the taskbar");
            }
            if shared.active.load(Ordering::Relaxed) {
                shared.active.store(false, Ordering::Relaxed);
            }
        }
    });
}

unsafe fn make_tooltip(hwnd: HWND, shared: &DockShared) -> HWND {
    use windows::core::w;
    use windows::Win32::Foundation::{LPARAM, WPARAM};
    use windows::Win32::UI::Controls::{
        InitCommonControlsEx, ICC_WIN95_CLASSES, INITCOMMONCONTROLSEX, TTTOOLINFOW, TTF_IDISHWND,
        TTF_SUBCLASS, TTM_ADDTOOLW,
    };
    use windows::Win32::UI::WindowsAndMessaging::{
        CreateWindowExW, SendMessageW, WINDOW_EX_STYLE, WINDOW_STYLE,
    };
    let _ = InitCommonControlsEx(&INITCOMMONCONTROLSEX {
        dwSize: std::mem::size_of::<INITCOMMONCONTROLSEX>() as u32,
        dwICC: ICC_WIN95_CLASSES,
    });
    let tip = CreateWindowExW(
        WINDOW_EX_STYLE::default(),
        w!("tooltips_class32"),
        None,
        WINDOW_STYLE::default(),
        0,
        0,
        0,
        0,
        hwnd,
        None,
        None,
        None,
    );
    let Ok(tip) = tip else { return HWND::default() };
    let text = shared.hover.lock().map(|s| s.clone()).unwrap_or_default();
    let mut wide_text = wide(&text);
    let info = TTTOOLINFOW {
        cbSize: std::mem::size_of::<TTTOOLINFOW>() as u32,
        uFlags: TTF_SUBCLASS | TTF_IDISHWND,
        hwnd,
        uId: hwnd.0 as usize,
        lpszText: windows::core::PWSTR(wide_text.as_mut_ptr()),
        ..Default::default()
    };
    SendMessageW(
        tip,
        TTM_ADDTOOLW,
        WPARAM(0),
        LPARAM(&info as *const TTTOOLINFOW as isize),
    );
    tip
}

unsafe fn update_tooltip(hwnd: HWND, tip: HWND, shared: &DockShared) {
    use windows::Win32::Foundation::{LPARAM, WPARAM};
    use windows::Win32::UI::Controls::{TTTOOLINFOW, TTM_UPDATETIPTEXTW};
    use windows::Win32::UI::WindowsAndMessaging::SendMessageW;
    if tip.0.is_null() {
        return;
    }
    let text = shared.hover.lock().map(|s| s.clone()).unwrap_or_default();
    let mut wide_text = wide(&text);
    let info = TTTOOLINFOW {
        cbSize: std::mem::size_of::<TTTOOLINFOW>() as u32,
        hwnd,
        uId: hwnd.0 as usize,
        lpszText: windows::core::PWSTR(wide_text.as_mut_ptr()),
        ..Default::default()
    };
    SendMessageW(
        tip,
        TTM_UPDATETIPTEXTW,
        WPARAM(0),
        LPARAM(&info as *const TTTOOLINFOW as isize),
    );
}

static TOOLTIP: std::sync::Mutex<isize> = std::sync::Mutex::new(0);

unsafe fn dock_wnd_proc(
    hwnd: HWND,
    msg: u32,
    wparam: windows::Win32::Foundation::WPARAM,
    lparam: windows::Win32::Foundation::LPARAM,
) -> windows::Win32::Foundation::LRESULT {
    use windows::Win32::Foundation::LRESULT;
    use windows::Win32::UI::WindowsAndMessaging::{
        DefWindowProcW, PostQuitMessage, WM_CREATE, WM_DESTROY, WM_PAINT, WM_RBUTTONUP, WM_TIMER,
    };
    match msg {
        x if x == WM_CREATE => {
            use windows::Win32::UI::WindowsAndMessaging::{
                SetWindowLongPtrW, CREATESTRUCTW, GWLP_USERDATA,
            };
            let cs = &*(lparam.0 as *const CREATESTRUCTW);
            let shared = &*(cs.lpCreateParams as *const DockShared);
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, shared as *const DockShared as isize);
            let tip = make_tooltip(hwnd, shared);
            if let Ok(mut t) = TOOLTIP.lock() {
                *t = tip.0 as isize;
            }
            LRESULT(0)
        }
        x if x == WM_TIMER => {
            if let Some(shared) = shared_of(hwnd) {
                adjust(hwnd, shared);
                if shared.visible.load(Ordering::Relaxed) {
                    use windows::Win32::Graphics::Gdi::InvalidateRect;
                    let tip = TOOLTIP.lock().map(|t| HWND(*t as *mut _)).unwrap_or_default();
                    update_tooltip(hwnd, tip, shared);
                    let _ = InvalidateRect(hwnd, None, false);
                }
            }
            LRESULT(0)
        }
        x if x == WM_PAINT => {
            paint(hwnd);
            LRESULT(0)
        }
        x if x == WM_RBUTTONUP => {
            if let Some(shared) = shared_of(hwnd) {
                show_menu(hwnd, shared);
            }
            LRESULT(0)
        }
        x if x == WM_DESTROY => {
            restore_tasklist();
            PostQuitMessage(0);
            LRESULT(0)
        }
        _ => DefWindowProcW(hwnd, msg, wparam, lparam),
    }
}

unsafe fn paint(hwnd: HWND) {
    use windows::Win32::Foundation::COLORREF;
    use windows::Win32::Graphics::Gdi::{
        BeginPaint, CreateFontW, CreateSolidBrush, DeleteObject, EndPaint, FillRect, SelectObject,
        SetBkMode, SetTextColor, TextOutW, PAINTSTRUCT, TRANSPARENT,
    };
    use windows::Win32::UI::WindowsAndMessaging::GetClientRect;
    let Some(shared) = shared_of(hwnd) else { return };
    let down = shared.down.lock().map(|s| s.clone()).unwrap_or_default();
    let up = shared.up.lock().map(|s| s.clone()).unwrap_or_default();
    let mut ps = PAINTSTRUCT::default();
    let hdc = BeginPaint(hwnd, &mut ps);
    let mut rect = RECT::default();
    let _ = GetClientRect(hwnd, &mut rect);
    let brush = CreateSolidBrush(COLORREF(0x00202020));
    FillRect(hdc, &rect, brush);
    let _ = DeleteObject(brush);
    let height = rect.bottom - rect.top;
    let font_h = -((height / 2) as f32 * 0.56) as i32;
    let face = wide("Segoe UI");
    let font = CreateFontW(
        font_h, 0, 0, 0, 400, 0, 0, 0, 0, 0, 0, 0, 0,
        windows::core::PCWSTR(face.as_ptr()),
    );
    let old = SelectObject(hdc, font);
    let _ = SetBkMode(hdc, TRANSPARENT);
    let line = |text: &str, y: i32, color: u32| {
        let _ = SetTextColor(hdc, COLORREF(color));
        let w = wide(text);
        let _ = TextOutW(hdc, 7, y, &w[..w.len().saturating_sub(1)]);
    };
    let half = height / 2;
    let ty = |slot: i32| slot * half + ((half + font_h) / 2);
    line(&format!("Down {down}"), ty(0), 0x0071CC2E);
    line(&format!("Up {up}"), ty(1), 0x00F6823B);
    let _ = SelectObject(hdc, old);
    let _ = DeleteObject(font);
    EndPaint(hwnd, &ps);
}

unsafe fn show_menu(hwnd: HWND, shared: &DockShared) {
    use windows::Win32::Foundation::POINT;
    use windows::Win32::UI::WindowsAndMessaging::{
        AppendMenuW, CreatePopupMenu, DestroyMenu, GetCursorPos, SetForegroundWindow,
        TrackPopupMenu, MF_CHECKED, MF_SEPARATOR, MF_STRING, MF_UNCHECKED, TPM_LEFTALIGN,
        TPM_RETURNCMD, TPM_TOPALIGN,
    };
    let Ok(menu) = CreatePopupMenu() else { return };
    let item = |id: u32, text: &str, checked: bool| {
        let w = wide(text);
        let flags = MF_STRING | if checked { MF_CHECKED } else { MF_UNCHECKED };
        let _ = AppendMenuW(menu, flags, id as usize, windows::core::PCWSTR(w.as_ptr()));
    };
    item(MENU_OPEN, "Open FluxLimiter", false);
    item(MENU_BITS, "Show speeds in bits", shared.use_bits.load(Ordering::Relaxed));
    item(MENU_HIDE, "Hide taskbar display", false);
    let _ = AppendMenuW(menu, MF_SEPARATOR, 0, windows::core::PCWSTR::null());
    item(MENU_EXIT, "Exit FluxLimiter", false);
    let mut pt = POINT::default();
    let _ = GetCursorPos(&mut pt);
    let _ = SetForegroundWindow(hwnd);
    let cmd = TrackPopupMenu(
        menu,
        TPM_LEFTALIGN | TPM_TOPALIGN | TPM_RETURNCMD,
        pt.x,
        pt.y,
        0,
        hwnd,
        None,
    );
    let _ = DestroyMenu(menu);
    match cmd.0 as u32 {
        MENU_OPEN => shared.set_action(StripAction::FocusMain),
        MENU_BITS => shared.set_action(StripAction::ToggleBits),
        MENU_HIDE => shared.set_action(StripAction::HideStrip),
        MENU_EXIT => shared.set_action(StripAction::Exit),
        _ => {}
    }
}
