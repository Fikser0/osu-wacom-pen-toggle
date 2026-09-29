# osu! Wacom Pen Tip Auto-Toggle

This tool fixes input inconsistencies of 1000hz custom firmware flashed Wacom Tablets

By automatically **disabling your pen tip and buttons while playing a map**, then **instantly turning them back on** the moment you pause, fail, or return to the menu so you can navigate normally.

<img width="1890" height="540" alt="wacom-comparison" src="https://github.com/user-attachments/assets/b284fec9-d390-4a68-8d35-683f6f5a51f2" />


> **Requirement:** You need a supported Wacom tablet running [shavit's custom firmware](https://files.shav.it/osu/tablet/).
- **osu! (stable)** - **Full Support** | Active gameplay, pause screens, intro / skip detection, song select, and menus.
- **osu! (lazer)** - **Basic Support** | Only detects whether you are playing a beatmap or are in the menu / song select.

**Tested on:** CTH-480, CTL-472, CTL-4100<br>
**Platform:** Windows 10/11, requires NET 4.8

---

## Credits
- **[shavit](https://github.com/shavitush)** — For creating custom 1000 hz Wacom firmwares with togglable Volatile memory settings.
- **[Piotrekol](https://github.com/Piotrekol)** — For **[OsuMemoryDataProvider](https://github.com/Piotrekol/ProcessMemoryDataFinder/tree/master/OsuMemoryDataProvider)** allowing this app to detect osu! gameplay states.

---

## Why use this?
Disabling the pen tip and buttons eliminates hardware-level input inconsistencies which is a thing yet to be fixed in custom firmwares.

**This tool fixes that completely:**
- **In Gameplay:** Pen tip and buttons are disabled for more consistent inputs.
- **In Menus & Pauses:** Re-enabled instantly so you can navigate osu! normally with your pen.

---

## Features

- **Pause/resume toggle:** Turns buttons back on the instant you pause, and shuts them off immediately when you resume playing.
- **Intro & skip support:** Leaves clicks enabled during song intros so you can skip or retry until the first hit object actually appears.<br>
  *(osu!stable only)*
- **Runs in tray:** Minimizes to the system tray.
- **Lightweight:** Negligible CPU usage (<1%).
- **Hotplug support:** Automatically picks up the tablet if you reconnect it without needing to restart the app.

---

## How to Use

1. Make sure your Wacom tablet has [shavit's custom firmware](https://files.shav.it/osu/tablet/) installed.
2. Launch **osu! Wacom Pen Tip Auto-Toggle**.
3. Start playing osu!—the app handles everything automatically in the background.

---

