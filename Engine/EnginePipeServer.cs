using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace IndexExplorer.Engine;

/// <summary>
/// 엔진 서비스의 접속 창구. 엔진은 PC의 모든 파일 이름을 알기 때문에
/// 설치한 사용자(와 관리자·SYSTEM)만 접속할 수 있게 파이프 권한을 제한한다.
/// </summary>
public sealed class EnginePipeServer : IDisposable
{
    private readonly EngineHost _host;
    private readonly string? _allowedSid;
    private readonly CancellationTokenSource _cts = new();

    public EnginePipeServer(EngineHost host, string? allowedSid)
    {
        _host = host;
        _allowedSid = allowedSid;
    }

    public void Start() => _ = Task.Run(AcceptLoop);

    private NamedPipeServerStream CreatePipe()
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        if (!string.IsNullOrEmpty(_allowedSid))
            sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(_allowedSid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(EngineProtocol.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, sec);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(_cts.Token);
            }
            catch (OperationCanceledException) { return; }
            catch { await Task.Delay(500); continue; }
            _ = Task.Run(() => Serve(pipe));
        }
    }

    private async Task Serve(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            try
            {
                while (pipe.IsConnected && !_cts.IsCancellationRequested)
                {
                    var req = await EngineProtocol.ReadAsync<EngineProtocol.Request>(pipe, _cts.Token);
                    if (req == null) return;
                    await EngineProtocol.WriteAsync(pipe, Handle(req), _cts.Token);
                }
            }
            catch { }
        }
    }

    private EngineProtocol.Response Handle(EngineProtocol.Request req)
    {
        try
        {
            var resp = new EngineProtocol.Response { Ok = true, Version = _host.Version, Files = _host.Volumes.Sum(v => (long)v.Count) };
            switch (req.Op)
            {
                case "status": resp.State = _host.State.ToDictionary(kv => kv.Key, kv => kv.Value); break;
                case "counts":
                    resp.Counts = _host.Counts(req.IncludeSystem, req.Excludes);
                    resp.AllCounts = _host.Counts(includeSystem: true);
                    break;
                case "query": resp.Items = _host.Query(req.Kind, req.Text, req.IncludeSystem, Math.Clamp(req.Max, 1, 200_000), req.Excludes); break;
                default: return new EngineProtocol.Response { Ok = false, Error = "알 수 없는 요청" };
            }
            return resp;
        }
        catch (Exception ex) { return new EngineProtocol.Response { Ok = false, Error = ex.Message }; }
    }

    public void Dispose() => _cts.Cancel();
}
