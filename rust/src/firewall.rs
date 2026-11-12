//! Windows Firewall enforcement for blocked rules, applied by
//! the GUI itself (standalone mode) through `netsh advfirewall`,
//! elevated with the "runas" verb — the user approves a UAC
//! prompt per change. This mirrors what the project's C# build
//! did from its service: one block rule per direction, named
//! after the executable so it can be found and removed again.
//!
//! The permanent home for this is the service (which runs as
//! SYSTEM and needs no prompt); until the GUI is wired to the
//! service, this keeps Block honest instead of saved-only.

fn rule_name(exe_path: &str, dir: &str) -> String {
    format!("FluxLimiter Block {dir} | {exe_path}")
}

/// Apply (`blocked = true`) or remove the firewall block for an
/// executable. Err carries a short user-facing reason.
pub fn set_blocked(exe_path: &str, blocked: bool) -> Result<(), String> {
    #[cfg(windows)]
    {
        for dir in ["out", "in"] {
            let name = rule_name(exe_path, dir);
            // Always clear any stale rule first (also the whole
            // job when unblocking). Deleting a rule that does not
            // exist makes netsh exit non-zero — that is fine.
            run_netsh(
                &format!("advfirewall firewall delete rule name=\"{name}\""),
                true,
            )?;
            if blocked {
                run_netsh(
                    &format!(
                        "advfirewall firewall add rule name=\"{name}\" dir={dir} action=block program=\"{exe_path}\" enable=yes profile=any"
                    ),
                    false,
                )?;
            }
        }
        Ok(())
    }
    #[cfg(not(windows))]
    {
        let _ = (exe_path, blocked);
        Err("Windows only".to_string())
    }
}

/// Run one netsh command elevated, hidden, and wait for its exit
/// code. A dismissed UAC prompt is reported, not hidden. With
/// `ignore_exit`, a non-zero exit code still counts as success.
#[cfg(windows)]
fn run_netsh(params: &str, ignore_exit: bool) -> Result<(), String> {
    use windows::core::{w, PCWSTR};
    use windows::Win32::Foundation::{CloseHandle, HWND, WAIT_OBJECT_0};
    use windows::Win32::System::Threading::{GetExitCodeProcess, WaitForSingleObject};
    use windows::Win32::UI::Shell::{
        ShellExecuteExW, SEE_MASK_NOCLOSEPROCESS, SHELLEXECUTEINFOW,
    };
    use windows::Win32::UI::WindowsAndMessaging::SW_HIDE;

    let file: Vec<u16> = "netsh.exe".encode_utf16().chain(std::iter::once(0)).collect();
    let args: Vec<u16> = params.encode_utf16().chain(std::iter::once(0)).collect();
    let mut info = SHELLEXECUTEINFOW {
        cbSize: std::mem::size_of::<SHELLEXECUTEINFOW>() as u32,
        fMask: SEE_MASK_NOCLOSEPROCESS,
        hwnd: HWND(std::ptr::null_mut()),
        lpVerb: w!("runas"),
        lpFile: PCWSTR(file.as_ptr()),
        lpParameters: PCWSTR(args.as_ptr()),
        nShow: SW_HIDE.0,
        ..Default::default()
    };
    let launched = unsafe { ShellExecuteExW(&mut info as *mut SHELLEXECUTEINFOW) };
    if launched.is_err() {
        return Err("admin approval was not given".to_string());
    }
    let process = info.hProcess;
    if process.is_invalid() {
        return Err("could not start netsh".to_string());
    }
    let mut code: u32 = 0;
    let result = unsafe {
        let waited = WaitForSingleObject(process, 30_000);
        if waited == WAIT_OBJECT_0 && GetExitCodeProcess(process, &mut code).is_ok() {
            if code == 0 || ignore_exit {
                Ok(())
            } else {
                Err(format!("netsh failed (exit {code})"))
            }
        } else {
            Err("netsh did not finish in time".to_string())
        }
    };
    unsafe {
        let _ = CloseHandle(process);
    }
    result
}
