using System.IO.Pipes;

namespace IndexExplorer.Engine;

/// <summary>화면 앱 쪽: 엔진 서비스에 묻는다. 서비스가 없거나 꺼져 있으면 null 을 돌려주고 앱은 1.0 방식으로 동작한다.</summary>
public sealed class EngineClient : IDisposable
{
    private NamedPipeClientStream? _pipe;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsConnected => _pipe is { IsConnected: true };

    /// <summary>자체 검증 중에는 실제 서비스에 붙지 않는다 (결과가 늘 같도록).</summary>
    public bool Disabled { get; set; }

    public async Task<EngineProtocol.Response?> AskAsync(EngineProtocol.Request req, int timeoutMs = 5000)
    {
        if (Disabled) return null;
        await _gate.WaitAsync();
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (_pipe is not { IsConnected: true })
                    {
                        _pipe?.Dispose();
                        _pipe = new NamedPipeClientStream(".", EngineProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                        await _pipe.ConnectAsync(400);
                    }
                    using var cts = new CancellationTokenSource(timeoutMs);
                    await EngineProtocol.WriteAsync(_pipe, req, cts.Token);
                    var resp = await EngineProtocol.ReadAsync<EngineProtocol.Response>(_pipe, cts.Token);
                    if (resp != null) return resp;
                }
                catch
                {
                    _pipe?.Dispose();
                    _pipe = null;
                }
            }
            return null;
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _pipe?.Dispose();
        _gate.Dispose();
    }
}
