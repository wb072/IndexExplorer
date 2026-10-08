using System.Collections.Concurrent;
using System.IO;
using System.IO.Enumeration;
using System.Windows.Threading;
using IndexExplorer.Native;

namespace IndexExplorer.Services;

/// <summary>
/// 감시 폴더를 훑어 색인을 최신으로 유지한다.
/// - 처음 한 번 전체 검사, 이후에는 FileSystemWatcher 변경분만 다시 검사
/// - 파일이 옮겨지거나 이름이 바뀌면 NTFS 파일 ID로 같은 파일임을 알아내 색인 연결을 그대로 유지
/// - 감시 범위 밖으로 옮겨진 색인 파일도 파일 ID로 새 위치를 찾아간다
/// </summary>
public sealed class Indexer : IDisposable
{
    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".svn", ".hg", ".vs", ".idea", "__pycache__", "$RECYCLE.BIN",
        "System Volume Information", "AppData", "obj", ".cache",
    };

    private readonly IndexStore _store;
    private readonly Settings _settings;
    private readonly Dispatcher _ui;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ConcurrentQueue<(string path, bool tree)> _pending = new();
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private Timer? _debounce;
    private volatile bool _fullRescan;

    public event Action<string>? Status;
    public bool IsScanning { get; private set; }

    public Indexer(IndexStore store, Settings settings, Dispatcher ui)
    {
        _store = store; _settings = settings; _ui = ui;
    }

    public void Start()
    {
        StopWatchers();
        foreach (var root in _settings.EffectiveRoots())
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                w.Created += (_, e) => Enqueue(e.FullPath);
                w.Changed += (_, e) => { if (!Directory.Exists(e.FullPath)) Enqueue(e.FullPath); };
                w.Deleted += (_, e) => Enqueue(e.FullPath);
                w.Renamed += (_, e) => { Enqueue(e.OldFullPath); Enqueue(e.FullPath); };
                w.Error += (_, _) => { _fullRescan = true; Kick(); };
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex) { App.Log($"watch {root}: {ex.Message}"); }
        }
        _ = FullScanAsync();
    }

    /// <summary>원본 보관함처럼 나중에 생긴 감시 폴더가 있으면 감시를 다시 시작한다.</summary>
    public void EnsureWatching()
    {
        var watched = _watchers.Select(w => w.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_settings.EffectiveRoots().Any(r => Directory.Exists(r) && !watched.Contains(r))) Start();
    }

    private void StopWatchers()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }

    private void Enqueue(string path)
    {
        // 제외 규칙은 감시 폴더 "안쪽" 경로에만 적용 (감시 폴더 자체가 AppData 아래일 수도 있음)
        var root = _settings.EffectiveRoots().FirstOrDefault(r => PathUtil.IsUnder(path, r));
        if (root == null || IsExcludedPath(path[root.Length..])) return;
        _pending.Enqueue((path, true));
        Kick();
    }

    private void Kick()
    {
        _debounce ??= new Timer(_ => _ = FlushAsync());
        _debounce.Change(500, Timeout.Infinite);
    }

    private async Task FlushAsync()
    {
        if (_fullRescan) { _fullRescan = false; while (_pending.TryDequeue(out _)) { } await FullScanAsync(); return; }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (_pending.TryDequeue(out var s)) paths.Add(s.path);
        if (paths.Count == 0) return;
        // 한꺼번에 수천 개가 바뀌면(대량 복사 등) 파일 하나하나 대신 상위 폴더 단위로 묶어 한 번에 검사
        if (paths.Count > 64)
        {
            var dirs = paths.Select(p => Directory.Exists(p) ? p : PathUtil.Dir(p))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d.Length).ToList();
            var top = new List<string>();
            foreach (var d in dirs) if (!top.Any(t => PathUtil.IsUnder(d, t))) top.Add(d);
            paths = top.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        await ScanAsync(paths.Select(p => (p, true)).ToList(), full: false);
    }

    public Task FullScanAsync() =>
        ScanAsync(_settings.EffectiveRoots().Where(Directory.Exists).Select(r => (r, true)).ToList(), full: true);

    private sealed record Snap(long Id, string Path, long Size, DateTime Modified, uint Vol, ulong Fid, bool Linked);
    private sealed record Seen(string Path, long Size, DateTime Modified, uint Vol, ulong Fid);

    /// <param name="scopes">(경로, 하위 전체) — 경로가 파일이면 그 파일만, 폴더면 그 아래 전체.</param>
    private async Task ScanAsync(List<(string path, bool tree)> scopes, bool full)
    {
        await _scanLock.WaitAsync();
        try
        {
            IsScanning = true;
            if (full) _ui.Invoke(() => Status?.Invoke("색인 확인 중…"));
            var roots = _settings.EffectiveRoots();

            // 1) 현재 색인 상태 스냅숏 (UI 스레드)
            var snapshot = await _ui.InvokeAsync(() =>
                _store.Files.Select(f => new Snap(f.Id, f.Path, f.Size, f.Modified, f.Volume, f.FileId, f.Folders.Count > 0)).ToList());

            // 2) 디스크 검사 (백그라운드)
            var (changed, missing) = await Task.Run(() =>
            {
                var known = snapshot.ToDictionary(s => s.Path, StringComparer.OrdinalIgnoreCase);
                var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var changedList = new List<Seen>();

                foreach (var (path, _) in scopes)
                {
                    foreach (var (p, size, mt) in Enumerate(path))
                    {
                        seenPaths.Add(p);
                        if (known.TryGetValue(p, out var k) && k.Size == size && k.Modified == mt && k.Fid != 0) continue;
                        FileIdentity.TryGet(p, out var vol, out var fid);
                        changedList.Add(new Seen(p, size, mt, vol, fid));
                    }
                }

                // 검사 범위 안에 있었는데 이번에 안 보인 파일 = 삭제되었거나 옮겨짐
                bool InScope(string p) => scopes.Any(s => PathUtil.IsUnder(p, s.path));
                var gone = snapshot.Where(s => !seenPaths.Contains(s.Path) && (InScope(s.Path)
                        // 전체 검사 때는 감시 범위 밖에 있는 색인 파일도 살아 있는지 확인
                        || (full && !roots.Any(r => PathUtil.IsUnder(s.Path, r)) && !File.Exists(s.Path))))
                    .ToList();

                var movedIds = changedList.Where(c => c.Fid != 0).Select(c => (c.Vol, c.Fid)).ToHashSet();
                var missingList = new List<(Snap snap, string? newPath)>();
                foreach (var g in gone)
                {
                    if (g.Fid != 0 && movedIds.Contains((g.Vol, g.Fid))) continue; // 범위 안에서 이동 → Upsert가 처리
                    string? resolved = null;
                    if (g.Linked && g.Fid != 0) resolved = FileIdentity.Resolve(g.Vol, g.Fid);
                    missingList.Add((g, resolved));
                }
                return (changedList, missingList);
            });

            // 3) 적용 (UI 스레드, 한 트랜잭션)
            await _ui.InvokeAsync(() =>
            {
                _store.Batch(() =>
                {
                    foreach (var c in changed) _store.Upsert(c.Path, c.Vol, c.Fid, c.Size, c.Modified);
                    foreach (var (snap, newPath) in missing)
                    {
                        var rec = _store.File(snap.Id);
                        if (rec == null || !rec.Path.Equals(snap.Path, StringComparison.OrdinalIgnoreCase)) continue;
                        if (File.Exists(rec.Path)) continue; // 그 사이 다시 생김
                        if (newPath != null && File.Exists(newPath)) { _store.UpdatePath(rec, newPath); continue; }
                        if (rec.Folders.Count > 0) _store.SetMissing(rec, true); // 색인 연결은 남겨 두고 "원본 없음" 표시
                        else _store.Remove(rec);
                    }
                    // 감시 범위에서 빠진, 어디에도 연결되지 않은 파일 정리
                    if (full)
                        foreach (var f in _store.Files.Where(f => f.Folders.Count == 0 && !roots.Any(r => PathUtil.IsUnder(f.Path, r))).ToList())
                            _store.Remove(f);
                });
                Status?.Invoke($"파일 {_store.FileCount:N0}개 색인됨");
            });
        }
        catch (Exception ex)
        {
            App.Log("scan: " + ex);
        }
        finally
        {
            IsScanning = false;
            _scanLock.Release();
        }
    }

    private static bool IsExcludedPath(string path)
    {
        foreach (var part in path.Split('\\'))
            if (ExcludedDirs.Contains(part)) return true;
        var name = Path.GetFileName(path);
        return name.StartsWith("~$", StringComparison.Ordinal) || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<(string path, long size, DateTime mtime)> Enumerate(string path)
    {
        if (File.Exists(path))
        {
            FileInfo fi;
            try { fi = new FileInfo(path); } catch { yield break; }
            if ((fi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 || IsExcludedPath(Path.GetFileName(path))) yield break;
            yield return (fi.FullName, fi.Length, fi.LastWriteTimeUtc);
            yield break;
        }
        if (!Directory.Exists(path)) yield break;

        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };
        var e = new FileSystemEnumerable<(string, long, DateTime)>(path,
            (ref FileSystemEntry x) => (x.ToFullPath(), x.Length, x.LastWriteTimeUtc.UtcDateTime), opts)
        {
            ShouldIncludePredicate = (ref FileSystemEntry x) => !x.IsDirectory && !x.FileName.StartsWith("~$")
                                                                && !x.FileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
                                                                && !x.FileName.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase),
            ShouldRecursePredicate = (ref FileSystemEntry x) => !ExcludedDirs.Contains(x.FileName.ToString()),
        };
        foreach (var item in e) yield return item;
    }

    public void Dispose()
    {
        StopWatchers();
        _debounce?.Dispose();
    }
}
