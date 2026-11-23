# Building from source

[← Back to README](../README.md)

## Requirements

- Windows 10 or 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- PowerShell 5.1 or newer (for `build.ps1`)

## Quick build

```powershell
git clone https://github.com/TermsMet/SFHud.git
cd SFHud
dotnet run --project src/SFHud.App      # debug run
```

> **Tip:** run debug builds against a scratch data folder so your real notes aren't touched:
> ```powershell
> $env:SFHUD_DATA_DIR = "$PWD\testdata"; dotnet run --project src/SFHud.App
> ```

## Release build

```powershell
.\build.ps1
```

This produces:

| Output | Path |
|---|---|
| Single-file exe | `publish\…\SFHud.exe` |
| Installer | `dist\SFHud-Free-Setup-<version>.exe` |
| Portable zip | `dist\SFHud-Free-v<version>-win-x64.zip` |
| Checksums | `dist\*.sha256` |

### `build.ps1` flags

| Flag | Effect |
|---|---|
| `-FrameworkDependent` | Much smaller output that needs the .NET 10 Desktop Runtime installed |
| `-NoSingleFile` | A folder of DLLs instead of one exe (no installer) |
| `-ReadyToRun` | Faster start-up, bigger exe |
| `-NoZip` | Skip the zip |
| `-Icon` | Rebuild `sfhud.ico` from `assets\icon.png` |
| `-StopRunning` / `-Run` | Close a copy running from the output folder / launch it when done |

## Command line and environment

| Option | Effect |
|---|---|
| `SFHud.exe --install` / `--uninstall` | Opens setup or uninstall. Any copy whose file name contains "Setup" starts in setup mode. |
| `SFHud.exe --page <id>` | Opens a page at start-up |
| `SFHUD_DATA_DIR` | Uses a different data folder (for portable or test copies), with its own single-instance lock |

## Notes

- **Single-file exe**: WPF's three native DLLs can't load from memory, so on first launch they're unpacked once to `%TEMP%\.net\SFHud\`.
- **Edition**: there is one edition with every feature. `Services/Edition.cs` keeps the old feature list, but every check returns true and there is no license code.

## Project layout

```text
src/SFHud.App/
├── Controls/     custom WPF controls (HUD frame, element editor, crosshair…)
├── Interop/      Win32: hotkeys, window shape, game detection
├── Services/     data store, DEFCON, timers, media, themes, presets…
├── Theme/        XAML colour, control and template dictionaries
└── Views/        launcher, overlay, note pad, quick note, installer windows
```
