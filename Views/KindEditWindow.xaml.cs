using System.Windows;
using System.Windows.Controls;
using IndexExplorer.Engine;
using IndexExplorer.Native;
using IndexExplorer.Services;

namespace IndexExplorer.Views;

/// <summary>파일 종류 하나의 이름·확장자 고치기 (새 종류 만들기 겸용).</summary>
public partial class KindEditWindow : Window
{
    private readonly KindSetting _kind;
    private readonly IReadOnlyList<KindSetting> _others;

    /// <summary>사용자가 만든 종류를 지우기로 함.</summary>
    public bool Deleted { get; private set; }

    public KindEditWindow(KindSetting kind, IReadOnlyList<KindSetting> others, bool isNew)
    {
        InitializeComponent();
        _kind = kind;
        _others = others.Where(o => o.Id != kind.Id).ToList();
        Heading.Text = isNew ? "새 종류" : kind.Name;
        NameBox.Text = kind.Name;
        ExtBox.Text = string.Join(", ", kind.Exts);
        DeleteBtn.Visibility = kind.IsCustom && !isNew ? Visibility.Visible : Visibility.Collapsed;
        ResetBtn.Visibility = kind.IsCustom ? Visibility.Collapsed : Visibility.Visible;
        UpdateIcon();
        Loaded += (_, _) => { (isNew ? NameBox : ExtBox).Focus(); (isNew ? NameBox : ExtBox).SelectAll(); };
    }

    private void UpdateIcon()
    {
        var exts = FileKinds.ParseExts(ExtBox.Text);
        KindIcon.Source = exts.Length > 0 ? ShellImages.Icon("x." + exts[0], false, 32) : null;
        // 아이콘이 없으면 자리도 비우지 않는다 (제목이 밀리지 않게)
        KindIcon.Visibility = KindIcon.Source == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ExtBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateIcon();
        var exts = FileKinds.ParseExts(ExtBox.Text);
        // 다른 종류와 겹치는 확장자는 이 종류가 가져간다고 알린다
        var taken = exts.Select(x => (x, k: _others.FirstOrDefault(o => o.Enabled && o.Exts.Contains(x))))
            .Where(p => p.k != null).Select(p => $".{p.x} ({p.k!.Name})").ToList();
        ExtHint.Text = taken.Count > 0
            ? $"다른 종류에도 있는 확장자: {string.Join(", ", taken)}. 겹치는 확장자는 설정 목록에서 더 뒤에 있는 종류에 나옵니다."
            : "쉼표나 띄어쓰기로 나눕니다. 예: py, ipynb";
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (FileKinds.Defaults.FirstOrDefault(d => d.Id == _kind.Id) is not { } d) return;
        NameBox.Text = d.Name;
        ExtBox.Text = string.Join(", ", d.Exts);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        Deleted = true;
        DialogResult = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var exts = FileKinds.ParseExts(ExtBox.Text);
        if (name.Length == 0) { NameBox.Focus(); return; }
        if (exts.Length == 0) { ExtHint.Text = "확장자를 하나 이상 넣으세요. 예: py"; ExtBox.Focus(); return; }
        _kind.Name = name;
        _kind.Exts = [.. exts];
        DialogResult = true;
    }
}
