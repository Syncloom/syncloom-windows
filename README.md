# syncloom-windows

Native Windows client: **WinUI 3** (Fluent, Mica) on the Windows App SDK, calling the shared Rust core through a small C ABI.

> **Status: public preview.** Built and run on Windows 11 on ARM64. The x64 build has not been tried.

## What it does
- Sign-in, registration, second factor (authenticator app, recovery codes) and enrolment, with the same flow as the other apps
- File browser (Files, Shared with me, Trash): upload, download, new folder, delete/restore
- Folder sync with the core's sync engine (`sync_once`), merge and conflict copies included
- Keys kept DPAPI-encrypted in `%LOCALAPPDATA%\Syncloom`
- Registers the sync folder as a Windows sync root (Explorer entry). On-demand placeholders (Cloud Files API hydration callbacks) are the next step

## Build (on Windows)
Needs Rust, the .NET SDK and Visual Studio with the C++ tools. These steps build for the machine's own architecture, shown here for ARM64. On x64, use `x64` and `win-x64` in the last command.

On ARM64 the core also needs clang (the "C++ Clang" component of Visual Studio). It does not have to be on the `PATH` permanently:

```powershell
$env:PATH = "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\Llvm\ARM64\bin;$env:PATH"
```

```powershell
# 1. the Rust core with the C ABI
cd ..\syncloom-core
cargo build --release --features capi
mkdir ..\syncloom-windows\Syncloom\native
copy target\release\syncloom_core.dll ..\syncloom-windows\Syncloom\native\

# 2. the app (the Windows App SDK comes from NuGet and is bundled with it)
cd ..\syncloom-windows\Syncloom
dotnet build -c Release -p:Platform=ARM64 -r win-arm64
```

## The C ABI
`syncloom_call(request_json) -> response_json` with `{ "op": "...", ... }`; responses are `{"ok": ...}` or `{"error": "..."}`. See `Services/Native.cs`.
