using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IndexExplorer.Native;
using IndexExplorer.Services;
using IndexExplorer.ViewModels;

namespace IndexExplorer.Views;

/// <summary>오른쪽 미리 보기·정보 창: 원본 위치와 색인 위치를 한눈에, 눌러서 바로 이동.</summary>
public partial class MainWindow
{
    private int _previewToken;

    private void UpdatePreview()
    {
        if (!_settings.PreviewPane || !IsInitialized) return;
        _previewToken++;
        PvBody.Children.Clear();
        var sel = Selected();
        if (sel.Count == 0) ShowLocationInfo();
        else if (sel.Count == 1) ShowItemInfo(sel[0]);
        else ShowMultiInfo(sel);
    }

    // ----- 구성 요소 -----

    private void SetPreviewImage(ImageSource? img, bool isIcon)
    {
        PvImage.Source = img;
        PvThumbBox.Visibility = img == null ? Visibility.Collapsed : Visibility.Visible;
        PvImage.MaxWidth = isIcon ? 72 : double.PositiveInfinity;
        PvImage.MaxHeight = isIcon ? 72 : double.PositiveInfinity;
    }

    private void Label(string text) => PvBody.Children.Add(new TextBlock
    {
        Text = text, FontSize = 11.5, Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 14, 0, 3),
    });

    private void Value(string text) => PvBody.Children.Add(new TextBlock
    {
        Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("InkBrush"),
    });

    private void Para(string text) => PvBody.Children.Add(new TextBlock
    {
        Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("SecondaryBrush"),
        Margin = new Thickness(0, 10, 0, 0), LineHeight = 19,
    });

    private void Link(string text, Action go, string? tooltip = null, ImageSource? icon = null)
    {
        var panel = new DockPanel();
        if (icon != null)
            panel.Children.Add(new Image { Source = icon, Width = 14, Height = 14, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Top });
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 });
        var b = new Button { Style = (Style)FindResource("LinkButton"), Content = panel, ToolTip = tooltip, Margin = new Thickness(0, 1, 0, 2) };
        b.Click += (_, _) => go();
        PvBody.Children.Add(b);
    }

    private void Badge(string text, bool violet)
    {
        PvBody.Children.Add(new Border
        {
            Background = (Brush)FindResource(violet ? "PinBadgeBgBrush" : "SidebarBrush"),
            CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 2, 7, 3), Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource(violet ? "PinBadgeBrush" : "SecondaryBrush") },
        });
    }

    private Button ActionButton(string text, Action go, bool primary = false)
    {
        var b = new Button { Style = (Style)FindResource(primary ? "PrimaryButton" : "PearlButton"), Content = text, Margin = new Thickness(0, 12, 8, 0) };
        b.Click += (_, _) => go();
        return b;
    }

    private void Buttons(params Button[] buttons)
    {
        var wrap = new WrapPanel();
        foreach (var b in buttons) wrap.Children.Add(b);
        PvBody.Children.Add(wrap);
    }

    // ----- 내용 -----

    private void ShowItemInfo(ListItem item)
    {
        PvTitle.Text = item.Name;
        PvSub.Text = item.TypeName;
        switch (item.Kind)
        {
            case ItemKind.IndexFolder:
                ShowIndexFolderInfo(item.FolderId, setHeader: true);
                return;
            case ItemKind.Drive:
                SetPreviewImage(ShellImages.Icon(item.Path, true, 48, perPath: true), true);
                Label("공간");
                Value(item.Location);
                return;
            case ItemKind.Dir:
                SetPreviewImage(item.BigIcon, true);
                Label("원본 위치");
                Value(item.Path);
                if (item.Modified != null) { Label("수정한 날짜"); Value(item.ModifiedText); }
                Buttons(ActionButton("색인에 추가", () => BuildFolderPicker(id => LinkItems(id, [item])).IsOpen = true));
                return;
        }

        // 파일
        SetPreviewImage(item.BigIcon, true);
        if (!item.Missing)
        {
            var token = _previewToken;
            ShellImages.RequestThumbnail(item.Path, 256, img =>
            {
                if (token == _previewToken && img != null) SetPreviewImage(img, false);
            });
        }
        PvSub.Text = item.Size is long s ? $"{item.TypeName} · {ListItem.FormatBytes(s)}" : item.TypeName;
        if (item.Modified != null) { Label("수정한 날짜"); Value(item.ModifiedText); }

        Label("원본 위치");
        if (item.Missing)
        {
            Badge("원본 없음 · 삭제되었거나 다른 드라이브로 옮겨졌습니다", false);
            Value(PathUtil.Dir(item.Path));
        }
        else Link(PathUtil.Dir(item.Path), () => RevealOriginal(item), "원본 폴더로 이동");

        Label("색인 위치");
        var rec = item.Record ?? _store.ByPath(item.Path);
        if (rec == null || rec.Folders.Count == 0) Badge("미분류 · 아직 어떤 색인에도 없습니다", false);
        else
        {
            var folderIcon = ShellImages.Icon(Settings.DataDir, true);
            foreach (var id in rec.Folders.OrderBy(id => _store.ChainText(id), NaturalComparer.Instance))
            {
                var fid = id;
                Link(_store.ChainText(fid), () => Navigate(new Location(LocKind.IndexFolder, fid), selectPath: item.Path), icon: folderIcon);
            }
        }
        var add = ActionButton("색인에 추가", () => { });
        add.Click += (_, _) =>
        {
            var m = BuildFolderPicker(id => LinkItems(id, [item]));
            m.PlacementTarget = add;
            m.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            m.IsOpen = true;
        };
        var buttons = new List<Button> { add };
        if (!item.Missing) buttons.Add(ActionButton("열기", () => Open([item])));
        Buttons(buttons.ToArray());
    }

    private void ShowMultiInfo(List<ListItem> sel)
    {
        SetPreviewImage(ShellImages.Icon(Settings.DataDir, true, 48), true);
        PvTitle.Text = $"{sel.Count:N0}개 항목";
        var size = sel.Where(i => i.Size.HasValue).Sum(i => i.Size!.Value);
        PvSub.Text = size > 0 ? $"합계 {ListItem.FormatBytes(size)}" : "";
        var files = sel.Where(i => i.Kind is ItemKind.File or ItemKind.Dir).ToList();
        if (files.Count == 0) return;
        Para("왼쪽 색인 폴더로 끌어다 놓거나 [색인에 추가]로 한 번에 분류할 수 있습니다. 파일은 원래 위치에 그대로 있습니다.");
        var add = ActionButton("색인에 추가", () => { }, primary: true);
        add.Click += (_, _) =>
        {
            var m = BuildFolderPicker(id => LinkItems(id, files));
            m.PlacementTarget = add;
            m.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            m.IsOpen = true;
        };
        Buttons(add);
    }

    private void ShowLocationInfo()
    {
        switch (_loc.Kind)
        {
            case LocKind.IndexFolder:
                ShowIndexFolderInfo(_loc.FolderId, setHeader: true);
                break;
            case LocKind.IndexRoot:
                SetPreviewImage((ImageSource)FindResource("IconIndexRoot"), true);
                PvTitle.Text = "색인";
                PvSub.Text = $"색인 폴더 {_store.Folders.Count():N0}개";
                Para("파일이 디스크 어디에 있든, 찾을 때 떠올리는 분류로 묶어 둡니다. 한 파일을 여러 색인에 넣을 수 있고, 파일은 옮기지 않습니다.");
                Label("원본 보관함");
                Link(_settings.StorageRoot, () => GoDirOrToast(_settings.StorageRoot));
                Para("저장 위치를 지정하지 않은 색인 폴더에서 새로 만들거나 붙여넣은 파일은 이곳에 색인과 같은 폴더 구조로 저장됩니다.");
                break;
            case LocKind.Inbox:
                SetPreviewImage((ImageSource)FindResource("IconInbox"), true);
                PvTitle.Text = "미분류 함";
                PvSub.Text = $"파일 {_items.Count:N0}개";
                Para("색인할 폴더에 있지만 아직 어떤 색인에도 연결되지 않은 파일입니다.");
                Para("왼쪽 색인 폴더로 끌어다 놓거나 [색인에 추가]를 누르면 분류됩니다. 분류한 파일은 이 목록에서 사라집니다.");
                break;
            case LocKind.AllFiles:
                SetPreviewImage((ImageSource)FindResource("IconAllFiles"), true);
                PvTitle.Text = "모든 파일";
                PvSub.Text = $"파일 {_items.Count:N0}개";
                Label("색인할 폴더");
                foreach (var r in _settings.EffectiveRoots())
                {
                    var p = r;
                    Link(p, () => GoDirOrToast(p));
                }
                Buttons(ActionButton("폴더 추가·변경", () => Settings_Click(this, new RoutedEventArgs())));
                break;
            case LocKind.Search:
                SetPreviewImage(null, true);
                PvTitle.Text = $"‘{_loc.Query}’";
                PvSub.Text = $"결과 {_items.Count:N0}개";
                Para("파일 이름과 색인 폴더 이름에서 찾습니다. 띄어 쓴 단어는 모두 포함된 항목만 보여줍니다.");
                break;
            case LocKind.Dir:
                SetPreviewImage(ShellImages.Icon(_loc.Path, true, 48, perPath: true), true);
                PvTitle.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(_loc.Path)) is { Length: > 0 } n ? n : ShellImages.DisplayName(_loc.Path);
                PvSub.Text = $"항목 {_items.Count:N0}개";
                Label("원본 위치");
                Value(_loc.Path);
                var watched = _settings.EffectiveRoots().Any(r => PathUtil.IsUnder(_loc.Path, r));
                Label("색인 대상");
                Badge(watched ? "예 · 새 파일이 미분류 함에 모입니다" : "아니요 · 필요하면 끌어다 색인에 연결하세요", false);
                break;
            case LocKind.ThisPC:
                SetPreviewImage(ShellImages.Icon("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", true, 48, perPath: true), true);
                PvTitle.Text = "내 PC";
                PvSub.Text = $"드라이브 {_items.Count}개";
                break;
        }
    }

    private void ShowIndexFolderInfo(long id, bool setHeader)
    {
        var f = _store.Folder(id);
        if (f == null) return;
        if (setHeader)
        {
            SetPreviewImage(ShellImages.Icon(Settings.DataDir, true, 48), true);
            PvTitle.Text = f.Name;
            PvSub.Text = $"색인 폴더 · 파일 {_store.CountIn(id):N0}개 · 하위 폴더 {_store.Children(id).Count():N0}개";
        }
        if (f.ParentId is long p)
        {
            Label("색인 위치");
            Value("색인 › " + _store.ChainText(p));
        }

        var (dir, pinnedBy) = StorageOf(id);
        Label("새 파일 저장 위치");
        Link(dir, () => OpenStorageDir(id), "이 색인에서 새로 만들거나 붙여넣은 파일이 저장되는 실제 폴더");
        if (pinnedBy == null) Badge("기본 · 원본 보관함에 색인과 같은 구조로", false);
        else if (pinnedBy.Id == id) Badge("이 폴더에 직접 지정", true);
        else Badge($"‘{pinnedBy.Name}’에 지정한 위치를 이어받음", true);

        var buttons = new List<Button> { ActionButton("저장 위치 지정…", () => ChooseStoragePath(id)) };
        if (!string.IsNullOrEmpty(f.StoragePath)) buttons.Add(ActionButton("기본으로", () => ClearStoragePath(id)));
        Buttons(buttons.ToArray());
    }

    private void GoDirOrToast(string dir)
    {
        if (Directory.Exists(dir)) Navigate(new Location(LocKind.Dir, Path: dir));
        else Toast("아직 폴더가 없습니다", $"{dir}\n처음 파일을 저장할 때 자동으로 만들어집니다.");
    }
}
