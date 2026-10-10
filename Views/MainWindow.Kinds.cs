using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using IndexExplorer.Engine;
using IndexExplorer.Native;
using IndexExplorer.Services;
using IndexExplorer.ViewModels;

namespace IndexExplorer.Views;

/// <summary>
/// 파일 종류 보기 (엑셀만, 이미지만 …).
/// 엔진 서비스가 있으면 PC 전체(모든 NTFS 드라이브)에서, 없으면 색인한 폴더 안에서 보여 준다.
/// 엔진은 2초마다 개수만 묻고, 지금 보는 종류의 개수가 달라졌을 때(또는 F5) 목록을 다시 받는다.
/// </summary>
public partial class MainWindow
{
    private TreeNode _kindsNode = null!;
    private readonly Dictionary<byte, TreeNode> _kindNodes = [];
    private DispatcherTimer? _engineTimer;

    // 엔진 상태는 창끼리 공유
    private static bool _engineUp;
    private static int[]? _engineCounts;
    private static int[]? _engineAllCounts;
    /// <summary>모두 보기 (자동으로 숨긴 잡동사니·직접 뺀 폴더도 보임).</summary>
    private bool _kindShowAll;
    private static string _engineFiles = "";

    private (byte kind, int count, List<EngineHost.Item> items)? _kindResult;
    private bool _kindLoading;

    public const int KindQueryMax = 20000;

    private static IEnumerable<FileKinds.Kind> ShownKinds => FileKinds.All.Where(k => !k.Hidden);

    private ImageSource? KindIcon(byte id) =>
        FileKinds.ById(id) is { } k ? ShellImages.Icon("x" + k.SampleExt, false) : null;

    private TreeNode BuildKindsNode(bool expanded)
    {
        var root = new TreeNode { Kind = NodeKind.KindsRoot, Name = "파일 종류", Icon = (ImageSource)FindResource("IconAllFiles"), IsExpanded = expanded };
        _kindNodes.Clear();
        foreach (var k in ShownKinds)
        {
            var n = new TreeNode { Kind = NodeKind.FileKind, FolderId = k.Id, Name = k.Name, Parent = root, Icon = KindIcon(k.Id) };
            root.Children.Add(n);
            _kindNodes[k.Id] = n;
        }
        return root;
    }

    private TreeNode? KindNode(long id) => _kindNodes.GetValueOrDefault((byte)id);

    private void StartEnginePolling()
    {
        _engineTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, async (_, _) => await PollEngine(), Dispatcher);
        _engineTimer.Start();
        _ = PollEngine();
    }

    private async Task PollEngine()
    {
        var r = await App.Engine.AskAsync(new EngineProtocol.Request { Op = "counts", Excludes = _settings.KindExcludes }, 3000);
        var up = r is { Ok: true, Counts: not null };
        var wasUp = _engineUp;
        _engineUp = up;
        if (up)
        {
            _engineCounts = r!.Counts;
            _engineAllCounts = r.AllCounts;
            _engineFiles = $"파일 {r.Files:N0}개";
        }
        UpdateKindBadges();
        if (_loc.Kind != LocKind.FileKind) return;
        var kind = (byte)_loc.FolderId;
        // 엔진이 새로 켜졌거나, 지금 보는 종류의 개수가 달라졌으면 목록을 다시
        if (up != wasUp || (up && _kindResult is { } res && res.kind == kind && CurrentKindCount(kind) != res.count))
            RequestKindItems(force: true);
        if (!up && wasUp) LoadItems(reset: false);
    }

    private int CurrentKindCount(byte kind) =>
        (_kindShowAll ? _engineAllCounts : _engineCounts) is { } c && kind < c.Length ? c[kind] : 0;

    private bool LocallyExcluded(string path) =>
        !_kindShowAll && _settings.KindExcludes.Any(x => PathUtil.IsUnder(path, x));

    private int LocalKindCount(byte id) =>
        _store.Files.Count(f => !f.Missing && FileKinds.Of(f.Name) == id && !LocallyExcluded(f.Path));

    private void UpdateKindBadges()
    {
        if (_kindNodes.Count == 0) return;
        foreach (var (id, node) in _kindNodes)
        {
            var n = _engineUp && _engineCounts != null && id < _engineCounts.Length ? _engineCounts[id] : LocalKindCount(id);
            node.Badge = n > 0 ? n.ToString("N0") : "";
        }
    }

    private async void RequestKindItems(bool force = false)
    {
        if (!_engineUp || _kindLoading) return;
        var kind = (byte)_loc.FolderId;
        if (!force && _kindResult is { } cur && cur.kind == kind) return;
        _kindLoading = true;
        try
        {
            var resp = await App.Engine.AskAsync(new EngineProtocol.Request
            {
                Op = "query", Kind = kind, Max = KindQueryMax, IncludeSystem = _kindShowAll, Excludes = _settings.KindExcludes,
            }, 30000);
            if (resp is not { Ok: true, Items: not null }) return;
            var count = CurrentKindCount(kind);
            _kindResult = (kind, count, resp.Items);
            if (_loc.Kind == LocKind.FileKind && (byte)_loc.FolderId == kind) LoadItems(reset: false);
        }
        finally { _kindLoading = false; }
    }

    /// <summary>파일 종류 목록: 엔진 결과(PC 전체) 또는 색인한 폴더 안.</summary>
    private void BuildKindItems(List<ListItem> list)
    {
        var kind = (byte)_loc.FolderId;
        if (_engineUp)
        {
            if (_kindResult is { } res && res.kind == kind)
            {
                foreach (var it in res.items)
                {
                    var rec = _store.ByPath(it.Path);
                    list.Add(new ListItem
                    {
                        Kind = ItemKind.File, Path = it.Path, Name = Path.GetFileName(it.Path), Record = rec,
                        Modified = it.ModifiedUtcTicks > 0 ? new DateTime(it.ModifiedUtcTicks, DateTimeKind.Utc) : null, Size = it.Size,
                        TypeName = ShellImages.TypeName(it.Path, false), Location = PathUtil.Dir(it.Path),
                        IndexText = rec != null ? IndexNames(rec) : "",
                    });
                }
                return;
            }
            RequestKindItems();
        }
        list.AddRange(_store.Files.Where(f => !f.Missing && FileKinds.Of(f.Name) == kind && !LocallyExcluded(f.Path)).Select(FileItem));
    }

    private void SetKindShowAll(bool all)
    {
        _kindShowAll = all;
        _kindResult = null;
        UpdateKindBadges();
        RequestKindItems(force: true);
        LoadItems(reset: false);
    }

    /// <summary>선택한 파일들의 폴더를 파일 종류 보기에서 뺀다 (그 아래 전체).</summary>
    private void ExcludeFoldersFromKinds(IEnumerable<string> dirs)
    {
        var added = 0;
        foreach (var d in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_settings.KindExcludes.Any(x => PathUtil.IsUnder(d, x))) continue;
            _settings.KindExcludes.RemoveAll(x => PathUtil.IsUnder(x, d)); // 더 넓은 폴더로 합친다
            _settings.KindExcludes.Add(d);
            added++;
        }
        if (added == 0) return;
        _settings.Save();
        _kindResult = null;
        _ = PollEngine();
        RequestKindItems(force: true);
        LoadItems(reset: false);
        Toast($"폴더 {added}개를 파일 종류에서 뺐습니다", "설정에서 되돌릴 수 있습니다. 파일은 그대로 있습니다.");
    }

    private void ShowKindInfo()
    {
        var k = FileKinds.ById((byte)_loc.FolderId);
        SetPreviewImage(k != null ? ShellImages.Icon("x" + k.SampleExt, false, 48) : null, true);
        PvTitle.Text = k?.Name ?? "파일 종류";
        PvSub.Text = $"파일 {_items.Count:N0}개" + (_items.Count >= KindQueryMax ? $" (많아서 앞 {KindQueryMax:N0}개만)" : "");
        if (k != null)
        {
            Label("확장자");
            Value(string.Join(", ", k.Exts.Select(e => "." + e)));
        }
        Label("찾는 범위");
        if (_engineUp)
        {
            Badge($"PC 전체 · {_engineFiles}", true);
            var id = (byte)_loc.FolderId;
            var hidden = (_engineAllCounts?[id] ?? 0) - (_engineCounts?[id] ?? 0);
            if (_kindShowAll)
            {
                Para("모두 보기: 프로그램·시스템 폴더, 휴지통, 숨김 파일, 직접 뺀 폴더까지 모두 보입니다.");
                Buttons(ActionButton("잡동사니 숨기기", () => SetKindShowAll(false), primary: true));
            }
            else
            {
                Para("모든 드라이브에서 찾습니다. 파일을 옮기거나 새로 만들면 곧바로 반영됩니다.");
                Label("숨긴 잡동사니");
                Value(hidden > 0 ? $"{hidden:N0}개" : "없음");
                Para("윈도우·프로그램이 설치된 폴더(dll 이 있는 곳), 점(.)으로 시작하는 설정 폴더, AppData, 휴지통, 숨김 파일, 직접 뺀 폴더는 보이지 않습니다.");
                var buttons = new List<System.Windows.Controls.Button>();
                if (hidden > 0) buttons.Add(ActionButton("모두 보기", () => SetKindShowAll(true)));
                if (_settings.KindExcludes.Count > 0) buttons.Add(ActionButton($"뺀 폴더 {_settings.KindExcludes.Count}개…", () => Settings_Click(this, new RoutedEventArgs())));
                if (buttons.Count > 0) Buttons(buttons.ToArray());
            }
        }
        else
        {
            Badge("색인한 폴더 안에서만", false);
            Para("PC 전체에서 찾으려면 색인 엔진을 설치하세요. 디스크 장부를 직접 읽어 모든 드라이브의 파일을 몇 초 만에 모읍니다.");
            Buttons(ActionButton("색인 엔진 설치…", () => Settings_Click(this, new RoutedEventArgs()), primary: true));
        }
        Para("정리하려면 파일을 왼쪽 색인 폴더로 끌어다 놓으세요. 원본은 그대로 있습니다.");
    }
}
