# osu! Wacom Pen Tip Auto-Toggle

Automatically toggles the **Pressure & Buttons** setting on Wacom tablets running shavit's 1000 Hz firmware based on osu! game state.

<img width="1890" height="540" alt="wacom-comparison" src="https://github.com/user-attachments/assets/b284fec9-d390-4a68-8d35-683f6f5a51f2" />

> *Input consistency test on CTH-480 at 1000 Hz. Disabling the tip eliminates dropped packets / gaps.*

## The Problem

On Wacom tablets running shavit's 1000 Hz custom firmware, reading pen pressure and button state can overwhelm the microcontroller, resulting in dropped reports (visible tracking gaps).

Disabling pressure and buttons in the firmware resolves these packet drops, but leaves the pen unable to click menus, select songs, or skip map intros.

This tool bridges that gap by reading osu! state in real time:
- **Gameplay:** Disables pressure and buttons to maintain consistent 1000 Hz tracking.
- **Menus, Pauses, and Intros:** Re-enables pressure and buttons so the pen functions normally.

## Features

- **Automatic Toggling:** Disables pressure and buttons during beatmaps and re-enables them on menus, pauses, breaks, and result screens.
- **Intro and Outro Detection (osu!stable):** Reads beatmap files to find the first and last hit objects. Keeps clicking enabled during long intros (for skipping) and re-enables it right after the final note.
- **Hotplug Support:** Listens to `WM_DEVICECHANGE` events to re-hook tablets when reconnected.
- **System Tray:** Minimizes to the tray with live status tooltips.

## Compatibility

### Requirements
- **Operating System:** Windows 10 / 11 (64-bit)
- **Runtime:** .NET Framework 4.8 *(pre-installed on Windows 10/11)*
- **Firmware:** [shavit's custom 1000 Hz Wacom firmware](https://files.shav.it/osu/tablet/) installed on a supported tablet.
> [!WARNING]
> *Flashing custom firmware carries risk of bricking your tablet. Follow the instructions on shavit's website carefully.*

### Tested Tablets
| Wacom |
| :--- |
| CTL-480 / CTL-680 |
| CTL-472 / CTL-672 |
| CTL-490 / CTL-690 |
| CTL-4100 / CTL-6100 |

### Client Support
| Client | Support Level | How Behaviors Are Handled |
| :--- | :--- | :--- |
| **osu! (stable)** | **Full** | Song select, active play, pause screen, fails, breaks/intros, beatmap outros. |
| **osu! (lazer)** | **Basic** | Active play vs. menus (simple window title inspection). |

## How It Works

1. **Detects Game State:** 
   - **osu! (stable):** Reads in-game memory using `OsuMemoryDataProvider` to know the exact millisecond gameplay starts, pauses, or ends.
   - **osu! (lazer):** Watches the osu! window title to detect when a beatmap is active.
2. **Sends HID Feature Reports:**
   - Sends HID feature reports to the tablet to change the firmware setting. (same as toggling it on the website)
   - Changes are written to volatile RAM only—firmware flash memory is untouched. Unplugging the tablet or closing the app restores default behavior.

## Usage

1. Flash your supported tablet with [shavit's custom firmware](https://files.shav.it/osu/tablet/).
2. Download the latest `osu-TipToggle.exe` from the [Releases](https://github.com/lukecupr/osu-wacom-pen-toggle/releases) page.
3. Launch the executable and start osu!. The tool will automatically hook the process and manage your tablet in the background.
> **Tip:** You can launch the app directly minimized to the system tray by passing the -tray argument.

## Credits

- **[shavit](https://github.com/shavitush)** — For creating the custom 1000 Hz Wacom firmwares.
- **[Piotrekol](https://github.com/Piotrekol)** — For **[OsuMemoryDataProvider](https://github.com/Piotrekol/ProcessMemoryDataFinder/tree/master/OsuMemoryDataProvider)**. (used for state detection)
