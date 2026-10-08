# osu-wacom-pen-toggle (Linux Port)

A standalone Python daemon for Linux that automatically disables pen tip clicks during osu! gameplay and re-enables them in menus.

Requires a Wacom tablet with [shavit's custom firmware](https://files.shav.it/osu/tablet/). Works across all major Linux distributions (Arch, Ubuntu, Fedora, Debian, etc.).

## Features
- **Zero dependencies** for basic functionality (uses standard library `fcntl` to send HID Feature Reports to `/dev/hidraw*`).
- **osu! lazer support (Native & Flatpak)**: Perfect pause/fail detection natively via window titles. Supports **Hyprland** (`hyprctl`), **X11** (`xprop`), and **Sway** (`swaymsg`). Gracefully falls back when a compositor or environment is unavailable.
- **osu! stable support (Wine)**: Connects to `tosu` or `gosumemory` via websocket to detect pauses based on frozen audio time (exactly like the Windows version).
- **Automated Setup**: Can automatically generate and install necessary `udev` permissions.

## Prerequisites
1. **udev rules**: You must have read/write access to your tablet's `/dev/hidraw*` interface. You can install this automatically by running:
   ```bash
   sudo ./osu-tip-toggle.py --install-udev
   ```
2. *(Optional)* **tosu**: For `osu! stable` support, the script requires the `websockets` Python package (`pip install websockets` or `pacman -S python-websockets`) and the [tosu](https://github.com/tosuapp/tosu) or [gosumemory](https://github.com/l3lackShark/gosumemory) bridge.

## Usage
Run the script before playing:
```bash
./osu-tip-toggle.py
```
It runs in the foreground. Press `Ctrl+C` when done, and it will safely re-enable the pen tip before exiting.

### Desktop Environment Support

- **Hyprland**: Supported natively via `hyprctl`.
- **Sway**: Supported natively via `swaymsg`.
- **X11**: Supported via `xprop` (install using your package manager, e.g., `sudo pacman -S xorg-xprop`).
- **KDE Plasma (Wayland)**: Supported via `kdotool`. Install via `sudo pacman -S kdotool`.
- **GNOME (Wayland)**: GNOME enforces strict security isolating window titles. To support GNOME Wayland, you **must** install a GNOME Shell extension that re-enables `org.gnome.Shell.Eval` (such as the `Eval-Gjs` extension) to allow `gdbus` queries. Alternatively, use X11.

### Automatic Desktop Launcher Integration

You can easily configure your desktop so that clicking your regular osu! icon automatically starts this daemon in the background and cleans it up when you exit the game. It also integrates flawlessly with OpenTabletDriver.

**1. Create a launch script**
Create a new file (e.g., `~/.local/bin/launch-osu.sh`) and add the following code:

```bash
#!/usr/bin/env bash

# Start the toggle script in the background
# (The --ensure-otd flag automatically stops and seamlessly restarts OpenTabletDriver to grab the tablet)
/path/to/osu-tip-toggle.py --ensure-otd >/dev/null 2>&1 &
TIP_PID=$!

# Run your actual osu! command
# Replace this line with how you normally launch osu! (e.g., /path/to/osu.AppImage)
osu-lazer "$@"

# Kill the toggle script when osu! closes
kill $TIP_PID 2>/dev/null
```
Make the script executable: `chmod +x ~/.local/bin/launch-osu.sh`

**2. Update your desktop shortcut**
Locate your osu! `.desktop` file (usually in `~/.local/share/applications/` or `/usr/share/applications/`). 
Open it in a text editor, find the `Exec=` line, and change it to point to your new script:
`Exec=/home/YOUR_USERNAME/.local/bin/launch-osu.sh`

Now, just launch the game from your application menu like normal!
