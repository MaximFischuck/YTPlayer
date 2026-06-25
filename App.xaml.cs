using System.Threading;
using System.Windows;

namespace YTPlayer
{
    public partial class App : System.Windows.Application
    {
        private Mutex? _mutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            _mutex = new Mutex(true, "YTPlayer_SingleInstance", out var isNew);

            if (!isNew)
            {
                System.Windows.MessageBox.Show("YT Player уже запущен.", "YT Player", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}
