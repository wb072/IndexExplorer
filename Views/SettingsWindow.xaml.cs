using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using IndexExplorer.Services;
using Microsoft.Win32;

namespace IndexExplorer.Views;

public partial class SettingsWindow : Window
{
    private readonly Settings _settings;
    private readonly ObservableCollection<string> _roots;
    private string _storage;

    public SettingsWindow(Settings settings, bool firstRun)
    {
        InitializeComponent();
        _settings = settings;
        _roots = new ObservableCollection<string>(settings.WatchedRoots);
        _storage = settings.StorageRoot;
        RootList.ItemsSource = _roots;
        StorageText.Text = _storage;
        if (firstRun)
        {
            Title = "색인 탐색기 시작하기";
            Heading.Text = "어떤 폴더의 파일을 정리할까요?";
            SaveBtn.Content = "시작하기";
            CancelBtn.Content = "기본값으로 시작";
        }
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

    private void RemoveRoot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string p }) _roots.Remove(p);
    }

    private void ChangeStorage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "원본 보관함으로 쓸 폴더", InitialDirectory = Directory.Exists(_storage) ? _storage : "" };
        if (dlg.ShowDialog(this) != true) return;
        _storage = dlg.FolderName;
        StorageText.Text = _storage;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.WatchedRoots = _roots.ToList();
        _settings.StorageRoot = _storage;
        DialogResult = true;
    }
}
