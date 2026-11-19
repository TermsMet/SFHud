# User guide

[← Back to README](../README.md)

- [How the overlay works](#how-the-overlay-works)
- [Note pads](#note-pads)
- [Overlay & customize mode](#overlay--customize-mode)
- [DEFCON](#defcon)
- [Server presets](#server-presets)
- [Timers & sounds](#timers--sounds)
- [Music player](#music-player)
- [Crosshair](#crosshair)
- [Look & themes](#look--themes)
- [Free edition limits](#free-edition-limits)

---

## How the overlay works

The overlay is a separate, transparent, click-through window. It is **never injected into the game**, and it shows only while the selected game, or an app on your **overlay whitelist** (Games & servers), is in front. It hides over the SFHud window, the desktop and every other app, and it never takes focus away from the game.

- Element positions are stored relative to the screen, and the overlay scales with the game's resolution (1080p = 1×).
- On 1440p and 4K, the overlay, its menu and the main window scale up automatically.
- On 4:3 / CRT resolutions (800×600, 1024×768, 1280×960), turn on **Overlay → Display → Smallest text on low resolutions**.
- **Display** options include overlay opacity, scale, distraction-free mode, and hiding SFHud from screenshots and OBS.

## Note pads

- **Tabs**: create as many pads as you need (up to 60 per preset). Each one has its own title, icon, colour, category and pin. Drag tabs to reorder them, or right-click a tab to manage it.
- **Notes**: copy, duplicate, edit, move between pads or delete. Deletes and clears can be undone, and the delete confirmation is optional.
- **Search** covers every pad. Drafts autosave.
- **Formatting**: bold, colours, headings, task lists, images and more. See [NOTE-FORMATTING.md](NOTE-FORMATTING.md).
- **Per-pad text size and line spacing.** Notes look the same on the overlay as in the app.
- **Floating pad window** (Ctrl+Alt+J): resize it from any edge, move it anywhere, and keep it over the game. Each pad remembers its own position, size, opacity, blur and always-on-top setting.
- **Pop out** any note onto the overlay as its own movable card.
- **Quick note** (Ctrl+Alt+N) adds a note to the current pad from inside the game. Tab switches between pads.
- **Import / export**: `.sfhudpad` for single pads. `.txt` and `.md` files can be imported as a pad.

## Overlay & customize mode

Press **Alt+~** in game to open customize mode.

- Drag elements to move them, pull a corner to resize, or right-click for options.
- Snap-to-grid is on by default (hold **Alt** to move freely). You also get alignment guides, a layout lock, undo/redo and arrow-key nudging.
- Changes preview live. **Apply** keeps them and **Revert** throws them away.

**Elements**: status strip, clock, timers, DEFCON badge, server intel, note pads, pinned notes, info cards, music player and text labels. Each element has its own name, colours, font, text size, opacity, background, border, corners, padding and size.

**Pages** hold sets of elements. You can create, rename, duplicate, delete and reorder pages, pick a default, and switch pages with **Ctrl+Alt+]** / **Ctrl+Alt+[** or a key of your own per page. Layouts can be saved and loaded, and positions reset.

## DEFCON

- Six levels by default. You can add or remove levels, numbered or named (for example `LOCKDOWN`), and rename, recolour, reorder and describe them.
- Each server preset has its own DEFCON table.
- Each level can have its own sound, toast, flash and overlay opacity. You can also show certain elements only at chosen levels.
- Change levels with **Ctrl+Alt+PgUp / PgDn** or with a key per level.

**Suggestions.** SFHud can *suggest* a level, with a confidence score, from:
- `console.log` announcements (start Garry's Mod with `-condebug`)
- server status text you paste in

Accept a suggestion with **Ctrl+Alt+Y** or dismiss it. If you want, very confident rule-based matches can apply on their own. SFHud never reads game memory.

## Server presets

Built-in presets, each with its own look and layout:

| Preset | Style |
|---|---|
| **Star Wars RP** | Republic blue. Mission briefing, training and personal pages |
| **Zombie Survival** | Dark red. Wave timers, objectives and threat cards |
| **Military RP** | Olive, tan and charcoal. Op orders, squad, radio frequencies, deployment and equipment |
| **General RP** | Neutral |

You can create, rename, duplicate, delete, export and import presets (`.sfhudpreset`). Each preset has its own icon and theme.

## Timers & sounds

- Run several timers at once: countdowns, stopwatches and repeating rounds, each with a colour and pause/resume.
- **Ctrl+T** starts a quick timer. **Ctrl+Alt+T** lets you type one:
  - `7m30s patrol` is a countdown
  - `@21:30 event` counts down to a time of day
  - `every 10m patrol` repeats
- Timer presets can have their own key and sound.
- SFHud comes with 10 soft beeps that it generates itself. **Import sound** adds your own `.wav` or `.mp3`.

## Music player

Play, pause and skip for the **Steam overlay browser**, any Chromium browser, Firefox, Spotify and other players, through Windows' media controls. **Auto** follows whatever is playing.

- **Ctrl+Alt+Space** plays or pauses. **Ctrl+Alt+M** shows or hides the player.
- The player follows you to every overlay page in game.

## Crosshair

Choose from 12 shapes and set the colour, size, thickness, gap, opacity, centre dot, and outline thickness and colour. You can save presets. The crosshair is drawn on SFHud's own window, not in the game.

## Look & themes

- **Themes**: SFHud Classic (default), Republic, Light, Midnight, Dark red, Blue, Purple, Zombie, Military and General.
- You can also set an accent colour, window opacity, chamfered, rounded or square corners, fonts, text scale, animations, scanlines and a compact sidebar.
- **Global search** (Ctrl+F) covers notes, settings, keybinds, pages and presets.

## Free edition limits

SFHud Free is the complete app, with two limits:

| | Free |
|---|---|
| Overlay pages | 5 per preset |
| Profiles | 1 |

Pro features (local AI, DEFCON auto-detection, video, custom themes, companion window and more) are shown faded in the app. They aren't part of this source code.
