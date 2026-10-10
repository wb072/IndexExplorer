using System.IO;
using System.Windows;
using System.Windows.Media;
using IndexExplorer.Engine;
using IndexExplorer.Native;
using IndexExplorer.Services;
using IndexExplorer.ViewModels;

namespace IndexExplorer.Views;

/// <summary>
/// 중복 파일: 내용이 같은 파일 묶음을 아낄 수 있는 용량이 큰 순서로 보여 준다.
/// 범위는 파일 종류 보기와 같다 (엔진이 있으면 PC 전체 사용자 파일, 없으면 색인한 폴더 안, 잡동사니 제외).
/// 지우기는 사용자가 직접 (선택 도우미 + Delete = 휴지통). 지우는 사본의 색인 연결은 남기는 파일로 옮긴다.
/// </summary>
public partial class MainWindow
{
    private TreeNode _dupNode = null!;

    // 결과는 창끼리 공유 (오래 걸리는 작업이라 한 번 찾은 것을 같이 쓴다)
    private static List<DuplicateFinder.Group>? _dupGroups;
    private static DateTime _dupFoundAt;
    private static bool _dupRunning;
    private static string _dupStatus = "";
    private static CancellationTokenSource? _dupCts;
    private static event Action? DupChanged;

    private void InitDuplicates() => DupChanged += OnDupChanged;

    private void OnDupChanged()
    {
        UpdateDupBadge();
        if (_loc.Kind == LocKind.Duplicates) { LoadItems(reset: false); }
    }

    private void UpdateDupBadge()
    {
        if (_dupNode == null) return;
        _dupNode.Badge = _dupRunning ? "…" : _dupGroups is { Count: > 0 } g ? g.Count.ToString("N0") : "";
    }

    /// <summary>중복 찾기 시작 (이미 돌고 있으면 그대로).</summary>
    private async void StartDuplicateScan()
    {
        if (_dupRunning) return;
        _dupRunning = true;
        _dupStatus = "파일 목록 모으는 중…";
        _dupCts = new CancellationTokenSource();
        DupChanged?.Invoke();
        try
        {
            var candidates = await CollectCandidates();
            _dupStatus = $"후보 {candidates.Count:N0}개 비교 준비";
            DupChanged?.Invoke();
            var finder = new DuplicateFinder(_store);
            var progress = new Progress<DuplicateFinder.Progress>(p =>
            {
                _dupStatus = $"{p.Stage} {p.Done:N0} / {p.Total:N0}";
                if (_loc.Kind == LocKind.Duplicates) UpdatePreview();
            });
            _dupGroups = await finder.FindAsync(candidates, progress, _dupCts.Token);
            _dupFoundAt = DateTime.Now;
            _dupStatus = "";
        }
        catch (OperationCanceledException) { _dupStatus = "멈췄습니다"; }
        catch (Exception ex) { _dupStatus = "찾지 못했습니다: " + ex.Message; App.Log("dup: " + ex); }
        finally
        {
            _dupRunning = false;
            DupChanged?.Invoke();
        }
    }

    /// <summary>비교할 파일: 파일 종류 보기와 같은 범위 (시스템 파일 종류는 뺀다).</summary>
    private async Task<List<DuplicateFinder.Candidate>> CollectCandidates()
    {
        var list = new List<DuplicateFinder.Candidate>();
        if (_engineUp)
        {
            foreach (var k in ShownKinds)
            {
                var r = await App.Engine.AskAsync(new EngineProtocol.Request
                {
                    Op = "query", Kind = k.Id, Max = 200_000, Excludes = _settings.KindExcludes, Kinds = FileKinds.CurrentDefs,
                }, 120_000);
                if (r?.Items == null) continue;
                list.AddRange(r.Items.Select(i => new DuplicateFinder.Candidate(i.Path, i.Size, i.ModifiedUtcTicks, i.Attr)));
            }
            if (list.Count > 0) return list;
        }
        // 엔진이 없으면 색인한 폴더 안
        list.AddRange(_store.Files.Where(f => !f.Missing && !LocallyExcluded(f.Path))
            .Select(f => new DuplicateFinder.Candidate(f.Path, f.Size, f.Modified.Ticks, 0)));
        return list;
    }

    private void BuildDuplicateItems(List<ListItem> list)
    {
        if (_dupGroups == null) { if (!_dupRunning) StartDuplicateScan(); return; }
        var rank = 0;
        foreach (var g in _dupGroups)
        {
            var live = g.Files.Where(f => File.Exists(f.Path)).ToList();
            if (live.Count < 2) continue;
            rank++;
            var first = Path.GetFileName(live[0].Path);
            var key = $"{rank}. {first} · {ListItem.FormatBytes(g.Size)}씩 · 아낄 수 있는 용량 {ListItem.FormatBytes(g.Size * (live.Count - 1))}";
            foreach (var f in live)
            {
                var rec = _store.ByPath(f.Path);
                list.Add(new ListItem
                {
                    Kind = ItemKind.File, Path = f.Path, Name = Path.GetFileName(f.Path), Record = rec,
                    Modified = f.ModifiedTicks > 0 ? new DateTime(f.ModifiedTicks, DateTimeKind.Utc) : null, Size = f.Size,
                    TypeName = ShellImages.TypeName(f.Path, false), Location = PathUtil.Dir(f.Path),
                    IndexText = rec != null ? IndexNames(rec) : "", DupKey = key, DupRank = rank,
                });
            }
        }
    }

    /// <summary>묶음마다 하나만 남기고 나머지를 선택 (지우기는 사용자가 Delete 로).</summary>
    private void SelectAllButOne(Func<List<ListItem>, ListItem> keep, string how)
    {
        var pick = new List<ListItem>();
        foreach (var g in _items.Where(i => i.DupRank != null).GroupBy(i => i.DupRank))
        {
            var files = g.ToList();
            var kept = keep(files);
            pick.AddRange(files.Where(f => f != kept));
        }
        List.SelectMany(pick); // 수천 개도 한 번의 선택 변경으로
        var n = pick.Count;
        UpdateStatus();
        Toast($"{how} 남기고 {n:N0}개를 선택했습니다", "Delete 를 누르면 휴지통으로 보냅니다. 지우는 파일의 색인 연결은 남기는 파일로 옮겨집니다.");
    }

    private static ListItem Oldest(List<ListItem> g) => g.OrderBy(i => i.Modified ?? DateTime.MaxValue).First();
    private static ListItem Newest(List<ListItem> g) => g.OrderByDescending(i => i.Modified ?? DateTime.MinValue).First();
    private static ListItem Indexed(List<ListItem> g) =>
        g.Where(i => i.Record is { Folders.Count: > 0 }).OrderByDescending(i => i.Record!.Folders.Count).FirstOrDefault() ?? Oldest(g);

    /// <summary>중복 보기에서 지우기 전에: 지우는 사본의 색인 연결을 같은 묶음에서 남는 파일로 옮긴다.</summary>
    private void MoveLinksToKeptDuplicates(IReadOnlyCollection<ListItem> deleting)
    {
        if (_loc.Kind != LocKind.Duplicates) return;
        var del = deleting.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _store.Batch(() =>
        {
            foreach (var g in _items.Where(i => i.DupRank != null).GroupBy(i => i.DupRank))
            {
                var kept = g.FirstOrDefault(i => !del.Contains(i.Path));
                if (kept == null) continue;
                var folders = g.Where(i => del.Contains(i.Path) && i.Record != null).SelectMany(i => i.Record!.Folders).Distinct().ToList();
                if (folders.Count == 0) continue;
                if ((kept.Record ?? _store.ByPath(kept.Path) ?? _store.UpsertFromDisk(kept.Path)) is not { } keptRec) continue;
                foreach (var fid in folders) _store.Link(fid, keptRec);
            }
        });
    }

    private void ShowDuplicatesInfo()
    {
        SetPreviewImage((ImageSource)FindResource("IconDuplicates"), true);
        PvTitle.Text = "중복 파일";
        var groups = _items.Where(i => i.DupRank != null).GroupBy(i => i.DupRank).ToList();
        var waste = groups.Sum(g => (g.First().Size ?? 0) * (g.Count() - 1));
        PvSub.Text = _dupRunning ? "찾는 중…" : $"같은 파일 묶음 {groups.Count:N0}개 · 아낄 수 있는 용량 {ListItem.FormatBytes(waste)}";
        if (_dupRunning)
        {
            Label("진행");
            Value(_dupStatus);
            Para("크기가 같은 파일만 골라 앞뒤 일부를 먼저 비교하고, 같으면 전체 내용을 비교합니다. 한 번 비교한 파일은 기억해 다음에는 빠릅니다.");
            Buttons(ActionButton("멈추기", () => _dupCts?.Cancel()));
            return;
        }
        Label("찾는 범위");
        Badge(_engineUp ? "PC 전체 사용자 파일 (잡동사니 제외)" : "색인한 폴더 안", _engineUp);
        if (_dupFoundAt != default) { Label("찾은 때"); Value(_dupFoundAt.ToString("yyyy-MM-dd tt h:mm")); }
        if (!string.IsNullOrEmpty(_dupStatus)) Para(_dupStatus);
        if (groups.Count > 0)
        {
            Label("정리 도우미 (선택만 합니다)");
            Buttons(
                ActionButton("가장 오래된 것만 남기기", () => SelectAllButOne(Oldest, "가장 오래된 것만")),
                ActionButton("가장 최근 것만 남기기", () => SelectAllButOne(Newest, "가장 최근 것만")),
                ActionButton("색인에 넣은 것 남기기", () => SelectAllButOne(Indexed, "색인에 넣은 것을")));
            Para("선택한 뒤 Delete 를 누르면 휴지통으로 보냅니다. 지우는 파일이 색인 폴더에 연결되어 있었다면 그 연결은 남기는 파일로 옮겨집니다.");
        }
        Buttons(ActionButton("다시 찾기", () => { _dupGroups = null; StartDuplicateScan(); }, primary: groups.Count == 0));
    }
}
