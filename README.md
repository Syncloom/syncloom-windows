# syncloom-windows

Native Windows client: **WinUI 3** (Fluent, Mica) on the Windows App SDK, calling the shared Rust core through a small C ABI.

> **Status: written but not compiled.** WinUI 3 and the Windows App SDK cannot be built or run on macOS, and no Windows machine was available while this was written. The C ABI it relies on (`syncloom-core/src/capi.rs`) is built and tested on macOS and Linux; the C#/XAML layer on top of it is **unverified**. Expect small compile fixes on the first build.

## What it does
- Sign-in, registration, second factor (authenticator app, recovery codes) and enrolment, with the same flow as the other apps
- File browser (Files, Shared with me, Trash): upload, download, new folder, delete/restore
- Folder sync with the core's sync engine (`sync_once`), merge and conflict copies included
- Keys kept DPAPI-encrypted in `%LOCALAPPDATA%\Syncloom`
- Registers the sync folder as a Windows sync root (Explorer entry). On-demand placeholders (Cloud Files API hydration callbacks) are the next step

## Build (on Windows)
```powershell
# 1. the Rust core with the C ABI
cd ..\syncloom-core
cargo build --release --features capi --target x86_64-pc-windows-msvc
mkdir ..\syncloom-windows\Syncloom\native
copy target\x86_64-pc-windows-msvc\release\syncloom_core.dll ..\syncloom-windows\Syncloom\native\

# 2. the app (.NET 8 SDK + Windows App SDK workload)
cd ..\syncloom-windows\Syncloom
dotnet build -c Release -p:Platform=x64
```

## The C ABI
`syncloom_call(request_json) -> response_json` with `{ "op": "...", ... }`; responses are `{"ok": ...}` or `{"error": "..."}`. See `Services/Native.cs`.
