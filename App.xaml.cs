using System.IO;
using System.Windows;
using System.Windows.Threading;
using IndexExplorer.Services;

namespace IndexExplorer;

public partial class App : Application
{
    private static readonly object LogLock = new();

    public static void Log(string message)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(Settings.DataDir);
                File.AppendAllText(Path.Combine(Settings.DataDir, "log.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }

    /// <summary>자체 검증 모드: --selftest &lt;결과 폴더&gt; &lt;테스트 루트&gt;</summary>
    public static string? SelfTestOut { get; private set; }
    public static string? SelfTestRoot { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length >= 3 && e.Args[0] == "--selftest")
        {
            SelfTestOut = e.Args[1];
            SelfTestRoot = e.Args[2];
        }
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log("fatal: " + a.ExceptionObject);
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("ui: " + e.Exception);
        e.Handled = true;
        if (SelfTestOut != null) return;
        MessageBox.Show("작업 중 문제가 생겼습니다.\n\n" + e.Exception.Message, "색인 탐색기", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
