using System.Diagnostics;
using System.IO;

namespace IndexExplorer.Engine;

/// <summary>
/// 색인 엔진: 모든 NTFS 드라이브의 전체 파일 목록을 유지한다 (서비스 안에서 돈다, 관리자 권한).
/// 드라이브마다:
///  1) 저장해 둔 목록을 불러오고, 변경 기록이 그때부터 이어지면 그대로 재생 (재부팅 후에도 다시 읽지 않음)
///  2) 이어지지 않으면(처음·기록이 지워짐·저널이 바뀜) 장부를 통째로 다시 읽음 (수 초)
///  3) 이후 변경 기록을 계속 따라가며 반영, 주기적으로 목록 저장
/// </summary>
public sealed class EngineHost : IDisposable
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IndexExplorer", "engine");

    private readonly List<VolumeIndex> _volumes = [];
    private readonly Dictionary<string, string> _state = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _tasks = [];
    private readonly Action<string> _log;

    public EngineHost(Action<string>? log = null) { _log = log ?? (_ => { }); }

    public IReadOnlyList<VolumeIndex> Volumes { get { lock (_volumes) return _volumes.ToList(); } }

    /// <summary>모든 드라이브의 변경 횟수 합 (화면이 다시 물어볼지 판단).</summary>
    public long Version => Volumes.Sum(v => v.Version);

    public IReadOnlyDictionary<string, string> State { get { lock (_state) return new Dictionary<string, string>(_state); } }

    private void SetState(string root, string s) { lock (_state) _state[root] = s; _log($"{root} {s}"); }

    public void Start()
    {
        Directory.CreateDirectory(DataDir);
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType != DriveType.Fixed || d.DriveFormat != "NTFS") continue;
            }
            catch { continue; }
            var root = d.Name.TrimEnd('\\');
            _tasks.Add(Task.Factory.StartNew(() => RunVolume(root, _cts.Token), TaskCreationOptions.LongRunning));
        }
    }

    private void RunVolume(string root, CancellationToken ct)
    {
        using var vol = NtfsVolume.Open(root, out var err);
        if (vol == null) { SetState(root, $"열 수 없음 (오류 {err})"); return; }

        if (!vol.QueryJournal(out var jd, out err))
        {
            if (err == NtfsVolume.ERROR_JOURNAL_NOT_ACTIVE && vol.CreateJournal() && vol.QueryJournal(out jd, out err))
                SetState(root, "변경 기록을 켰습니다");
            else { SetState(root, $"변경 기록을 쓸 수 없음 (오류 {err})"); return; }
        }

        var idx = new VolumeIndex(root) { Serial = vol.Serial };
        lock (_volumes) _volumes.Add(idx);
        var snap = Path.Combine(DataDir, $"vol-{vol.Serial:X8}.idx");

        // 1) 저장한 목록이 지금 변경 기록과 이어지면 재생만
        var sw = Stopwatch.StartNew();
        var resumed = idx.Load(snap) && idx.Serial == vol.Serial && idx.JournalId == jd.JournalId
                      && idx.NextUsn >= jd.FirstUsn && idx.NextUsn <= jd.NextUsn;
        if (resumed) SetState(root, $"저장한 목록 {idx.Count:N0}개 불러옴 ({sw.ElapsedMilliseconds}ms), 변경 기록 이어 읽는 중");
        else FullRead(vol, idx, jd, root);

        var lastSave = Stopwatch.StartNew();
        long sinceSave = 0;
        while (!ct.IsCancellationRequested)
        {
            long applied = 0;
            int error = 0;
            idx.Write(v =>
            {
                v.NextUsn = vol.ReadChanges(v.NextUsn, v.JournalId, (frn, parent, name, attr, reason) =>
                {
                    Apply(v, frn, parent, name, attr, reason);
                    applied++;
                }, out error);
            }, bump: false);
            if (applied > 0) idx.Touch();
            if (error is NtfsVolume.ERROR_JOURNAL_ENTRY_DELETED or NtfsVolume.ERROR_JOURNAL_NOT_ACTIVE or NtfsVolume.ERROR_JOURNAL_DELETE_IN_PROGRESS)
            {
                // 기록이 너무 오래돼 지워졌거나 저널이 바뀜 → 통째로 다시
                if (vol.QueryJournal(out jd, out _)) FullRead(vol, idx, jd, root);
                continue;
            }
            sinceSave += applied;
            if (sinceSave > 0 && (lastSave.Elapsed.TotalMinutes >= 5 || sinceSave > 50_000))
            {
                TrySave(idx, snap, root);
                lastSave.Restart();
                sinceSave = 0;
            }
            if (ct.WaitHandle.WaitOne(400)) break;
        }
        TrySave(idx, snap, root);
    }

    private void FullRead(NtfsVolume vol, VolumeIndex idx, NtfsVolume.JournalData jd, string root)
    {
        SetState(root, "디스크 장부 읽는 중…");
        var sw = Stopwatch.StartNew();
        idx.Write(v =>
        {
            v.Clear();
            vol.EnumerateAll(jd.NextUsn, (frn, parent, name, attr) => v.Upsert(frn, parent, name, attr));
            v.JournalId = jd.JournalId;
            v.NextUsn = jd.NextUsn;
        });
        SetState(root, $"장부 {idx.Count:N0}개 읽음 ({sw.ElapsedMilliseconds:N0}ms)");
        TrySave(idx, Path.Combine(DataDir, $"vol-{vol.Serial:X8}.idx"), root);
    }

    /// <summary>변경 기록 한 줄 반영.</summary>
    public static void Apply(VolumeIndex v, ulong frn, ulong parent, ReadOnlySpan<char> name, uint attr, uint reason)
    {
        if ((reason & NtfsVolume.REASON_FILE_DELETE) != 0) { v.Remove(frn); return; }
        if ((reason & NtfsVolume.REASON_RENAME_OLD_NAME) != 0 && (reason & NtfsVolume.REASON_RENAME_NEW_NAME) == 0) return; // 새 이름이 뒤따름
        if ((reason & (NtfsVolume.REASON_FILE_CREATE | NtfsVolume.REASON_RENAME_NEW_NAME | NtfsVolume.REASON_BASIC_INFO_CHANGE
                       | NtfsVolume.REASON_HARD_LINK_CHANGE | NtfsVolume.REASON_DATA_EXTEND | NtfsVolume.REASON_DATA_OVERWRITE
                       | NtfsVolume.REASON_DATA_TRUNCATION)) != 0)
            v.Upsert(frn, parent, name, attr);
    }

    private void TrySave(VolumeIndex idx, string path, string root)
    {
        try { idx.Save(path); }
        catch (Exception ex) { SetState(root, "목록 저장 실패: " + ex.Message); }
    }

    // ===================== 조회 =====================

    /// <summary>종류별 개수 (모든 드라이브 합).</summary>
    public int[] Counts(bool includeSystem)
    {
        var total = new int[FileKinds.All.Length + 1];
        foreach (var v in Volumes)
        {
            var c = v.CountByKind(includeSystem);
            for (int i = 0; i < total.Length; i++) total[i] += c[i];
        }
        return total;
    }

    public sealed record Item(string Path, long Size, long ModifiedUtcTicks, uint Attr, uint Serial, ulong Frn);

    /// <summary>종류·검색어로 찾고, 크기·수정 시각은 실제 파일에서 읽어 붙인다 (장부에는 없으므로).</summary>
    public List<Item> Query(byte kind, string? text, bool includeSystem, int max)
    {
        var result = new List<Item>();
        foreach (var v in Volumes)
        {
            foreach (var h in v.Query(kind, text, includeSystem, max - result.Count))
            {
                long size = 0, mtime = 0;
                try
                {
                    var fi = new FileInfo(h.Path);
                    if (!fi.Exists) continue;
                    size = fi.Length;
                    mtime = fi.LastWriteTimeUtc.Ticks;
                }
                catch { }
                result.Add(new Item(h.Path, size, mtime, h.Attr, v.Serial, h.Frn));
            }
            if (result.Count >= max) break;
        }
        return result;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { Task.WaitAll(_tasks.ToArray(), TimeSpan.FromSeconds(10)); } catch { }
        _cts.Dispose();
    }
}
