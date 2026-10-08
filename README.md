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

### Automatic Desktop Launcher Integration (Wrapper Script)

This is the recommended setup. It only runs the script while the game is open (0% background CPU) and automatically starts/stops OpenTabletDriver for you.

**1. Find your current osu! launch command:**
Open terminal and find your osu! shortcut:
```bash
find ~/.local/share/applications /usr/share/applications -iname "*osu*.desktop"
```
Open the file it finds (e.g. `nano ~/.local/share/applications/osu-lazer.desktop`). Find the `Exec=` line and copy the command.

**2. Create the wrapper script:**
```bash
mkdir -p ~/.local/bin
nano ~/.local/bin/launch-osu.sh
```
Paste this inside. **Important:** Update the two marked lines!
```bash
#!/usr/bin/env bash

# Check if OpenTabletDriver was active
was_active=$(systemctl --user is-active opentabletdriver.service 2>/dev/null)

# 1. CHANGE THIS to where you cloned this repository:
/home/YOUR_USERNAME/osu-wacom-pen-toggle/osu-tip-toggle.py --ensure-otd >/dev/null 2>&1 &
TIP_PID=$!

# 2. CHANGE THIS to the original Exec= command you found in step 1 (without "Exec="):
/usr/bin/osu-lazer "$@"

# Cleanup after game closes
kill $TIP_PID 2>/dev/null
if [ "$was_active" != "active" ]; then
    systemctl --user stop opentabletdriver.service
fi
```
Make it executable:
```bash
chmod +x ~/.local/bin/launch-osu.sh
```

**3. Update your desktop shortcut:**
Go back to your `.desktop` file from Step 1.
Replace the **entire** `Exec=` line with your new wrapper script (keep `%U` if it had one):
```ini
Exec=/home/YOUR_USERNAME/.local/bin/launch-osu.sh %U
```
Now just launch the game from your application menu!
