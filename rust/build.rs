//! Build script: give the Windows executables the FluxLimiter
//! icon (the blue bars mark from the project's reference design).
//!
//! The .ico travels through the repo as base85 text
//! (`assets/icon.b85`) because the sync channel for this project
//! only carries text files; decode it here, hand it to
//! `winresource`, and degrade gracefully (plain build, no icon)
//! if the resource compiler is unavailable.

fn b85_decode(text: &str) -> Vec<u8> {
    // Python base64.b85 alphabet (index = digit value).
    const ALPHABET: &[u8] = b"0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz!#$%&()*+-;<=>?@^_`{|}~";
    let mut rev = [u8::MAX; 256];
    for (i, &c) in ALPHABET.iter().enumerate() {
        rev[c as usize] = i as u8;
    }
    let digits: Vec<u8> = text
        .bytes()
        .filter_map(|b| match rev[b as usize] {
            u8::MAX => None,
            v => Some(v),
        })
        .collect();
    let mut out = Vec::with_capacity(digits.len() * 4 / 5 + 4);
    for chunk in digits.chunks(5) {
        let mut acc: u64 = 0;
        for &d in chunk {
            acc = acc * 85 + d as u64;
        }
        // A trailing group of n chars encodes n-1 bytes; pad it
        // with the max digit (84) before decoding, as b85 requires.
        let nbytes = if chunk.len() == 5 {
            4
        } else {
            for _ in chunk.len()..5 {
                acc = acc * 85 + 84;
            }
            chunk.len() - 1
        };
        let be = (acc as u32).to_be_bytes();
        out.extend_from_slice(&be[..nbytes]);
    }
    out
}

fn main() {
    println!("cargo:rerun-if-changed=assets/icon.b85");
    println!("cargo:rerun-if-changed=build.rs");
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") {
        return;
    }
    let Ok(text) = std::fs::read_to_string("assets/icon.b85") else {
        println!("cargo:warning=assets/icon.b85 missing; building without an exe icon");
        return;
    };
    let ico = b85_decode(&text);
    let out_dir = std::env::var("OUT_DIR").unwrap_or_else(|_| ".".into());
    let ico_path = std::path::Path::new(&out_dir).join("fluxlimiter.ico");
    if std::fs::write(&ico_path, &ico).is_err() {
        println!("cargo:warning=could not write icon file; building without an exe icon");
        return;
    }
    let mut res = winresource::WindowsResource::new();
    res.set_icon(ico_path.to_string_lossy().as_ref());
    res.set("ProductName", "FluxLimiter");
    res.set("FileDescription", "FluxLimiter - network monitor and limiter");
    if let Err(e) = res.compile() {
        // Never fail the whole build over an icon.
        println!("cargo:warning=icon resource not embedded ({e}); continuing without it");
    }
}
