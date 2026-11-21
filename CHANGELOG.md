# Changelog

## 1.2.3

### Note pad: select and copy text
- You can now drag across a note in the note pad (dashboard Notes page and pad windows) to select any part of it, across lines, and copy it with **Ctrl+C** or right-click › **Copy selection**. **Ctrl+A** / **Select all text** selects the whole note. Formatting, links and task boxes work as before.

### Pop-ups open on the game's monitor
- Note pad windows, quick note, the timer prompt and the AI window now open on the monitor the game is on, instead of the primary monitor (or wherever the mouse was). Pad positions are remembered relative to that monitor.

### Music player: volume slider
- The music player has a **volume slider** (on the dashboard Now Playing card, and on the overlay player in customize mode). It changes only the music app's own volume in the Windows mixer, never the game or your master volume.
- New keys: **Ctrl+Alt+Num +** / **Ctrl+Alt+Num -** turn the music up / down in game (rebind them in Settings › Keybinds). A toast shows the new level.

### Licensing stays on SFHud's own server (Pro)
- Pro's license checks use only sfhud.site and license.sfhud.site. The old backup address on a third-party DNS name is gone.

## 1.2.2

### Local AI: UNLOAD now stays unloaded (Pro)
- Clicking **UNLOAD** freed the model, but it loaded straight back: the Local AI page redrew itself and preloaded it again, and opening the page or the AI window later did the same. Now an unload holds until you click **LOAD** or ask the AI something yourself.
- While it's unloaded, the background features (DEFCON auto-switch, chat event alerts, the server-intel chat summary) leave it off instead of loading it again.
- The 10-minute idle unload is unchanged.

## 1.2.1

### Music player fixes
- **The music player didn't show in game:** for many people the overlay player's on/off setting had been switched off (one stray Ctrl+Alt+M or HIDE ON OVERLAY), so the only player they ever saw was the old pop-up out of game. Updating to 1.2.1 switches the overlay player back on once, in every profile.
- If music is playing when you enter the game and the player is switched off, a toast tells you once per session which key shows it.
- **LibreWolf (and Firefox, Waterfox, Floorp, Zen) not detected:** these browsers report their music under an anonymous install id, which SFHud couldn't match to the browser. It now reads the browser's own install list, so they show by name and the Firefox / LibreWolf source works.

### Free stays offline
- SFHud Free has no license, account or hardware-id checks at all, and works fully offline. Only the optional update check and trial offers ever go online. License checks and HWID locking are Pro only.

### Music player: on the overlay, in game only (Free and Pro)
- The music player now lives only on the in-game overlay, in both editions. The separate pop-up player window (which could appear on the desktop and over other apps) has been removed, along with its key (Ctrl+Alt+Shift+M).
- The overlay player's track progress bar is now in Free too.

### New info bar look
- The overlay's top bar has been redesigned: every item sits on one line at the same height, names and numbers each use one font (numbers no longer jitter), and the separators are even.
- DEFCON is now a chip in the level's colour. ASSUMED / STALE shows as a quiet tag beside it instead of italic text.
- Clean cut corners; the corner brackets that stuck out on the thin bar are gone.

### Performance
- The music player used to rebuild itself (and the SFHud home page) every 2 seconds when nothing was playing, and on every timeline tick from Chromium browsers. It now updates only when something actually changes.
- Looking up which app owns a window took a snapshot of every process on the PC; it now asks that one process directly.
- Matching a media player to its app no longer queries hundreds of hidden windows, and an unknown player (such as the Steam overlay) is looked up once a minute instead of every 5 seconds.
- The info bar updates its text in place each second instead of rebuilding.

## 1.2.0

### Update bar (Free and Pro)
- When a new version is posted on sfhud.site, a bar appears at the top of the SFHud window: **UPDATE NOW**, **WHAT'S NEW**, or close it until the next start. SFHud checks at startup and every 30 minutes.
- UPDATE NOW downloads the installer for your edition, checks its SHA-256 against the one the site published, and runs setup. All your notes, layouts and settings are kept. Pro downloads with your license key.
- If you're in game when an update arrives, you get a short toast instead; the bar waits in the SFHud window.
- News posts still pop up once each, never over the game.

### DEFCON: the local AI switches automatically (Pro)
- New **AI switches DEFCON automatically** (on by default; DEFCON › Detection and the in-game menu). While the game is open, the local AI reads new console.log, screen-region and status-URL text and switches the level by itself, marked **AI-DETECTED**.
- It only switches when the words it cites are really in the text and it is confident enough (minimum adjustable). An exact "DEFCON n" in those words always beats the AI's guess. The rules still run first; the AI only looks at text the rules couldn't place, and only when there's something new.
- New key **Back to default DEFCON** (Ctrl+Alt+End): returns to your fallback level (DEFCON 6 by default) and blocks the level that was auto-detected for 10 minutes so it doesn't flip straight back. Also a button in the in-game menu.
- While AI auto-switch is on, confident exact rule matches (console.log, status URL) apply automatically too.
- Everything runs on your PC; nothing is sent anywhere.

### Event alerts from chat (Pro)
- Chat alerts are back. Besides your own rules, the local AI now reads server chat (from console.log) and tells you when an event, training, tryout, deployment or restart is coming: a toast on the overlay, plus a countdown timer when the message says when ("EVENT in 10 MINUTES").
- A quick filter means the model only looks at lines that could be announcements, one every 8 s at most; a line that arrives meanwhile waits instead of being dropped. On by default (Settings › Chat alerts; needs console.log under Server intel).
- "Keep clear of the game's HUD" is now **Keep clear of the game's chat**: elements stay off the chat box only (no more health / ammo / kill-feed zones).

### Fixes
- **Music player missing in game:** the overlay's music player belongs to one page, so on other pages (or after a DEFCON page switch) there was none and you only saw the pop-up player when you alt-tabbed. It now follows you to every page in game, in the same spot.
- **Music player stayed hidden:** hiding it from the in-game menu and showing it again from the SFHud window or Ctrl+Alt+M didn't bring it back. Every music toggle now does the same thing.
- **Pop-up player jumped back to the bottom corner:** it remembered its place on the main monitor only. It now keeps its exact spot on any monitor, including after Windows moves it.
- **Placeholder track:** the "Clone Wars Ambience · 501st Mix" placeholder belongs to the screenshot/test mode, which installed builds can no longer turn on; the player always shows what is really playing (Spotify, browsers, Apple Music, VLC and other players are named properly).
- **Out-of-memory crash:** game detection took a snapshot of every process every 2 seconds. It now checks the visible windows instead, which is much lighter.

## 1.1.0

### Renamed to SFHud
- The app is now **SFHud** everywhere: `SFHud.exe`, `SFHud-Setup-1.1.0.exe`, window titles, the Start Menu / desktop shortcuts and Apps & Features.
- Your data moves from `%LOCALAPPDATA%\SHud` to `%LOCALAPPDATA%\SFHud` on first start. If an old SHud is still open, it is asked to close first (it saves). If the folder can't be moved, it is copied and the old one is left alone.
- Setup removes the old SHud shortcuts, uninstall entry and exe. 1.0 `.shudpad` / `.shudpreset` / `.shudprofile` exports still import, and `SHUD_DATA_DIR` still works next to the new `SFHUD_DATA_DIR`.

### Overlay whitelist
- The overlay only appears while the selected game, or an app on the **overlay whitelist** (Games & servers), is in front. It never shows over the desktop, browsers, Discord or the SFHud window. With no game running it waits instead of previewing on the desktop; the customize menu still previews.
- The whitelist starts with every built-in game. Add any `.exe` by name, or SCAN a running app (it's whitelisted when added). "Any Game" is now **Any whitelisted app**.
- Pop-ups you open over the game (quick note, timer, AI, pads) keep the overlay up; the main SFHud window hides it.
- Follows the game straight away through out-of-process window-event hooks (move, resize, minimize). Like other anti-cheat-safe overlays, it's a separate click-through window; nothing is injected into the game.

### 4:3 / CRT and large displays
- New **Smallest text on low resolutions** (default 0.8×) so notes stay readable at 800×600, 1024×768 and 1280×960.
- Elements are kept within the screen, so 16:9 layouts fit 4:3 and 5:4 games.
- The in-game menu, top bar, status bar and toasts scale with the screen: bigger on 1440p / 4K, fitted on 4:3 (the bar wraps instead of running off). Toggle in Display.
- Overlay scale goes up to 3×. The main window shrinks to 640×460, for small CRT desktops.

### Window and tabs
- The main window is now cut to its frame's shape, so nothing shows past the cut corners. Windows 11's rounded corners and accent border no longer show around it. Rounded themes use Windows' own rounded corners.
- Maximizing no longer pushes the frame and title bar off the screen edges.
- Emoji on the sidebar, page headers, Overlay sub-tabs, in-game menu tabs and Settings sections. The compact sidebar shows the emoji (it used to show only numbers).

### Overlay and the game's HUD
- **Keep clear of the game's HUD:** zones are built in for Garry's Mod, TF2, CS2, Rust and s&box (health, armor, ammo, chat, kill feed, radar), and the bottom corners for other games. Customize mode shows them as amber boxes.
- **Elements always stay where you put them.** They no longer get pushed around live, which made them jump and land on top of each other when going in and out of the game. Instead, the first time you're in a game, any element sitting on its HUD is moved just clear (and clear of your other elements) once, and that spot is saved. Dropping an element on the game's HUD in customize mode does the same; hold Alt while dropping to place it anyway. **Reset positions** re-checks them.
- The customize bar lifts above elements at the bottom of the screen instead of covering them. The menu picks the side that covers fewer elements, and it can be collapsed to its tabs.
- The status strip was redesigned: labelled segments with thin dividers (`SESSION 00:12:31 │ 22:53 │ DEFCON 6 assumed`) instead of one dotted line. It now sits a little in from the corner (existing layouts are moved once).
- Pop-ups (pads, quick note, AI, timer prompt) belong to the game, like Discord's overlay. Their keys only open them over the hooked game or SFHud, they hide whenever you leave the game, and they come back with it.
- The music element has a pop-up player button. With Pro it also shows the track progress and time (the same seek data as the pop-up player); in Free the button opens the Pro page.

### Local AI speed
- Uses 8 CPU threads by default (auto: a quarter of your threads, 4 to 8) and flash attention. Reading prompts went from 250 to 418 tokens/s on a test PC; a briefing summary takes under 2 s. More threads only took frames from the game.
- The model starts loading as soon as you open the AI menu or page, so the first answer doesn't wait for it.

### SFHud Free and SFHud Pro
- SFHud now comes in two editions. **SFHud Free** is open source and complete. **SFHud Pro** costs **$30 once (lifetime) or $5 a month** and adds:
  - the local AI
  - DEFCON auto-detection (screen reading, status URL)
  - layout and note pad per DEFCON level
  - the pop-up mini player
  - videos on the overlay
  - unlimited pages and profiles
  - custom themes
  - the companion window
- New **SFHud Pro** page (⭐ in the sidebar): a Free vs Pro table, both plans, free trial offers, and in the Pro edition the license (type, expiry, connection).
- **Licensing:** Pro checks in with the SFHud license server while it runs (every few seconds, signed replies). Refunds, ended subscriptions and expired trials turn Pro off at the next check. If your internet drops, Pro keeps working offline for up to 3 days. A key is tied to one PC; support can move it.
- **Free trials:** when SFHud posts a trial offer, people who have never had Pro see it in the app (a banner, and FREE TRIAL in the title bar). START FREE TRIAL gives a key and the Pro add-on download; one trial per PC.
- **Activation after install:** the Pro add-on's setup asks for your key on its last page (or later on the SFHud Pro page).
- Chat alerts are switched off in every edition for now.
- Locked controls show an amber **★ PRO** tag that opens the page. Nothing you made is ever deleted. Pages past the free 5 per preset and profiles past the first are kept, just locked.
- In SFHud Free, Pro options are **faded out** (the Local AI sidebar entry, screen reading, custom themes, the pop-up player, the companion window, video, and new pages or profiles once you reach the free limit). Clicking one opens the SFHud Pro page.
- The title bar's DONATE button is now **GET PRO** (or **PRO** once unlocked, which opens your license).
- **Upgrading:** Pro is a separate download. Its installer replaces SFHud Free's program files in the same folder and keeps everything in `%LOCALAPPDATA%\SFHud` (notes, layouts, settings, sounds, backups), so your data carries straight over. Setup says which edition it is replacing, and Apps & Features shows "SFHud Pro".

### Notes: images and pop-outs
- **Images in notes.** Insert one with the picture button in the pad editor, paste a screenshot with Ctrl+V, or drop image files on the editor. They're copied into the data folder (`attachments`) and shown in pads, on the overlay and in pinned notes.
- **Resize images** by dragging the corner grip, or right-click for Small / Medium / Large / Fit width / Actual size. The size is saved in the note (`![caption](file.png =320)`).
- **Pop out a note onto the overlay.** The new button on every note (and its menu) puts that one note on the overlay as its own card. It moves, resizes and scales like any element, follows edits to the note, and pressing the button again takes it off.
- The pad window no longer floats over other apps: pad, quick-note, AI and timer pop-ups opened over the game step aside while you alt-tab away and come back, without stealing focus, when you return (Overlay › Display).
- Fixed: every pad window you opened stayed subscribed to note changes after closing. Switching presets with the pad window open now loads the new preset's pads instead of writing into the old one.

### Music and video
- **Pop-up player** (Ctrl+Alt+Shift+M, or Settings › Music): a small always-on-top player with title, seek bar, previous / play-pause / next and the source picker. It never takes focus from the game.
- **Play / pause while you're out of the game.** When the overlay is hidden and something is playing, the mini player appears on the desktop by itself. It goes away when you're back in game, and closing it keeps it away until your next game session.
- **Video element.** Play a local video file (MP4 and others) or a direct video link on the overlay, with loop, mute and volume. Videos start muted and pause while you're out of the game. The pop-up player and a new key (Video: play / pause) control them.
- Fixed: the **Steam browser** source never matched. The Steam overlay browser draws inside the game and has no window of its own, so Windows reports it under an id SFHud couldn't trace. It's now recognised while Steam is running. Unidentified players show their Windows id in Settings › Music so you can PIN one.
- Fixed: play / pause in a single-source mode (e.g. Steam browser) pressed the keyboard media key when that source had nothing open, which paused a different app. The key is now only used in Auto.

### DEFCON layouts
- New **Layout per level** table at the top of the DEFCON page and in the in-game DEFCON tab: pick the overlay page each level shows, or press **+ NEW** to copy a page for that level and **EDIT** to arrange it in game.
- When the level changes to one without its own layout, the overlay goes back to your default page instead of staying on the old level's page (can be turned off).

### Fixes
- Local AI on the SFHud window's AI page: answers never appeared. The page rebuilt every time the model changed state (loading, thinking) and threw the answer away. It now keeps the question and answer, and adds COPY / CLEAR.
- Local AI: a slow answer that hit the 90 s limit crashed the request instead of saying so. When the model server stops, SFHud now shows why. Unfinished `<think>` blocks no longer leak into answers. AI errors are logged.
- The amber message banner sat on top of the page header with a see-through background, so the page text showed through it. It now has its own row above the page, a solid background, and short messages close on their own.
- "Screen: game window not found" now explains what to do.

### DEFCON: fallback vs. detected
- On startup and when you switch to another server preset, SFHud shows **DEFCON 6** labelled **ASSUMED**, never as if it were verified. The badge is dimmed until the level is verified.
- The screen region (OCR of the configured part of the GMod window only) is the main detector. A level is applied as **AUTO-DETECTED** only after it has been read the same way several times in a row (3 reads at ≥ 80% confidence by default).
- If confirming reads stop, the level is kept as **STALE**, then goes back to the assumed DEFCON 6 after a timeout (30 s / 3 min by default).
- New settings under DEFCON › Detection › Fallback & verification: the fallback level, reads needed, minimum confidence, the stale and assumed timeouts, and auto-apply on/off.
- Console.log, the status URL and pasted text still only suggest. AI guesses always ask.

### Now Playing
- Fixed: Now Playing showed nothing while music played in a browser. The old default listened only to the Steam overlay browser, and SFHud only re-checked players when one appeared or closed.
- New **source picker** (SOURCE ▾ on Home, the badge on the overlay player, Settings › Music):
  - Auto (the new default) follows whatever is playing.
  - Single sources: Steam browser, any Chromium browser, Chrome, Brave, Edge, Opera, Vivaldi, Firefox / LibreWolf, Spotify.
  - You can also pin one player.
- When another player is playing, SFHud says so and offers a one-click **LISTEN TO BRAVE** (or whichever player it is).
- Players that report an anonymous id are now named correctly (looked up from their window).

### Saving
- Fixed: moving or resizing the SFHud window (or a note pad window) saved on every pixel of the drag. Position and size are now saved quietly a few seconds after you let go, and when the window closes.
- Changes in a burst (typing, dragging a slider) are written once, about a second after they stop, and at least every 8 s while they keep coming.
- The LOCAL · SAVED badge only shows SAVING… when a save is actually pending, instead of flickering on every keystroke.

### Note pads
- **Esc** (or **Ctrl+W**) always closes the note pad window. Before, Esc did nothing while the text box had text in it. What you were typing is kept as the pad's draft.
- The note pad keys (**Ctrl+Alt+J** and each pad's own key) now toggle: press again to close the window and go back to the game.
- A **Welcome to SFHud** guide is pinned at the very top of every preset's first pad, covering keys, DEFCON, music and tips. It's hidden on the in-game overlay so it doesn't cover the HUD. It is added once, so if you delete it, it stays deleted.

### Bug fixes (full UI test pass)
- Title bar: on narrower windows the search bar slid behind the SFHUD icon and title. It now fits the gap between the title and the buttons. The "// GAME OVERLAY & FIELD MANUAL" tagline hides first when space is short, and the search hint shortens.
- Companion window: the top bar wraps onto a second line instead of cutting off the clock and DEFCON.
- DEFCON › Detection: spacing added around the region line and the Fallback & verification heading.
- Settings › Music: the PIN buttons line up with their player rows.
- Keybinds: the filter box says what it's for. The ✕ / reset icons no longer render green on some rows and blue on others.
- Favourite servers: the Name and Address boxes show examples.
- The AI install size is the same everywhere (837 MB with its runtime).
- Note pads: the "hidden on overlay" note tag is now "not in game", so it fits.
- A scripted run through every page, sub-tab, settings section, timer, search, pad, companion and AI window logged no warnings or errors.

### UI polish
- Music: one-click **LISTEN TO** chips on Home (Auto · Steam · Chrome · Brave · Edge · Spotify; ● marks what's playing). The full list is still under SOURCE ▾.
- Overlay top bar sizes to its content, so the clock and DEFCON are no longer cut off. The session shows as "SESSION 00:12:03" instead of a stray icon.
- Overlay DEFCON badge: one short status line ("ASSUMED · not verified", "AUTO-DETECTED · verified 4s ago"), and the badge lines up with the level name.
- Overlay note pads no longer count notes hidden on the overlay (like the guide) as "+1 more".
- Overlay Now Playing: no more "via Brave · Playing" repeating the badge. It shows the artist, plus "paused" when it is.
- Home: SESSION and TOTAL PLAYED are aligned stat blocks. DEFCON status is a small pill with readable text. Text boxes show what to type. The timer examples wrap instead of being cut off.
- Buttons keep their natural width instead of stretching across cards (Scan running apps, New preset, Load now…). Settings section subtitles no longer run into the expand arrow.
- Games: each target is its own padded row, and the active game is highlighted with an ACTIVE tag. The description now matches how "Any whitelisted app" works.
- Local AI: the model path wraps instead of ending in "…".

### Top bar & clock
- New **Overlay › Display › Top bar & clock**. Choose what the overlay's top bar shows and in which order: game, server preset, profile, server name, session time, total playtime, clock, date, DEFCON. A live preview shows the result.
- The clock can be 24-hour or 12-hour and can show seconds. It can use another time zone (e.g. your server's) with an optional label such as SERVER. The Clock element uses the same settings.

### Display
- The main window scales up on large windows (1440p, ultrawide, 4K at 100%) instead of shrinking into a strip, and shows at most 4 columns of cards. 1080p and 4:3 keep the normal size. This can be turned off with Settings › Appearance › Auto-scale on large screens.
- The overlay's "Scale with game resolution" also fits narrow 4:3 / 5:4 screens, so a layout built on 16:9 doesn't crowd them.
- Fixed: Status card rows were misaligned and long values ran over on narrow windows.
- The Home page heading now reads **SFHud**.

## 1.0.0

The first full release. Everything from 0.3 is kept; most of it was rebuilt on a new data model.

### New
- **Redesigned main window**: slim sidebar (collapses to icons on small windows), global search (Ctrl+F) across notes, settings, keybinds, pages and presets, save indicator, Donate button, collapsible settings sections, and tooltips on every setting.
- **Note pads**: unlimited tabs (up to 60 per preset) with titles, icons, colours and categories. You can pin them, drag them to reorder, rename, duplicate or delete them, and export or import them. Search covers every pad, and drafts autosave. Deletes and clears can be undone (the last 50 are kept), and asking before deleting is optional.
  - Each pad has its own keys to open it and to add a note. There are also keys to clear the current tab, switch tabs, and quick-paste the clipboard.
  - The floating pad window can be resized from any edge or corner and moved anywhere. Each pad remembers its own position and size, along with min/max size, opacity, blur and always-on-top.
  - Formatting: bold, italic, underline, strikethrough, text colour, highlight, headings, bullets, numbered lists, checkboxes, links, timestamps, coloured labels, and a preview. Text size and line spacing are set per pad. Formatting draws the same way on the overlay.
  - Import a .txt / .md document as a pad.
- **Overlay pages and elements**: build your own HUD from status strips, clocks, timers, DEFCON badges, intel, note pads, info cards, a music player and text labels.
  - Each element has its own name, colours, font, text size, opacity, background, border, corners, padding and size.
  - Pages can be created, renamed, duplicated, deleted, reordered, given a default and switched with keys.
  - Layouts can be saved and loaded, and positions reset.
- **In-game customize menu (Alt+~)**:
  - Drag elements, resize them from the corners, and right-click for options.
  - Grid snapping, alignment guides, a layout lock, undo/redo, preview with REVERT/APPLY, and arrow-key nudging.
  - Tabs for elements, the element editor, pages, crosshair, DEFCON and display.
  - A MUSIC toggle, and shortcuts to open the note pads and the AI menu.
- **DEFCON**:
  - Six levels by default. You can add or remove levels (numbered or named), rename and reorder them, and give each a colour and description.
  - Per level: a page to switch to automatically, a pad that only shows at that level, a sound, a toast, a flash and an overlay opacity. Elements can be limited to certain levels.
  - Detection only ever suggests a level and shows how confident it is. Sources:
    - screen-region OCR (Windows' built-in, offline)
    - a status URL you provide
    - console.log announcements
    - pasted text
    - the local AI
  - You accept a suggestion (Ctrl+Alt+Y) or dismiss it. Very confident rule-based matches can be set to apply automatically if you opt in. AI guesses always ask first.
- **Keybinds**: almost every action can be rebound. Includes per-pad, per-page, per-DEFCON-level, per-element and per-timer-preset actions. Duplicate combos and combos owned by other apps are detected, and so are risky ones. You can mark a key "in-game only", clear it, reset it, or reset all. Keybinds are saved per profile.
- **Themes**: Dark, Light, Midnight, Dark red, Blue, Purple, plus each preset's own style. A custom theme editor, accent override, background image, window opacity, chamfered/rounded/square corners, font choice and scaling, animation and scanline toggles, and compact mode.
- **Server presets with their own look**: Star Wars RP (Republic blue), Zombie Survival (dark red), Military RP (olive/tan/charcoal) and General RP (neutral). Create, rename, duplicate, delete, export and import presets; give them icons and themes.
- **Profiles**: personal configurations (Main RP, Training, Event…), each with its own settings, keybinds, presets, layouts and notes.
- **Timers**: several at once, countdown or stopwatch, repeat rounds, colours, pause/resume, presets with their own keys and sounds.
- **Sounds**: 10 built-in soft timer and notification beeps synthesized by SFHud, import your own .wav/.mp3 (for example from Pixabay), volume and mute.
- **Music player**: play/pause/next/previous for the Steam overlay browser through Windows' media controls. Experimental Chrome/Brave mode, or any player. Show or hide it from the Alt+~ menu.
- **Local AI menu in game (Ctrl+Alt+A)**: summarize briefings, suggest quick notes, label notes, organize a pad, check DEFCON and ask questions. Response length, creativity, threads and idle unload are configurable.
- **Crosshair**: center dot, outline thickness and colour, and saved presets.
- **Info cards** with icons, titles, formatted text and colours.
- **Saving**: autosave, Ctrl+S, rolling backups (at start-up, every 30 min and before destructive actions), automatic recovery from a damaged file, import/export of everything, and migration of 0.x settings and notes.
- **Installer**: per-user setup (`SFHud-Setup-1.0.0.exe`) with a choice of folder, desktop and Start Menu shortcuts, and an Apps & Features uninstall entry. Updates keep your data, and uninstalling keeps your data unless you tick the box.
- **Stability**: errors are logged to `%LOCALAPPDATA%\SFHud\logs`, data is saved before any crash dialog, a second launch brings the running copy forward, and the AI runtime is closed with SFHud.

### Changed
- DEFCON from chat is now a *suggestion*, not an automatic change.
- Background chat summaries by the AI are off by default to keep CPU free while playing.
- Version 1.0.0; the data file is `userdata.json` (0.x `settings.json` / `notes.json` are imported once and left in place).

## 0.3.0
- Layout mode, quick notes and tasks on the overlay, Ctrl+T / Ctrl+Alt+T timers, console.log intel, local Qwen AI, 12 crosshair styles, single-file exe, build script.
