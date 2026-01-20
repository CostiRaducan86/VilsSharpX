using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace VideoStreamPlayer
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // UI thread exceptions
            this.DispatcherUnhandledException += (s, ex) =>
            {
                Log("DispatcherUnhandledException", ex.Exception);
                MessageBox.Show(ex.Exception.ToString(), "Crash (UI thread)");
                ex.Handled = true; // prevents silent shutdown
            };

            // Background thread exceptions
            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            {
                var err = ex.ExceptionObject as Exception ?? new Exception(ex.ExceptionObject?.ToString());
                Log("UnhandledException", err);
                MessageBox.Show(err.ToString(), "Crash (non-UI thread)");
            };

            // Task exceptions
            TaskScheduler.UnobservedTaskException += (s, ex) =>
            {
                Log("UnobservedTaskException", ex.Exception);
                MessageBox.Show(ex.Exception.ToString(), "Crash (Task)");
                ex.SetObserved();
            };

            base.OnStartup(e);
        }

        private static void Log(string tag, Exception ex)
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "crash.log");
                File.AppendAllText(path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {tag}\n{ex}\n\n");
            }
            catch { /* ignore logging errors */ }
        }
    }
}
