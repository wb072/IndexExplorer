using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Principal;
using IndexExplorer.Engine;
using System.IO;
using System.Windows;
using IndexExplorer.Services;
using Microsoft.Win32;

namespace IndexExplorer.Views;

public partial class SettingsWindow : Window
{
    private readonly Settings _settings;
    private readonly ObservableCollection<string> _roots;
    private readonly ObservableCollection<string> _kindEx;
    private readonly ObservableCollection<KindRow> _kinds;
    private readonly string _kindsBefore;
    private string _storage;

    /// <summary>파일 종류 설정이 바뀌었나 (저장했을 때).</summary>
    public bool KindsChanged { get; private set; }

    /// <summary>설정 화면의 파일 종류 한 줄.</summary>
    public sealed class KindRow : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        public KindSetting S { get; }
        public KindRow(KindSetting s) { S = s; }
        public bool Enabled { get => S.Enabled; set { if (S.Enabled != value) { S.Enabled = value; OnPropertyChanged(); } } }
        public string Name => S.Name;
        public string ExtShort => string.Join(" ", S.Exts);
        public string ExtTip => string.Join(", ", S.Exts.Select(e => "." + e));
        public System.Windows.Media.ImageSource? Icon => S.Exts.Count > 0 ? Native.ShellImages.Icon("x." + S.Exts[0], false) : null;
    }

    private static string KindsSignature(IEnumerable<KindSetting> kinds) =>
        string.Join("|", kinds.Select(k => $"{k.Id}:{k.Name}:{k.Enabled}:{string.Join(",", k.Exts)}"));

    public SettingsWindow(Settings settings, bool firstRun)
    {
        Resources["FolderIcon"] = Native.ShellImages.Icon(Path.GetTempPath(), true);
        InitializeComponent();
        _settings = settings;
        _roots = new ObservableCollection<string>(settings.WatchedRoots);
        _storage = settings.StorageRoot;
        RootList.ItemsSource = _roots;
        _kindEx = new ObservableCollection<string>(settings.KindExcludes);
        KindExList.ItemsSource = _kindEx;
        _kindEx.CollectionChanged += (_, _) => UpdateKindExEmpty();
        UpdateKindExEmpty();
        settings.NormalizeKinds();
        _kindsBefore = KindsSignature(settings.Kinds);
        _kinds = new ObservableCollection<KindRow>(settings.Kinds.Select(k => new KindRow(k.Clone())));
        KindList.ItemsSource = _kinds;
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 40);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 40);
        ShowStorage();
        var ver = typeof(App).Assembly.GetName().Version;
        var built = File.GetLastWriteTime(typeof(App).Assembly.Location);
        VersionText.Text = $"버전 {ver?.Major}.{ver?.Minor} · {built:yyyy-MM-dd} 빌드";
        DataText.Text = Settings.DataDir;
        UpdateEngineStatus();
        if (firstRun)
        {
            Title = "색인 탐색기 시작하기";
            Heading.Visibility = Visibility.Visible;
            SaveBtn.Content = "시작하기";
            CancelBtn.Content = "기본값으로 시작";
        }
    }

    /// <summary>탭 열기: general · kinds · engine · about.</summary>
    public void SelectTab(string tab)
    {
        (tab switch { "kinds" => TabKinds, "engine" => TabEngine, "about" => TabAbout, _ => TabGeneral }).IsChecked = true;
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (PageGeneral == null) return; // 만드는 중
        PageGeneral.Visibility = TabGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageKinds.Visibility = TabKinds.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageEngine.Visibility = TabEngine.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = TabAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateKindExEmpty() => KindExEmpty.Visibility = _kindEx.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void ShowStorage()
    {
        StorageName.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(_storage)) is { Length: > 0 } n ? n : _storage;
        StorageText.Text = _storage;
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(Settings.DataDir); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Settings.DataDir}\"") { UseShellExecute = true }); }
        catch { }
    }

    private void KindRow_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { Tag: KindRow row }) { EditKind(row); e.Handled = true; }
    }

    private void UpdateEngineStatus()
    {
        string s;
        try { s = EngineInstaller.StatusText(); } catch { s = "확인 불가"; }
        EngineStatus.Text = s;
        var running = s == "실행 중";
        EngineBadge.Background = (System.Windows.Media.Brush)FindResource(running ? "PinBadgeBgBrush" : "SidebarBrush");
        EngineStatus.Foreground = (System.Windows.Media.Brush)FindResource(running ? "PinBadgeBrush" : "SecondaryBrush");
        EngineInstallBtn.Content = s == "설치 안 됨" ? "설치" : "다시 설치";
        EngineRemoveBtn.IsEnabled = s != "설치 안 됨";
        if (running) _ = CheckEngineVersion();
        else DrivesSection.Visibility = Visibility.Collapsed;
    }

    /// <summary>드라이브별 상태를 보여 주고, 실행 중인 엔진이 예전 버전이면 (직접 정한 파일 종류를 모름) 업데이트를 권한다.</summary>
    private async Task CheckEngineVersion()
    {
        var r = await App.Engine.AskAsync(new EngineProtocol.Request { Op = "status" }, 2000);
        if (r is not { Ok: true }) return;
        if (r.State is { Count: > 0 } st)
        {
            var drives = st.OrderBy(kv => kv.Key).ToList();
            DriveList.ItemsSource = drives.Select((kv, i) => new DriveRow(kv.Key, DriveName(kv.Key), FriendlyState(kv.Value), i < drives.Count - 1, kv.Value.Contains("이어 읽는 중") || kv.Value.Contains("읽음"),
                Native.ShellImages.Icon(kv.Key + "\\", true, 16, perPath: true))).ToList();
            EngineFiles.Text = $"모든 드라이브 합계 파일 {r.Files:N0}개를 따라가고 있습니다.";
            DrivesSection.Visibility = Visibility.Visible;
        }
        if (r.Proto >= EngineProtocol.CurrentProto) return;
        EngineStatus.Text = "업데이트 필요";
        EngineBadge.Background = (System.Windows.Media.Brush)FindResource("SidebarBrush");
        EngineStatus.Foreground = (System.Windows.Media.Brush)FindResource("SecondaryBrush");
        EngineInstallBtn.Content = "업데이트";
    }

    /// <summary>자체 검증: 이름 붙은 카드가 보이게 스크롤.</summary>
    internal int TestDriveCount() => DriveList.Items.Count;

    public sealed record DriveRow(string Root, string Name, string State, bool Line, bool Live, System.Windows.Media.ImageSource? Icon)
    {
        public Thickness Border => new(0, 0, 0, Line ? 1 : 0);
        public Visibility LiveVis => Live ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string DriveName(string root)
    {
        try { var label = new DriveInfo(root).VolumeLabel; return label.Length > 0 ? $"{label} ({root})" : $"로컬 디스크 ({root})"; }
        catch { return root; }
    }

    /// <summary>엔진의 드라이브 상태 문구를 사용자 말로: "저장한 목록 612,461개 불러옴 (597ms), 변경 기록 이어 읽는 중" → "파일 612,461개 · 바뀌는 즉시 반영".</summary>
    private static string FriendlyState(string s)
    {
        var m = System.Text.RegularExpressions.Regex.Match(s, @"([\d,]+)개");
        if (m.Success && (s.Contains("이어 읽는 중") || s.Contains("읽음"))) return $"파일 {m.Groups[1].Value}개 · 바뀌는 즉시 반영";
        if (s.Contains("장부 읽는 중")) return "처음 읽는 중…";
        if (s.StartsWith("변경 기록을 켰")) return "준비 중…";
        return s;
    }

    internal string TestVisiblePage() =>
        new FrameworkElement[] { PageGeneral, PageKinds, PageEngine, PageAbout }.Single(p => p.Visibility == Visibility.Visible).Name;

    private void EditKind_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: KindRow row }) EditKind(row);
    }

    private void EditKind(KindRow row)
    {
        var copy = row.S.Clone();
        var w = new KindEditWindow(copy, _kinds.Where(r => r != row).Select(r => r.S).ToList(), isNew: false) { Owner = this };
        if (w.ShowDialog() != true) return;
        var i = _kinds.IndexOf(row);
        if (w.Deleted) { _kinds.RemoveAt(i); return; }
        _kinds[i] = new KindRow(copy);
    }

    private void AddKind_Click(object sender, RoutedEventArgs e)
    {
        var used = _kinds.Select(r => (int)r.S.Id).ToHashSet();
        var id = Enumerable.Range(FileKinds.FirstCustomId, 255 - FileKinds.FirstCustomId).FirstOrDefault(n => !used.Contains(n));
        if (id == 0) return;
        var k = new KindSetting { Id = (byte)id, Name = "", Enabled = true };
        var w = new KindEditWindow(k, _kinds.Select(r => r.S).ToList(), isNew: true) { Owner = this };
        if (w.ShowDialog() == true && !w.Deleted) _kinds.Add(new KindRow(k));
    }

    /// <summary>같은 exe 를 관리자 권한으로 실행해 서비스를 설치·제거 (권한 확인 창 1회).</summary>
    private void RunElevated(string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            p?.WaitForExit(60000);
        }
        catch (System.ComponentModel.Win32Exception) { /* 권한 확인 창에서 취소 */ }
        UpdateEngineStatus();
    }

    private void EngineInstall_Click(object sender, RoutedEventArgs e)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "";
        RunElevated($"--install-service {sid}");
    }

    private void EngineRemove_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "색인 엔진을 제거할까요?\n\n파일 종류 보기는 색인한 폴더 안에서만 동작하게 됩니다. 파일은 영향이 없습니다.",
                "색인 엔진", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        RunElevated("--uninstall-service");
    }

    private void AddRoot_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "색인할 폴더 선택", Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var p in dlg.FolderNames)
        {
            if (_roots.Any(r => PathUtil.IsUnder(p, r)))
            {
                MessageBox.Show(this, $"{p}\n\n이미 목록에 있는 폴더 안에 있어서 따로 추가하지 않아도 됩니다.", "폴더 추가",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                continue;
            }
            if (Path.GetPathRoot(p) == p &&
                MessageBox.Show(this, $"드라이브 전체({p})를 색인하면 프로그램 파일까지 수만 개가 목록에 오를 수 있습니다.\n\n그래도 추가할까요?", "폴더 추가",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                continue;
            // 새로 고른 폴더가 기존 폴더를 감싸면 기존 것은 뺀다
            foreach (var inner in _roots.Where(r => PathUtil.IsUnder(r, p)).ToList()) _roots.Remove(inner);
            _roots.Add(p);
        }
    }

    private void RemoveKindEx_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string p }) _kindEx.Remove(p);
    }

    private void RemoveRoot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string p }) _roots.Remove(p);
    }

    private void ChangeStorage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "원본 보관함으로 쓸 폴더", InitialDirectory = Directory.Exists(_storage) ? _storage : "" };
        if (dlg.ShowDialog(this) != true) return;
        _storage = dlg.FolderName;
        ShowStorage();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.WatchedRoots = _roots.ToList();
        _settings.StorageRoot = _storage;
        _settings.KindExcludes = _kindEx.ToList();
        var kinds = _kinds.Select(r => r.S).ToList();
        KindsChanged = KindsSignature(kinds) != _kindsBefore;
        _settings.Kinds = kinds;
        DialogResult = true;
    }
}
