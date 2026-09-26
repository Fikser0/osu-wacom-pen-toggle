# osu! Wacom Pen Tip Auto-Toggle

This tool fixes input inconsistencies of 1000hz custom firmware flashed Wacom Tablets


By automatically **disabling your pen tip and buttons while playing a map**, then **instantly turning them back on** the moment you pause, fail, or return to the menu so you can navigate normally.

> **Requirement:** You need a supported Wacom tablet running [shavit's custom firmware](https://files.shav.it/osu/tablet/).
- **osu! (stable)** | **Full Support** | Active gameplay, pause screens, intro / skip detection, song select, and menus. |
- **osu! (lazer)** | **Basic Support** | Automatically toggles between actively playing a beatmap and being in the menus. |

---

## Credits
- **[shavit](https://github.com/shavitush)** — For creating the custom Wacom open firmware that makes hardware toggling possible.
- **[Piotrekol](https://github.com/Piotrekol)** — For **[OsuMemoryDataProvider](https://github.com/Piotrekol/ProcessMemoryDataFinder/tree/master/OsuMemoryDataProvider)**.

---

## Why use this?
If you drag your pen or accidentally tap the tablet surface while aiming, you normally have to disable your pen tip completely in your tablet drivers. However, doing that makes navigating song select, settings, and your desktop frustrating because your pen can't click anything.

**This tool fixes that completely:**
- **In Gameplay:** Your pen tip is disabled. Drag, tap, and hover freely with zero risk of accidental clicks.
- **In Menus & Pauses:** Your pen tip works like a normal mouse again so you can select songs and click buttons effortlessly.

---

## Features

- **Instant Pause & Resume:** Pausing immediately re-enables your pen click. Resuming turns it off with zero noticeable delay.
- **Smart Intro / Skip Detection:** Keep your pen clickable during the intro until the very first hit circle appears.
- **Set It & Forget It:** Minimizes quietly to the system tray so your taskbar stays clean.
- **Zero Impact on Performance:** Optimized to run silently in the background without causing lag or frame drops in osu!.
- **Automatic Reconnect:** Plug or unplug your tablet anytime—the app automatically detects your device without needing a restart.

---

## How to Use

1. Make sure your Wacom tablet has [shavit's custom firmware](https://files.shav.it/osu/tablet/) installed.
2. Launch **osu! Tablet Auto-Toggle**.
3. Start playing osu!—the app handles everything automatically in the background.

---

