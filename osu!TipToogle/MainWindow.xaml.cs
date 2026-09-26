#pragma warning disable CS4014

using OsuMemoryDataProvider;
using OsuMemoryDataProvider.OsuMemoryModels;
using OsuMemoryDataProvider.OsuMemoryModels.Direct;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.IO.Path;

namespace osu_TipToogle
{
    public partial class MainWindow : Window
    {
        private readonly StructuredOsuMemoryReader _reader;
        private OsuBaseAddresses _baseAddresses;
        private CancellationTokenSource? _cts;
        private EventWaitHandle? _restoreWaitHandle;

        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        private int _lastAudioTime = -1;
        private int _frozenTicks = 0;
        private bool? _lastTargetState = null;
        private string _lastHardwareResult = "";

        // Gameplay enter grace period (osu!stable)
        private OsuMemoryStatus _lastOsuStatus = OsuMemoryStatus.Unknown;
        private long _gameplayEnterMs = 0;

        // Skip detection cache (osu!stable)
        private int _lastMapId = -1;
        private int _firstHitObjectTime = 0;

        // Process tracking & Re-hook system (osu!stable)
        private int _lastStablePid = -1;
        private long _lastStableProcessCheckMs = 0;
        private long _lastRehookMs = 0;
        private int _consecutiveFailedReads = 0;
        private long _unknownStatusStartMs = 0;

        // Cached window handle & rate limit for osu!(lazer)
        private IntPtr _cachedLazerHwnd = IntPtr.Zero;
        private long _lastLazerScanMs = 0;

        // Hardware Debounce (prevents on/off/on flashes during song selection/loading)
        private const int HardwareDebounceMs = 100;
        private bool? _pendingHardwareState = null;
        private long _pendingHardwareStateStartTime = 0;

        // UI Debounce
        private enum DisplayCategory
        {
            None = 0,
            Waiting,
            Connecting,
            Menu,
            Paused,
            SkipIntro,
            Playing
        }

        private const int UiDebounceMs = 450;
        private DisplayCategory _pendingCategory = DisplayCategory.None;
        private bool _pendingIsLazer = false;
        private long _pendingCategoryStartTime = 0;
        private DisplayCategory _currentAppliedCategory = DisplayCategory.None;
        private bool _appliedIsLazer = false;
        private string _appliedMenuStatusDetail = "";

        // UI update throttling (eliminates WPF layout CPU overhead)
        private long _lastUiAudioUpdateMs = 0;
        private string _lastUiAudioText = "";
        private string _lastUiTabletInfo = "";

        #region Win32 Window & Tray APIs

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        private static extern uint TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private const uint NIM_ADD = 0x00000000;
        private const uint NIM_MODIFY = 0x00000001;
        private const uint NIM_DELETE = 0x00000002;
        private const uint NIF_MESSAGE = 0x00000001;
        private const uint NIF_ICON = 0x00000002;
        private const uint NIF_TIP = 0x00000004;

        private const int WM_DEVICECHANGE = 0x0219;
        private long _lastDeviceSearchMs = 0;
        private const int WM_TRAYICON = 0x8000 + 100;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONUP = 0x0205;
        private const uint WM_GETICON = 0x007F;
        private const uint WM_NULL = 0x0000;
        private static readonly IntPtr IDI_APPLICATION = new IntPtr(32512);

        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const uint TPM_RETURNCMD = 0x0100;
        private const uint MF_STRING = 0x00000000;
        private const uint MF_SEPARATOR = 0x00000800;

        private bool _isTrayIconActive = false;

        #endregion

        public MainWindow()
        {
            InitializeComponent();

            _reader = StructuredOsuMemoryReader.Instance;
            _baseAddresses = new OsuBaseAddresses();

            SourceInitialized += MainWindow_SourceInitialized;
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            IntPtr hWnd = new WindowInteropHelper(this).Handle;
            HwndSource? source = HwndSource.FromHwnd(hWnd);
            source?.AddHook(HwndMessageHook);
        }

        #region Tray Icon & Single Instance Unhide

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState == WindowState.Minimized)
            {
                HideToTray();
            }
        }

        private void HideToTray()
        {
            AddTrayIcon();
            Hide();
        }

        public void RestoreFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
            RemoveTrayIcon();
            _lastUiAudioText = "";
            _lastUiTabletInfo = "";
        }

        private string GetCurrentTipStatusText()
        {
            if (WacomDevice.LastDetectedModel == "Not Found" ||
                _lastHardwareResult.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Tablet not found";
            }

            return "Running";
        }

        private void AddTrayIcon()
        {
            if (_isTrayIconActive) return;

            IntPtr hWnd = new WindowInteropHelper(this).Handle;
            NOTIFYICONDATA nid = new NOTIFYICONDATA();
            nid.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
            nid.hWnd = hWnd;
            nid.uID = 1;
            nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            nid.uCallbackMessage = WM_TRAYICON;
            nid.hIcon = GetTrayIconHandle();
            nid.szTip = $"osu! Pen Tip Auto-Toggle\n{GetCurrentTipStatusText()}";

            Shell_NotifyIcon(NIM_ADD, ref nid);
            _isTrayIconActive = true;
        }

        private void UpdateTrayTooltip()
        {
            if (!_isTrayIconActive) return;

            IntPtr hWnd = new WindowInteropHelper(this).Handle;
            NOTIFYICONDATA nid = new NOTIFYICONDATA();
            nid.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
            nid.hWnd = hWnd;
            nid.uID = 1;
            nid.uFlags = NIF_TIP;
            nid.szTip = $"osu! Pen Tip Auto-Toggle\n{GetCurrentTipStatusText()}";

            Shell_NotifyIcon(NIM_MODIFY, ref nid);
        }

        private void RemoveTrayIcon()
        {
            if (!_isTrayIconActive) return;

            IntPtr hWnd = new WindowInteropHelper(this).Handle;
            NOTIFYICONDATA nid = new NOTIFYICONDATA();
            nid.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
            nid.hWnd = hWnd;
            nid.uID = 1;

            Shell_NotifyIcon(NIM_DELETE, ref nid);
            _isTrayIconActive = false;
        }

        private IntPtr GetTrayIconHandle()
        {
            IntPtr hWnd = new WindowInteropHelper(this).Handle;
            IntPtr hIcon = SendMessage(hWnd, WM_GETICON, IntPtr.Zero, IntPtr.Zero);
            if (hIcon == IntPtr.Zero)
            {
                hIcon = LoadIcon(IntPtr.Zero, IDI_APPLICATION);
            }
            return hIcon;
        }

        private IntPtr HwndMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_DEVICECHANGE)
            {
                WacomDevice.InvalidateCache();
                _lastTargetState = null;
                _lastDeviceSearchMs = 0;
            }

            if (msg == WM_TRAYICON)
            {
                int mouseMsg = (int)(lParam.ToInt64() & 0xFFFF);
                if (mouseMsg == WM_LBUTTONUP || mouseMsg == WM_LBUTTONDBLCLK)
                {
                    RestoreFromTray();
                    handled = true;
                }
                else if (mouseMsg == WM_RBUTTONUP)
                {
                    ShowTrayContextMenu();
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        private void ShowTrayContextMenu()
        {
            IntPtr hWnd = new WindowInteropHelper(this).Handle;
            GetCursorPos(out POINT pt);

            IntPtr hMenu = CreatePopupMenu();
            AppendMenu(hMenu, MF_STRING, 1, "Open");
            AppendMenu(hMenu, MF_SEPARATOR, 0, string.Empty);
            AppendMenu(hMenu, MF_STRING, 2, "Exit");

            SetForegroundWindow(hWnd);
            uint cmd = TrackPopupMenuEx(hMenu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.X, pt.Y, hWnd, IntPtr.Zero);
            PostMessage(hWnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            DestroyMenu(hMenu);

            if (cmd == 1)
            {
                RestoreFromTray();
            }
            else if (cmd == 2)
            {
                Close();
            }
        }

        #endregion

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _cts = new CancellationTokenSource();

            try
            {
                _restoreWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, App.RestoreEventName);
                Task.Factory.StartNew(() => ListenForRestore(_cts.Token), TaskCreationOptions.LongRunning);
            }
            catch { }

            // Explicit Func<Task> delegate eliminates CS4014 cleanly
            Task.Run(new Func<Task>(() => MonitorLoop(_cts.Token)));
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _cts?.Cancel();
            _restoreWaitHandle?.Dispose();
            RemoveTrayIcon();
            WacomDevice.SetPressureAndButtons(true);
        }

        private void ListenForRestore(CancellationToken token)
        {
            if (_restoreWaitHandle == null) return;

            WaitHandle[] handles = new WaitHandle[] { _restoreWaitHandle, token.WaitHandle };
            while (!token.IsCancellationRequested)
            {
                int index = WaitHandle.WaitAny(handles);
                if (index == 0)
                {
                    Dispatcher.BeginInvoke(new Action(RestoreFromTray));
                }
                else
                {
                    break;
                }
            }
        }

        #region Process & Window Detection Helpers

        private int GetStableOsuProcessId(bool isCurrentlyReading)
        {
            long now = _stopwatch.ElapsedMilliseconds;

            if (isCurrentlyReading && _lastStablePid > 0)
            {
                return _lastStablePid;
            }

            if (now - _lastStableProcessCheckMs < 350 && _lastStablePid > 0)
            {
                try
                {
                    using var existing = Process.GetProcessById(_lastStablePid);
                    if (!existing.HasExited) return _lastStablePid;
                }
                catch
                {
                    _lastStablePid = -1;
                }
            }
            _lastStableProcessCheckMs = now;

            try
            {
                var procs = Process.GetProcessesByName("osu!");
                if (procs.Length == 0) procs = Process.GetProcessesByName("osu");

                foreach (var p in procs)
                {
                    try
                    {
                        if (p.HasExited) continue;

                        string? modulePath = p.MainModule?.FileName?.ToLowerInvariant();
                        if (modulePath != null && (modulePath.Contains("osulazer") || modulePath.Contains("osu-lazer")))
                            continue;

                        return p.Id;
                    }
                    catch
                    {
                        if (!p.HasExited) return p.Id;
                    }
                }
            }
            catch { }
            return -1;
        }

        private string? GetLazerWindowTitle()
        {
            uint currentPid = (uint)Process.GetCurrentProcess().Id;

            if (_cachedLazerHwnd != IntPtr.Zero)
            {
                if (IsWindow(_cachedLazerHwnd) && IsWindowVisible(_cachedLazerHwnd))
                {
                    string title = ReadWindowTitle(_cachedLazerHwnd);
                    if (IsOsuTitle(title))
                    {
                        return title;
                    }
                }
                _cachedLazerHwnd = IntPtr.Zero;
            }

            long now = _stopwatch.ElapsedMilliseconds;
            if (now - _lastLazerScanMs < 1000)
            {
                return null;
            }
            _lastLazerScanMs = now;

            IntPtr foundHwnd = IntPtr.Zero;
            string? foundTitle = null;

            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd)) return true;

                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == currentPid) return true;

                string title = ReadWindowTitle(hWnd);
                if (IsOsuTitle(title) && IsLazerWindow(hWnd, pid))
                {
                    foundHwnd = hWnd;
                    foundTitle = title;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            if (foundHwnd != IntPtr.Zero)
            {
                _cachedLazerHwnd = foundHwnd;
                return foundTitle;
            }

            return null;
        }

        private static bool IsLazerWindow(IntPtr hWnd, uint pid)
        {
            var classSb = new StringBuilder(256);
            GetClassName(hWnd, classSb, classSb.Capacity);
            string className = classSb.ToString();

            if (className.StartsWith("WindowsForms10", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (className.Equals("SDL_app", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                using var proc = Process.GetProcessById((int)pid);
                string? mainModule = proc.MainModule?.FileName?.ToLowerInvariant();
                if (mainModule != null)
                {
                    if (mainModule.Contains("osulazer") || mainModule.Contains("osu-lazer") || mainModule.Contains("osu.desktop"))
                        return true;

                    if (mainModule.Contains(@"\osu!\osu!.exe") || mainModule.Contains(@"\osu\osu!.exe"))
                        return false;
                }

                string procName = proc.ProcessName.ToLowerInvariant();
                if (procName == "osulazer" || procName == "osu-lazer" || procName == "osu.desktop")
                    return true;
            }
            catch { }

            return false;
        }

        private static bool IsOsuTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return false;
            return title.Equals("osu!", StringComparison.OrdinalIgnoreCase)
                || title.StartsWith("osu! ", StringComparison.OrdinalIgnoreCase)
                || title.StartsWith("osu! -", StringComparison.OrdinalIgnoreCase)
                || title.StartsWith("osu!(lazer)", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadWindowTitle(IntPtr hWnd)
        {
            var sb = new StringBuilder(512);
            int len = GetWindowText(hWnd, sb, sb.Capacity);
            return len > 0 ? sb.ToString() : string.Empty;
        }

        private void ForceRehook(long now)
        {
            _baseAddresses = new OsuBaseAddresses();
            _lastRehookMs = now;
            _frozenTicks = 0;
            _lastAudioTime = -1;
            _lastMapId = -1;
            _firstHitObjectTime = 0;
            _unknownStatusStartMs = 0;

            try
            {
                var clearMethod = _reader.GetType().GetMethod("InvalidateCaches")
                               ?? _reader.GetType().GetMethod("ClearCaches");
                clearMethod?.Invoke(_reader, null);
            }
            catch { }
        }

        #endregion

        #region Time Formatting Helper (<10000 ms in ms, then mm:ss)

        private static string FormatAudioTime(int ms)
        {
            if (ms < 10000)
            {
                return $"Song timeline: {ms} ms";
            }

            int totalSeconds = ms / 1000;
            int minutes = totalSeconds / 60;
            int seconds = totalSeconds % 60;

            return $"Song timeline: {minutes:D2}:{seconds:D2}";
        }

        #endregion

        #region Smooth Dot Color Animation (Fade)

        private static void AnimateDotColor(Shape dot, Color targetColor, int durationMs = 220)
        {
            if (!(dot.Fill is SolidColorBrush brush))
            {
                dot.Fill = new SolidColorBrush(targetColor);
                return;
            }

            if (brush.IsFrozen)
            {
                brush = brush.Clone();
                dot.Fill = brush;
            }

            if (brush.Color == targetColor) return;

            var animation = new ColorAnimation
            {
                To = targetColor,
                Duration = TimeSpan.FromMilliseconds(durationMs)
            };
            brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
        }

        #endregion


        private async Task MonitorLoop(CancellationToken token)
        {
            bool lastReadGeneral = false;

            while (!token.IsCancellationRequested)
            {
                long now = _stopwatch.ElapsedMilliseconds;
                int currentStablePid = GetStableOsuProcessId(lastReadGeneral);

                if (currentStablePid != _lastStablePid)
                {
                    _lastStablePid = currentStablePid;
                    _consecutiveFailedReads = 0;
                    ForceRehook(now);
                }

                bool readGeneral = false;
                bool readBeatmap = false;

                if (currentStablePid > 0)
                {
                    readGeneral = _reader.TryRead(_baseAddresses.GeneralData);
                    if (readGeneral)
                    {
                        readBeatmap = _reader.TryRead(_baseAddresses.Beatmap);
                    }
                }
                lastReadGeneral = readGeneral;

                DisplayCategory candidateCategory;
                bool isLazerCandidate = false;
                string audioTimeText = "";
                string menuStatusDetail = "";

                if (readGeneral)
                {
                    _consecutiveFailedReads = 0;
                    isLazerCandidate = false;
                    var status = _baseAddresses.GeneralData.OsuStatus;
                    int audioTime = _baseAddresses.GeneralData.AudioTime;

                    if (status == OsuMemoryStatus.Unknown)
                    {
                        if (_unknownStatusStartMs == 0) _unknownStatusStartMs = now;
                        else if (now - _unknownStatusStartMs > 3000 && now - _lastRehookMs > 3000)
                        {
                            ForceRehook(now);
                        }
                    }
                    else
                    {
                        _unknownStatusStartMs = 0;
                    }

                    if (status == OsuMemoryStatus.Playing &&
                        (_lastOsuStatus != OsuMemoryStatus.Playing || audioTime < (_lastAudioTime - 1500)))
                    {
                        _gameplayEnterMs = now;
                        _frozenTicks = 0;
                    }
                    _lastOsuStatus = status;

                    if (status == OsuMemoryStatus.Playing)
                    {
                        audioTimeText = FormatAudioTime(audioTime);

                        if (audioTime <= 0 && (now - _gameplayEnterMs > 4000) && (now - _lastRehookMs > 3000))
                        {
                            ForceRehook(now);
                        }

                        if (audioTime == _lastAudioTime)
                        {
                            _frozenTicks++;
                        }
                        else
                        {
                            _frozenTicks = 0;
                        }
                        _lastAudioTime = audioTime;

                        if (readBeatmap && _baseAddresses.Beatmap.Id != _lastMapId)
                        {
                            _lastMapId = _baseAddresses.Beatmap.Id;
                            _firstHitObjectTime = ResolveFirstHitObjectTime(_baseAddresses.Beatmap, currentStablePid);
                        }

                        bool isSkipAvailable = (_firstHitObjectTime > 0 && audioTime < (_firstHitObjectTime - 3000))
                                               || audioTime < 0;

                        bool isIntroGracePeriod = (now - _gameplayEnterMs < 1500);

                        if (isIntroGracePeriod)
                        {
                            _frozenTicks = 0;
                            candidateCategory = DisplayCategory.SkipIntro;
                        }
                        // FAST PAUSE DETECTION: 2 ticks
                        else if (_frozenTicks >= 2)
                        {
                            candidateCategory = DisplayCategory.Paused;
                        }
                        else if (isSkipAvailable)
                        {
                            candidateCategory = DisplayCategory.SkipIntro;
                        }
                        else
                        {
                            candidateCategory = DisplayCategory.Playing;
                        }
                    }
                    else
                    {
                        _frozenTicks = 0;
                        _lastMapId = -1;
                        _firstHitObjectTime = 0;
                        menuStatusDetail = status.ToString();
                        audioTimeText = FormatAudioTime(audioTime);
                        candidateCategory = DisplayCategory.Menu;
                    }
                }
                else if (currentStablePid > 0)
                {
                    _consecutiveFailedReads++;
                    _frozenTicks = 0;
                    _lastMapId = -1;
                    _firstHitObjectTime = 0;
                    _lastOsuStatus = OsuMemoryStatus.Unknown;
                    _unknownStatusStartMs = 0;
                    isLazerCandidate = false;

                    if (now - _lastRehookMs >= 1200)
                    {
                        ForceRehook(now);
                    }

                    if (_consecutiveFailedReads > 20)
                    {
                        audioTimeText = "Re-hooking... (Run as Admin if stuck)";
                        menuStatusDetail = "Retrying Hook";
                    }
                    else
                    {
                        audioTimeText = "Hooking osu! memory...";
                        menuStatusDetail = "Connecting";
                    }

                    candidateCategory = DisplayCategory.Connecting;
                }
                else
                {
                    _frozenTicks = 0;
                    _lastMapId = -1;
                    _firstHitObjectTime = 0;
                    _lastOsuStatus = OsuMemoryStatus.Unknown;
                    _unknownStatusStartMs = 0;
                    _consecutiveFailedReads = 0;

                    string? lazerTitle = GetLazerWindowTitle();
                    if (lazerTitle != null)
                    {
                        isLazerCandidate = true;
                        bool hasBeatmapName = lazerTitle.Contains(" - ") || lazerTitle.Contains(" – ");
                        if (hasBeatmapName)
                        {
                            candidateCategory = DisplayCategory.Playing;
                            audioTimeText = $"Song: {lazerTitle}";
                        }
                        else
                        {
                            candidateCategory = DisplayCategory.Menu;
                            menuStatusDetail = "osu!Lazer";
                            audioTimeText = "Song timeline: In Menus";
                        }
                    }
                    else
                    {
                        candidateCategory = DisplayCategory.Waiting;
                        audioTimeText = "Song timeline: 0 ms";
                    }
                }

                // -------------------------------------------------------------
                // 1. HARDWARE TOGGLE: Instant on Pause/Resume, Debounced on Song Select
                // -------------------------------------------------------------
                bool isActivelyPlaying = (candidateCategory == DisplayCategory.Playing);
                string? hardwareResult = null;
                long currentMs = _stopwatch.ElapsedMilliseconds;

                if (_lastHardwareResult.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    currentMs - _lastDeviceSearchMs > 2000)
                {
                    _lastDeviceSearchMs = currentMs;
                    _lastTargetState = null;
                }

                bool isPauseResumeTransition =
                    (_lastTargetState.HasValue &&
                     ((_lastTargetState.Value && candidateCategory == DisplayCategory.Paused) ||
                      (!_lastTargetState.Value && candidateCategory == DisplayCategory.Playing && _lastOsuStatus == OsuMemoryStatus.Playing)));

                if (_lastTargetState == null)
                {
                    _lastTargetState = isActivelyPlaying;
                    _pendingHardwareState = isActivelyPlaying;
                    hardwareResult = WacomDevice.SetPressureAndButtons(!isActivelyPlaying);
                    if (hardwareResult != null) _lastHardwareResult = hardwareResult;
                }
                else if (isActivelyPlaying != _lastTargetState)
                {
                    if (isPauseResumeTransition)
                    {
                        _lastTargetState = isActivelyPlaying;
                        _pendingHardwareState = isActivelyPlaying;
                        hardwareResult = WacomDevice.SetPressureAndButtons(!isActivelyPlaying);
                        if (hardwareResult != null) _lastHardwareResult = hardwareResult;
                    }
                    else
                    {
                        if (_pendingHardwareState != isActivelyPlaying)
                        {
                            _pendingHardwareState = isActivelyPlaying;
                            _pendingHardwareStateStartTime = currentMs;
                        }
                        else if (currentMs - _pendingHardwareStateStartTime >= HardwareDebounceMs)
                        {
                            _lastTargetState = isActivelyPlaying;
                            hardwareResult = WacomDevice.SetPressureAndButtons(!isActivelyPlaying);
                            if (hardwareResult != null) _lastHardwareResult = hardwareResult;
                        }
                    }
                }
                else
                {
                    _pendingHardwareState = isActivelyPlaying;
                }

                // -------------------------------------------------------------
                // 2. UI TRANSITION: Instant on Pause & Resume; Debounced for other states
                // -------------------------------------------------------------
                bool isPauseResumeUiTransition =
                    (candidateCategory == DisplayCategory.Paused && _currentAppliedCategory == DisplayCategory.Playing) ||
                    (candidateCategory == DisplayCategory.Playing && _currentAppliedCategory == DisplayCategory.Paused);

                bool uiStateChanged = false;

                if (isPauseResumeUiTransition)
                {
                    // Instant UI update for pause and unpause
                    _currentAppliedCategory = candidateCategory;
                    _appliedIsLazer = isLazerCandidate;
                    _appliedMenuStatusDetail = menuStatusDetail;
                    _pendingCategory = candidateCategory;
                    _pendingIsLazer = isLazerCandidate;
                    _pendingCategoryStartTime = currentMs;
                    uiStateChanged = true;
                }
                else
                {
                    if (candidateCategory != _pendingCategory || isLazerCandidate != _pendingIsLazer)
                    {
                        _pendingCategory = candidateCategory;
                        _pendingIsLazer = isLazerCandidate;
                        _pendingCategoryStartTime = currentMs;
                    }

                    if (_currentAppliedCategory == DisplayCategory.None ||
                        ((_pendingCategory != _currentAppliedCategory || _pendingIsLazer != _appliedIsLazer) &&
                         (currentMs - _pendingCategoryStartTime >= UiDebounceMs)))
                    {
                        _currentAppliedCategory = _pendingCategory;
                        _appliedIsLazer = _pendingIsLazer;
                        _appliedMenuStatusDetail = menuStatusDetail;
                        uiStateChanged = true;
                    }
                }

                string gameStateText;
                Color dotColor;

                switch (_currentAppliedCategory)
                {
                    case DisplayCategory.Playing:
                        gameStateText = _appliedIsLazer ? "Actively Playing (osu!Lazer)" : "Actively Playing";
                        dotColor = Color.FromRgb(236, 72, 153); // Pink
                        break;
                    case DisplayCategory.SkipIntro:
                        gameStateText = "Intro / Skip Available";
                        dotColor = Color.FromRgb(59, 130, 246); // Blue
                        break;
                    case DisplayCategory.Paused:
                        gameStateText = "Paused in Beatmap";
                        dotColor = Color.FromRgb(234, 179, 8); // Yellow
                        break;
                    case DisplayCategory.Menu:
                        gameStateText = $"In Menu / Song Select ({_appliedMenuStatusDetail})";
                        dotColor = Color.FromRgb(34, 197, 94); // Green
                        break;
                    case DisplayCategory.Connecting:
                        gameStateText = $"Connecting to osu!... ({_appliedMenuStatusDetail})";
                        dotColor = Color.FromRgb(234, 179, 8); // Yellow
                        break;
                    default:
                        gameStateText = "Waiting for osu! to launch...";
                        dotColor = Color.FromRgb(113, 113, 122); // Gray
                        break;
                }

                string detectedTabletInfo = WacomDevice.LastDetectedModel;

                bool shouldUpdateAudio = (currentMs - _lastUiAudioUpdateMs >= 80) && (_lastUiAudioText != audioTimeText);
                bool shouldUpdateTablet = (_lastUiTabletInfo != detectedTabletInfo);

                if (hardwareResult != null || uiStateChanged || shouldUpdateAudio || shouldUpdateTablet)
                {
                    if (shouldUpdateAudio)
                    {
                        _lastUiAudioUpdateMs = currentMs;
                        _lastUiAudioText = audioTimeText;
                    }
                    if (shouldUpdateTablet)
                    {
                        _lastUiTabletInfo = detectedTabletInfo;
                    }

                    string capturedAudio = audioTimeText;
                    string capturedTablet = detectedTabletInfo;
                    string? capturedHwResult = hardwareResult;
                    bool capturedUiChanged = uiStateChanged;
                    bool capturedIsPlaying = isActivelyPlaying;
                    string capturedGameText = gameStateText;
                    Color capturedColor = dotColor;

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        bool isWindowVisible = IsVisible && WindowState != WindowState.Minimized;

                        if (isWindowVisible)
                        {
                            if (shouldUpdateAudio) TxtAudioTime.Text = capturedAudio;
                            if (shouldUpdateTablet) TxtDetectedTablet.Text = $"Device: {capturedTablet}";
                        }

                        if (capturedHwResult != null)
                        {
                            if (isWindowVisible)
                            {
                                TxtTabletStatus.Text = $"Pen Tip & Buttons: {capturedHwResult}";
                                Color tabletDotColor = (capturedHwResult.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0)
                                    ? Color.FromRgb(113, 113, 122)
                                    : (capturedIsPlaying ? Color.FromRgb(239, 68, 68) : Color.FromRgb(59, 130, 246));
                                AnimateDotColor(TabletStatusDot, tabletDotColor);
                                TxtLog.Text = $"[{DateTime.Now:HH:mm:ss}] Hardware -> {capturedHwResult}";
                            }
                            UpdateTrayTooltip();
                        }

                        if (capturedUiChanged)
                        {
                            if (isWindowVisible)
                            {
                                TxtGameStatus.Text = capturedGameText;
                                AnimateDotColor(GameStatusDot, capturedColor);
                            }
                            UpdateTrayTooltip();
                        }
                    }));
                }

                int pollDelay = (readGeneral || currentStablePid > 0 || _cachedLazerHwnd != IntPtr.Zero) ? 25 : 200;
                await Task.Delay(pollDelay, token);
            }
        }

        private int ResolveFirstHitObjectTime(CurrentBeatmap beatmap, int stablePid)
        {
            try
            {
                if (string.IsNullOrEmpty(beatmap.FolderName) || string.IsNullOrEmpty(beatmap.OsuFileName))
                    return 0;

                string? osuFolder = null;
                if (stablePid > 0)
                {
                    try
                    {
                        using var proc = Process.GetProcessById(stablePid);
                        string? exePath = proc.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(exePath))
                        {
                            osuFolder = Path.GetDirectoryName(exePath);
                        }
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(osuFolder) || !Directory.Exists(osuFolder))
                {
                    osuFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osu!");
                }

                string mapPath = Path.Combine(osuFolder, "Songs", beatmap.FolderName, beatmap.OsuFileName);

                if (!File.Exists(mapPath))
                {
                    string fallbackPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osu!", "Songs", beatmap.FolderName, beatmap.OsuFileName);
                    if (File.Exists(fallbackPath)) mapPath = fallbackPath;
                    else return 0;
                }

                using var fs = new FileStream(mapPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs, Encoding.UTF8);

                bool inHitObjects = false;
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Trim() == "[HitObjects]")
                    {
                        inHitObjects = true;
                        continue;
                    }

                    if (inHitObjects && !string.IsNullOrWhiteSpace(line))
                    {
                        var parts = line.Split(',');
                        if (parts.Length > 2 && int.TryParse(parts[2], out int firstTime))
                        {
                            return firstTime;
                        }
                        break;
                    }
                }
            }
            catch { }

            return 0;
        }
    }
}