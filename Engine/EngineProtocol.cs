using System.IO;
using System.Text.Json;

namespace IndexExplorer.Engine;

/// <summary>화면 앱 ↔ 엔진 서비스 통신 (이름 있는 파이프, 길이 4바이트 + JSON).</summary>
public static class EngineProtocol
{
    public const string PipeName = "IndexExplorer.Engine.v1";
    public const string ServiceName = "IndexExplorerEngine";

    public sealed class Request
    {
        /// <summary>status | counts | query</summary>
        public string Op { get; set; } = "";
        public byte Kind { get; set; }
        public string? Text { get; set; }
        public bool IncludeSystem { get; set; }
        public int Max { get; set; } = 20000;
    }

    public sealed class Response
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public long Version { get; set; }
        public long Files { get; set; }
        public int[]? Counts { get; set; }
        public List<EngineHost.Item>? Items { get; set; }
        public Dictionary<string, string>? State { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task WriteAsync<T>(Stream s, T msg, CancellationToken ct = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(msg, Json);
        var len = BitConverter.GetBytes(body.Length);
        await s.WriteAsync(len, ct);
        await s.WriteAsync(body, ct);
        await s.FlushAsync(ct);
    }

    public static async Task<T?> ReadAsync<T>(Stream s, CancellationToken ct = default)
    {
        var len = new byte[4];
        if (!await ReadExactly(s, len, ct)) return default;
        var n = BitConverter.ToInt32(len);
        if (n <= 0 || n > 256 << 20) return default;
        var body = new byte[n];
        if (!await ReadExactly(s, body, ct)) return default;
        return JsonSerializer.Deserialize<T>(body, Json);
    }

    private static async Task<bool> ReadExactly(Stream s, byte[] buf, CancellationToken ct)
    {
        int got = 0;
        while (got < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(got), ct);
            if (n == 0) return false;
            got += n;
        }
        return true;
    }
}
