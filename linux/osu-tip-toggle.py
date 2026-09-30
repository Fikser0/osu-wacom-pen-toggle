#!/usr/bin/env python3
import os
import fcntl
import struct
import glob
import time
import asyncio
import signal
import sys
import json

try:
    import websockets
    HAS_WEBSOCKETS = True
except ImportError:
    HAS_WEBSOCKETS = False


def _ioc(dir, type, nr, size):
    return (dir << 30) | (ord(type) << 8) | nr | (size << 16)

# ioctl constants
HIDIOCGRAWINFO = _ioc(2, 'H', 0x03, 8)

class WacomModel:
    def __init__(self, name, pids, report_id, report_len, write_offset):
        self.name = name
        self.pids = pids
        self.report_id = report_id
        self.report_len = report_len
        self.write_offset = write_offset

MODELS = [
    WacomModel("CTL/CTH-x80", [0x0302, 0x030E, 0x0303, 0x0323], 0x24, 32, 1),
    WacomModel("CTL-x72", [0x037A, 0x037B], 0x24, 32, 1),
    WacomModel("CTL-4100/6100", [0x0374, 0x0375, 0x0376, 0x0377, 0x03C5], 0x60, 64, 3),
    WacomModel("PTK-x70", [0x03F5, 0x03F7, 0x03F9], 0x60, 64, 1),
]

def HIDIOCGFEATURE(size):
    return _ioc(3, 'H', 0x07, size)

def HIDIOCSFEATURE(size):
    return _ioc(3, 'H', 0x06, size)

class TabletController:
    def __init__(self):
        self.fd = None
        self.model = None
        self.dev_path = None
        self.cached_config1 = 0
        self.cached_config2 = 0
        self.cached_motion_sync = 0
        self.motion_sync_supported = False
        
    def open_device(self):
        self.close()
        for path in sorted(glob.glob('/dev/hidraw*')):
            try:
                fd = os.open(path, os.O_RDWR | os.O_NONBLOCK)
                info = bytearray(8)
                fcntl.ioctl(fd, HIDIOCGRAWINFO, info)
                _, vid, pid = struct.unpack('<Ihh', info)
                vid &= 0xFFFF
                pid &= 0xFFFF
                
                if vid == 0x056A:
                    for model in MODELS:
                        if pid in model.pids:
                            # Try to read feature report
                            buf = bytearray(model.report_len)
                            buf[0] = model.report_id
                            try:
                                fcntl.ioctl(fd, HIDIOCGFEATURE(model.report_len), buf)
                                if buf[1] == ord('T') and buf[2] == ord('V') and buf[3] == 1:
                                    self.fd = fd
                                    self.model = model
                                    self.dev_path = path
                                    self.cached_config1 = buf[4]
                                    self.cached_config2 = buf[5]
                                    self.motion_sync_supported = (buf[7] & 4) != 0
                                    self.cached_motion_sync = buf[8]
                                    print(f"[Wacom] Found {model.name} (PID: {pid:#06x}) at {path}")
                                    return True
                            except OSError:
                                pass
                os.close(fd)
            except OSError:
                continue
        return False
        
    def close(self):
        if self.fd is not None:
            try:
                os.close(self.fd)
            except OSError:
                pass
            self.fd = None

    def set_tip_enabled(self, enable: bool):
        if self.fd is None:
            if not self.open_device():
                print("[Wacom] Tablet not found or access denied.")
                return False
                
        buf = bytearray(self.model.report_len)
        buf[0] = self.model.report_id
        offset = self.model.write_offset
        
        buf[offset] = ord('T')
        buf[offset+1] = ord('V')
        buf[offset+2] = 1
        buf[offset+3] = self.cached_config1
        buf[offset+4] = self.cached_config2
        buf[offset+5] = 1 if enable else 0
        buf[offset+6] = self.cached_motion_sync if self.motion_sync_supported else 0
        buf[offset+7] = 0
        
        try:
            fcntl.ioctl(self.fd, HIDIOCSFEATURE(self.model.report_len), buf)
            print(f"[Wacom] Tip {'ENABLED' if enable else 'DISABLED'}")
            return True
        except OSError as e:
            print(f"[Wacom] Failed to send report: {e}")
            self.close()
            return False

class LogTailer:
    def __init__(self, tablet_ctrl):
        self.tablet = tablet_ctrl
        self.log_dir = os.path.expanduser('~/.local/share/osu/logs')
        self.current_log_path = None
        self.is_playing = False
        self.last_toggle_time = 0
        
    def find_latest_log(self):
        try:
            logs = glob.glob(os.path.join(self.log_dir, '*.runtime.log'))
            if not logs:
                return None
            return max(logs, key=os.path.getmtime)
        except Exception:
            return None

    def handle_line(self, line):
        if "entered SoloPlayer#" in line or "entered MultiplayerPlayer#" in line:
            self._set_state(False) # tip disabled
        elif "exit from SoloPlayer#" in line or "exit from MultiplayerPlayer#" in line or "resume to SoloSongSelect#" in line or "resume to MainMenu#" in line:
            self._set_state(True) # tip enabled

    def _set_state(self, tip_enabled):
        if tip_enabled != (not self.is_playing):
            self.is_playing = not tip_enabled
            self.tablet.set_tip_enabled(tip_enabled)

    async def tail(self):
        f = None
        inode = None
        
        while True:
            latest = self.find_latest_log()
            
            # If no log file found or log file changed (new run)
            if latest and latest != self.current_log_path:
                print(f"[LogTailer] Tailing {latest}")
                self.current_log_path = latest
                if f:
                    f.close()
                f = open(latest, 'r', encoding='utf-8', errors='ignore')
                inode = os.fstat(f.fileno()).st_ino
                f.seek(0, 2) # Go to end
                
                # Assume not playing initially
                self._set_state(True)
                
            if f is None:
                await asyncio.sleep(1)
                continue
                
            # Check if file was rotated/replaced
            try:
                current_st = os.stat(self.current_log_path)
                if current_st.st_ino != inode:
                    self.current_log_path = None # Force reopen
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

    async def watch(self):
        while True:
            await asyncio.sleep(0.1)
            try:
                # Fast check for Hyprland
                proc = await asyncio.create_subprocess_exec(
                    "hyprctl", "activewindow", "-j",
                    stdout=asyncio.subprocess.PIPE,
                    stderr=asyncio.subprocess.DEVNULL
                )
                stdout, _ = await proc.communicate()
                
                if stdout:
                    data = json.loads(stdout)
                    win_class = data.get("class", "").lower()
                    win_title = data.get("title", "")
                    
                    if "osu" in win_class or "osu" in win_title.lower():
                        # Lazer changes title to "osu! - Artist - Title" when playing.
                        # When paused/failed/menu, it reverts to "osu!" or similar without hyphens.
                        is_playing = " - " in win_title or " \u2013 " in win_title
                        
                        # Override LogTailer state
                        self.log_tailer._set_state(not is_playing)
            except Exception:
                pass


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
                # If we are supposed to be playing but audio time hasn't updated for 200ms -> paused or failed
                if self.is_playing and time.time() - self.last_update_time > 0.2:
                    self._set_state(True) # Enable tip

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
                                    self._set_state(False) # Disable tip
                            else:
                                self._set_state(True) # Enable tip
                        except json.JSONDecodeError:
                            pass
            except Exception:
                # Connection failed or disconnected
                await asyncio.sleep(2)

async def main():
    tablet = TabletController()
    if not tablet.open_device():
        print("Warning: Could not find supported Wacom tablet with shavit's firmware.")
        print("Make sure you have permissions (e.g. udev rules) to read/write /dev/hidraw*")
    
    # Ensure tip is enabled at startup
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
    
    import shutil
    if shutil.which("hyprctl"):
        title_watcher = LazerTitleWatcher(tablet, tailer)
        tasks.append(asyncio.create_task(title_watcher.watch()))
        print("[LazerTitleWatcher] Enabled Hyprland window title detection for Lazer pauses.")
    
    if HAS_WEBSOCKETS:
        # Check if tosu exists in the same directory and isn't already running
        tosu_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "tosu")
        if os.path.exists(tosu_path) and os.access(tosu_path, os.X_OK):
            import subprocess
            try:
                print(f"[Daemon] Auto-starting bundled tosu: {tosu_path}")
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
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
