using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using IndexExplorer.Native;
using IndexExplorer.Services;

namespace IndexExplorer.ViewModels;

public enum ItemKind { IndexFolder, Dir, Drive, File }

/// <summary>가운데 목록의 한 줄.</summary>
public sealed partial class ListItem : ObservableObject
{
    public ItemKind Kind { get; init; }
    public string Path { get; set; } = "";
    public long FolderId { get; init; }
    public FileRecord? Record { get; init; }
    public DateTime? Modified { get; init; }
    public long? Size { get; init; }
    public bool Missing { get; init; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private bool _isCut;
    [ObservableProperty] private bool _isDropTarget;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DisplayImage))] private ImageSource? _thumbnail;

    /// <summary>큰 아이콘 보기: 썸네일이 오면 썸네일, 아니면 큰 아이콘.</summary>
    public ImageSource? DisplayImage => Thumbnail ?? BigIcon;

    /// <summary>원본 위치 (색인 보기에서 표시).</summary>
    public string Location { get; init; } = "";
    /// <summary>연결된 색인 폴더 이름 (원본 보기에서 표시).</summary>
    public string IndexText { get; init; } = "";

    public bool IsFolderLike => Kind != ItemKind.File;
    public string TypeName { get; init; } = "";

    public string ModifiedText => Modified is DateTime d ? d.ToLocalTime().ToString("yyyy-MM-dd tt h:mm") : "";

    public string SizeText => Size is long s ? FormatSize(s) : "";

    public static string FormatSize(long s) => s switch
    {
        < 1024 when s == 0 => "0KB",
        < 1024 => "1KB",
        _ => $"{(s + 1023) / 1024:N0}KB", // 탐색기와 같은 KB 단위 표기
    };

    /// <summary>합계·여유 공간처럼 큰 수: "1.2GB"</summary>
    public static string FormatBytes(long s) => s switch
    {
        >= 1L << 40 => $"{s / (double)(1L << 40):0.#}TB",
        >= 1L << 30 => $"{s / (double)(1L << 30):0.#}GB",
        >= 1L << 20 => $"{s / (double)(1L << 20):0.#}MB",
        _ => FormatSize(s),
    };

    private ImageSource? _icon;
    public ImageSource? Icon => _icon ??= Kind switch
    {
        ItemKind.IndexFolder => ShellImages.Icon(Settings.DataDir, true),
        ItemKind.Drive => ShellImages.Icon(Path, true, perPath: true),
        _ => ShellImages.Icon(Path, Kind == ItemKind.Dir),
    };

    /// <summary>
    /// 큰 아이콘 보기의 그림 크기. 화면에 그릴 크기 그대로(배율 반영 픽셀) 받아 와서 1:1 로 그려야 또렷하다
    /// (작게 받아 키우거나 크게 받아 줄이면 흐려진다).
    /// </summary>
    public const int TileImageSize = 64;

    private ImageSource? _bigIcon;
    public ImageSource? BigIcon => _bigIcon ??= Kind switch
    {
        ItemKind.IndexFolder => ShellImages.Icon(Settings.DataDir, true, TileImageSize),
        ItemKind.Drive => ShellImages.Icon(Path, true, TileImageSize, perPath: true),
        _ => ShellImages.Icon(Path, Kind == ItemKind.Dir, TileImageSize),
    };

    private bool _thumbRequested;

    /// <summary>큰 아이콘 보기에서 화면에 나타날 때 썸네일 요청.</summary>
    public void EnsureThumbnail()
    {
        if (_thumbRequested || Kind != ItemKind.File || Missing) return;
        _thumbRequested = true;
        ShellImages.RequestThumbnail(Path, TileImageSize, img => { if (img != null) Thumbnail = img; });
    }

    /// <summary>같은 항목인지 (새로 고침 후 선택 유지용).</summary>
    public string Key => Kind == ItemKind.IndexFolder ? "F" + FolderId : "P" + Path.ToLowerInvariant();
}
