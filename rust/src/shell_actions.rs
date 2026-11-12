//! Windows Explorer actions for an application row, the way
//! NetLimiter offers them: jump to the executable on disk, or open
//! its Explorer Properties sheet.
//!
//! Both are Windows-only; elsewhere they report `false` and the UI
//! disables the menu items instead of pretending.

/// Open Explorer with the executable selected ("Open file location").
///
/// Goes through ShellExecute so the `/select,"..."` parameter
/// reaches Explorer verbatim — passing it as a `Command` argument
/// gets the embedded quotes re-escaped by the runtime, and
/// Explorer then falls back to opening a default folder instead.
pub fn open_file_location(exe_path: &str) -> bool {
    #[cfg(windows)]
    {
        use windows::core::{w, PCWSTR};
        use windows::Win32::Foundation::HWND;
        use windows::Win32::UI::Shell::ShellExecuteW;

        let params: Vec<u16> = format!("/select,\"{exe_path}\"")
            .encode_utf16()
            .chain(std::iter::once(0))
            .collect();
        let result = unsafe {
            ShellExecuteW(
                HWND(std::ptr::null_mut()),
                w!("open"),
                w!("explorer.exe"),
                PCWSTR(params.as_ptr()),
                PCWSTR::null(),
                windows::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL,
            )
        };
        // ShellExecute returns a pseudo-handle > 32 on success.
        return result.0 as isize > 32;
    }
    #[cfg(not(windows))]
    {
        let _ = exe_path;
        false
    }
}

/// Open the Explorer Properties dialog for the executable.
pub fn file_properties(exe_path: &str) -> bool {
    #[cfg(windows)]
    {
        use windows::core::{w, PCWSTR};
        use windows::Win32::Foundation::HWND;
        use windows::Win32::UI::Shell::{
            ShellExecuteExW, SEE_MASK_INVOKEIDLIST, SHELLEXECUTEINFOW,
        };

        let file: Vec<u16> = exe_path.encode_utf16().chain(std::iter::once(0)).collect();
        let mut info = SHELLEXECUTEINFOW {
            cbSize: std::mem::size_of::<SHELLEXECUTEINFOW>() as u32,
            fMask: SEE_MASK_INVOKEIDLIST,
            hwnd: HWND(std::ptr::null_mut()),
            lpVerb: w!("properties"),
            lpFile: PCWSTR(file.as_ptr()),
            nShow: 1, // SW_SHOWNORMAL
            ..Default::default()
        };
        return unsafe { ShellExecuteExW(&mut info as *mut SHELLEXECUTEINFOW) }.is_ok();
    }
    #[cfg(not(windows))]
    {
        let _ = exe_path;
        false
    }
}

/// Whether the shell actions can work on this platform at all.
pub const AVAILABLE: bool = cfg!(windows);
