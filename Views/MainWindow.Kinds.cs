using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    /// <summary>엔진의 기능 수준. 2 미만(예전 엔진)이면 사용자가 정한 종류를 모르므로 그 종류는 색인한 폴더 안에서만 보여 준다.</summary>
    private static int _engineProto;

    private (byte kind, int count, List<EngineHost.Item> items)? _kindResult;
    private bool _kindLoading;
    /// <summary>마지막 엔진 조회가 실패함 (다시 그릴 때 또 묻지 않는다. F5·다른 종류로 가면 다시 묻는다).</summary>
    private byte? _kindFailed;

    public const int KindQueryMax = 20000;

    private static IEnumerable<FileKinds.Kind> ShownKinds => FileKinds.Current.Where(k => !k.Hidden);

    /// <summary>이 종류를 엔진이 PC 전체에서 찾아 줄 수 있나 (예전 엔진은 기본 종류만, 기본 확장자로).</summary>
    private static bool EngineServes(byte kind) => _engineUp && (_engineProto >= EngineProtocol.CurrentProto || kind < FileKinds.Presets[0].Id);

    /// <summary>엔진을 새 버전으로 바꿔야 설정한 종류가 PC 전체에 적용된다.</summary>
    private static bool EngineOutdated => _engineUp && _engineProto < EngineProtocol.CurrentProto;

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
        var r = await App.Engine.AskAsync(new EngineProtocol.Request { Op = "counts", Excludes = _settings.KindExcludes, Kinds = FileKinds.CurrentDefs }, 3000);
        var up = r is { Ok: true, Counts: not null };
        var wasUp = _engineUp;
        _engineUp = up;
        if (up)
        {
            _engineCounts = r!.Counts;
            _engineAllCounts = r.AllCounts;
            _engineProto = r.Proto;
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
        int[]? local = null;
        if (!_engineUp || _engineCounts == null || EngineOutdated)
        {
            // 엔진이 없으면(또는 예전 엔진이 모르는 종류는) 색인한 파일을 한 번만 훑어 모든 종류를 같이 센다
            local = new int[256];
            foreach (var f in _store.Files)
                if (!f.Missing && !LocallyExcluded(f.Path)) local[FileKinds.Of(f.Name)]++;
        }
        foreach (var (id, node) in _kindNodes)
        {
            var n = EngineServes(id) && _engineCounts != null ? (id < _engineCounts.Length ? _engineCounts[id] : 0) : local![id];
            node.Badge = n > 0 ? n.ToString("N0") : "";
        }
    }

    private async void RequestKindItems(bool force = false)
    {
        var kind = (byte)_loc.FolderId;
        if (!EngineServes(kind) || _kindLoading) return;
        if (!force && _kindResult is { } cur && cur.kind == kind) return;
        if (!force && _kindFailed == kind) return;
        _kindLoading = true;
        try
        {
            var resp = await App.Engine.AskAsync(new EngineProtocol.Request
            {
                Op = "query", Kind = kind, Max = KindQueryMax, IncludeSystem = _kindShowAll, Excludes = _settings.KindExcludes, Kinds = FileKinds.CurrentDefs,
            }, 30000);
            if (resp is not { Ok: true, Items: not null })
            {
                // 실패해도 "찾는 중"에 멈춰 있지 않게: 알리고 화면을 다시 그린다 (F5로 다시 시도)
                _kindFailed = kind;
                _kindLoading = false;
                if (_loc.Kind == LocKind.FileKind && (byte)_loc.FolderId == kind) LoadItems(reset: false);
                return;
            }
            _kindFailed = null;
            var count = CurrentKindCount(kind);
            _kindResult = (kind, count, resp.Items);
            _kindLoading = false;
            if (_loc.Kind == LocKind.FileKind && (byte)_loc.FolderId == kind) LoadItems(reset: false);
        }
        finally { _kindLoading = false; }
    }

    /// <summary>파일 종류 목록: 엔진 결과(PC 전체) 또는 색인한 폴더 안.</summary>
    private void BuildKindItems(List<ListItem> list)
    {
        var kind = (byte)_loc.FolderId;
        if (EngineServes(kind))
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

    /// <summary>사이드바 "파일 종류" 우클릭: 편집·숨기기 (종류 위에서), 새 종류, 설정.</summary>
    private ContextMenu KindMenu(byte? kindId)
    {
        var menu = new ContextMenu();
        void Add(string header, string glyph, Action act)
        {
            var mi = new MenuItem { Header = header, Icon = new TextBlock { Text = glyph, Style = (Style)FindResource("Glyph"), FontSize = 13, Foreground = (Brush)FindResource("AccentBrush") } };
            mi.Click += (_, _) => act();
            menu.Items.Add(mi);
        }
        if (kindId is { } id && _settings.Kinds.FirstOrDefault(k => k.Id == id) is { } ks)
        {
            Add("이름·확장자 편집…", "", () => EditKindSetting(ks, isNew: false));
            Add("목록에서 숨기기", "", () => { ks.Enabled = false; ApplyKindSettings(); });
            menu.Items.Add(new Separator());
        }
        Add("새 파일 종류…", "", () =>
        {
            var used = _settings.Kinds.Select(k => (int)k.Id).ToHashSet();
            var nid = Enumerable.Range(FileKinds.FirstCustomId, 255 - FileKinds.FirstCustomId).FirstOrDefault(n => !used.Contains(n));
            if (nid > 0) EditKindSetting(new KindSetting { Id = (byte)nid, Enabled = true }, isNew: true);
        });
        Add("파일 종류 설정…", "", () => OpenSettings("kinds"));
        return menu;
    }

    /// <summary>사이드바에서 바로 종류 하나를 편집·추가하고 곧바로 적용.</summary>
    private void EditKindSetting(KindSetting ks, bool isNew)
    {
        var copy = ks.Clone();
        var w = new KindEditWindow(copy, _settings.Kinds, isNew) { Owner = this };
        if (w.ShowDialog() != true) return;
        if (w.Deleted) _settings.Kinds.RemoveAll(k => k.Id == ks.Id);
        else if (isNew) _settings.Kinds.Add(copy);
        else { ks.Name = copy.Name; ks.Exts = copy.Exts; }
        ApplyKindSettings();
        if (isNew && !w.Deleted) Navigate(new Location(LocKind.FileKind, copy.Id));
    }

    private void ApplyKindSettings()
    {
        _settings.Save();
        FileKinds.SetCurrent(_settings.EnabledKinds());
        foreach (var mw in Application.Current.Windows.OfType<MainWindow>()) mw.OnKindsChanged();
    }

    /// <summary>설정에서 파일 종류가 바뀜: 사이드바를 다시 만들고, 지금 보는 종류가 꺼졌으면 "모든 파일"로.</summary>
    public void OnKindsChanged()
    {
        _kindResult = null;
        _kindFailed = null;
        _engineCounts = null;
        _engineAllCounts = null;
        if (_loc.Kind == LocKind.FileKind && FileKinds.ById((byte)_loc.FolderId) is null)
            Navigate(new Location(LocKind.AllFiles));
        BuildIndexTree();
        SyncTreeSelection();
        _ = PollEngine();
        LoadItems(reset: false);
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
            if (_settings.Kinds.FirstOrDefault(s => s.Id == k.Id) is { } ks)
                Buttons(ActionButton("이름·확장자 편집…", () => EditKindSetting(ks, isNew: false)));
        }
        Label("찾는 범위");
        if (EngineServes((byte)_loc.FolderId))
        {
            Badge($"PC 전체 · {_engineFiles}", true);
            if (EngineOutdated)
            {
                Para("색인 엔진이 예전 버전이라 직접 바꾼 확장자가 PC 전체에는 아직 적용되지 않습니다. 설정에서 엔진을 업데이트하세요.");
                Buttons(ActionButton("엔진 업데이트…", () => OpenSettings("engine"), primary: true));
            }
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
                if (_settings.KindExcludes.Count > 0) buttons.Add(ActionButton($"뺀 폴더 {_settings.KindExcludes.Count}개…", () => OpenSettings("kinds")));
                if (buttons.Count > 0) Buttons(buttons.ToArray());
            }
        }
        else if (_engineUp)
        {
            // 예전 엔진이 모르는 (새로 켠·만든) 종류
            Badge("색인한 폴더 안에서만", false);
            Para("이 종류를 PC 전체에서 찾으려면 색인 엔진을 새 버전으로 업데이트하세요. 관리자 승인이 한 번 필요합니다.");
            Buttons(ActionButton("엔진 업데이트…", () => OpenSettings("engine"), primary: true));
        }
        else
        {
            Badge("색인한 폴더 안에서만", false);
            Para("PC 전체에서 찾으려면 색인 엔진을 설치하세요. 디스크 장부를 직접 읽어 모든 드라이브의 파일을 몇 초 만에 모읍니다.");
            Buttons(ActionButton("색인 엔진 설치…", () => OpenSettings("engine"), primary: true));
        }
        Para("정리하려면 파일을 왼쪽 색인 폴더로 끌어다 놓으세요. 원본은 그대로 있습니다.");
    }
}
