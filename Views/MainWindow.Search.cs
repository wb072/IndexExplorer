using System.IO;
using IndexExplorer.Engine;
using IndexExplorer.Native;
using IndexExplorer.Services;
using IndexExplorer.ViewModels;

namespace IndexExplorer.Views;

/// <summary>
/// 검색 범위 (탐색기와 같음): 원본 폴더를 보다가 찾으면 그 폴더와 하위 폴더 안에서만, 색인 폴더를 보다가 찾으면
/// 그 색인과 하위 색인 안에서만 찾는다. 그 밖(미분류 함·모든 파일 …)에서는 전체.
/// 디스크를 훑지 않는다: 색인할 폴더 안이면 메모리 목록에서 바로, 그 밖의 원본 폴더는 색인 엔진에서 (엔진이 없을 때만 직접 훑는다).
/// 검색 위치(Location.Search)에 범위를 담는다: Path = 원본 폴더, FolderId = 색인 폴더, 둘 다 없으면 전체.
/// </summary>
public partial class MainWindow
{
    /// <summary>"모든 곳에서 찾기"를 눌렀음 (검색 칸을 비우면 풀린다).</summary>
    private bool _searchEverywhere;

    // 색인할 폴더 밖 원본 폴더 검색 결과 (엔진·디스크에서 비동기로 받는다)
    private string? _outsideKey;
    private List<ListItem>? _outsideResult;
    private CancellationTokenSource? _outsideCts;

    private static bool ScopedLoc(Location l) => l.Kind is LocKind.Dir or LocKind.IndexFolder;

    private Location SearchLocation(string q, Location? from) =>
        !_searchEverywhere && from is { } f && ScopedLoc(f)
            ? new Location(LocKind.Search, f.Kind == LocKind.IndexFolder ? f.FolderId : 0, f.Kind == LocKind.Dir ? f.Path : "", q)
            : new Location(LocKind.Search, Query: q);

    private string? SearchScopeName(Location l) =>
        l.Path.Length > 0 ? DirLeaf(l.Path) : l.FolderId > 0 ? _store.Folder(l.FolderId)?.Name : null;

    /// <summary>검색 칸 안내 글자: 탐색기처럼 "‘폴더’에서 검색".</summary>
    private void UpdateSearchHint()
    {
        var at = _loc.Kind == LocKind.Search ? _beforeSearch : _loc;
        var name = at is { } a && ScopedLoc(a) && !_searchEverywhere
            ? (a.Kind == LocKind.Dir ? DirLeaf(a.Path) : _store.Folder(a.FolderId)?.Name)
            : null;
        SearchHint.Text = name != null ? $"‘{name}’에서 검색" : "모든 파일·색인 검색";
    }

    private void SetSearchEverywhere(bool all)
    {
        _searchEverywhere = all;
        UpdateSearchHint();
        if (_loc.Kind == LocKind.Search) RunSearch();
    }

    private void BuildSearchItems(List<ListItem> list)
    {
        var q = _loc.Query;
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool NameHas(string name) => words.All(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

        if (_loc.FolderId > 0)
        {
            // 이 색인과 하위 색인 안에서
            var ids = _store.Descendants(_loc.FolderId).Append(_loc.FolderId).ToHashSet();
            list.AddRange(_store.SearchFolders(q).Where(f => f.Id != _loc.FolderId && ids.Contains(f.Id)).Select(f => FolderItem(f, withLocation: true)));
            list.AddRange(_store.SearchFiles(q).Where(f => f.Folders.Overlaps(ids)).Take(5000).Select(FileItem));
            return;
        }
        if (_loc.Path.Length > 0)
        {
            var dir = _loc.Path;
            if (_settings.EffectiveRoots().Any(r => PathUtil.IsUnder(dir, r)))
            {
                // 색인할 폴더 안: 메모리 목록에서 바로 (하위 폴더 이름도 파일 경로에서 찾는다)
                var files = _store.Files.Where(f => PathUtil.IsUnder(f.Path, dir) && !f.Path.Equals(dir, StringComparison.OrdinalIgnoreCase)).ToList();
                var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in files)
                {
                    for (var d = f.Dir; d.Length > dir.Length && PathUtil.IsUnder(d, dir); d = PathUtil.Dir(d))
                        if (!dirs.Add(d)) break;
                }
                list.AddRange(dirs.Where(d => NameHas(DirLeaf(d))).Take(1000).Select(d => new ListItem
                {
                    Kind = ItemKind.Dir, Path = d, Name = DirLeaf(d), TypeName = "파일 폴더", Location = PathUtil.Dir(d),
                }));
                list.AddRange(files.Where(f => NameHas(f.Name)).Take(5000).Select(FileItem));
                return;
            }
            // 색인할 폴더 밖: 엔진(또는 직접)에서 받아 둔 결과
            var key = dir + "|" + q;
            if (_outsideKey == key && _outsideResult != null) { list.AddRange(_outsideResult); return; }
            StartOutsideSearch(key, dir, q, words);
            return;
        }
        list.AddRange(_store.SearchFolders(q).Select(f => FolderItem(f, withLocation: true)));
        list.AddRange(_store.SearchFiles(q).Take(5000).Select(FileItem));
    }

    private bool OutsideSearching => _outsideCts != null;

    private async void StartOutsideSearch(string key, string dir, string q, string[] words)
    {
        if (_outsideKey == key && _outsideCts != null) return; // 이미 찾는 중
        _outsideCts?.Cancel();
        var cts = _outsideCts = new CancellationTokenSource();
        _outsideKey = key;
        _outsideResult = null;
        List<Hit>? hits = null;
        try
        {
            if (_engineUp)
            {
                var r = await App.Engine.AskAsync(new EngineProtocol.Request { Op = "query", Kind = FileKinds.None, Text = q, IncludeSystem = true, Max = 50_000 }, 30_000);
                if (r?.Items != null)
                    hits = r.Items.Where(i => PathUtil.IsUnder(i.Path, dir)).Take(5000)
                        .Select(i => new Hit(i.Path, false, i.ModifiedUtcTicks > 0 ? new DateTime(i.ModifiedUtcTicks, DateTimeKind.Utc) : null, i.Size)).ToList();
            }
            // 엔진이 없으면 직접 훑는다 (탐색기와 같은 방식이라 느릴 수 있다)
            hits ??= await Task.Run(() => WalkSearch(dir, words, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            _outsideResult = hits.Select(h =>
            {
                var rec = h.IsDir ? null : _store.ByPath(h.Path);
                return new ListItem
                {
                    Kind = h.IsDir ? ItemKind.Dir : ItemKind.File, Path = h.Path, Name = Path.GetFileName(h.Path), Record = rec,
                    Modified = h.Modified, Size = h.Size, TypeName = ShellImages.TypeName(h.Path, h.IsDir), Location = PathUtil.Dir(h.Path),
                    IndexText = rec != null ? IndexNames(rec) : "",
                };
            }).ToList();
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { App.Log("search outside: " + ex.Message); _outsideResult = []; }
        finally { if (_outsideCts == cts) _outsideCts = null; }
        if (_loc.Kind == LocKind.Search && _loc.Path + "|" + _loc.Query == key) LoadItems(reset: false);
    }

    private sealed record Hit(string Path, bool IsDir, DateTime? Modified, long? Size);

    /// <summary>엔진이 없을 때만: 폴더를 직접 훑는다 (백그라운드, 화면 처리는 하지 않는다).</summary>
    private static List<Hit> WalkSearch(string dir, string[] words, CancellationToken ct)
    {
        var list = new List<Hit>();
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.System };
        foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts))
        {
            ct.ThrowIfCancellationRequested();
            if (!words.All(w => e.Name.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(new Hit(e.FullName, e is DirectoryInfo, e.LastWriteTimeUtc, (e as FileInfo)?.Length));
            if (list.Count >= 5000) break;
        }
        return list;
    }
}
