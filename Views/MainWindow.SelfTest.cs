using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IndexExplorer.Native;
using IndexExplorer.Services;
using IndexExplorer.ViewModels;

namespace IndexExplorer.Views;

/// <summary>
/// 자체 검증 (IndexExplorer.exe --selftest &lt;결과 폴더&gt; &lt;테스트 루트&gt;).
/// INDEXEXPLORER_DATA 로 격리된 데이터에서 핵심 기능을 실제로 실행하고 화면을 PNG로 남긴다.
/// </summary>
public partial class MainWindow
{
    private readonly List<string> _results = [];

    private void Check(string name, bool ok, string detail = "") =>
        _results.Add($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  — " + detail : "")}");

    private static async Task WaitUntil(Func<bool> cond, int ms = 8000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < ms) await Task.Delay(100);
    }

    private async Task Settle(int ms = 500)
    {
        await Task.Delay(ms);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private void Shot(string name)
    {
        UpdateLayout();
        var root = (FrameworkElement)Content;
        var dpi = VisualTreeHelper.GetDpi(this);
        var size = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, size);
            dc.DrawRectangle(new VisualBrush(root), null, size);
        }
        var rtb = new RenderTargetBitmap((int)(size.Width * dpi.DpiScaleX), (int)(size.Height * dpi.DpiScaleY),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(Path.Combine(App.SelfTestOut!, name + ".png"));
        enc.Save(fs);
    }

    internal void TestPaste() => Paste(_loc);

    /// <summary>대량 시험: 디스크 없이 합성 항목을 목록에 넣는다 (화면 처리 속도만 잰다).</summary>
    private void ShowItemsForTest(List<ListItem> list)
    {
        _items = new System.Collections.ObjectModel.ObservableCollection<ListItem>(list);
        _view = new ListCollectionView(_items) { CustomSort = new ItemComparer(_sortKey, _sortDesc) };
        ApplyGrouping();
        List.ItemsSource = _view;
        UpdateStatus();
    }
    internal List<string> TestItemNames() => _items.Select(i => i.Name).ToList();
    internal void TestShot(string name) => Shot(name);

    private ListItem? ItemNamed(string name) => _items.FirstOrDefault(i => i.Name == name);

    private void SelectNamed(string name)
    {
        List.SelectedItems.Clear();
        if (ItemNamed(name) is { } i) List.SelectedItems.Add(i);
    }

    private async Task RunSelfTest()
    {
        var T = App.SelfTestRoot!;
        var S = _settings.StorageRoot;
        try
        {
            if (!App.Engine.Disabled)
            {
                // 실제 엔진 서비스로 화면 확인 (PC 전체 파일 종류 보기)
                await WaitUntil(() => _engineUp, 10000);
                Navigate(new Location(LocKind.FileKind, 7));
                await WaitUntil(() => _kindResult is { kind: 7 }, 20000);
                await Settle(800);
                Check("실제 엔진: PC 전체 이미지", _engineUp && _items.Count > 0 && _items.Count == Math.Min(_engineCounts![7], KindQueryMax),
                    $"{_items.Count:N0}개 (엔진 {_engineCounts?[7]:N0}), 배지 {KindNode(7)?.Badge}, 폴더 {_view.Groups?.Count}곳");
                List.SelectedItems.Clear();
                await Settle(600);
                Shot("21_엔진_이미지");
                return;
            }
            // 0) 아이콘 크기별 선명도 비교용 (윈도우 아이콘은 16·32·48·256 크기를 갖고 있다)
            var bat = Path.Combine(App.SelfTestOut!, "sample.bat");
            File.WriteAllText(bat, "@echo off");
            foreach (var sz in new[] { 48, 64, 96 })
            {
                if (ShellImages.Icon(bat, false, sz) is BitmapSource bs)
                {
                    var e0 = new PngBitmapEncoder();
                    e0.Frames.Add(BitmapFrame.Create(bs));
                    using var fs0 = File.Create(Path.Combine(App.SelfTestOut!, $"icon_bat_{sz}.png"));
                    e0.Save(fs0);
                    _results.Add($"INFO  bat 아이콘 {sz} 요청 → {bs.PixelWidth}x{bs.PixelHeight}px, 배율 {ShellImages.Scale}");
                }
            }

            // 1) 초기 색인
            await WaitUntil(() => _store.FileCount >= 11 && !_indexer.IsScanning, 15000);
            Check("초기 색인: 감시 폴더의 파일 11개", _store.FileCount == 11, $"실제 {_store.FileCount}개");

            // 2) 색인 폴더 만들기 (계층)
            var a = _store.CreateFolder(null, "A 프로젝트");
            var q = _store.CreateFolder(a.Id, "견적");
            var m = _store.CreateFolder(a.Id, "회의록");
            var b = _store.CreateFolder(null, "B 고객사");
            var r = _store.CreateFolder(null, "정기 보고");
            RefreshNow();
            Check("색인 폴더 계층 생성", _store.Children(a.Id).Count() == 2 && FindIndexNode(q.Id) != null);

            Navigate(new Location(LocKind.Inbox));
            await Settle();
            Check("미분류 함: 처음엔 11개 모두", _items.Count == 11, $"{_items.Count}개");
            Shot("01_미분류함");

            // 3) 연결 (파일은 그대로)
            string P(string rel) => Path.Combine(T, rel);
            LinkPaths(q.Id, [P(@"업무\2024\A프로젝트_견적서_v3.xlsx"), P(@"Downloads\A프로젝트_견적서_v2.xlsx")]);
            LinkPaths(m.Id, [P(@"바탕 화면\킥오프_회의록.docx"), P(@"Downloads\주간회의_1006.docx")]);
            LinkPaths(r.Id, [P(@"Downloads\주간회의_1006.docx"), P(@"업무\월간실적_9월.xlsx")]);
            LinkPaths(b.Id, [P(@"업무\계약\B사_계약서_2026.pdf"), P(@"바탕 화면\B사_미팅메모.txt")]);
            RefreshNow();
            await Settle();
            Check("분류 후 미분류 함 4개", _items.Count == 4, $"{_items.Count}개");
            Check("한 파일이 두 색인에 동시 소속", _store.ByPath(P(@"Downloads\주간회의_1006.docx"))?.Folders.Count == 2);
            Check("연결해도 원본 파일은 제자리", File.Exists(P(@"Downloads\주간회의_1006.docx")));

            Navigate(new Location(LocKind.IndexFolder, q.Id));
            await Settle();
            SelectNamed("A프로젝트_견적서_v3.xlsx");
            await Settle(900);
            Check("색인 폴더 목록 (흩어진 원본 2개)", _items.Count == 2);
            Check("트리에서 현재 폴더 선택·상위 펼침", FindIndexNode(a.Id)?.IsExpanded == true && FindIndexNode(q.Id)?.IsSelected == true);
            Shot("02_색인폴더_견적");

            // 4) 색인 폴더에서 새 문서 → 원본 보관함에 같은 구조로
            var txt = ShellNew.Items().FirstOrDefault(i => i.Extension == ".txt");
            Check("새로 만들기 메뉴에 텍스트 문서", txt != null, string.Join(", ", ShellNew.Items().Select(i => i.Extension)));
            string? newDoc = null;
            if (txt != null)
            {
                NewDocument(txt);
                CancelAllEdits();
                newDoc = Path.Combine(S, "A 프로젝트", "견적", $"새 {txt.DisplayName}.txt");
                Check("새 문서가 원본 보관함\\A 프로젝트\\견적 에 생성", File.Exists(newDoc), newDoc);
                Check("새 문서가 견적 색인에 연결", _store.ByPath(newDoc)?.Folders.Contains(q.Id) == true);
            }

            // 5) 색인 폴더 이름 바꾸면 보관함 폴더도 따라감
            RenameIndexFolder(q.Id, "견적서");
            RefreshNow();
            var renamedDir = Path.Combine(S, "A 프로젝트", "견적서");
            Check("색인 이름 변경 → 보관함 폴더 이름도 변경", Directory.Exists(renamedDir) && !Directory.Exists(Path.Combine(S, "A 프로젝트", "견적")));
            var docRec = _store.Files.FirstOrDefault(f => f.Name.StartsWith("새 ") && f.Folders.Contains(q.Id));
            Check("보관함 파일 경로 갱신·연결 유지", docRec != null && File.Exists(docRec.Path) && PathUtil.IsUnder(docRec.Path, renamedDir), docRec?.Path ?? "");

            // 6) 색인 사이 이동: 보관함 파일만 실제로 따라 옮겨짐
            var v3 = _store.ByPath(P(@"업무\2024\A프로젝트_견적서_v3.xlsx"))!;
            if (docRec != null) MoveLinks(q.Id, m.Id, [docRec.Id, v3.Id]);
            RefreshNow();
            Check("이동한 보관함 파일은 회의록 폴더로 실제 이동", docRec != null && PathUtil.IsUnder(docRec.Path, Path.Combine(S, "A 프로젝트", "회의록")) && File.Exists(docRec.Path), docRec?.Path ?? "");
            Check("보관함 밖 원본은 그대로", v3.Path == P(@"업무\2024\A프로젝트_견적서_v3.xlsx") && v3.Folders.Contains(m.Id) && !v3.Folders.Contains(q.Id));

            // 7) 탐색기에서 파일을 옮겨도 연결 유지 (감시 범위 안)
            var scan = _store.ByPath(P(@"Downloads\스캔_20261007.pdf"))!;
            _store.Link(b.Id, scan);
            File.Move(scan.Path, P(@"업무\스캔_이동됨.pdf"));
            await WaitUntil(() => scan.Path.EndsWith("스캔_이동됨.pdf"), 8000);
            Check("바깥에서 이동·이름 변경 → 색인 연결 유지", scan.Path == P(@"업무\스캔_이동됨.pdf") && scan.Folders.Contains(b.Id), scan.Path);

            // 8) 감시 범위 밖으로 옮겨도 파일 ID로 추적
            var memo = _store.ByPath(P(@"바탕 화면\B사_미팅메모.txt"))!;
            Directory.CreateDirectory(P("범위밖"));
            File.Move(memo.Path, P(@"범위밖\B사_미팅메모.txt"));
            await WaitUntil(() => memo.Path.Contains("범위밖"), 8000);
            Check("감시 범위 밖 이동도 추적 (파일 ID)", memo.Path == P(@"범위밖\B사_미팅메모.txt") && !memo.Missing && memo.Folders.Contains(b.Id), memo.Path + (memo.Missing ? " (원본 없음)" : ""));

            // 9) 바깥에서 복사해 붙여넣기 = 보관함에 실제 복사 + 연결
            ImportCopy(r.Id, [P(@"외부\보고서_초안.docx")], move: false);
            var imported = Path.Combine(S, "정기 보고", "보고서_초안.docx");
            Check("외부 파일 붙여넣기 → 원본 보관함\\정기 보고 에 복사", File.Exists(imported) && File.Exists(P(@"외부\보고서_초안.docx")));
            Check("붙여넣은 파일이 정기 보고 색인에 연결", _store.ByPath(imported)?.Folders.Contains(r.Id) == true);

            // 10) 저장 위치 지정 + 하위 상속
            var pin = P(@"업무\B사");
            _store.SetStoragePath(b.Id, pin);
            var bc = _store.CreateFolder(b.Id, "계약");
            Check("지정 위치 하위 상속", StorageOf(bc.Id).Dir == Path.Combine(pin, "계약") && StorageOf(bc.Id).PinnedBy?.Id == b.Id, StorageOf(bc.Id).Dir);
            Navigate(new Location(LocKind.IndexFolder, b.Id));
            if (txt != null)
            {
                NewDocument(txt);
                CancelAllEdits();
                Check("지정 위치에 새 문서 생성", File.Exists(Path.Combine(pin, $"새 {txt.DisplayName}.txt")));
            }
            await Settle();
            SelectNamed("B사_계약서_2026.pdf");
            await Settle(600);
            Shot("03_색인폴더_지정위치");

            // 11) 색인에서 빼기 (원본 그대로)
            Navigate(new Location(LocKind.IndexFolder, r.Id));
            await Settle();
            SelectNamed("월간실적_9월.xlsx");
            DeleteSelected(shift: false);
            var monthly = _store.ByPath(P(@"업무\월간실적_9월.xlsx"));
            Check("Delete = 이 색인에서 빼기, 원본은 그대로", monthly != null && !monthly.Folders.Contains(r.Id) && File.Exists(monthly.Path));
            HideToast();

            // 12) 실제 파일 이름 바꾸기 → 연결 유지
            var v2 = _store.ByPath(P(@"Downloads\A프로젝트_견적서_v2.xlsx"))!;
            var renamed = RenamePhysical(v2.Path, "A프로젝트_견적서_v2_검토.xlsx", false);
            Check("실제 파일 이름 바꾸기 → 연결 유지", renamed != null && v2.Path == renamed && v2.Folders.Contains(q.Id));

            // 13) 검색
            SetSearchText("견적");
            RunSearch();
            await Settle();
            Check("검색 '견적' (파일+색인 폴더)", _items.Count >= 3, $"{_items.Count}개: " + string.Join(", ", _items.Select(i => i.Name)));
            Shot("04_검색");
            ClearSearch();

            // 14) 원본 보기
            Navigate(new Location(LocKind.Dir, Path: P("업무")));
            await Settle(800);
            Check("원본 보기: 폴더 + 색인 열 표시", _items.Any(i => i.Kind == ItemKind.Dir && i.Name == "2024") && _items.Any(i => i.Name == "스캔_이동됨.pdf" && i.IndexText == "B 고객사"));
            Shot("05_원본보기");

            // 15) 큰 아이콘 보기
            Navigate(new Location(LocKind.Dir, Path: P("Downloads")));
            SetViewMode(true);
            await Settle(1500);
            Shot("06_큰아이콘");
            SetViewMode(false);

            // 16) 트리 가이드 선 (깊은 폴더 선택)
            var deep = _store.CreateFolder(m.Id, "2026 하반기");
            RefreshNow();
            Navigate(new Location(LocKind.IndexFolder, deep.Id));
            await Settle(700);
            Shot("07_트리가이드");

            // 17) 색인 폴더 삭제 → 파일은 그대로
            _store.DeleteFolder(m.Id);
            RefreshNow();
            var kick = _store.ByPath(P(@"바탕 화면\킥오프_회의록.docx"));
            Check("색인 폴더 삭제해도 파일은 그대로 + 미분류로", kick != null && File.Exists(kick.Path) && kick.Folders.Count == 0);

            // 18) 성능: 파일 5,000개를 한꺼번에 복사해 넣기 → 자동 색인 → 목록·검색 응답
            var before = _store.FileCount;
            var bulk = P(@"업무\대량");
            await Task.Run(() =>
            {
                Directory.CreateDirectory(bulk);
                for (int i = 1; i <= 5000; i++) File.WriteAllText(Path.Combine(bulk, $"자료_{i:D4}.txt"), "x");
            });
            var sw = Stopwatch.StartNew();
            await WaitUntil(() => _store.FileCount >= before + 5000, 60000);
            Check("대량 추가 5,000개 자동 색인", _store.FileCount >= before + 5000, $"{sw.ElapsedMilliseconds}ms, 총 {_store.FileCount:N0}개");
            sw.Restart();
            Navigate(new Location(LocKind.AllFiles));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check("모든 파일 5,000여 개 목록 표시", sw.ElapsedMilliseconds < 1500 && _items.Count >= 5000, $"{sw.ElapsedMilliseconds}ms, {_items.Count:N0}개 / 색인 {_store.FileCount:N0}개");
            sw.Restart();
            SetSearchText("자료 0999");
            RunSearch();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check("5,000여 개 중 검색", sw.ElapsedMilliseconds < 500 && _items.Count == 1, $"{sw.ElapsedMilliseconds}ms, {_items.Count}개");
            ClearSearch();
            Navigate(new Location(LocKind.IndexFolder, q.Id));
            await Settle(600);
            Shot("08_최종_색인폴더");

            // 19) 끌어서 여러 개 선택 — 자세히 보기, 화면 밖까지 (자동 스크롤 구간 포함)
            Navigate(new Location(LocKind.Dir, Path: bulk));
            await Settle(700);
            if (FindListScroll() && RowGeometry() is { } geo)
            {
                var (h, o) = geo;
                ArmMarqueeAt(new Point(330, o + 2.5 * h - _mSv!.VerticalOffset));
                StartMarquee();
                _mSv.ScrollToVerticalOffset(h * 45);
                _mSv.UpdateLayout();
                // 사용자가 61번째 줄(번호 60)을 가리킨 상황 — 스크롤 뒤 화면이 다 그려진 다음 위치를 읽는다
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                List.ScrollIntoView(List.Items[60]);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var row60 = List.ItemContainerGenerator.ContainerFromIndex(60) as ListViewItem;
                var y60 = row60?.TranslatePoint(new Point(0, row60.ActualHeight / 2), _mScp).Y ?? 0;
                UpdateMarquee(new Point(520, y60));
                await Settle(300);
                Shot("09_범위선택_자세히");
                var cnt = List.SelectedItems.Count;
                var firstSel = List.Items.IndexOf(List.SelectedItems.Cast<ListItem>().MinBy(i => List.Items.IndexOf(i))!);
                EndMarquee();
                Check("끌어서 여러 개 선택: 화면 밖까지 59개 (3~61번째)", cnt == 59 && firstSel == 2, $"{cnt}개, 시작 {firstSel}");
            }
            else Check("끌어서 여러 개 선택: 목록 스크롤 영역 찾기", false);

            // 큰 아이콘 보기에서 범위 선택
            Navigate(new Location(LocKind.Dir, Path: P("Downloads")));
            SetViewMode(true);
            await Settle(900);
            if (FindListScroll() && List.ItemContainerGenerator.ContainerFromIndex(0) is ListViewItem t0
                && List.ItemContainerGenerator.ContainerFromIndex(1) is ListViewItem t1)
            {
                var a0 = t0.TranslatePoint(new Point(-2, -2), _mScp);
                var a1 = t1.TranslatePoint(new Point(t1.ActualWidth / 2, t1.ActualHeight / 2), _mScp);
                List.SelectedItems.Clear();
                ArmMarqueeAt(a0);
                StartMarquee();
                UpdateMarquee(a1);
                await Settle(300);
                Shot("10_범위선택_큰아이콘");
                var cnt = List.SelectedItems.Count;
                EndMarquee();
                Check("큰 아이콘 보기 범위 선택: 2개", cnt == 2, $"{cnt}개");
            }
            else Check("큰 아이콘 보기 칸 찾기", false);
            SetViewMode(false);

            // 21) 최근 문서 (윈도우 최근 항목)
            var sw2 = Stopwatch.StartNew();
            Navigate(new Location(LocKind.Recent));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            sw2.Stop();
            await WaitUntil(() => !RecentFiles.IsLoading, 15000);
            await Settle(400);
            Check("최근 문서 열기 (화면 멈춤 없이)", _loc.Kind == LocKind.Recent && _recentNode.IsSelected && sw2.ElapsedMilliseconds < 1000, $"{_items.Count}개, {sw2.ElapsedMilliseconds}ms");
            Shot("12_최근문서");
            Navigate(new Location(LocKind.IndexFolder, q.Id));
            await Settle(400);
            Check("최근 문서에서 나오면 원래 정렬로", _sortKey == "Name" && !_sortDesc);
            Shot("13_사이드바_툴바");

            // 22) 열 필터 (탐색기와 같음)
            Navigate(new Location(LocKind.Dir, Path: P("Downloads")));
            await Settle(500);
            var pngType = _items.First(i => i.Name.EndsWith(".png")).TypeName;
            _filters["Type"] = [pngType];
            ApplyFilter();
            await Settle(300);
            Check("유형 필터: 그림 파일만", _view.Count == 1, $"{_view.Count}개 표시 / 전체 {_items.Count}개");
            Shot("14_필터");
            _filters.Clear();
            _filters["Name"] = ["A–H"];
            ApplyFilter();
            Check("이름 필터: A–H", _view.Count == 1 && ((ListItem)_view.GetItemAt(0)).Name.StartsWith("A"), $"{_view.Count}개");
            _filters.Clear();
            ApplyFilter();
            Check("필터 지우면 전체", _view.Count == _items.Count);
            var hdr = FindDescendant<GridViewColumnHeader>(List);
            if (hdr != null)
                _results.Add($"INFO  열 제목 위치: 목록 위에서 {hdr.TranslatePoint(new Point(0, 0), List).Y}px, 높이 {hdr.ActualHeight}px, 목록 위치 {List.TranslatePoint(new Point(0, 0), (UIElement)Content).Y}px");

            // 23) 메뉴 항목 강조 색 (첫 항목에 키보드 포커스 = 마우스 올림과 같은 강조)
            var testMenu = new ContextMenu { PlacementTarget = List };
            testMenu.Items.Add(new MenuItem { Header = "열기" });
            testMenu.Items.Add(new MenuItem { Header = "새 창에서 열기" });
            testMenu.Items.Add(new MenuItem { Header = "이름 바꾸기", InputGestureText = "F2" });
            testMenu.IsOpen = true;
            await Settle(300);
            ((MenuItem)testMenu.Items[1]).Focus();
            await Settle(300);
            var mrtb = new RenderTargetBitmap((int)(testMenu.ActualWidth * 1.0), (int)(testMenu.ActualHeight * 1.0), 96, 96, PixelFormats.Pbgra32);
            mrtb.Render(testMenu);
            var menc = new PngBitmapEncoder();
            menc.Frames.Add(BitmapFrame.Create(mrtb));
            using (var mfs = File.Create(Path.Combine(App.SelfTestOut!, "15_메뉴강조.png"))) menc.Save(mfs);
            testMenu.IsOpen = false;

            // 24) 탭: 탭마다 위치·기록을 따로 기억
            Navigate(new Location(LocKind.IndexFolder, q.Id));
            Navigate(new Location(LocKind.IndexFolder, m.Id));
            var firstTab = _tab;
            NewTab(new Location(LocKind.Dir, Path: P("Downloads")));
            await Settle(500);
            Check("새 탭 열기", _tabs.Count == 2 && _loc.Kind == LocKind.Dir && _tab.Title == "Downloads", $"{_tabs.Count}개, 제목 {_tab.Title}");
            Shot("16_탭");
            SwitchTo(firstTab);
            await Settle(400);
            Check("탭 전환 시 위치·뒤로 기록 복원", _loc.Kind == LocKind.IndexFolder && _loc.FolderId == m.Id && _back.Count > 0 && _back.Peek().FolderId == q.Id);
            CycleTab(1);
            Check("Ctrl+Tab 다음 탭", _loc.Kind == LocKind.Dir);
            CloseTab(_tab);
            await Settle(300);
            Check("탭 닫기", _tabs.Count == 1 && _tab == firstTab && _loc.FolderId == m.Id);

            // 크롬식: 몇 개일 땐 넓게, 많아지면 함께 좁아지고, 최소 폭에서는 화살표
            NewTab(new Location(LocKind.Recent));
            NewTab(new Location(LocKind.Dir, Path: P("업무")));
            await Settle(400);
            var wideW = _tabPanel?.TabWidth ?? 0;
            Shot("17_탭_3개");
            for (int k = 0; k < 6; k++) NewTab(new Location(LocKind.Dir, Path: P("Downloads")));
            await Settle(400);
            var midW = _tabPanel?.TabWidth ?? 0;
            var midOverflow = _tabPanel?.IsOverflowing ?? true;
            Shot("18_탭_9개");
            for (int k = 0; k < 8; k++) NewTab(new Location(LocKind.Inbox));
            await Settle(500);
            Check("탭: 적을 땐 넓게(220) → 많으면 함께 좁아짐 → 최소 폭에서 화살표",
                wideW >= 219 && midW < wideW && midW > 96 && !midOverflow && _tabPanel!.IsOverflowing && TabRightBtn.Visibility == Visibility.Visible,
                $"3개 {wideW:0}px, 9개 {midW:0}px, 17개 {_tabPanel?.TabWidth:0}px 넘침 {_tabPanel?.IsOverflowing}");
            Check("마지막(지금) 탭이 보이도록 밀림", _tabPanel!.Offset > 0 && !_tabPanel.CanScrollRight);
            Shot("19_탭_넘침");
            while (_tabs.Count > 1) CloseTab(_tabs[^1]);
            SwitchTo(_tabs[0]);
            await Settle(300);

            // 25) 윈도우 도구 버튼
            Check("메모장·cmd·PowerShell 아이콘 (윈도우 프로그램에서)", NotepadIcon.Source != null && CmdIcon.Source != null && PsIcon.Source != null);
            Navigate(new Location(LocKind.Dir, Path: P("업무")));
            Check("명령 창 폴더: 원본 폴더는 그 폴더", ConsoleDir() == P("업무"), ConsoleDir());
            Navigate(new Location(LocKind.IndexFolder, b.Id));
            await Settle(300);
            SelectNamed("B사_계약서_2026.pdf");
            Check("명령 창 폴더: 색인에선 선택한 파일의 실제 폴더", ConsoleDir() == P(@"업무\계약"), ConsoleDir());

            // 26) 엔진 저장소 (디스크 장부 압축 구조) — 장부·변경 기록을 흉내 내어
            {
                var vi = new IndexExplorer.Engine.VolumeIndex("X:");
                const uint Dir = IndexExplorer.Engine.VolumeIndex.AttrDirectory;
                ulong rootF = 5, users = 100, work = 101, win = 102, f1 = 200, f2 = 201, f3 = 202;
                vi.Write(v =>
                {
                    v.Upsert(users, rootF, "업무", Dir);
                    v.Upsert(work, users, "견적", Dir);
                    v.Upsert(win, rootF, "Windows", Dir);
                    v.Upsert(f1, work, "견적서.xlsx", 0x20);
                    v.Upsert(f2, win, "system.xlsx", 0x20);
                    v.Upsert(f3, work, "사진.png", 0x20);
                });
                var c1 = vi.CountByKind(false);
                Check("엔진: 종류 분류·시스템 폴더 제외", c1[1] == 1 && c1[7] == 1 && vi.CountByKind(true)[1] == 2, $"엑셀 {c1[1]}, 이미지 {c1[7]}");
                Check("엔진: 경로 조립", vi.PathOf(f1) == @"X:\업무\견적\견적서.xlsx", vi.PathOf(f1) ?? "");
                // 변경 기록: 이름 변경 → 다른 폴더로 이동 → 삭제
                vi.Write(v => IndexExplorer.Engine.EngineHost.Apply(v, f1, work, "견적서_최종.xlsx", 0x20, IndexExplorer.Engine.NtfsVolume.REASON_RENAME_NEW_NAME));
                Check("엔진: 이름 변경 반영", vi.PathOf(f1) == @"X:\업무\견적\견적서_최종.xlsx");
                vi.Write(v => IndexExplorer.Engine.EngineHost.Apply(v, work, rootF, "견적", Dir, IndexExplorer.Engine.NtfsVolume.REASON_RENAME_NEW_NAME));
                Check("엔진: 폴더 이동 → 안의 파일 경로도 즉시", vi.PathOf(f1) == @"X:\견적\견적서_최종.xlsx", vi.PathOf(f1) ?? "");
                vi.Write(v => IndexExplorer.Engine.EngineHost.Apply(v, work, users, "견적", Dir, IndexExplorer.Engine.NtfsVolume.REASON_RENAME_NEW_NAME));
                vi.Write(v => IndexExplorer.Engine.EngineHost.Apply(v, win, rootF, "Windows", Dir, 0));
                vi.Write(v => IndexExplorer.Engine.EngineHost.Apply(v, f3, work, "사진.png", 0x20, IndexExplorer.Engine.NtfsVolume.REASON_FILE_DELETE));
                Check("엔진: 삭제 반영", vi.CountByKind(false)[7] == 0 && vi.Count == 5);
                var hits = vi.Query(1, "최종", false, 100);
                Check("엔진: 종류 + 이름 검색", hits.Count == 1 && hits[0].Path.EndsWith("견적서_최종.xlsx"));
                vi.JournalId = 0xABC; vi.NextUsn = 12345;
                var snapPath = Path.Combine(App.SelfTestOut!, "test.idx");
                vi.Save(snapPath);
                var vi2 = new IndexExplorer.Engine.VolumeIndex("X:");
                Check("엔진: 목록 저장·복원 (재부팅 후 이어 읽기용)", vi2.Load(snapPath) && vi2.Count == 5 && vi2.JournalId == 0xABC && vi2.NextUsn == 12345
                    && vi2.PathOf(f1) == @"X:\업무\견적\견적서_최종.xlsx");
                // 잡동사니 거르기: 점(.) 폴더, dll 이 3개 이상인 프로그램 폴더, 직접 뺀 폴더, 모두 보기
                var jv = new IndexExplorer.Engine.VolumeIndex("Z:");
                jv.Write(v =>
                {
                    v.Upsert(20, 5, "사용자", Dir);
                    v.Upsert(21, 20, ".eclipse", Dir);
                    v.Upsert(22, 21, "아이콘.png", 0x20);            // 점 폴더 → 숨김
                    v.Upsert(30, 5, "게임", Dir);
                    v.Upsert(31, 30, "client", Dir);
                    for (ulong i = 0; i < 3; i++) v.Upsert(40 + i, 31, $"lib{i}.dll", 0x20);
                    v.Upsert(32, 31, "cache", Dir);
                    v.Upsert(33, 32, "배경.jpg", 0x20);              // 프로그램 폴더 아래 → 숨김
                    v.Upsert(50, 20, "다운로드", Dir);
                    v.Upsert(51, 50, "setup.exe", 0x20);             // exe 하나로는 프로그램 폴더 아님
                    v.Upsert(52, 50, "사진.png", 0x20);              // 보임
                    v.Upsert(60, 20, "옛날자료", Dir);
                    v.Upsert(61, 60, "스캔.png", 0x20);              // 직접 빼면 숨김
                });
                var ex = new List<string> { @"Z:\사용자\옛날자료" };
                var jc = jv.CountByKind(false, ex);
                var jAll = jv.CountByKind(true);
                Check("잡동사니: 점 폴더·프로그램 폴더·직접 뺀 폴더 숨김", jc[7] == 1 && jAll[7] == 4 && jv.Query(7, null, false, 10, ex).Single().Path == @"Z:\사용자\다운로드\사진.png",
                    $"보이는 이미지 {jc[7]} / 전체 {jAll[7]}");
                Check("잡동사니: 직접 뺀 폴더를 빼면 다시 보임", jv.CountByKind(false)[7] == 2);
                jv.Write(v => { v.Remove(41); v.Remove(42); });
                Check("잡동사니: dll 이 줄면 프로그램 폴더 판별도 풀림", jv.CountByKind(false)[7] == 3);

                // 프로그램 폴더 꼭대기: dll 폴더에서 두 단계 위까지 (드라이브 바로 아래·사용자 폴더는 제외)
                var pv = new IndexExplorer.Engine.VolumeIndex("W:");
                pv.Write(v =>
                {
                    v.Upsert(70, 5, "개발", Dir);                     // 드라이브 바로 아래 → 절대 숨기지 않음
                    v.Upsert(71, 70, "Git", Dir);
                    v.Upsert(72, 71, "ucrt64", Dir);
                    v.Upsert(73, 72, "bin", Dir);
                    for (ulong i = 0; i < 3; i++) v.Upsert(80 + i, 73, $"m{i}.dll", 0x20);
                    v.Upsert(74, 72, "lib", Dir);
                    v.Upsert(75, 74, "그림.png", 0x20);                // Git 안 → 숨김
                    v.Upsert(76, 70, "내프로젝트", Dir);
                    v.Upsert(77, 76, "화면.png", 0x20);                // 개발\내프로젝트 → 보임
                });
                pv.Write(v =>
                {
                    v.Upsert(90, 5, "VoiceAI", Dir);                   // 드라이브 바로 아래 → 지킴
                    v.Upsert(91, 90, "GPT-SoVITS", Dir);               // 프로그램 꼭대기 → 숨김
                    v.Upsert(92, 91, "runtime", Dir);
                    for (ulong i = 0; i < 3; i++) v.Upsert(95 + i, 92, $"py{i}.dll", 0x20);
                    v.Upsert(93, 91, "fig", Dir);
                    v.Upsert(94, 93, "구조.png", 0x20);
                    v.Upsert(98, 90, "내목소리.png", 0x20);            // VoiceAI 바로 안 → 보임
                });
                pv.Write(v =>
                {
                    v.Upsert(100, 70, "내앱", Dir);                     // 개발 프로젝트 → 지킴
                    v.Upsert(101, 100, "내앱.csproj", 0x20);
                    v.Upsert(102, 100, "Assets", Dir);
                    v.Upsert(103, 102, "로고.png", 0x20);              // 보임
                    v.Upsert(104, 100, "bin", Dir);
                    v.Upsert(105, 104, "Debug", Dir);
                    for (ulong i = 0; i < 3; i++) v.Upsert(106 + i, 105, $"a{i}.dll", 0x20);
                    v.Upsert(110, 105, "빌드그림.png", 0x20);           // 빌드 결과물 → 숨김
                });
                var dev = pv.Query(7, null, false, 20).Where(h => h.Path.Contains("내앱")).Select(h => h.Path).ToList();
                Check("잡동사니: 개발 프로젝트는 빌드 결과물(bin)만 숨기고 프로젝트는 지킴",
                    dev.Count == 1 && dev[0] == @"W:\개발\내앱\Assets\로고.png", string.Join(", ", dev));
                var pc = pv.Query(7, null, false, 10).Where(h => !h.Path.Contains("VoiceAI") && !h.Path.Contains("내앱")).ToList();
                var pcVoice = pv.Query(7, null, false, 10).Where(h => h.Path.Contains("VoiceAI")).Select(h => h.Path).ToList();
                Check("잡동사니: 드라이브 바로 아래 폴더 안의 프로그램(GPT-SoVITS)은 숨기고 그 폴더는 지킴",
                    pcVoice.Count == 1 && pcVoice[0] == @"W:\VoiceAI\내목소리.png", string.Join(", ", pcVoice));
                Check("잡동사니: 프로그램 꼭대기까지 숨기되 드라이브 바로 아래 폴더는 지킴",
                    pc.Count == 1 && pc[0].Path == @"W:\개발\내프로젝트\화면.png", string.Join(", ", pc.Select(h => h.Path)));

                // 대량: 50만 개 넣고 조회 속도
                var big = new IndexExplorer.Engine.VolumeIndex("Y:");
                var swE = Stopwatch.StartNew();
                big.Write(v =>
                {
                    v.Upsert(10, 5, "data", Dir);
                    for (ulong i = 0; i < 500_000; i++) v.Upsert(1000 + i, 10, $"file_{i}.{(i % 10 == 0 ? "xlsx" : "txt")}", 0x20);
                });
                var buildMs = swE.ElapsedMilliseconds;
                swE.Restart();
                var cnt = big.CountByKind(false);
                var countMs = swE.ElapsedMilliseconds;
                swE.Restart();
                var q50 = big.Query(1, null, false, 100_000);
                Check("엔진: 50만 개 — 담기·세기·엑셀 조회", cnt[1] == 50_000 && q50.Count == 50_000 && countMs < 500,
                    $"담기 {buildMs}ms, 종류별 세기 {countMs}ms, 엑셀 5만 개 경로까지 {swE.ElapsedMilliseconds}ms, 메모리 약 {big.EstimatedBytes / (1024 * 1024)}MB");
            }

            // 27) 파일 종류 화면 (엔진 없이 = 색인한 폴더 안에서)
            Navigate(new Location(LocKind.FileKind, 1));
            await Settle(500);
            Check("파일 종류: 엑셀 (엔진 없으면 색인한 폴더 안)", _loc.Kind == LocKind.FileKind && _items.Count > 0 && _items.All(i => i.Name.EndsWith(".xlsx") || i.Name.EndsWith(".csv")),
                $"{_items.Count}개, 배지 {KindNode(1)?.Badge}");
            Check("파일 종류: 트리 선택·제목", KindNode(1)?.IsSelected == true && _tab.Title == "엑셀");
            Shot("20_파일종류_엑셀");

            // 28) 묶어 보기: 파일 종류는 폴더별이 기본
            var dirs = _items.Select(i => i.Location).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            Check("묶어 보기: 파일 종류는 처음부터 폴더별", _groupBy == GroupBy.Folder && _view.Groups?.Count == dirs, $"폴더 {_view.Groups?.Count}곳 / 실제 {dirs}곳");
            Check("묶어 보기: 상태 표시줄에 폴더 수", StatusLeft.Text.Contains("폴더"), StatusLeft.Text);
            // 묶은 상태에서 범위 선택: 목록 맨 위에서 맨 아래까지 끌면 모두
            if (FindListScroll())
            {
                List.SelectedItems.Clear();
                ArmMarqueeAt(new Point(300, 2));
                StartMarquee();
                UpdateMarquee(new Point(320, _mScp!.ActualHeight - 4));
                var gsel = List.SelectedItems.Count;
                EndMarquee();
                Check("묶어 보기: 범위 선택 (머리줄이 끼어 있어도)", gsel == _items.Count, $"{gsel}/{_items.Count}");
            }
            // 접기
            var firstToggle = FindDescendant<System.Windows.Controls.Primitives.ToggleButton>(FindDescendant<GroupItem>(List)!);
            firstToggle!.IsChecked = false;
            await Settle(300);
            var hiddenRows = FindDescendant<GroupItem>(List) is { } gi && FindDescendant<ItemsPresenter>(gi) is { Visibility: Visibility.Collapsed };
            Check("묶어 보기: 머리줄로 접기", hiddenRows);
            firstToggle.IsChecked = true;
            Shot("22_묶어보기_폴더별");
            // 날짜별·유형별
            Navigate(new Location(LocKind.Dir, Path: P("Downloads")));
            await Settle(300);
            Check("묶어 보기: 원본 보기는 기본으로 묶지 않음", _groupBy == GroupBy.None && !IsGrouped);
            SetGroupBy(GroupBy.Date);
            await Settle(300);
            Check("묶어 보기: 수정한 날짜별 (오늘)", _view.Groups?.Count == 1 && (_view.Groups[0] as CollectionViewGroup)?.Name as string == "오늘", (_view.Groups?[0] as CollectionViewGroup)?.Name?.ToString() ?? "");
            SetGroupBy(GroupBy.Type);
            await Settle(300);
            Check("묶어 보기: 유형별", _view.Groups?.Count == _items.Select(i => i.TypeName).Distinct().Count());
            Shot("23_묶어보기_유형별");
            // 탭마다 기억
            NewTab(new Location(LocKind.FileKind, 1));
            await Settle(300);
            var newTabGroup = _groupBy;
            CloseTab(_tab);
            await Settle(300);
            Check("묶어 보기: 탭마다 따로 기억", newTabGroup == GroupBy.Folder && _groupBy == GroupBy.Type && _loc.Kind == LocKind.Dir, $"새 탭 {newTabGroup}, 돌아온 탭 {_groupBy}");
            SetGroupBy(GroupBy.None);

            // 29) 중복 파일: 이름이 달라도 내용이 같으면 묶고, 크기만 같은 파일은 빼고, 지울 때 색인 연결을 남는 파일로
            {
                var big = new byte[200_000];
                new Random(7).NextBytes(big);
                var nearly = (byte[])big.Clone();
                nearly[^1] ^= 0xFF;                                            // 크기는 같고 마지막 1바이트만 다름
                File.WriteAllBytes(P(@"업무\원본자료.bin"), big);
                await Task.Delay(1100);
                File.WriteAllBytes(P(@"Downloads\원본자료 (1).bin"), big);
                await Task.Delay(1100);
                File.WriteAllBytes(P(@"바탕 화면\받은 사본.bin"), big);
                File.WriteAllBytes(P(@"새 폴더 (2)\비슷한것.bin"), nearly);
                File.WriteAllText(P(@"업무\메모.txt"), new string('메', 1500));
                File.WriteAllText(P(@"Downloads\메모 복사본.txt"), new string('메', 1500));
                await WaitUntil(() => _store.ByPath(P(@"새 폴더 (2)\비슷한것.bin")) != null && _store.ByPath(P(@"Downloads\메모 복사본.txt")) != null, 15000);
                var copy = _store.ByPath(P(@"바탕 화면\받은 사본.bin"))!;
                _store.Link(r.Id, copy);                                        // 지울 사본이 색인에 연결되어 있다
                var swD = Stopwatch.StartNew();
                Navigate(new Location(LocKind.Duplicates));
                await WaitUntil(() => !_dupRunning && _dupGroups != null, 30000);
                var firstMs = swD.ElapsedMilliseconds;
                await Settle(500);
                var groupsNow = _items.Where(i => i.DupRank != null).GroupBy(i => i.DupRank).Select(g => g.Count()).OrderByDescending(x => x).ToList();
                Check("중복 파일: 이름이 달라도 내용이 같은 묶음만 (크기만 같은 파일 제외)",
                    groupsNow.SequenceEqual(new[] { 3, 2 }) && !_items.Any(i => i.Name == "비슷한것.bin"),
                    $"묶음 {string.Join("+", groupsNow)}, {firstMs}ms, 상태 {_dupStatus}");
                Check("중복 파일: 낭비 큰 묶음이 먼저", _items.First().Size == 200_000);
                Shot("24_중복파일");
                SelectAllButOne(Oldest, "가장 오래된 것만");
                Check("중복 파일: 오래된 것만 남기고 선택", List.SelectedItems.Count == 3 && !Selected().Any(i => i.Name == "원본자료.bin"), $"{List.SelectedItems.Count}개");
                DeleteSelected(shift: false);
                HideToast();
                await Settle(800);
                var kept = _store.ByPath(P(@"업무\원본자료.bin"));
                Check("중복 파일: 지운 사본의 색인 연결이 남은 파일로", kept != null && kept.Folders.Contains(r.Id) && !File.Exists(P(@"바탕 화면\받은 사본.bin")),
                    kept == null ? "남은 파일 없음" : string.Join(",", kept.Folders));
                Check("중복 파일: 다 정리하면 목록이 빔", _items.Count(i => i.DupRank != null) == 0, $"{_items.Count}개 남음");
                // 다시 찾기 (지문 기억) — 같은 내용 파일을 다시 만들어 비교
                File.WriteAllBytes(P(@"Downloads\또사본.bin"), big);
                await WaitUntil(() => _store.ByPath(P(@"Downloads\또사본.bin")) != null, 10000);
                _dupGroups = null;
                swD.Restart();
                StartDuplicateScan();
                await WaitUntil(() => !_dupRunning && _dupGroups != null, 30000);
                Check("중복 파일: 다시 찾기 (비교한 파일은 기억)", _dupGroups!.Count == 1 && _dupGroups[0].Files.Count == 2, $"{swD.ElapsedMilliseconds}ms");
            }

            // 30) 대량: 5만 개 목록 — 표시·전체 선택·하나만 남기고 선택·범위 선택·큰 아이콘·묶어 보기
            {
                Navigate(new Location(LocKind.Dir, Path: P("Downloads")));
                await Settle(300);
                var many = new List<ListItem>(50_000);
                var now = DateTime.UtcNow;
                for (int i = 0; i < 50_000; i++)
                    many.Add(new ListItem
                    {
                        Kind = ItemKind.File, Path = $@"X:\대량\폴더{i % 200:D3}\파일{i:D5}.txt", Name = $"파일{i:D5}.txt", Size = 1000 + i,
                        Modified = now.AddMinutes(-i), TypeName = "텍스트 문서", Location = $@"X:\대량\폴더{i % 200:D3}",
                        DupKey = $"{i / 2}", DupRank = i / 2,
                    });
                var swB = Stopwatch.StartNew();
                ShowItemsForTest(many);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var showMs = swB.ElapsedMilliseconds;
                swB.Restart();
                List.SelectAll();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var allMs = swB.ElapsedMilliseconds;
                var allCount = List.SelectedItems.Count;
                swB.Restart();
                SelectAllButOne(Oldest, "가장 오래된 것만");
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var butOneMs = swB.ElapsedMilliseconds;
                var butOneCount = List.SelectedItems.Count;
                HideToast();
                Check("대량 5만: 목록 표시", showMs < 2000, $"{showMs}ms");
                Check("대량 5만: 전체 선택", allCount == 50_000 && allMs < 2000, $"{allMs}ms");
                Check("대량 5만: 하나만 남기고 선택 (2만5천 묶음)", butOneCount == 25_000 && butOneMs < 5000, $"{butOneMs}ms (WPF 선택 처리 한계, 기준 5초)");
                // 범위 선택: 맨 위에서 3만 번째 줄까지 (자동 스크롤 구간 포함)
                List.SelectedItems.Clear();
                if (FindListScroll() && RowGeometry() is { } g5)
                {
                    swB.Restart();
                    ArmMarqueeAt(new Point(300, g5.origin + 0.5 * g5.h - _mSv!.VerticalOffset));
                    StartMarquee();
                    List.ScrollIntoView(List.Items[30_000]);
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var c30 = List.ItemContainerGenerator.ContainerFromIndex(30_000) as ListViewItem;
                    UpdateMarquee(new Point(320, c30?.TranslatePoint(new Point(0, c30.ActualHeight / 2), _mScp).Y ?? 0));
                    var mCount = List.SelectedItems.Count;
                    EndMarquee();
                    Check("대량 5만: 끌어서 3만 개 선택", mCount == 30_001 && swB.ElapsedMilliseconds < 3000, $"{mCount:N0}개 {swB.ElapsedMilliseconds}ms");
                }
                // 큰 아이콘: 화면에 보이는 칸만 만들어야 한다
                swB.Restart();
                SetViewMode(true);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var tileMs = swB.ElapsedMilliseconds;
                var made = FindDescendant<VirtualizingTilePanel>(List)?.Children.Count ?? -1;
                swB.Restart();
                FindDescendant<ScrollViewer>(List)!.ScrollToEnd();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var endMs = swB.ElapsedMilliseconds;
                var lastShown = FindDescendant<VirtualizingTilePanel>(List)!.Children.OfType<ListViewItem>().Any(c => (c.DataContext as ListItem)?.Name == "파일49999.txt");
                Check("대량 5만: 큰 아이콘 (보이는 칸만 만듦)", made > 0 && made < 400 && tileMs < 2000, $"만든 칸 {made}개, {tileMs}ms");
                Check("대량 5만: 큰 아이콘 맨 끝까지 스크롤", lastShown && endMs < 1000, $"{endMs}ms");
                Shot("25_대량_큰아이콘");
                SetViewMode(false);
                // 묶어 보기 (자세히): 200개 폴더
                swB.Restart();
                SetGroupBy(GroupBy.Folder);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check("대량 5만: 폴더별로 묶기 (200곳)", _view.Groups?.Count == 200 && swB.ElapsedMilliseconds < 3000, $"{swB.ElapsedMilliseconds}ms");
                SetGroupBy(GroupBy.None);
                Navigate(new Location(LocKind.Dir, Path: P("Downloads")));
                await Settle(300);
            }

            // 20) 창 두 개: 한 창에서 복사 → 다른 창의 색인 폴더에 붙여넣기 = 연결 (복사본 안 생김)
            var w2 = App.OpenWindow(new Location(LocKind.IndexFolder, r.Id), this);
            await Settle(900);
            Navigate(new Location(LocKind.Dir, Path: P("새 폴더 (2)")));
            await Settle(400);
            SelectNamed("설계서_v1.pptx");
            CopySelected(cut: false);
            w2.TestPaste();
            await Settle(700);
            var design = _store.ByPath(P(@"새 폴더 (2)\설계서_v1.pptx"));
            Check("다른 창에 붙여넣기 = 색인 연결, 복사본 안 생김",
                design?.Folders.Contains(r.Id) == true && !File.Exists(Path.Combine(S, "정기 보고", "설계서_v1.pptx")));
            Check("다른 창 목록에도 바로 반영", w2.TestItemNames().Contains("설계서_v1.pptx"));
            await Settle(300);
            Check("두 번째 창 색인 표시 공유", w2.TestItemNames().Count == _store.FilesIn(r.Id).Count());
            w2.TestShot("11_두번째창");
            w2.Close();
        }
        catch (Exception ex)
        {
            _results.Add("ERROR  " + ex);
        }
        finally
        {
            File.WriteAllLines(Path.Combine(App.SelfTestOut!, "results.txt"), _results);
            Close();
        }
    }
}
