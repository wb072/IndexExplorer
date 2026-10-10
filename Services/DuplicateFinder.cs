using System.IO;
using System.Security.Cryptography;

namespace IndexExplorer.Services;

/// <summary>
/// 중복 파일 찾기. 내용이 같은 파일을 이름과 상관없이 찾는다.
///  1) 크기가 같은 것끼리만 후보 (대부분 여기서 걸러진다)
///  2) 앞·뒤 64KB 지문으로 다시 거르고
///  3) 그래도 같으면 파일 전체 SHA-256 으로 확정
/// 지문은 저장해 두고(경로·크기·수정 시각이 같으면) 다시 읽지 않는다.
/// 클라우드에만 있는 OneDrive 파일은 읽으면 내려받기가 시작되므로 건너뛴다.
/// </summary>
public sealed class DuplicateFinder
{
    public sealed record Candidate(string Path, long Size, long ModifiedTicks, uint Attr);
    public sealed record Group(string Hash, long Size, List<Candidate> Files)
    {
        /// <summary>하나만 남기면 아낄 수 있는 용량.</summary>
        public long Waste => Size * (Files.Count - 1);
    }

    public sealed record Progress(string Stage, int Done, int Total);

    private const int Edge = 64 * 1024;
    /// <summary>이보다 작은 파일은 뺀다 (빈 파일·작은 설정 파일이 거대한 가짜 묶음을 만들지 않게).</summary>
    public const long MinSize = 1024;
    private const uint AttrOffline = 0x1000, AttrRecallOnOpen = 0x40000, AttrRecallOnDataAccess = 0x400000;

    private readonly IndexStore _cache;

    public DuplicateFinder(IndexStore cache) { _cache = cache; }

    public static bool CloudOnly(uint attr) => (attr & (AttrOffline | AttrRecallOnOpen | AttrRecallOnDataAccess)) != 0;

    public async Task<List<Group>> FindAsync(IEnumerable<Candidate> all, IProgress<Progress>? progress, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            // 1) 크기가 같은 것끼리 (0 바이트·클라우드 전용 제외, 같은 경로 중복 제거)
            var bySize = all.Where(c => c.Size >= MinSize && !CloudOnly(c.Attr))
                .DistinctBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
                .GroupBy(c => c.Size).Where(g => g.Count() > 1).ToList();
            var total = bySize.Sum(g => g.Count());
            var done = 0;
            var result = new List<Group>();

            foreach (var sizeGroup in bySize)
            {
                ct.ThrowIfCancellationRequested();
                var size = sizeGroup.Key;
                // 2) 앞·뒤 지문 (작은 파일은 이것이 곧 전체 지문)
                var byPartial = new Dictionary<string, List<Candidate>>();
                foreach (var c in sizeGroup)
                {
                    ct.ThrowIfCancellationRequested();
                    var h = PartialHash(c);
                    done++;
                    if (done % 20 == 0) progress?.Report(new Progress("비교하는 중", done, total));
                    if (h == null) continue;
                    if (!byPartial.TryGetValue(h, out var list)) byPartial[h] = list = [];
                    list.Add(c);
                }
                foreach (var (ph, list) in byPartial)
                {
                    if (list.Count < 2) continue;
                    if (size <= 2 * Edge) { result.Add(new Group(ph, size, list)); continue; }
                    // 3) 전체 지문으로 확정
                    var byFull = new Dictionary<string, List<Candidate>>();
                    foreach (var c in list)
                    {
                        ct.ThrowIfCancellationRequested();
                        progress?.Report(new Progress($"전체 비교 ({Services.PathUtil.Dir(c.Path)})", done, total));
                        var fh = FullHash(c, ct);
                        if (fh == null) continue;
                        if (!byFull.TryGetValue(fh, out var fl)) byFull[fh] = fl = [];
                        fl.Add(c);
                    }
                    foreach (var (fh, fl) in byFull) if (fl.Count > 1) result.Add(new Group(fh, size, fl));
                }
            }
            progress?.Report(new Progress("끝", total, total));
            return result.OrderByDescending(g => g.Waste).ToList();
        }, ct);
    }

    private string? PartialHash(Candidate c)
    {
        var (cached, _) = _cache.GetHash(c.Path, c.Size, c.ModifiedTicks);
        if (cached != null) return cached;
        try
        {
            using var fs = new FileStream(c.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, Edge, FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[Edge];
            var n = fs.ReadAtLeast(buf, (int)Math.Min(Edge, c.Size), throwOnEndOfStream: false);
            sha.AppendData(buf, 0, n);
            if (c.Size > 2 * Edge)
            {
                fs.Seek(-Edge, SeekOrigin.End);
                n = fs.ReadAtLeast(buf, Edge, throwOnEndOfStream: false);
                sha.AppendData(buf, 0, n);
            }
            else if (c.Size > Edge)
            {
                n = fs.ReadAtLeast(buf, (int)(c.Size - Edge), throwOnEndOfStream: false);
                sha.AppendData(buf, 0, n);
            }
            var h = Convert.ToHexString(sha.GetHashAndReset());
            _cache.PutHash(c.Path, c.Size, c.ModifiedTicks, h, c.Size <= 2 * Edge ? h : null);
            return h;
        }
        catch { return null; } // 사용 중·권한 없음
    }

    private string? FullHash(Candidate c, CancellationToken ct)
    {
        var (_, cached) = _cache.GetHash(c.Path, c.Size, c.ModifiedTicks);
        if (cached != null) return cached;
        try
        {
            using var fs = new FileStream(c.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[1 << 20];
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                sha.AppendData(buf, 0, n);
            }
            var h = Convert.ToHexString(sha.GetHashAndReset());
            _cache.PutHash(c.Path, c.Size, c.ModifiedTicks, null, h);
            return h;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}
