# Install & update

[← Back to README](../README.md)

## Requirements

- Windows 10 (1809 or newer) or Windows 11, 64-bit
- A game running in **borderless** or **windowed** mode, because the overlay can't draw over exclusive fullscreen

Nothing else is needed. The .NET runtime is built into `SFHud.exe`.

## Installer (recommended)

1. Download `SFHud-Free-Setup-<version>.exe` from [Releases](https://github.com/TermsMet/thesfhud/releases/latest).
2. Run it. If Windows SmartScreen appears, choose **More info → Run anyway**. The exe isn't code-signed, so you can [check the checksum](#check-a-download) first if you like.
3. Pick a folder. The default, `%LOCALAPPDATA%\Programs\SFHud`, needs no admin rights.
4. Choose whether to add desktop and Start Menu shortcuts.
5. Click **Install**, then **Launch SFHud**.

## Portable

Extract `SFHud-Free-v<version>-win-x64.zip` anywhere and run `SFHud.exe`. The portable copy uses the same data folder as the installed one. To give it its own data folder, see `SFHUD_DATA_DIR` in [BUILDING.md](BUILDING.md#command-line-and-environment). You can install a portable copy later from **Settings → About → Install / update SFHud**.

## Updating

When a new version is out, a bar appears at the top of the SFHud window. **UPDATE NOW** downloads the installer, checks its SHA-256 and runs setup. You can also run a newer `SFHud-Free-Setup-x.y.z.exe` by hand.

Setup closes SFHud (which saves first) and replaces the program. **Your notes, settings, keybinds and backups are kept.**

## Uninstalling

Go to **Windows Settings → Apps → SFHud → Uninstall**. Your data is kept unless you tick *Also delete my notes and settings*. Backups are only removed if you tick that box as well.

## Check a download

```powershell
Get-FileHash .\SFHud-Free-Setup-1.2.0.exe -Algorithm SHA256
```

Compare the hash with the one in the matching `.sha256` file on the release page.

## Troubleshooting

| Problem | Fix |
|---|---|
| The overlay doesn't show in game | Switch the game to borderless or windowed mode, and check the game is selected under **Games & servers**. The overlay hides whenever another app is in front. |
| The overlay shows over the wrong app | Edit the **overlay whitelist** under **Games & servers**. |
| A hotkey does nothing | Another app may already own that key. **Overlay → Keybinds** flags conflicts. |
| Text is too small on 4:3 or CRT | Turn on **Overlay → Display → Smallest text on low resolutions**. |
| The first launch is slow | The single-file exe unpacks three WPF DLLs to `%TEMP%\.net\SFHud\` once. Later launches are fast. |
