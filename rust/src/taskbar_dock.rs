//! Taskbar docking, TrafficMonitor-style (Windows only).
//!
//! Instead of a floating strip, this creates a small Win32 child
//! window and parents it into the taskbar itself (`Shell_TrayWnd`),
//! positioned in the gap just left of the notification area
//! (`TrayNotifyWnd`). It paints the Down/Up lines with GDI on a
//! timer, follows the tray when it moves, re-docks if Explorer
//! restarts, shows the usage tooltip on hover, and offers the
//! same right-click menu as the floating strip (via the shared
//! StripAction slot).
//!
//! The floating egui strip stays as the fallback: the GUI only
//! skips it while `shared.active` reports a live docked window.

use crate::StripAction;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};

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

pub fn spawn(shared: Arc<DockShared>) {
    std::thread::spawn(move || unsafe { run(shared) });
}

unsafe fn run(shared: Arc<DockShared>) {
    use windows::core::{w, PCWSTR};
    use windows::Win32::Foundation::{HWND, LPARAM, LRESULT, WPARAM};
    use windows::Win32::System::LibraryLoader::GetModuleHandleW;
    use windows::Win32::UI::WindowsAndMessaging::{
        CreateWindowExW, DispatchMessageW, GetMessageW, LoadCursorW, RegisterClassExW,
        TranslateMessage, IDC_ARROW, MSG, WINDOW_EX_STYLE, WM_DESTROY, WNDCLASSEXW,
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
        return;
    }

    extern "system" fn wnd_proc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
        unsafe { dock_wnd_proc(hwnd, msg, wparam, lparam) }
    }

    // Created hidden and off-screen as a popup; try_dock then
    // re-parents it into the taskbar as a child window.
    let hwnd = CreateWindowExW(
        WINDOW_EX_STYLE::default(),
        class_name,
        PCWSTR::null(),
        windows::Win32::UI::WindowsAndMessaging::WS_POPUP,
        -100,
        -100,
        10,
        10,
        HWND::default(),
        None,
        hinstance,
        Some(state as *const DockShared as *const std::ffi::c_void),
    );
    let Ok(hwnd) = hwnd else { return };
    if try_dock(hwnd) {
        state.active.store(true, Ordering::Relaxed);
    }
    dock_set_timer(hwnd);

    let mut msg = MSG::default();
    loop {
        let r = GetMessageW(&mut msg, HWND::default(), 0, 0);
        if r.0 <= 0 {
            break;
        }
        let _ = TranslateMessage(&msg);
        DispatchMessageW(&msg);
        if msg.message == WM_DESTROY {
            break;
        }
    }
    state.active.store(false, Ordering::Relaxed);
}

unsafe fn dock_set_timer(hwnd: windows::Win32::Foundation::HWND) {
    use windows::Win32::UI::WindowsAndMessaging::SetTimer;
    SetTimer(hwnd, 1, 500, None);
}

unsafe fn shared_of(
    hwnd: windows::Win32::Foundation::HWND,
) -> Option<&'static DockShared> {
    use windows::Win32::UI::WindowsAndMessaging::{GetWindowLongPtrW, GWLP_USERDATA};
    let p = GetWindowLongPtrW(hwnd, GWLP_USERDATA);
    if p == 0 {
        None
    } else {
        Some(&*(p as *const DockShared))
    }
}

/// Find the taskbar and its notification area.
unsafe fn tray_windows() -> Option<(
    windows::Win32::Foundation::HWND,
    windows::Win32::Foundation::HWND,
)> {
    use windows::core::w;
    use windows::Win32::Foundation::HWND;
    use windows::Win32::UI::WindowsAndMessaging::{FindWindowExW, FindWindowW};
    let tray = FindWindowW(w!("Shell_TrayWnd"), None).ok()?;
    let notify = FindWindowExW(tray, HWND::default(), w!("TrayNotifyWnd"), None).ok()?;
    Some((tray, notify))
}

/// Parent the dock window into the taskbar and place it in the
/// gap left of the notification area. False when the taskbar is
/// vertical or missing (the caller keeps the floating strip).
unsafe fn try_dock(hwnd: windows::Win32::Foundation::HWND) -> bool {
    use windows::Win32::Foundation::{HWND, RECT};
    use windows::Win32::Graphics::Gdi::ScreenToClient;
    use windows::Win32::UI::WindowsAndMessaging::{
        GetWindowRect, SetParent, SetWindowPos, SWP_NOACTIVATE,
    };
    let Some((tray, notify)) = tray_windows() else {
        return false;
    };
    let mut tray_rect = RECT::default();
    let mut notify_rect = RECT::default();
    if GetWindowRect(tray, &mut tray_rect).is_err()
        || GetWindowRect(notify, &mut notify_rect).is_err()
    {
        return false;
    }
    let bar_w = tray_rect.right - tray_rect.left;
    let bar_h = tray_rect.bottom - tray_rect.top;
    if bar_w <= bar_h {
        return false; // vertical taskbar: not supported, use the strip
    }
    if SetParent(hwnd, tray).is_err() {
        return false;
    }
    // Popup -> child, now that it has a parent.
    use windows::Win32::UI::WindowsAndMessaging::{
        GetWindowLongPtrW, SetWindowLongPtrW, GWL_STYLE, WS_CHILD, WS_POPUP,
    };
    let style = GetWindowLongPtrW(hwnd, GWL_STYLE);
    let child_style = (style & !(WS_POPUP.0 as isize)) | WS_CHILD.0 as isize;
    SetWindowLongPtrW(hwnd, GWL_STYLE, child_style);
    // Width scales with the bar height (about 3x), roughly 120 px
    // on a standard 40 px taskbar.
    let width = (bar_h * 3).max(96);
    let mut pt = windows::Win32::Foundation::POINT {
        x: notify_rect.left - width - 2,
        y: tray_rect.top,
    };
    let _ = ScreenToClient(tray, &mut pt);
    let _ = SetWindowPos(
        hwnd,
        HWND::default(),
        pt.x,
        pt.y,
        width,
        bar_h,
        SWP_NOACTIVATE,
    );
    true
}

/// Reposition if the tray moved (resolution change, taskbar
/// re-layout, Explorer restart).
unsafe fn follow_tray(hwnd: windows::Win32::Foundation::HWND) {
    use windows::Win32::Foundation::HWND;
    use windows::Win32::UI::WindowsAndMessaging::GetParent;
    let parent_ok = match tray_windows() {
        Some((tray, _)) => GetParent(hwnd).map(|p| p == tray).unwrap_or(false),
        None => false,
    };
    if !parent_ok {
        if let Some(shared) = shared_of(hwnd) {
            let docked = try_dock(hwnd);
            shared.active.store(docked, std::sync::atomic::Ordering::Relaxed);
        }
        return;
    }
    // Already docked: only move when the tray layout changed.
    use windows::Win32::Foundation::RECT;
    use windows::Win32::UI::WindowsAndMessaging::GetWindowRect;
    if let Some((_, notify)) = tray_windows() {
        let mut mine = RECT::default();
        let mut nrect = RECT::default();
        if GetWindowRect(hwnd, &mut mine).is_ok() && GetWindowRect(notify, &mut nrect).is_ok() {
            let width = mine.right - mine.left;
            if mine.left != nrect.left - width - 2 {
                let _ = try_dock(hwnd);
            }
        }
    }
}

unsafe fn make_tooltip(
    hwnd: windows::Win32::Foundation::HWND,
    shared: &DockShared,
) -> windows::Win32::Foundation::HWND {
    use windows::core::w;
    use windows::Win32::Foundation::{HWND, LPARAM, WPARAM};
    use windows::Win32::UI::Controls::{
        InitCommonControlsEx, ICC_WIN95_CLASSES, INITCOMMONCONTROLSEX, TTTOOLINFOW, TTF_IDISHWND,
        TTF_SUBCLASS, TTM_ADDTOOLW,
    };
    use windows::Win32::UI::WindowsAndMessaging::{CreateWindowExW, SendMessageW, WINDOW_EX_STYLE, WINDOW_STYLE};
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
    SendMessageW(tip, TTM_ADDTOOLW, WPARAM(0), LPARAM(&info as *const TTTOOLINFOW as isize));
    tip
}

unsafe fn update_tooltip(
    hwnd: windows::Win32::Foundation::HWND,
    tip: windows::Win32::Foundation::HWND,
    shared: &DockShared,
) {
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
    SendMessageW(tip, TTM_UPDATETIPTEXTW, WPARAM(0), LPARAM(&info as *const TTTOOLINFOW as isize));
}

static TOOLTIP: std::sync::Mutex<isize> = std::sync::Mutex::new(0);

unsafe fn dock_wnd_proc(
    hwnd: windows::Win32::Foundation::HWND,
    msg: u32,
    wparam: windows::Win32::Foundation::WPARAM,
    lparam: windows::Win32::Foundation::LPARAM,
) -> windows::Win32::Foundation::LRESULT {
    use windows::Win32::Foundation::{HWND, LRESULT};
    use windows::Win32::UI::WindowsAndMessaging::{
        DefWindowProcW, PostQuitMessage, WM_CREATE, WM_DESTROY, WM_PAINT, WM_RBUTTONUP,
        WM_TIMER,
    };
    const CREATE_STRUCT_OFF: usize = 0;
    let _ = CREATE_STRUCT_OFF;
    match msg {
        x if x == WM_CREATE => {
            use windows::Win32::UI::WindowsAndMessaging::{SetWindowLongPtrW, CREATESTRUCTW, GWLP_USERDATA};
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
                use windows::Win32::Graphics::Gdi::InvalidateRect;
                use windows::Win32::UI::WindowsAndMessaging::{ShowWindow, SW_HIDE, SW_SHOW};
                if shared.visible.load(Ordering::Relaxed) {
                    if shared.active.load(Ordering::Relaxed) {
                        follow_tray(hwnd);
                        let _ = ShowWindow(hwnd, SW_SHOW);
                    } else if try_dock(hwnd) {
                        shared.active.store(true, Ordering::Relaxed);
                        let _ = ShowWindow(hwnd, SW_SHOW);
                    }
                    let tip = TOOLTIP.lock().map(|t| HWND(*t as *mut _)).unwrap_or_default();
                    update_tooltip(hwnd, tip, shared);
                    let _ = InvalidateRect(hwnd, None, false);
                } else {
                    let _ = ShowWindow(hwnd, SW_HIDE);
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
            PostQuitMessage(0);
            LRESULT(0)
        }
        _ => DefWindowProcW(hwnd, msg, wparam, lparam),
    }
}

unsafe fn paint(hwnd: windows::Win32::Foundation::HWND) {
    use windows::Win32::Foundation::{COLORREF, RECT};
    use windows::Win32::Graphics::Gdi::{
        BeginPaint, CreateFontW, CreateSolidBrush, DeleteObject, EndPaint, FillRect,
        SelectObject, SetBkMode, SetTextColor, TextOutW, PAINTSTRUCT, TRANSPARENT,
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
    line(&format!("Down {down}"), ty(0), 0x0071CC_2E);
    line(&format!("Up {up}"), ty(1), 0x00F6823B);
    let _ = SelectObject(hdc, old);
    let _ = DeleteObject(font);
    EndPaint(hwnd, &ps);
}

unsafe fn show_menu(hwnd: windows::Win32::Foundation::HWND, shared: &DockShared) {
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
