using System.Diagnostics;
using System.IO;
using System.ServiceProcess;

namespace IndexExplorer.Engine;

/// <summary>윈도우 서비스로 도는 엔진 (IndexExplorer.exe --service --user &lt;SID&gt;).</summary>
public sealed class EngineService : ServiceBase
{
    private readonly string? _userSid;
    private EngineHost? _host;
    private EnginePipeServer? _server;

    public EngineService(string? userSid)
    {
        ServiceName = EngineProtocol.ServiceName;
        _userSid = userSid;
    }

    public static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(EngineHost.DataDir);
            File.AppendAllText(Path.Combine(EngineHost.DataDir, "engine.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
        }
        catch { }
    }

    protected override void OnStart(string[] args)
    {
        Log("엔진 시작");
        _host = new EngineHost(Log);
        _host.Start();
        _server = new EnginePipeServer(_host, _userSid);
        _server.Start();
    }

    protected override void OnStop()
    {
        Log("엔진 중지");
        _server?.Dispose();
        _host?.Dispose();
    }
}

/// <summary>엔진 서비스 설치·제거 (관리자 권한으로 실행된 exe 에서).</summary>
public static class EngineInstaller
{
    private static int Sc(string args)
    {
        var psi = new ProcessStartInfo("sc.exe", args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    public static bool IsInstalled() => ServiceController.GetServices().Any(s => s.ServiceName == EngineProtocol.ServiceName);

    public static string StatusText()
    {
        var s = ServiceController.GetServices().FirstOrDefault(x => x.ServiceName == EngineProtocol.ServiceName);
        return s == null ? "설치 안 됨" : s.Status switch
        {
            ServiceControllerStatus.Running => "실행 중",
            ServiceControllerStatus.Stopped => "멈춤",
            ServiceControllerStatus.StartPending => "시작하는 중",
            _ => s.Status.ToString(),
        };
    }

    /// <summary>서비스가 실행하는 복사본 위치 (개발 폴더의 exe 를 잠그지 않도록 따로 둔다).</summary>
    public static string InstallDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "IndexExplorer", "Engine");

    /// <summary>설치(있으면 새로 복사해 다시 설치) 후 시작. 0 = 성공.</summary>
    public static int Install(string exePath, string userSid)
    {
        if (IsInstalled()) Uninstall();
        // 실행 파일들을 Program Files 로 복사 (단일 exe 배포판이면 exe 하나)
        var src = Path.GetDirectoryName(exePath)!;
        Directory.CreateDirectory(InstallDir);
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            var dst = Path.Combine(InstallDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst, overwrite: true);
        }
        var target = Path.Combine(InstallDir, Path.GetFileName(exePath));
        var bin = $"\\\"{target}\\\" --service --user {userSid}";
        var r = Sc($"create {EngineProtocol.ServiceName} binPath= \"{bin}\" start= auto DisplayName= \"색인 탐색기 엔진\"");
        if (r != 0) return r;
        Sc($"description {EngineProtocol.ServiceName} \"색인 탐색기: PC 전체 파일 목록을 디스크 장부로 유지합니다 (읽기 전용).\"");
        Sc($"failure {EngineProtocol.ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/30000");
        return Sc($"start {EngineProtocol.ServiceName}") is 0 or 1056 ? 0 : 1;
    }

    public static int Uninstall()
    {
        Sc($"stop {EngineProtocol.ServiceName}");
        for (int i = 0; i < 20 && StatusText() is not ("멈춤" or "설치 안 됨"); i++) Thread.Sleep(250);
        var r = Sc($"delete {EngineProtocol.ServiceName}");
        Thread.Sleep(500);
        try { if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, recursive: true); } catch { /* 다음 설치 때 덮어쓴다 */ }
        return r;
    }
}
