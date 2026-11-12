//! `fluxlimiter-service` — headless service binary.
//!
//! On Windows it listens on the protocol-v1 named pipe
//! `\\.\pipe\FluxLimiter.Control` (one client at a time, first
//! instance owns the pipe). Elsewhere it serves the same
//! length-prefixed frames on stdin/stdout, so the dispatch logic can
//! be exercised and tested on any platform.
//!
//! Still honest about the limits: the service stores rules and
//! reports live speeds, but nothing here enforces a limit yet —
//! that needs the driver engine (phase 2). Run as administrator on
//! Windows for the full connection tables.

#[cfg(windows)]
#[cfg(windows)]
use fluxlimiter::protocol::PIPE_NAME;
use fluxlimiter::service::Service;

fn main() {
    let mut service = Service::new();
    #[cfg(windows)]
    run_pipe(&mut service);
    #[cfg(not(windows))]
    {
        let _ = run_stdio_loop(&mut service);
    }
}

#[cfg(not(windows))]
fn run_stdio_loop(service: &mut Service) -> std::io::Result<()> {
    use std::io::{Read, Write};
    let stdin = std::io::stdin();
    let stdout = std::io::stdout();
    let mut input = stdin.lock();
    let mut output = stdout.lock();
    let mut len_buf = [0u8; 4];
    loop {
        // Read the 4-byte little-endian length prefix.
        match input.read_exact(&mut len_buf) {
            Ok(()) => {}
            Err(e) if e.kind() == std::io::ErrorKind::UnexpectedEof => break,
            Err(e) => return Err(e),
        }
        let len = i32::from_le_bytes(len_buf);
        if len < 0 || len as usize > fluxlimiter::protocol::MAX_FRAME_BYTES {
            return Err(std::io::Error::new(
                std::io::ErrorKind::InvalidData,
                "frame too large",
            ));
        }
        let mut payload = vec![0u8; len as usize];
        input.read_exact(&mut payload)?;
        let Some(req) = fluxlimiter::protocol::decode_request(&payload) else {
            continue;
        };
        let resp = service.handle(&req);
        let resp_json = serde_json::to_string(&resp).unwrap_or_default();
        let resp_frame = fluxlimiter::protocol::encode_frame(&resp_json);
        output.write_all(&resp_frame)?;
        output.flush()?;
    }
    Ok(())
}

#[cfg(windows)]
fn run_pipe(service: &mut Service) {
    use windows::Win32::Foundation::{CloseHandle, HANDLE, INVALID_HANDLE_VALUE};
    use windows::Win32::Security::Authorization::{
        ConvertStringSecurityDescriptorToSecurityDescriptorW, SDDL_REVISION_1,
    };
    use windows::Win32::Security::{PSECURITY_DESCRIPTOR, SECURITY_ATTRIBUTES};
    use windows::Win32::Storage::FileSystem::{FILE_FLAG_FIRST_PIPE_INSTANCE, PIPE_ACCESS_DUPLEX};
    use windows::Win32::System::Pipes::{
        ConnectNamedPipe, CreateNamedPipeW, DisconnectNamedPipe, PIPE_READMODE_BYTE,
        PIPE_TYPE_BYTE, PIPE_WAIT,
    };
    use windows::core::PCWSTR;

    // Pipe security, mirroring the C# service's PipeSecurity:
    // deny the network SID outright; full control for SYSTEM and
    // administrators; read/write for built-in users (the GUI runs
    // unprivileged). First-instance ownership stops a second process
    // from squatting the pipe name.
    let sddl: Vec<u16> = "D:P(D;;FA;;;NU)(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGW;;;BU)"
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect();
    let mut sd = PSECURITY_DESCRIPTOR(std::ptr::null_mut());
    let sd_ok = unsafe {
        ConvertStringSecurityDescriptorToSecurityDescriptorW(
            PCWSTR(sddl.as_ptr()),
            SDDL_REVISION_1,
            &mut sd,
            None,
        )
    }
    .is_ok();
    let sa = SECURITY_ATTRIBUTES {
        nLength: std::mem::size_of::<SECURITY_ATTRIBUTES>() as u32,
        lpSecurityDescriptor: sd.0,
        bInheritHandle: false.into(),
    };
    let sa_ptr: Option<*const SECURITY_ATTRIBUTES> =
        if sd_ok { Some(&sa as *const SECURITY_ATTRIBUTES) } else { None };

    let mut name: Vec<u16> = PIPE_NAME.encode_utf16().chain(std::iter::once(0)).collect();
    let mut first_instance = true;
    loop {
        let open_mode = if first_instance {
            PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE
        } else {
            PIPE_ACCESS_DUPLEX
        };
        let pipe: HANDLE = unsafe {
            CreateNamedPipeW(
                PCWSTR(name.as_mut_ptr()),
                open_mode,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                1,
                65536,
                65536,
                0,
                sa_ptr,
            )
        };
        first_instance = false;
        if pipe == INVALID_HANDLE_VALUE {
            std::thread::sleep(std::time::Duration::from_secs(1));
            continue;
        }
        if unsafe { ConnectNamedPipe(pipe, None) }.is_err() {
            unsafe { CloseHandle(pipe).ok() };
            continue;
        }
        serve_pipe_client(service, pipe);
        unsafe {
            DisconnectNamedPipe(pipe).ok();
            CloseHandle(pipe).ok();
        }
    }
}

#[cfg(windows)]
fn serve_pipe_client(service: &mut Service, pipe: windows::Win32::Foundation::HANDLE) {
    use windows::Win32::Foundation::HANDLE;
    use windows::Win32::Storage::FileSystem::{FlushFileBuffers, ReadFile, WriteFile};

    let read_exact = |pipe: HANDLE, buf: &mut [u8]| -> bool {
        let mut offset = 0;
        while offset < buf.len() {
            let mut read: u32 = 0;
            let ok = unsafe { ReadFile(pipe, Some(&mut buf[offset..]), Some(&mut read), None) };
            if ok.is_err() || read == 0 {
                return false;
            }
            offset += read as usize;
        }
        true
    };
    loop {
        let mut len_buf = [0u8; 4];
        if !read_exact(pipe, &mut len_buf) {
            return;
        }
        let len = i32::from_le_bytes(len_buf);
        if len < 0 || len as usize > fluxlimiter::protocol::MAX_FRAME_BYTES {
            return;
        }
        let mut payload = vec![0u8; len as usize];
        if !read_exact(pipe, &mut payload) {
            return;
        }
        let Some(req) = fluxlimiter::protocol::decode_request(&payload) else {
            return;
        };
        let resp = service.handle(&req);
        let resp_json = serde_json::to_string(&resp).unwrap_or_default();
        let resp_frame = fluxlimiter::protocol::encode_frame(&resp_json);
        let mut written: u32 = 0;
        let ok = unsafe { WriteFile(pipe, Some(&resp_frame), Some(&mut written), None) };
        if ok.is_err() || written as usize != resp_frame.len() {
            return;
        }
        unsafe { FlushFileBuffers(pipe).ok() };
    }
}
