using System.IO;
using System.Windows;
using System.Windows.Threading;
using IndexExplorer.Services;
using IndexExplorer.Views;

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

    // 모든 창이 함께 쓰는 색인 (창마다 따로 두면 서로의 변경이 보이지 않는다)
    public static Settings Settings { get; private set; } = null!;
    public static IndexStore Store { get; private set; } = null!;
    public static Indexer Indexer { get; private set; } = null!;
    public static bool IndexerStarted { get; set; }
    /// <summary>엔진 서비스 접속 (없으면 null 응답 → 1.0 방식).</summary>
    public static Engine.EngineClient Engine { get; } = new();

    // 탐색기처럼 한 프로그램에 창 여러 개: 이미 실행 중이면 그쪽에 새 창을 열게 하고 끝낸다
    private const string InstanceName = "IndexExplorer.SingleInstance";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _newWindowSignal;

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

        if (SelfTestOut == null && !ClaimSingleInstance())
        {
            Shutdown();
            return;
        }

        // 자체 검증 중에는 실제 서비스에 붙지 않는다 (INDEXEXPLORER_SELFTEST_ENGINE=1 이면 실제 엔진으로 화면 확인)
        Engine.Disabled = SelfTestOut != null && Environment.GetEnvironmentVariable("INDEXEXPLORER_SELFTEST_ENGINE") != "1";
        Settings = Settings.Load();
        Store = new IndexStore(Path.Combine(Settings.DataDir, "index.db"));
        Indexer = new Indexer(Store, Settings, Dispatcher);
        new MainWindow().Show();
    }

    private bool ClaimSingleInstance()
    {
        var name = $"{InstanceName}.{Environment.UserName}";
        _instanceMutex = new Mutex(true, name, out var created);
        if (!created)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(name + ".NewWindow");
                signal.Set();
            }
            catch { }
            return false;
        }
        _newWindowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".NewWindow");
        new Thread(() =>
        {
            while (_newWindowSignal.WaitOne())
                Dispatcher.BeginInvoke(() => OpenWindow(null));
        }) { IsBackground = true, Name = "NewWindowSignal" }.Start();
        return true;
    }

    /// <summary>새 창 (Ctrl+N, exe 재실행, "새 창에서 열기").</summary>
    public static MainWindow OpenWindow(Location? start, Window? from = null)
    {
        var w = new MainWindow(start);
        if (from is { WindowState: WindowState.Normal })
        {
            w.KeepPosition = true;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = from.Left + 32;
            w.Top = from.Top + 32;
            w.Width = from.Width;
            w.Height = from.Height;
        }
        w.Show();
        if (SelfTestOut == null) w.Activate(); // 자체 검증 중에는 포커스를 빼앗지 않는다
        return w;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Settings?.Save();
            Indexer?.Dispose();
            Store?.Dispose();
            _newWindowSignal?.Dispose();
            _instanceMutex?.Dispose();
        }
        catch { }
        base.OnExit(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("ui: " + e.Exception);
        e.Handled = true;
        if (SelfTestOut != null) return;
        MessageBox.Show("작업 중 문제가 생겼습니다.\n\n" + e.Exception.Message, "색인 탐색기", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
