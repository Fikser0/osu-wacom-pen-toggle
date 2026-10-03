using System;
using System.Threading;
using System.Windows;

namespace osu_TipToggle
{
    public partial class App : Application
    {
        private const string MutexName = "osu_TipToggle_SingleInstance_Mutex";
        public const string RestoreEventName = "osu_TipToggle_RestoreEvent";

        private static Mutex? _mutex;
        private static bool _hasHandle = false;

        protected override void OnStartup(StartupEventArgs e)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _mutex = new Mutex(true, MutexName, out bool createdNew);
            _hasHandle = createdNew;

            bool startInTray = false;
            foreach (var arg in e.Args)
            {
                if (arg.Equals("-tray", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("/tray", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("--tray", StringComparison.OrdinalIgnoreCase))
                {
                    startInTray = true;
                    break;
                }
            }

            if (!_hasHandle)
            {
                // Only bring the existing instance forward if launched normally (without -tray)
                if (!startInTray)
                {
                    try
                    {
                        if (EventWaitHandle.TryOpenExisting(RestoreEventName, out EventWaitHandle? restoreEvent))
                        {
                            restoreEvent.Set();
                            restoreEvent.Dispose();
                        }
                    }
                    catch
                    {

                    }
                }

                Shutdown();
                return;
            }

            base.OnStartup(e);

            var mainWindow = new MainWindow(startInTray);
            MainWindow = mainWindow;

            if (!startInTray)
            {
                mainWindow.Show();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_hasHandle && _mutex != null)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch
                {
                    
                }

                _mutex.Dispose();
                _mutex = null;
                _hasHandle = false;
            }

            base.OnExit(e);
        }
    }
}