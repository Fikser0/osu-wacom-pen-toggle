# osu-wacom-pen-toggle (Linux Port)

A standalone Python daemon for Linux that automatically disables pen tip clicks during osu! gameplay and re-enables them in menus.

Requires a Wacom tablet with [shavit's custom firmware](https://files.shav.it/osu/tablet/).

## Features
- **Zero dependencies** for basic functionality (uses standard library `fcntl` to send HID Feature Reports to `/dev/hidraw*`).
- **osu! lazer support**: Instant detection via Hyprland window titles (`hyprctl`) or by tailing `.runtime.log`.
- **osu! stable support**: Connects to `tosu` or `gosumemory` via websocket to detect pauses based on frozen audio time (exactly like the Windows version).

## Prerequisites
1. **udev rules**: You must have read/write access to your tablet's `/dev/hidraw*` interface.
2. *(Optional)* **tosu**: For osu! stable support, the script requires the `websockets` Python package (`pip install websockets` or `pacman -S python-websockets`) and the [tosu](https://github.com/tosuapp/tosu) binary in the same folder.

## Usage
Run the script before playing:
```bash
./osu-tip-toggle.py
```
It runs in the foreground. Press `Ctrl+C` when done, and it will safely re-enable the pen tip before exiting.
