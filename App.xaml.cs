using System.Threading;
using System.Windows.Threading;

namespace ClaudePetOverlay;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private bool _ownsMutex;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        // Some agent-launched processes inherit SystemRoot without the usual
        // WINDIR alias. WPF's font cache expects WINDIR to be a valid path.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WINDIR")))
        {
            var windowsDirectory = Environment.GetEnvironmentVariable("SystemRoot")
                                   ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            Environment.SetEnvironmentVariable("WINDIR", windowsDirectory, EnvironmentVariableTarget.Process);
        }

        _singleInstance = new Mutex(true, "Local\\ClaudePetOverlay", out _ownsMutex);
        if (!_ownsMutex)
        {
            Shutdown(0);
            return;
        }

        var qaMode = e.Args.Contains("--qa", StringComparer.OrdinalIgnoreCase);
        var window = new MainWindow(qaMode);
        MainWindow = window;
        window.Show();

        if (e.Args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                window.Close();
            };
            timer.Start();
        }
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        if (_ownsMutex)
        {
            _singleInstance?.ReleaseMutex();
        }

        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
