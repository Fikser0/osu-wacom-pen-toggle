#pragma warning disable CS4014

using OsuMemoryDataProvider;
using OsuMemoryDataProvider.OsuMemoryModels;
using OsuMemoryDataProvider.OsuMemoryModels.Direct;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Math = System.Math;
using System.Globalization;
using System.IO;
using System.Reflection;
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

namespace osu_TipToggle
{
    public partial class MainWindow : Window
    {
        private readonly StructuredOsuMemoryReader _reader;
        private OsuBaseAddresses _baseAddresses;
        private CancellationTokenSource? _cts;
        private Task? _monitorTask;
        private EventWaitHandle? _restoreWaitHandle;

        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        private int _lastAudioTime = -1;
        private int _frozenTicks = 0;
        private bool _hasAudioStarted = false;
        private bool? _lastTargetState = null;
        private string _lastHardwareResult = "";

        // osu!stable tracking
        private OsuMemoryStatus _lastOsuStatus = OsuMemoryStatus.Unknown;
        private long _gameplayEnterMs = 0;
        private int _lastMapId = -1;
        private int _firstHitObjectTime = 0;
        private int _lastHitObjectTime = 0;
        private int _lastStablePid = -1;
        private long _lastStableProcessCheckMs = 0;
        private long _lastRehookMs = 0;
        private int _consecutiveFailedReads = 0;
        private long _unknownStatusStartMs = 0;

        // osu!(lazer) fallback window tracking
        private IntPtr _cachedLazerHwnd = IntPtr.Zero;
        private long _lastLazerScanMs = 0;

        private const int MinHardwareToggleIntervalMs = 1000;
        private const int HardwareSearchRetryIntervalMs = 10000;
        private long _lastHardwareToggleTimeMs = 0;
        private long _nextHardwareAttemptMs = 0;
        private bool? _pendingHardwareState = null;

        // UI transition smoothing
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

        private const int LastNoteDelayMs = 100;

        private const int UiDebounceMs = 450;
        private DisplayCategory _pendingCategory = DisplayCategory.None;
        private bool _pendingIsLazer = false;
        private string _pendingMenuStatusDetail = "";
        private long _pendingCategoryStartTime = 0;
        private DisplayCategory _currentAppliedCategory = DisplayCategory.None;
        private bool _appliedIsLazer = false;
        private string _appliedMenuStatusDetail = "";

        // UI rendering throttles
        private long _lastUiAudioUpdateMs = 0;
        private string _lastUiAudioText = "";
        private string _lastUiTabletInfo = "";

        #region Native Win32 APIs

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

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern uint ExtractIconEx(string? szFileName, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

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

        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_RESTORE = 0xF120;
        private const int WM_DEVICECHANGE = 0x0219;
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
        private IntPtr _hTrayIcon = IntPtr.Zero;

        #endregion

        public MainWindow() : this(false)
        {
        }

        public MainWindow(bool startInTray)
        {
            InitializeComponent();

            string appVersion = GetAppVersion();
            Title = $"osu!TipToggle v{appVersion}";
            TxtVersion.Text = $"v{appVersion}";
            TxtLog.Text = $"[{DateTime.Now:HH:mm:ss}] Hardware -> Ready";

            _reader = StructuredOsuMemoryReader.Instance;
            _baseAddresses = new OsuBaseAddresses();

            SourceInitialized += MainWindow_SourceInitialized;
            Closing += MainWindow_Closing;

            if (startInTray)
            {
                var helper = new WindowInteropHelper(this);
                helper.EnsureHandle();
                AddTrayIcon();
                StartBackgroundTasks();
            }
            else
            {
                Loaded += MainWindow_Loaded;
            }
        }

        private static string GetAppVersion()
        {
            var asm = Assembly.GetExecutingAssembly();
            string? infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(infoVer))
            {
                return infoVer!.Split('+')[0];
            }

            var ver = asm.GetName().Version;
            return ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "0.1.1";
        }

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            IntPtr hWnd = new WindowInteropHelper(this).Handle;
            HwndSource? source = HwndSource.FromHwnd(hWnd);
            source?.AddHook(HwndMessageHook);
        }

        #region Tray & Window Management

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState == WindowState.Minimized)
            {
                HideToTray();
            }
            else if (WindowState == WindowState.Normal && _isTrayIconActive)
            {
                RestoreFromTray();
            }
        }

        private void HideToTray()
        {
            AddTrayIcon();
            Hide();
        }

        public void RestoreFromTray()
        {
            RemoveTrayIcon();
            Show();
            WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();

            _lastUiAudioText = "";
            _lastUiTabletInfo = "";
            _currentAppliedCategory = DisplayCategory.None;
            _lastTargetState = null;
        }

        private string GetHardwareSummaryText()
        {
            if (WacomDevice.LastDetectedModel.IndexOf("Not Found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                _lastHardwareResult.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Tablet not found";
            }

            if (_lastHardwareResult.IndexOf("not available", StringComparison.OrdinalIgnoreCase) >= 0 ||
                WacomDevice.LastDetectedModel.IndexOf("Custom firmware not installed", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Custom firmware not installed";
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
            nid.szTip = GetHardwareSummaryText();

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
            nid.szTip = GetHardwareSummaryText();

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

            if (_hTrayIcon != IntPtr.Zero)
            {
                DestroyIcon(_hTrayIcon);
                _hTrayIcon = IntPtr.Zero;
            }
        }

        private IntPtr GetTrayIconHandle()
        {
            if (_hTrayIcon != IntPtr.Zero)
                return _hTrayIcon;

            try
            {
                string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    uint count = ExtractIconEx(exePath, 0, out IntPtr hLarge, out IntPtr hSmall, 1);
                    if (hLarge != IntPtr.Zero) DestroyIcon(hLarge);
                    if (hSmall != IntPtr.Zero)
                    {
                        _hTrayIcon = hSmall;
                        return _hTrayIcon;
                    }
                }
            }
            catch { }

            IntPtr hWnd = new WindowInteropHelper(this).Handle;
            IntPtr hIcon = SendMessage(hWnd, WM_GETICON, IntPtr.Zero, IntPtr.Zero);
            if (hIcon != IntPtr.Zero)
            {
                return hIcon;
            }

            return LoadIcon(IntPtr.Zero, IDI_APPLICATION);
        }

        private IntPtr HwndMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_SYSCOMMAND && (wParam.ToInt32() & 0xFFF0) == SC_RESTORE)
            {
                if (_isTrayIconActive)
                {
                    RestoreFromTray();
                    handled = true;
                    return IntPtr.Zero;
                }
            }

            // Re-scan HID devices when hardware is plugged or unplugged
            if (msg == WM_DEVICECHANGE)
            {
                WacomDevice.InvalidateCache();
                _lastTargetState = null;
                _lastHardwareToggleTimeMs = 0;
                _nextHardwareAttemptMs = 0;
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
            AppendMenu(hMenu, MF_STRING, 1, "Open on github.com");
            AppendMenu(hMenu, MF_SEPARATOR, 0, string.Empty);
            AppendMenu(hMenu, MF_STRING, 2, "Exit");

            SetForegroundWindow(hWnd);
            uint cmd = TrackPopupMenuEx(hMenu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.X, pt.Y, hWnd, IntPtr.Zero);
            PostMessage(hWnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            DestroyMenu(hMenu);

            if (cmd == 1)
            {
                OpenGithubReleases();
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
        private void BtnGithub_Click(object sender, RoutedEventArgs e) => OpenGithubReleases();

        private void BtnGithub_MouseEnter(object sender, MouseEventArgs e)
        {
            TxtLog.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(180)));
            TxtGithubHint.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(180)));
        }

        private void BtnGithub_MouseLeave(object sender, MouseEventArgs e)
        {
            TxtLog.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(180)));
            TxtGithubHint.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(180)));
        }

        private static void OpenGithubReleases()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://github.com/lukecupr/osu-wacom-pen-toggle",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            StartBackgroundTasks();
        }

        private void StartBackgroundTasks()
        {
            if (_monitorTask != null) return;

            _cts = new CancellationTokenSource();

            try
            {
                _restoreWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, App.RestoreEventName);
                Task.Factory.StartNew(() => ListenForRestore(_cts.Token), TaskCreationOptions.LongRunning);
            }
            catch { }

            _monitorTask = Task.Run(() => MonitorLoop(_cts.Token));
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _cts?.Cancel();
            _restoreWaitHandle?.Dispose();
            RemoveTrayIcon();

            try
            {
                _monitorTask?.Wait(500);
            }
            catch { }

            WacomDevice.SetPressureAndButtons(true);
            Application.Current.Shutdown();
        }

        private void ListenForRestore(CancellationToken token)
        {
            if (_restoreWaitHandle == null) return;

            try
            {
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
            catch
            {
                
            }
        }

        #region Process & Window Detection

        private int GetStableOsuProcessId(bool isCurrentlyReading)
        {
            long now = _stopwatch.ElapsedMilliseconds;

            if (isCurrentlyReading && _lastStablePid > 0)
            {
                return _lastStablePid;
            }

            if (now - _lastStableProcessCheckMs < 350)
            {
                if (_lastStablePid > 0)
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
                return _lastStablePid;
            }
            _lastStableProcessCheckMs = now;

            try
            {
                var procs = Process.GetProcessesByName("osu!");
                if (procs.Length == 0) procs = Process.GetProcessesByName("osu");

                int matchedPid = -1;
                foreach (var p in procs)
                {
                    try
                    {
                        if (matchedPid == -1 && !p.HasExited)
                        {
                            string? modulePath = null;
                            try { modulePath = p.MainModule?.FileName?.ToLowerInvariant(); } catch { }

                            if (modulePath == null || (!modulePath.Contains("osulazer") && !modulePath.Contains("osu-lazer")))
                            {
                                matchedPid = p.Id;
                            }
                        }
                    }
                    catch { }
                    finally
                    {
                        p.Dispose();
                    }
                }
                return matchedPid;
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

        private static string FormatMenuStatus(OsuMemoryStatus status)
        {
            return status.ToString() switch
            {
                "SongSelect" => "Song select",
                "SongSelectEdit" => "Editor song select",
                "ResultsScreen" => "Result screen",
                "MainMenu" => "Main menu",
                "MultiplayerRoom" => "Multiplayer room",
                "MultiplayerSongSelect" => "Multiplayer song select",
                "EditingMap" => "Beatmap editor",
                "OsuDirect" => "In osu!direct",
                "GameShutdownAnimation" => "Exiting...",
                string s => s
            };
        }

        private void ForceRehook(long now)
        {
            _baseAddresses = new OsuBaseAddresses();
            _lastRehookMs = now;
            _frozenTicks = 0;
            _hasAudioStarted = false;
            _lastAudioTime = -1;
            _lastMapId = -1;
            _firstHitObjectTime = 0;
            _lastHitObjectTime = 0;
            _unknownStatusStartMs = 0;
        }

        #endregion

        #region UI Helpers

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
            try
            {
                bool lastReadGeneral = false;

                while (!token.IsCancellationRequested)
                {
                    try
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
                                if (readBeatmap && _baseAddresses.Beatmap.Id != _lastMapId && _baseAddresses.Beatmap.Id > 0)
                                {
                                    _lastMapId = _baseAddresses.Beatmap.Id;
                                    (_firstHitObjectTime, _lastHitObjectTime) = ResolveHitObjectTimes(_baseAddresses.Beatmap, currentStablePid);
                                }
                            }
                        }
                        lastReadGeneral = readGeneral;

                        DisplayCategory candidateCategory;
                        bool isLazerCandidate = false;
                        bool isOutro = false;
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

                            if (status == OsuMemoryStatus.Playing && audioTime == 0 && _lastAudioTime < -50)
                            {
                                audioTime = _lastAudioTime;
                            }

                            bool isInitialAttachToPlaying = (_lastOsuStatus == OsuMemoryStatus.Unknown && status == OsuMemoryStatus.Playing);

                            bool isMapRestart = (status == OsuMemoryStatus.Playing) &&
                                                (!isInitialAttachToPlaying && _lastOsuStatus != OsuMemoryStatus.Playing ||
                                                 (_hasAudioStarted && (audioTime < (_lastAudioTime - 500) || (_lastAudioTime > 0 && audioTime <= 0))));

                            if (isInitialAttachToPlaying)
                            {
                                _hasAudioStarted = true;
                                _gameplayEnterMs = now - 5000;
                                _lastAudioTime = audioTime;
                            }
                            else if (isMapRestart)
                            {
                                _gameplayEnterMs = now;
                                _frozenTicks = 0;
                                _hasAudioStarted = false;
                                _lastAudioTime = audioTime;
                            }
                            _lastOsuStatus = status;

                            if (status == OsuMemoryStatus.Playing)
                            {
                                if (readBeatmap && _baseAddresses.Beatmap.Id != _lastMapId)
                                {
                                    _lastMapId = _baseAddresses.Beatmap.Id;
                                    (_firstHitObjectTime, _lastHitObjectTime) = ResolveHitObjectTimes(_baseAddresses.Beatmap, currentStablePid);
                                }

                                long elapsedFromEnter = now - _gameplayEnterMs;

                                if (!_hasAudioStarted && _lastAudioTime != -1 && audioTime > _lastAudioTime)
                                {
                                    _hasAudioStarted = true;
                                }

                                if (!_hasAudioStarted && elapsedFromEnter < 600)
                                {
                                    audioTimeText = "Song timeline: Loading...";
                                }
                                else
                                {
                                    audioTimeText = FormatAudioTime(audioTime);
                                }

                                if (!_hasAudioStarted)
                                {
                                    _frozenTicks = 0;
                                }
                                else if (audioTime == _lastAudioTime)
                                {
                                    _frozenTicks++;
                                }
                                else
                                {
                                    _frozenTicks = 0;
                                }
                                _lastAudioTime = audioTime;

                                bool isUnknownDuringLoad = (_firstHitObjectTime <= 0 && elapsedFromEnter < 500);
                                bool isLongIntro = _firstHitObjectTime >= 3000 && (!_hasAudioStarted || audioTime < (_firstHitObjectTime - 1000));
                                bool isIntro = isLongIntro || isUnknownDuringLoad;

                                isOutro = _hasAudioStarted && (_lastHitObjectTime > 0 && audioTime > (_lastHitObjectTime + LastNoteDelayMs));

                                if (_frozenTicks >= 8)
                                {
                                    candidateCategory = DisplayCategory.Paused;
                                }
                                else if (isIntro)
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
                                _hasAudioStarted = false;
                                menuStatusDetail = FormatMenuStatus(status);
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
                            _lastHitObjectTime = 0;
                            _lastOsuStatus = OsuMemoryStatus.Unknown;
                            _unknownStatusStartMs = 0;
                            isLazerCandidate = false;

                            if (now - _lastRehookMs >= 1200)
                            {
                                ForceRehook(now);
                            }

                            if (_consecutiveFailedReads > 20)
                            {
                                audioTimeText = "Try to \"Run as Admin\" if stuck";
                                menuStatusDetail = "Re-hooking...";
                            }
                            else
                            {
                                audioTimeText = "Connecting";
                                menuStatusDetail = "Hooking memory...";
                            }

                            candidateCategory = DisplayCategory.Connecting;
                        }
                        else
                        {
                            _frozenTicks = 0;
                            _lastMapId = -1;
                            _firstHitObjectTime = 0;
                            _lastHitObjectTime = 0;
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
                                    menuStatusDetail = "In menus";
                                    audioTimeText = "Waiting for gameplay";
                                }
                            }
                            else
                            {
                                candidateCategory = DisplayCategory.Waiting;
                                audioTimeText = "See you next time...";
                            }
                        }

                        // Hardware rate-limited toggling (prevents MCU / digitizer desync at 1000Hz)
                        bool isActivelyPlaying = (candidateCategory == DisplayCategory.Playing) && !isOutro;
                        string? hardwareResult = null;
                        long currentMs = _stopwatch.ElapsedMilliseconds;

                        if (token.IsCancellationRequested) break;

                        if (_lastTargetState == null)
                        {
                            if (currentMs >= _nextHardwareAttemptMs)
                            {
                                _pendingHardwareState = isActivelyPlaying;
                                hardwareResult = WacomDevice.SetPressureAndButtons(!isActivelyPlaying);
                                if (hardwareResult != null)
                                {
                                    _lastHardwareResult = hardwareResult;
                                    bool isSuccess = hardwareResult == "Active (ON)" || hardwareResult == "Disabled (OFF)";
                                    if (isSuccess)
                                    {
                                        _lastTargetState = isActivelyPlaying;
                                        _lastHardwareToggleTimeMs = currentMs;
                                    }
                                    else
                                    {
                                        _nextHardwareAttemptMs = currentMs + HardwareSearchRetryIntervalMs;
                                    }
                                }
                                else
                                {
                                    _nextHardwareAttemptMs = currentMs + HardwareSearchRetryIntervalMs;
                                }
                            }
                        }
                        else if (isActivelyPlaying != _lastTargetState)
                        {
                            _pendingHardwareState = isActivelyPlaying;

                            if (currentMs - _lastHardwareToggleTimeMs >= MinHardwareToggleIntervalMs)
                            {
                                hardwareResult = WacomDevice.SetPressureAndButtons(!isActivelyPlaying);
                                if (hardwareResult != null)
                                {
                                    _lastHardwareResult = hardwareResult;
                                    bool isSuccess = hardwareResult == "Active (ON)" || hardwareResult == "Disabled (OFF)";
                                    if (isSuccess)
                                    {
                                        _lastTargetState = isActivelyPlaying;
                                        _lastHardwareToggleTimeMs = currentMs;
                                    }
                                    else
                                    {
                                        _lastTargetState = null;
                                        _nextHardwareAttemptMs = currentMs + HardwareSearchRetryIntervalMs;
                                    }
                                }
                                else
                                {
                                    _lastTargetState = null;
                                    _nextHardwareAttemptMs = currentMs + HardwareSearchRetryIntervalMs;
                                }
                            }
                        }
                        else
                        {
                            _pendingHardwareState = isActivelyPlaying;
                        }

                        // UI status update debounce
                        bool isPauseResumeUiTransition =
                            (candidateCategory == DisplayCategory.Paused && _currentAppliedCategory == DisplayCategory.Playing) ||
                            (candidateCategory == DisplayCategory.Playing && _currentAppliedCategory == DisplayCategory.Paused);

                        bool uiStateChanged = false;

                        if (isPauseResumeUiTransition)
                        {
                            _currentAppliedCategory = candidateCategory;
                            _appliedIsLazer = isLazerCandidate;
                            _appliedMenuStatusDetail = menuStatusDetail;
                            _pendingCategory = candidateCategory;
                            _pendingIsLazer = isLazerCandidate;
                            _pendingMenuStatusDetail = menuStatusDetail;
                            _pendingCategoryStartTime = currentMs;
                            uiStateChanged = true;
                        }
                        else
                        {
                            if (candidateCategory != _pendingCategory || isLazerCandidate != _pendingIsLazer || menuStatusDetail != _pendingMenuStatusDetail)
                            {
                                _pendingCategory = candidateCategory;
                                _pendingIsLazer = isLazerCandidate;
                                _pendingMenuStatusDetail = menuStatusDetail;
                                _pendingCategoryStartTime = currentMs;
                            }

                            if (_currentAppliedCategory == DisplayCategory.None ||
                                ((_pendingCategory != _currentAppliedCategory || _pendingIsLazer != _appliedIsLazer || _pendingMenuStatusDetail != _appliedMenuStatusDetail) &&
                                 (currentMs - _pendingCategoryStartTime >= UiDebounceMs)))
                            {
                                _currentAppliedCategory = _pendingCategory;
                                _appliedIsLazer = _pendingIsLazer;
                                _appliedMenuStatusDetail = _pendingMenuStatusDetail;
                                uiStateChanged = true;
                            }
                        }

                        string gameStateText;
                        Color dotColor;

                        string clientPrefix = _appliedIsLazer ? "osu!(Lazer): " : "osu!(stable): ";

                        switch (_currentAppliedCategory)
                        {
                            case DisplayCategory.Playing:
                                gameStateText = $"{clientPrefix}Playing a beatmap";
                                dotColor = Color.FromRgb(236, 72, 153);
                                break;
                            case DisplayCategory.SkipIntro:
                                gameStateText = $"{clientPrefix}Intro skip available";
                                dotColor = Color.FromRgb(59, 130, 246);
                                break;
                            case DisplayCategory.Paused:
                                gameStateText = $"{clientPrefix}Paused in beatmap";
                                dotColor = Color.FromRgb(234, 179, 8);
                                break;
                            case DisplayCategory.Menu:
                                string menuDetail = _appliedIsLazer
                                    ? "In menus"
                                    : (string.IsNullOrEmpty(_appliedMenuStatusDetail) ? "Main menu" : _appliedMenuStatusDetail);
                                gameStateText = $"{clientPrefix}{menuDetail}";
                                dotColor = Color.FromRgb(34, 197, 94);
                                break;
                            case DisplayCategory.Connecting:
                                gameStateText = $"{clientPrefix}{_appliedMenuStatusDetail}";
                                dotColor = Color.FromRgb(234, 179, 8);
                                break;
                            default:
                                gameStateText = "Waiting for osu! to launch...";
                                dotColor = Color.FromRgb(113, 113, 122);
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
                                    if (shouldUpdateTablet)
                                    {
                                        TxtDetectedTablet.Text = capturedTablet.Equals("Not Found", StringComparison.OrdinalIgnoreCase)
                                            ? "Connect supported device"
                                            : $"Device: {capturedTablet}";
                                    }
                                }

                                if (capturedHwResult != null)
                                {
                                    if (isWindowVisible)
                                    {
                                        TxtTabletStatus.Text = $"Pressure & Buttons: {capturedHwResult}";
                                        bool isUnavailable = (capturedHwResult.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                              capturedHwResult.IndexOf("not available", StringComparison.OrdinalIgnoreCase) >= 0);

                                        Color tabletDotColor = isUnavailable
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
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[MonitorLoop] Loop error: {ex.Message}");
                        await Task.Delay(250, token);
                    }
                }
            }
            finally
            {
                try
                {
                    WacomDevice.SetPressureAndButtons(true);
                }
                catch { }
            }
        }

        private struct TimingPoint
        {
            public double Time;
            public double BeatLength;
            public bool Uninherited;
        }

        private static string ResolveSongsFolder(string? osuFolder)
        {
            if (string.IsNullOrEmpty(osuFolder) || !Directory.Exists(osuFolder))
                return string.Empty;

            try
            {
                var cfgFiles = Directory.GetFiles(osuFolder, "osu!*.cfg");
                foreach (var cfgFile in cfgFiles)
                {
                    string fileName = Path.GetFileName(cfgFile);
                    if (!fileName.StartsWith("osu!.", StringComparison.OrdinalIgnoreCase) &&
                        !fileName.Equals("osu!.cfg", StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (var line in File.ReadLines(cfgFile))
                    {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith("BeatmapDirectory", StringComparison.OrdinalIgnoreCase))
                        {
                            int eqIdx = trimmed.IndexOf('=');
                            if (eqIdx >= 0 && eqIdx < trimmed.Length - 1)
                            {
                                string customDir = trimmed.Substring(eqIdx + 1).Trim();
                                if (!string.IsNullOrEmpty(customDir))
                                {
                                    string resolved = Path.IsPathRooted(customDir)
                                        ? customDir
                                        : Path.Combine(osuFolder, customDir);

                                    if (Directory.Exists(resolved))
                                    {
                                        return resolved;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            return Path.Combine(osuFolder, "Songs");
        }

        private (int firstTime, int lastTime) ResolveHitObjectTimes(CurrentBeatmap beatmap, int stablePid)
        {
            try
            {
                if (string.IsNullOrEmpty(beatmap.FolderName) || string.IsNullOrEmpty(beatmap.OsuFileName))
                    return (0, 0);

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

                string songsFolder = ResolveSongsFolder(osuFolder);
                if (string.IsNullOrEmpty(songsFolder))
                {
                    songsFolder = Path.Combine(osuFolder, "Songs");
                }
                string mapPath = Path.Combine(songsFolder, beatmap.FolderName, beatmap.OsuFileName);

                if (!File.Exists(mapPath))
                {
                    string defaultSongs = Path.Combine(osuFolder, "Songs", beatmap.FolderName, beatmap.OsuFileName);
                    if (File.Exists(defaultSongs))
                    {
                        mapPath = defaultSongs;
                    }
                    else
                    {
                        string localAppDataSongs = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "osu!", "Songs", beatmap.FolderName, beatmap.OsuFileName);

                        if (File.Exists(localAppDataSongs))
                        {
                            mapPath = localAppDataSongs;
                        }
                        else
                        {
                            return (0, 0);
                        }
                    }
                }

                using var fs = new FileStream(mapPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs, Encoding.UTF8);

                bool inGeneral = false;
                bool inDifficulty = false;
                bool inTimingPoints = false;
                bool inHitObjects = false;

                double sliderMultiplier = 1.4;
                var timingPoints = new List<TimingPoint>();

                int firstHitObjectTime = 0;
                int lastHitObjectTime = 0;
                string? line;

                while ((line = sr.ReadLine()) != null)
                {
                    string trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("//"))
                        continue;

                    if (trimmed.StartsWith("["))
                    {
                        inGeneral = (trimmed == "[General]");
                        inDifficulty = (trimmed == "[Difficulty]");
                        inTimingPoints = (trimmed == "[TimingPoints]");
                        inHitObjects = (trimmed == "[HitObjects]");
                        continue;
                    }

                    // Only support standard osu! (Mode: 0) - ignore Taiko, Catch, Mania
                    if (inGeneral && trimmed.StartsWith("Mode:", StringComparison.OrdinalIgnoreCase))
                    {
                        string modeVal = trimmed.Substring(5).Trim();
                        if (int.TryParse(modeVal, out int mode) && mode != 0)
                        {
                            return (0, 0);
                        }
                    }

                    if (inDifficulty && trimmed.StartsWith("SliderMultiplier:", StringComparison.OrdinalIgnoreCase))
                    {
                        string smVal = trimmed.Substring(17).Trim();
                        double.TryParse(smVal, NumberStyles.Float, CultureInfo.InvariantCulture, out sliderMultiplier);
                    }

                    if (inTimingPoints)
                    {
                        var tpParts = trimmed.Split(',');
                        if (tpParts.Length >= 2 &&
                            double.TryParse(tpParts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double tpTime) &&
                            double.TryParse(tpParts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double beatLen))
                        {
                            bool isUninh = (tpParts.Length > 6 && int.TryParse(tpParts[6], out int u)) ? (u == 1) : (beatLen > 0);
                            timingPoints.Add(new TimingPoint
                            {
                                Time = tpTime,
                                BeatLength = beatLen,
                                Uninherited = isUninh
                            });
                        }
                    }

                    if (inHitObjects)
                    {
                        var parts = trimmed.Split(',');
                        if (parts.Length > 2 && int.TryParse(parts[2], out int objTime))
                        {
                            if (firstHitObjectTime == 0)
                            {
                                firstHitObjectTime = objTime;
                            }

                            int endTime = objTime;

                            if (parts.Length > 3 && int.TryParse(parts[3], out int objType))
                            {
                                // Spinner (type bit 3)
                                if ((objType & 8) != 0 && parts.Length > 5 && int.TryParse(parts[5], out int spinnerEnd))
                                {
                                    endTime = spinnerEnd;
                                }
                                // Slider (type bit 1)
                                else if ((objType & 2) != 0 && parts.Length > 7)
                                {
                                    if (int.TryParse(parts[6], out int slides) &&
                                        double.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out double pixelLength))
                                    {
                                        double uninheritedBeatLength = 500.0;
                                        double svMultiplier = 1.0;

                                        for (int i = 0; i < timingPoints.Count; i++)
                                        {
                                            var tp = timingPoints[i];
                                            if (tp.Time > objTime) break;

                                            if (tp.Uninherited)
                                            {
                                                uninheritedBeatLength = tp.BeatLength;
                                                svMultiplier = 1.0;
                                            }
                                            else
                                            {
                                                svMultiplier = Math.Max(0.1, Math.Min(10.0, -100.0 / tp.BeatLength));
                                            }
                                        }

                                        double pixelsPerBeat = sliderMultiplier * 100.0 * svMultiplier;
                                        if (pixelsPerBeat > 0)
                                        {
                                            double beats = (pixelLength * slides) / pixelsPerBeat;
                                            int duration = (int)Math.Round(beats * uninheritedBeatLength);
                                            endTime = objTime + Math.Max(0, duration);
                                        }
                                    }
                                }
                            }

                            if (endTime > lastHitObjectTime)
                            {
                                lastHitObjectTime = endTime;
                            }
                        }
                    }
                }

                return (firstHitObjectTime, lastHitObjectTime);
            }
            catch { }

            return (0, 0);
        }
    }
}