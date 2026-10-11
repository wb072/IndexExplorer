using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using IndexExplorer.Native;
using IndexExplorer.Services;

namespace IndexExplorer.Views;

/// <summary>
/// 주소 줄 드롭다운 (크롬 주소창처럼): 치는 동안 맞는 폴더를 아래에 보여 준다.
///  - 경로를 치면 (C:\…, D:\개발\In, %USERPROFILE%\…) 그 위치의 하위 폴더 중 이름이 맞는 것 (탐색기 자동 완성과 같음)
///  - "색인\…"을 치면 그 아래 색인 폴더
///  - 이름만 치면 이름이 맞는 색인 폴더와 원본 폴더 (색인할 폴더 안, 디스크를 훑지 않고 색인 목록에서)
/// ↑↓로 고르고 Enter로 이동, Tab은 그 경로를 칸에 채움, Esc는 닫기. 고르지 않고 Enter면 예전처럼 친 경로로 이동.
/// </summary>
public partial class MainWindow
{
    public sealed record AddressSuggestion(string Title, string Sub, ImageSource? Icon, Location Target, string Fill);

    private Popup? _addrPopup;
    private ListBox? _addrList;
    private HashSet<string>? _knownDirs;   // 색인할 폴더 안 원본 폴더 (주소 편집을 시작할 때 색인 목록에서 만든다)
    private int _addrVersion;

    private void EnsureAddressPopup()
    {
        if (_addrPopup != null) return;
        _addrList = new ListBox
        {
            MaxHeight = 360, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Focusable = false,
            Foreground = (Brush)FindResource("InkBrush"),
            ItemTemplate = (DataTemplate)FindResource("AddressRowTemplate"),
            ItemContainerStyle = (Style)FindResource("AddressListItem"),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_addrList, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(_addrList, true);
        VirtualizingPanel.SetVirtualizationMode(_addrList, VirtualizationMode.Recycling);
        // 누르는 순간 이동 (주소 칸의 포커스가 빠져 닫히기 전에)
        _addrList.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is AddressSuggestion s)
            {
                e.Handled = true;
                GoSuggestion(s);
            }
        };
        var border = new Border
        {
            Background = Brushes.White, BorderBrush = (Brush)FindResource("HairlineBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(0, 4, 0, 4), Margin = new Thickness(8, 2, 8, 10), Child = _addrList,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.12, Color = Colors.Black },
        };
        _addrPopup = new Popup
        {
            Child = border, PlacementTarget = CrumbBox, Placement = PlacementMode.Bottom, StaysOpen = true,
            AllowsTransparency = true, HorizontalOffset = -8, Focusable = false,
        };
    }

    private void CloseAddressPopup() { if (_addrPopup != null) _addrPopup.IsOpen = false; }

    /// <summary>색인할 폴더 안의 원본 폴더 목록 (파일 경로에서 조상 폴더까지). 7만 개 파일 기준 수십 ms.</summary>
    private void BuildKnownDirs()
    {
        var roots = _settings.EffectiveRoots();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in roots) set.Add(r);
        foreach (var f in _store.Files)
        {
            for (var d = f.Dir; d.Length > 3 && set.Add(d); d = PathUtil.Dir(d))
                if (roots.Any(r => r.Equals(d, StringComparison.OrdinalIgnoreCase))) break;
        }
        _knownDirs = set;
    }

    private void AddressBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (AddressBox.Visibility != Visibility.Visible || !AddressBox.IsKeyboardFocused) return;
        UpdateAddressSuggestions(AddressBox.Text.Trim().Trim('"'));
    }

    private static bool LooksLikePath(string t) =>
        t.Length >= 2 && (t[1] == ':' || t.StartsWith(@"\\") || t.StartsWith('%'));

    private async void UpdateAddressSuggestions(string text)
    {
        var version = ++_addrVersion;
        if (text.Length == 0) { CloseAddressPopup(); return; }
        List<AddressSuggestion> list;
        if (LooksLikePath(text))
        {
            // 경로: 그 위치의 하위 폴더 (느린 드라이브에서 멈추지 않게 백그라운드)
            var expanded = Environment.ExpandEnvironmentVariables(text);
            var parent = expanded.EndsWith('\\') ? expanded : Path.GetDirectoryName(expanded) ?? expanded;
            var prefix = expanded.EndsWith('\\') ? "" : Path.GetFileName(expanded);
            var dirs = await Task.Run(() => ChildDirs(parent, prefix));
            if (version != _addrVersion) return;
            var folderIcon = ShellImages.Icon(Settings.DataDir, true);
            list = dirs.Select(d => new AddressSuggestion(DirLeaf(d), d, folderIcon, new Location(LocKind.Dir, Path: d), d)).ToList();
        }
        else if (text.StartsWith("색인", StringComparison.Ordinal) && (text.Length == 2 || text[2] is '\\' or '/' or ' ' or '›'))
            list = IndexPathSuggestions(text);
        else list = NameSuggestions(text);

        if (version != _addrVersion) return;
        if (list.Count == 0) { CloseAddressPopup(); return; }
        EnsureAddressPopup();
        _addrList!.ItemsSource = list;
        _addrList.SelectedIndex = -1;
        ((Border)_addrPopup!.Child).Width = CrumbBox.ActualWidth;
        _addrPopup.IsOpen = true;
    }

    /// <summary>parent 바로 아래 폴더 중 이름이 prefix 로 시작하는 것 먼저, 그다음 들어 있는 것 (숨김·시스템 폴더 제외).</summary>
    private static List<string> ChildDirs(string parent, string prefix)
    {
        try
        {
            if (!Directory.Exists(parent)) return [];
            var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
            var all = Directory.EnumerateDirectories(parent, "*", opts).Take(5000).ToList();
            var starts = all.Where(d => Path.GetFileName(d).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            var contains = prefix.Length == 0 ? [] : all.Where(d => !Path.GetFileName(d).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                                                   && Path.GetFileName(d).Contains(prefix, StringComparison.OrdinalIgnoreCase));
            return starts.Order(NaturalComparer.Instance).Concat(contains.Order(NaturalComparer.Instance)).Take(50).ToList();
        }
        catch { return []; }
    }

    private List<AddressSuggestion> IndexPathSuggestions(string text)
    {
        var parts = text.Split(['\\', '/', '›'], StringSplitOptions.TrimEntries).Skip(1).ToList();
        var prefix = parts.Count > 0 ? parts[^1] : "";
        long? cur = null;
        foreach (var name in parts.Take(Math.Max(0, parts.Count - 1)))
        {
            if (name.Length == 0) continue;
            var next = _store.Children(cur).FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (next == null) return [];
            cur = next.Id;
        }
        var icon = ShellImages.Icon(Settings.DataDir, true);
        return _store.Children(cur).Where(f => f.Name.Contains(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Take(50)
            .Select(f => IndexSuggestion(f.Id, icon)).ToList();
    }

    private AddressSuggestion IndexSuggestion(long id, ImageSource? icon)
    {
        var chain = _store.Chain(id).Select(f => f.Name).ToList();
        return new AddressSuggestion(chain[^1], "색인 › " + string.Join(" › ", chain), icon, new Location(LocKind.IndexFolder, id), "색인\\" + string.Join("\\", chain));
    }

    /// <summary>이름만 쳤을 때: 색인 폴더 + 색인할 폴더 안 원본 폴더 + 바탕 화면·다운로드·문서. 이름이 그 글자로 시작하는 것 먼저, 짧은 경로 먼저.</summary>
    private List<AddressSuggestion> NameSuggestions(string text)
    {
        if (_knownDirs == null) BuildKnownDirs();
        var icon = ShellImages.Icon(Settings.DataDir, true);
        static int Rank(string name, string q) => name.Equals(q, StringComparison.OrdinalIgnoreCase) ? 0 : name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 1 : 2;

        var index = _store.Folders.Where(f => f.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Rank(f.Name, text)).ThenBy(f => f.Name, NaturalComparer.Instance).Take(15)
            .Select(f => IndexSuggestion(f.Id, icon));

        var quick = new[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "바탕 화면"),
            (KnownFolders.Downloads, "다운로드"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "문서"),
        }.Where(q => q.Item1.Length > 0 && (q.Item2.Contains(text, StringComparison.OrdinalIgnoreCase) || DirLeaf(q.Item1).Contains(text, StringComparison.OrdinalIgnoreCase)))
         .Select(q => new AddressSuggestion(q.Item2, q.Item1, ShellImages.Icon(q.Item1, true, perPath: true), new Location(LocKind.Dir, Path: q.Item1), q.Item1));

        var dirs = _knownDirs!.Where(d => DirLeaf(d).Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => Rank(DirLeaf(d), text)).ThenBy(d => d.Length).Take(40)
            .Select(d => new AddressSuggestion(DirLeaf(d), d, icon, new Location(LocKind.Dir, Path: d), d));

        return quick.Concat(index).Concat(dirs).Take(50).ToList();
    }

    private void GoSuggestion(AddressSuggestion s)
    {
        CloseAddressPopup();
        EndAddressEdit();
        Navigate(s.Target);
    }

    /// <summary>주소 칸 키: 드롭다운이 열려 있으면 ↑↓·Enter·Tab·Esc 를 먼저 처리. 처리했으면 true.</summary>
    private bool AddressPopupKey(Key key)
    {
        if (_addrPopup is not { IsOpen: true } || _addrList == null || _addrList.Items.Count == 0) return false;
        switch (key)
        {
            case Key.Down:
                _addrList.SelectedIndex = Math.Min(_addrList.Items.Count - 1, _addrList.SelectedIndex + 1);
                _addrList.ScrollIntoView(_addrList.SelectedItem);
                return true;
            case Key.Up:
                _addrList.SelectedIndex = Math.Max(-1, _addrList.SelectedIndex - 1);
                if (_addrList.SelectedItem != null) _addrList.ScrollIntoView(_addrList.SelectedItem);
                return true;
            case Key.Enter when _addrList.SelectedItem is AddressSuggestion s:
                GoSuggestion(s);
                return true;
            case Key.Tab when (_addrList.SelectedItem ?? _addrList.Items[0]) is AddressSuggestion t:
                // 그 경로를 칸에 채우고 더 쳐 나갈 수 있게 (끝에 \ 를 붙여 하위 폴더가 바로 보인다)
                AddressBox.Text = t.Fill.EndsWith('\\') ? t.Fill : t.Fill + "\\";
                AddressBox.CaretIndex = AddressBox.Text.Length;
                return true;
            case Key.Escape:
                CloseAddressPopup();
                return true;
        }
        return false;
    }

    // ----- 자체 검증 -----
    internal List<AddressSuggestion> TestAddressSuggestions() => _addrPopup is { IsOpen: true } && _addrList?.ItemsSource is List<AddressSuggestion> l ? l : [];
    internal void TestShotAddress(string name)
    {
        if (_addrPopup?.Child is not FrameworkElement fe) return;
        ShotElement(fe, name);
    }
}
