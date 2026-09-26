using System;
using System.Threading;
using System.Windows;

namespace osu_TipToogle
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private const string MutexName = "osu_TipToggle_SingleInstance_Mutex";
        public const string RestoreEventName = "osu_TipToggle_RestoreEvent";

        private static Mutex? _mutex;
        private static bool _hasHandle = false;

        protected override void OnStartup(StartupEventArgs e)
        {
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            _hasHandle = createdNew;

            if (!_hasHandle)
            {
                // Signal existing instance in tray to unhide & restore
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
                    // Ignore signaling errors
                }

                Shutdown();
                return;
            }

            base.OnStartup(e);
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
                    // Ignore release errors during shutdown
                }
                _mutex.Dispose();
                _mutex = null;
                _hasHandle = false;
            }

            base.OnExit(e);
        }
    }
}