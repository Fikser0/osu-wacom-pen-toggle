#!/usr/bin/env python3
import os
import sys
import glob
import time
import fcntl
import asyncio
import argparse
import signal
import json
import shutil
import subprocess

try:
    import websockets
    HAS_WEBSOCKETS = True
except ImportError:
    HAS_WEBSOCKETS = False

HIDIOCSFEATURE = 0xC0204806
HIDIOCGFEATURE = 0xC0204807

class WacomModel:
    def __init__(self, pid, name):
        self.pid = pid
        self.name = name

MODELS = [
    WacomModel(0x033b, "CTL-490"),
    WacomModel(0x033c, "CTH-490"),
    WacomModel(0x030e, "CTL/CTH-x80"),
    WacomModel(0x033e, "CTL-471/671"),
    WacomModel(0x0302, "CTL-470"),
    WacomModel(0x00cc, "CTH-470"),
    WacomModel(0x0374, "CTL-472"),
    WacomModel(0x0375, "CTL-672"),
    WacomModel(0x037a, "CTL-4100"),
    WacomModel(0x037b, "CTL-6100"),
    WacomModel(0x037c, "CTL-4100WL"),
    WacomModel(0x037d, "CTL-6100WL"),
    WacomModel(0x03c8, "Wacom One 12"),
    WacomModel(0x03c9, "Wacom One 13"),
]

class TabletController:
    def __init__(self):
        self.fds = []
        self.model = None

    def open_device(self):
        found = False
        import os
        import fcntl
        for i in range(20):
            path = f"/dev/hidraw{i}"
            if not os.path.exists(path):
                continue
            
            try:
                fd = os.open(path, os.O_RDWR)
                buf = bytearray(32)
                buf[0] = 0x24 # Report ID
                try:
                    fcntl.ioctl(fd, HIDIOCGFEATURE, buf)
                    if buf[1:4] == b'TV':
                        self.fds.append(fd)
                        print(f"[Wacom] Found shavit firmware interface at {path}")
                        found = True
                        continue
                except OSError:
                    pass
                os.close(fd)
            except OSError:
                pass
        return found

    def set_tip_enabled(self, enable):
        if not self.fds:
            return
        
        success = False
        import fcntl
        for fd in self.fds:
            try:
                buf = bytearray(32)
                buf[0] = 0x24
                fcntl.ioctl(fd, HIDIOCGFEATURE, buf)
                
                if buf[1:4] != b'TV':
                    continue
                    
                # Create a fresh buffer to write to avoid sending back read-only data
                write_buf = bytearray(32)
                write_buf[0] = 0x24
                write_buf[1] = ord('T')
                write_buf[2] = ord('V')
                write_buf[3] = 1
                # Copy configs
                write_buf[4] = buf[4]
                write_buf[5] = buf[5]
                # Tip/Pressure flag (0 = disabled, 1 = enabled)
                write_buf[6] = 1 if enable else 0
                
                # Motion sync logic identical to C# SetPressureAndButtons
                motion_sync_supported = (buf[7] & 4) != 0
                write_buf[7] = buf[8] if motion_sync_supported else 0
                write_buf[8] = 0
                
                fcntl.ioctl(fd, HIDIOCSFEATURE, write_buf)
                success = True
            except OSError:
                pass
                
        if success:
            state_str = "ENABLED" if enable else "DISABLED"
            print(f"[Wacom] Tip {state_str}")
        else:
            print(f"[Wacom] Error setting tip on all interfaces")

class LogTailer:
    def __init__(self, tablet_ctrl):
        self.tablet = tablet_ctrl
        self.current_log_path = None
        self.is_playing = False
        self.log_dirs = [
            os.path.expanduser("~/.local/share/osu/logs"),
            os.path.expanduser("~/.var/app/sh.ppy.osu/data/osu/logs")
        ]
        
    def find_latest_log(self):
        logs = []
        for d in self.log_dirs:
            logs.extend(glob.glob(os.path.join(d, '*.runtime.log')))
        
        if not logs:
            return None
        return max(logs, key=os.path.getmtime)

    def handle_line(self, line):
        if "entered SoloPlayer#" in line or "entered MultiplayerPlayer#" in line:
            self._set_state(False)
        elif "exit from SoloPlayer#" in line or "exit from MultiplayerPlayer#" in line or "resume to SoloSongSelect#" in line or "resume to MainMenu#" in line:
            self._set_state(True)

    def _set_state(self, tip_enabled):
        if tip_enabled != (not self.is_playing):
            self.is_playing = not tip_enabled
            self.tablet.set_tip_enabled(tip_enabled)

    async def tail(self):
        f = None
        inode = None
        
        while True:
            latest = self.find_latest_log()
            
            if latest and latest != self.current_log_path:
                print(f"[LogTailer] Tailing {latest}")
                self.current_log_path = latest
                if f:
                    f.close()
                f = open(latest, 'r', encoding='utf-8', errors='ignore')
                inode = os.fstat(f.fileno()).st_ino
                f.seek(0, 2)
                self._set_state(True)
                
            if f is None:
                await asyncio.sleep(1)
                continue
                
            try:
                current_st = os.stat(self.current_log_path)
                if current_st.st_ino != inode:
                    self.current_log_path = None
                    continue
            except OSError:
                self.current_log_path = None
                continue
                
            line = f.readline()
            if not line:
                await asyncio.sleep(0.05)
                continue
                
            self.handle_line(line)

class LazerTitleWatcher:
    def __init__(self, tablet_ctrl, log_tailer):
        self.tablet = tablet_ctrl
        self.log_tailer = log_tailer
        self.method = self.detect_method()

    def detect_method(self):
        if shutil.which("hyprctl"):
            return "hyprland"
        if shutil.which("swaymsg"):
            return "sway"
        if shutil.which("xprop"):
            return "x11"
        return None

    async def get_window_info(self):
        try:
            if self.method == "hyprland":
                proc = await asyncio.create_subprocess_exec(
                    "hyprctl", "activewindow", "-j",
                    stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.DEVNULL
                )
                stdout, _ = await proc.communicate()
                if stdout:
                    data = json.loads(stdout)
                    return data.get("class", ""), data.get("title", "")
            elif self.method == "sway":
                proc = await asyncio.create_subprocess_exec(
                    "swaymsg", "-t", "get_tree",
                    stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.DEVNULL
                )
                stdout, _ = await proc.communicate()
                if stdout:
                    # Very naive parsing for sway
                    return "osu", stdout.decode('utf-8')
            elif self.method == "x11":
                proc1 = await asyncio.create_subprocess_shell(
                    "xprop -root _NET_ACTIVE_WINDOW",
                    stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.DEVNULL
                )
                stdout1, _ = await proc1.communicate()
                if stdout1 and b"window id #" in stdout1:
                    win_id = stdout1.split(b"#")[1].strip().split()[0].decode('utf-8')
                    if win_id != "0x0":
                        proc2 = await asyncio.create_subprocess_shell(
                            f"xprop -id {win_id} _NET_WM_NAME WM_CLASS",
                            stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.DEVNULL
                        )
                        stdout2, _ = await proc2.communicate()
                        out_str = stdout2.decode('utf-8')
                        return out_str, out_str
        except Exception:
            pass
        return "", ""

    async def watch(self):
        if not self.method:
            return
            
        print(f"[LazerTitleWatcher] Using {self.method} for window title detection (pauses).")
        while True:
            await asyncio.sleep(0.1)
            win_class, win_title = await self.get_window_info()
            
            if "osu" in win_class.lower() or "osu" in win_title.lower():
                is_playing = " - " in win_title or " – " in win_title
                self.log_tailer._set_state(not is_playing)

class TosuWatcher:
    def __init__(self, tablet_ctrl):
        self.tablet = tablet_ctrl
        self.is_playing = False
        self.last_time = None
        self.last_update_time = time.time()
        
    def _set_state(self, tip_enabled):
        if tip_enabled != (not self.is_playing):
            self.is_playing = not tip_enabled
            self.tablet.set_tip_enabled(tip_enabled)

    async def watch(self):
        uri = "ws://127.0.0.1:24050/ws"
        
        async def check_frozen():
            while True:
                await asyncio.sleep(0.1)
                if self.is_playing and time.time() - self.last_update_time > 0.2:
                    self._set_state(True)

        asyncio.create_task(check_frozen())
        
        while True:
            try:
                async with websockets.connect(uri) as ws:
                    print(f"[TosuWatcher] Connected to {uri} (osu! stable / tosu detection)")
                    async for msg in ws:
                        try:
                            data = json.loads(msg)
                            menu_state = data.get("menu", {}).get("state", 0)
                            current_time = data.get("menu", {}).get("bm", {}).get("time", {}).get("current", 0)
                            
                            is_playing_state = (menu_state == 2)
                            
                            if is_playing_state:
                                if current_time != self.last_time:
                                    self.last_time = current_time
                                    self.last_update_time = time.time()
                                    self._set_state(False)
                            else:
                                self._set_state(True)
                        except json.JSONDecodeError:
                            pass
            except Exception:
                await asyncio.sleep(2)

def install_udev():
    rules_content = '''# Grant read/write access to Wacom tablets for the "users" group
SUBSYSTEM=="hidraw", ATTRS{idVendor}=="056a", MODE="0666"
'''
    dest = "/etc/udev/rules.d/99-wacom-osu.rules"
    print(f"Installing udev rules to {dest}...")
    try:
        with open(dest, 'w') as f:
            f.write(rules_content)
        subprocess.run(["udevadm", "control", "--reload-rules"], check=True)
        subprocess.run(["udevadm", "trigger"], check=True)
        print("Success! Please unplug and re-plug your tablet.")
    except PermissionError:
        print("Error: You must run this command with sudo!")
        sys.exit(1)
    except Exception as e:
        print(f"Error: {e}")
        sys.exit(1)
    sys.exit(0)

async def main(args):
    if args.install_udev:
        install_udev()

    import subprocess
    import time
    
    otd_was_active = False
    try:
        res = subprocess.run(["systemctl", "--user", "is-active", "opentabletdriver.service"], capture_output=True, text=True)
        if res.stdout.strip() == "active":
            print("[Daemon] Temporarily stopping OpenTabletDriver to claim device...")
            subprocess.run(["systemctl", "--user", "stop", "opentabletdriver.service"])
            time.sleep(1.5) # Give udev time to recreate /dev/hidraw nodes
            otd_was_active = True
    except Exception:
        pass

    tablet = TabletController()
    if not tablet.open_device():
        print("Warning: Could not find supported Wacom tablet with shavit's firmware.")
        print("Make sure you have permissions (e.g. udev rules) to read/write /dev/hidraw*")
        print("Try running: sudo ./osu-tip-toggle.py --install-udev")
    
    if otd_was_active:
        print("[Daemon] Restarting OpenTabletDriver...")
        try:
            subprocess.run(["systemctl", "--user", "start", "opentabletdriver.service"])
        except Exception:
            pass

    
    tablet.set_tip_enabled(True)
    
    tailer = LogTailer(tablet)
    
    global tosu_process
    tosu_process = None
    
    def cleanup(signum, frame):
        print("\n[Shutdown] Re-enabling pen tip...")
        tablet.set_tip_enabled(True)
        global tosu_process
        if tosu_process:
            tosu_process.terminate()
        sys.exit(0)
        
    signal.signal(signal.SIGINT, cleanup)
    signal.signal(signal.SIGTERM, cleanup)
    
    print("[Daemon] Running. Press Ctrl+C to stop.")
    
    tasks = [asyncio.create_task(tailer.tail())]
    
    title_watcher = LazerTitleWatcher(tablet, tailer)
    if title_watcher.method:
        tasks.append(asyncio.create_task(title_watcher.watch()))
    
    if HAS_WEBSOCKETS:
        tosu_path = args.tosu_path or os.path.join(os.path.dirname(os.path.abspath(__file__)), "tosu")
        if os.path.exists(tosu_path) and os.access(tosu_path, os.X_OK):
            try:
                print(f"[Daemon] Auto-starting tosu: {tosu_path}")
                tosu_process = subprocess.Popen([tosu_path], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            except Exception as e:
                print(f"[Daemon] Failed to start tosu: {e}")
                
        tosu_watcher = TosuWatcher(tablet)
        tasks.append(asyncio.create_task(tosu_watcher.watch()))
    else:
        print("[TosuWatcher] python-websockets not installed. osu! stable detection disabled.")
        
    try:
        await asyncio.gather(*tasks)
    finally:
        if tosu_process:
            tosu_process.terminate()

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="osu! Wacom Pen Tip Toggle (Linux)")
    parser.add_argument("--install-udev", action="store_true", help="Install udev rules and exit")
    parser.add_argument("--tosu-path", type=str, help="Path to custom tosu binary for osu! stable")
    args = parser.parse_args()

    try:
        asyncio.run(main(args))
    except KeyboardInterrupt:
        pass
