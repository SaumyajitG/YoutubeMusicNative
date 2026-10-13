using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using YoutubeMusicNative.Services;
using YoutubeMusicNative.ViewModels;

namespace YoutubeMusicNative;

public partial class App : Application
{
    public static HttpClient Http { get; } = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        MaxConnectionsPerServer = 16,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public static MainViewModel Vm { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = true;
            Vm?.ShowToast(args.Exception.Message);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log(args.Exception);
            args.SetObserved();
        };

        var settings = Settings.Load();
        Vm = new MainViewModel(settings);
        var window = new MainWindow { DataContext = Vm };
        MainWindow = window;
        window.Show();
        Vm.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Vm?.Settings.Save();
        Vm?.Player.Dispose();
        base.OnExit(e);
    }

    private static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.AppendAllText(System.IO.Path.Combine(Settings.Folder, "error.log"), $"[{DateTime.Now:O}] {ex}\n\n");
        }
        catch
        {
            // ignore
        }
    }
}
