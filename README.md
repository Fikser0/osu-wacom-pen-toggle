# osu! Wacom Pen Tip Auto-Toggle

An ultra-low latency background utility for osu!(stable) and osu!(lazer) that automatically disables the Wacom pen tip during active gameplay and re-enables it in menus, pause screens, and song intros.

> **Requirements:** This tool requires a supported Wacom tablet running [shavit's custom firmware](https://files.shav.it/osu/tablet/).

## Features
- **Ultra-low latency pause detection (~30ms):** Instantly turns the pen tip ON when pausing and OFF when resuming.
- **Skip / Intro Detection:** Keeps pen tip active during intro until the first hit object appears.
- **System Tray:** Minimizes to system tray quietly without cluttering the taskbar.
- **Near-zero CPU usage (<0.1%):** Background thread reads memory at 15ms while UI updates are throttled.
- **Single Instance:** Running the program again restores the existing instance from the tray.
