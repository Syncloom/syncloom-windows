# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [semantic versioning](https://semver.org/). Syncloom is a public preview until 1.0.

## [Unreleased]

### Added
- On-demand files: the sync folder holds placeholders for everything on the server. Opening a file downloads and decrypts it. Local changes, deletes and renames in Explorer are sent to the server, conflicts become copies, and server changes appear as placeholders. See the README.
- Existing files in the sync folder are only taken for synced when their content hash matches the server version, not just name and size. Different content becomes a conflict copy.
- `Syncloom.Sync` (the engine, unit-tested on any OS) and `Syncloom.CloudFiles` (the Cloud Files API calls).

### Changed
- The folder sync no longer uses the core's `sync_once`.

### Known limits
- The Cloud Files part has not been run on Windows yet.
- A file is downloaded completely on first open.

## [0.1.1] - 2026-11-02

First version built and run on Windows (Windows 11 on ARM64).

### Fixed
- The project file loads: an XML comment contained `--`.
- Compile errors in session storage, the authenticator QR code and the upload timestamp.
- The app starts without a separately installed Windows App SDK runtime, which is now bundled with it.
- The app finds `syncloom_core.dll`, which is now copied next to the executable.

### Known limits
- The x64 build has not been tried.
- Cloud Files hydration callbacks are not implemented.

## [0.1.0] - 2026-11-02

First public preview.

### Added
- WinUI 3 client skeleton that registers a sync root through the Cloud Files API, on top of the C ABI of the core.

### Known limits
- Never built or run: it cannot be compiled on the development machine.
- Cloud Files hydration callbacks are not implemented.
