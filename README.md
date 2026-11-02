# syncloom-windows

Native Windows client: **WinUI 3** (Fluent, Mica) on the Windows App SDK, calling the shared Rust core through a small C ABI.

> **Status: public preview.** The app built and ran on Windows 11 on ARM64 (0.1.1). **On-demand files (0.1.2) are written and unit-tested, but have not been run on Windows yet**: the sync logic is tested on any OS, the Cloud Files calls compile but need a first run. The x64 build has not been tried.

## What it does
- Sign-in, registration, second factor (authenticator app, recovery codes) and enrolment, with the same flow as the other apps
- File browser (Files, Shared with me, Trash): upload, download, new folder, delete/restore
- **On-demand files**: the sync folder shows everything on the server as placeholders. A file downloads and decrypts when you open it; "Free up space" in Explorer drops the local copy again
- Keys kept DPAPI-encrypted in `%LOCALAPPDATA%\Syncloom`
- Registers the sync folder as a Windows sync root (Explorer entry with status icons)

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

## On-demand files

Three projects, so the logic can be tested without Windows:

| Project | What |
|---|---|
| `Syncloom.Sync` | The engine (plain .NET): mirrors the server tree as placeholders, uploads what you create or edit, makes conflict copies, follows renames and deletes both ways. Talks to the server and the disk only through two small interfaces. |
| `Syncloom.Sync.Tests` | xUnit tests with an in-memory server and disk. `dotnet test Syncloom.Sync.Tests` works on Windows, macOS and Linux. |
| `Syncloom.CloudFiles` | The Windows part (Cloud Files API through CsWin32): creates and updates placeholders, answers the hydration request when a file is opened, and asks the engine before Explorer deletes or renames a file. |

Rebuild `syncloom_core.dll` for this version (`cargo build --release --features capi`, then copy it to `Syncloom\native\`): the engine uses the C ABI operations `upload_version`, `move_to`, `changes`, `versions` and `hash_file`, which older builds do not have.

How it behaves:
- **Opening a file** downloads it completely, decrypts it and hands it to Windows (policy "full"). Large files therefore take a moment on first open.
- **New and edited files** in the folder are uploaded (a new version, or a new file). If both sides changed a file, your edit is kept as `name (conflict – device – date).ext`.
- **Delete and rename in Explorer** go to the server (the trash and a rename or move). If the server refuses, Windows keeps the file.
- **Changes on the server** show up within about ten seconds: new files as placeholders, edited ones dropped to a placeholder again (unless you edited them locally).
- **Existing files** from the older plain-folder sync become placeholders when their content hash equals the server version, so nothing is uploaded twice. A file with the same name but other content (even of the same size) is never taken for synced: it is uploaded as a conflict copy.
- Files such as `~$*`, `*.tmp`, `desktop.ini` and `Thumbs.db` are never uploaded.

Limits: a move on the server shows up as delete and create (the file has to be downloaded again); signing out unregisters the sync root and leaves the folder with empty placeholders; a file that Explorer or another app has open cannot be dropped to a placeholder until it is closed.
