# Data & privacy

[← Back to README](../README.md)

## Where your data lives

Everything is kept in `%LOCALAPPDATA%\SFHud`, with one folder per Windows user:

| Path | Contents |
|---|---|
| `userdata.json` | Profiles, presets, pads, notes, keybinds and settings |
| `backups\` | Rolling copies of `userdata.json` |
| `sounds\` | Sounds you imported |
| `logs\` | Diagnostic logs |

- SFHud saves a moment after every change. **Ctrl+S** saves immediately.
- Backups are taken at start-up, every 30 minutes and before anything destructive. You can restore one from **Settings → Data & backups**.
- If `userdata.json` is ever damaged, SFHud restores the newest backup automatically and keeps the damaged copy next to it.

## Moving to another PC

**Settings → Data → Export everything** writes a single file. On the new PC, use **Import everything**.

Single pads (`.sfhudpad`) and presets (`.sfhudpreset`) can be shared on their own too.

## Privacy

- Notes, settings and layouts never leave your PC.
- SFHud doesn't read game memory or inject into any process. The overlay is its own window.
- SFHud connects to the network only to:
  - check the [GitHub releases](https://github.com/TermsMet/SFHud/releases) for updates
  - read a server status URL or open a link, if you set one up yourself

## Upgrading from SFHud 0.x

The old `settings.json` and `notes.json` files are imported on first launch and left untouched.
