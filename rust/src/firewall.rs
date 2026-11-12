//! Windows Firewall enforcement for blocked rules, applied by
//! the GUI itself (standalone mode) through `netsh advfirewall`,
//! elevated with the "runas" verb — the user approves a UAC
//! prompt per change. One block rule per direction, named after
//! the executable so it can be found and removed again.
//!
//! The flow first checks (unelevated) whether each rule already
//! exists, so a toggle only prompts for the changes it actually
//! needs, and afterwards verifies the final state — a rule that
//! claims success but is not really there is reported as an
//! error, not hidden. The permanent home for this is the
//! service (SYSTEM, no prompts); until the GUI is wired to the
//! service, this keeps Block honest instead of saved-only.

/// Short stable hash of the exe path (djb2, hex). Rule names
/// carry this instead of the raw path: full paths in rule names
/// made names long and full of characters (`|`, spaces,
/// backslashes) that netsh's parser can trip on — an `add rule`
/// for brave.exe was seen failing with exit 1 while deletes of
/// the same-named rules worked.
fn path_hash(exe_path: &str) -> String {
    let mut h: u32 = 5381;
    for b in exe_path.to_ascii_lowercase().bytes() {
        h = h.wrapping_mul(33).wrapping_add(b as u32);
    }
    format!("{h:08x}")
}

fn rule_name(exe_path: &str, dir: &str) -> String {
    format!("FluxLimiter Block {dir} {}", path_hash(exe_path))
}

/// The name scheme used by the first builds (path embedded).
fn legacy_rule_name(exe_path: &str, dir: &str) -> String {
    format!("FluxLimiter Block {dir} | {exe_path}")
}

/// Apply (`blocked = true`) or remove the firewall block for an
/// executable. On success returns the executable path the rules
/// point at, verified by reading the rules back.
pub fn set_blocked(exe_path: &str, blocked: bool) -> Result<String, String> {
    #[cfg(windows)]
    {
        // Clean up rules left by the first builds' name scheme,
        // so an old rule can never keep an app blocked (or
        // half-blocked) after the toggle says otherwise.
        for dir in ["out", "in"] {
            let legacy = legacy_rule_name(exe_path, dir);
            if inspect_rule(&legacy, exe_path).0 {
                let _ = run_netsh(
                    &format!("advfirewall firewall delete rule name=\"{legacy}\""),
                    true,
                );
            }
        }
        for dir in ["out", "in"] {
            let name = rule_name(exe_path, dir);
            let (exists, program_ok) = inspect_rule(&name, exe_path);
            if blocked {
                if exists && !program_ok {
                    // Stale rule for a different program under our
                    // name: replace it.
                    run_netsh(&format!("advfirewall firewall delete rule name=\"{name}\""), true)?;
                    run_netsh(&add_params(&name, dir, exe_path), false)?;
                } else if !exists {
                    run_netsh(&add_params(&name, dir, exe_path), false)?;
                }
            } else if exists {
                run_netsh(&format!("advfirewall firewall delete rule name=\"{name}\""), false)?;
            }
        }
        // Verify the end state instead of trusting exit codes.
        for dir in ["out", "in"] {
            let name = rule_name(exe_path, dir);
            let (exists, program_ok) = inspect_rule(&name, exe_path);
            if blocked && !(exists && program_ok) {
                return Err(format!(
                    "the {dir} rule could not be verified in Windows Firewall"
                ));
            }
            if !blocked && exists {
                return Err(format!("the {dir} rule is still present in Windows Firewall"));
            }
        }
        Ok(exe_path.to_string())
    }
    #[cfg(not(windows))]
    {
        let _ = (exe_path, blocked);
        Err("Windows only".to_string())
    }
}

fn add_params(name: &str, dir: &str, exe_path: &str) -> String {
    format!(
        "advfirewall firewall add rule name=\"{name}\" dir={dir} action=block program=\"{exe_path}\" enable=yes profile=any"
    )
}

/// Read a rule back (no elevation needed). Returns whether it
/// exists and whether its program path matches `exe_path`.
#[cfg(windows)]
fn inspect_rule(name: &str, exe_path: &str) -> (bool, bool) {
    use std::os::windows::process::CommandExt;
    const CREATE_NO_WINDOW: u32 = 0x0800_0000;
    let output = std::process::Command::new("netsh")
        .args(["advfirewall", "firewall", "show", "rule", &format!("name={name}")])
        .creation_flags(CREATE_NO_WINDOW)
        .output();
    match output {
        Ok(out) if out.status.success() => {
            let text = String::from_utf8_lossy(&out.stdout).to_lowercase();
            (true, text.contains(&exe_path.to_lowercase()))
        }
        _ => (false, false),
    }
}

/// Run one netsh command elevated, hidden, and wait for its exit
/// code. The command runs under `cmd /c` with its output
/// redirected to a temp file, so a failure reports **netsh's own
/// words** (an exit code alone — "exit 1" — proved useless for
/// diagnosis). A dismissed UAC prompt is reported, not hidden.
/// With `ignore_exit`, a non-zero exit code still counts as
/// success.
#[cfg(windows)]
fn run_netsh(params: &str, ignore_exit: bool) -> Result<(), String> {
    use windows::core::{w, PCWSTR};
    use windows::Win32::Foundation::{CloseHandle, HWND, WAIT_OBJECT_0};
    use windows::Win32::System::Threading::{GetExitCodeProcess, WaitForSingleObject};
    use windows::Win32::UI::Shell::{
        ShellExecuteExW, SEE_MASK_NOCLOSEPROCESS, SHELLEXECUTEINFOW,
    };
    use windows::Win32::UI::WindowsAndMessaging::SW_HIDE;

    let out_file = std::env::temp_dir().join("fluxlimiter-netsh.txt");
    let _ = std::fs::remove_file(&out_file);
    let command = format!(
        "/c netsh {params} > \"{}\" 2>&1",
        out_file.to_string_lossy()
    );
    let file: Vec<u16> = "cmd.exe".encode_utf16().chain(std::iter::once(0)).collect();
    let args: Vec<u16> = command.encode_utf16().chain(std::iter::once(0)).collect();
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
                // netsh's own explanation, first meaningful lines.
                let text = std::fs::read(&out_file)
                    .map(|b| String::from_utf8_lossy(&b).into_owned())
                    .unwrap_or_default();
                let snippet: String = text
                    .lines()
                    .map(|l| l.trim())
                    .filter(|l| !l.is_empty())
                    .take(2)
                    .collect::<Vec<_>>()
                    .join(" ")
                    .chars()
                    .take(160)
                    .collect();
                if snippet.is_empty() {
                    Err(format!("netsh failed (exit {code})"))
                } else {
                    Err(format!("netsh: {snippet}"))
                }
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
