using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using IndexExplorer.Engine;

namespace IndexExplorer;

/// <summary>
/// 시작점. 한 exe 가 여러 역할을 한다.
///   (없음)                      화면 앱
///   --service --user &lt;SID&gt;     엔진 서비스 (윈도우 서비스 관리자가 실행)
///   --install-service &lt;SID&gt;    엔진 서비스 설치 (관리자 권한으로)
///   --uninstall-service          엔진 서비스 제거 (관리자 권한으로)
///   --engine-console &lt;결과파일&gt; 엔진을 한 번 돌려 결과를 파일로 (검증용, 관리자 권한으로)
///   --selftest ...               화면 앱 자체 검증
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var a = args.Length > 0 ? args[0] : "";
        switch (a)
        {
            case "--service":
                var sid = args.SkipWhile(x => x != "--user").Skip(1).FirstOrDefault();
                ServiceBase.Run(new EngineService(sid));
                return 0;
            case "--install-service":
                return EngineInstaller.Install(Environment.ProcessPath!, args.Length > 1 ? args[1] : "");
            case "--uninstall-service":
                return EngineInstaller.Uninstall();
            case "--engine-check":
                return EngineCheck(args.Length > 1 ? args[1] : "engine-check.txt");
            case "--engine-console":
                return EngineConsole(args.Length > 1 ? args[1] : "engine-console.txt");
        }
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    /// <summary>설치된 엔진 서비스에 일반 권한으로 물어본다 (접속 권한·응답 확인용).</summary>
    private static int EngineCheck(string outPath)
    {
        var log = new List<string>();
        using var c = new EngineClient();
        var sw = Stopwatch.StartNew();
        var st = c.AskAsync(new EngineProtocol.Request { Op = "status" }).GetAwaiter().GetResult();
        log.Add($"상태 응답: {(st?.Ok == true ? "성공" : "실패")} {sw.ElapsedMilliseconds}ms, 파일 {st?.Files:N0}개");
        if (st?.State != null) foreach (var kv in st.State) log.Add($"  {kv.Key} {kv.Value}");
        sw.Restart();
        var cn = c.AskAsync(new EngineProtocol.Request { Op = "counts" }).GetAwaiter().GetResult();
        log.Add($"종류별 개수 응답: {sw.ElapsedMilliseconds}ms");
        if (cn?.Counts != null) foreach (var k in FileKinds.All) log.Add($"  {k.Name,-8} {cn.Counts[k.Id],8:N0}");
        sw.Restart();
        var q = c.AskAsync(new EngineProtocol.Request { Op = "query", Kind = 7, Max = 20000 }, 30000).GetAwaiter().GetResult();
        log.Add($"이미지 조회: {q?.Items?.Count:N0}개 {sw.ElapsedMilliseconds}ms");
        File.WriteAllLines(outPath, log);
        return st?.Ok == true ? 0 : 1;
    }

    /// <summary>엔진을 한 번 끝까지 돌려 드라이브별 상태·개수·시간을 기록 (검증용).</summary>
    private static int EngineConsole(string outPath)
    {
        var log = new List<string>();
        var sw = Stopwatch.StartNew();
        using var host = new EngineHost(m => { lock (log) log.Add($"{sw.ElapsedMilliseconds,7}ms  {m}"); });
        host.Start();
        // 모든 드라이브가 첫 읽기를 마칠 때까지 (최대 3분)
        var deadline = DateTime.Now.AddMinutes(3);
        while (DateTime.Now < deadline && (host.Volumes.Count == 0 || host.State.Values.Any(s => s.Contains("읽는 중…"))))
            Thread.Sleep(200);
        Thread.Sleep(1500); // 변경 기록 한 바퀴
        var counts = host.Counts(includeSystem: false);
        var all = host.Counts(includeSystem: true);
        lock (log)
        {
            log.Add("");
            foreach (var v in host.Volumes) log.Add($"{v.Root}  {v.Count:N0}개  메모리 약 {v.EstimatedBytes / (1024 * 1024):N0}MB  변경 기록 {v.JournalId:X}");
            log.Add("종류별 (사용자 파일 / 전체):");
            foreach (var k in FileKinds.All) log.Add($"  {k.Name,-8} {counts[k.Id],9:N0} / {all[k.Id],9:N0}");
            var sw2 = Stopwatch.StartNew();
            var excel = host.Query(1, null, false, 100000);
            log.Add($"엑셀 조회 {excel.Count:N0}개 (크기·날짜 포함) {sw2.ElapsedMilliseconds}ms");
            var shown = excel.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            log.Add("사용자 파일에서 빠진 엑셀 (확인용):");
            foreach (var i in host.Query(1, null, true, 100000).Where(i => !shown.Contains(i.Path)).Take(25))
                log.Add($"  [{i.Attr:X}] {i.Path}");
        }
        File.WriteAllLines(outPath, log);
        return 0;
    }
}
