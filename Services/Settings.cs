using System.IO;
using System.Text.Json;

namespace IndexExplorer.Services;

/// <summary>앱 설정 (%AppData%\IndexExplorer\settings.json).</summary>
public sealed class Settings
{
    /// <summary>데이터 폴더. INDEXEXPLORER_DATA 환경 변수로 바꿀 수 있다 (자체 검증용 격리).</summary>
    public static string DataDir { get; } = Environment.GetEnvironmentVariable("INDEXEXPLORER_DATA") is { Length: > 0 } d
        ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IndexExplorer");

    private static string FilePath => Path.Combine(DataDir, "settings.json");

    /// <summary>색인 대상(감시) 폴더. 이 안의 파일은 자동으로 목록에 오르고, 분류 안 된 파일은 미분류 함에 모인다.</summary>
    public List<string> WatchedRoots { get; set; } = [];

    /// <summary>원본 보관함: 저장 위치를 지정하지 않은 색인 폴더에서 새로 만들거나 붙여넣은 파일이 같은 구조로 저장되는 곳.</summary>
    public string StorageRoot { get; set; } = "";

    public bool FirstRunDone { get; set; }

    /// <summary>파일 종류 보기에서 직접 뺀 폴더 (그 아래 전체가 안 보인다).</summary>
    public List<string> KindExcludes { get; set; } = [];
    public bool IconView { get; set; }
    public bool PreviewPane { get; set; } = true;
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1180;
    public double Height { get; set; } = 740;
    public bool Maximized { get; set; }
    public double SidebarWidth { get; set; } = 250;

    // 창 위치의 NaN(처음 실행) 값도 저장할 수 있도록
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static Settings Load()
    {
        Settings s;
        try { s = File.Exists(FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new() : new(); }
        catch { s = new(); }
        if (string.IsNullOrWhiteSpace(s.StorageRoot)) s.StorageRoot = DefaultStorageRoot();
        if (s.WatchedRoots.Count == 0 && !s.FirstRunDone) s.WatchedRoots = DefaultRoots();
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) { App.Log("settings save: " + ex.Message); }
    }

    public static string DefaultStorageRoot()
    {
        var d = DriveInfo.GetDrives().FirstOrDefault(x => x.IsReady && x.DriveType == DriveType.Fixed && x.Name.StartsWith("D", StringComparison.OrdinalIgnoreCase));
        return d != null
            ? Path.Combine(d.RootDirectory.FullName, "원본 보관함")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "원본 보관함");
    }

    public static List<string> DefaultRoots() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        KnownFolders.Downloads,
    ];

    /// <summary>실제 감시 대상 = 지정 폴더 + 원본 보관함 (중복·하위 폴더 제거).</summary>
    public IReadOnlyList<string> EffectiveRoots()
    {
        var all = WatchedRoots.Append(StorageRoot)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Length).ToList();
        var result = new List<string>();
        foreach (var p in all)
            if (!result.Any(r => PathUtil.IsUnder(p, r))) result.Add(p);
        return result;
    }
}

public static class KnownFolders
{
    public static string Downloads
    {
        get
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders");
                if (k?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string p && Directory.Exists(p)) return p;
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }
}

public static class PathUtil
{
    /// <summary>path 가 root 와 같거나 그 아래에 있는가.</summary>
    public static bool IsUnder(string path, string root)
    {
        root = Path.TrimEndingDirectorySeparator(root);
        path = Path.TrimEndingDirectorySeparator(path);
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        return path.Length > root.Length && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
               && (path[root.Length] == '\\' || root.EndsWith('\\'));
    }

    public static string Dir(string path) => Path.GetDirectoryName(path) ?? path;
}
