using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
            Check("모든 파일 5,000여 개 목록 표시", sw.ElapsedMilliseconds < 1500 && _items.Count >= 5000, $"{sw.ElapsedMilliseconds}ms");
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
