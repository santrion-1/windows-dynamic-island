using System;
using System.IO;
using System.Threading;

namespace DynamicIsland
{
    public partial class App : System.Windows.Application
    {
        private static Mutex? _singleInstanceMutex;

        protected override void OnStartup(System.Windows.StartupEventArgs e)
        {
            DispatcherUnhandledException += (s, ex) =>
            {
                try
                {
                    File.WriteAllText("startup_error.log", ex.Exception.ToString());
                }
                catch { }
            };

            _singleInstanceMutex = new Mutex(true, "DynamicIsland_SingleInstance_Mutex", out bool createdNew);
            if (!createdNew)
            {
                Shutdown();
                return;
            }
            base.OnStartup(e);
        }

        protected override void OnExit(System.Windows.ExitEventArgs e)
        {
            try
            {
                _singleInstanceMutex?.ReleaseMutex();
                _singleInstanceMutex?.Dispose();
            }
            catch { }
            base.OnExit(e);
        }
    }
}
