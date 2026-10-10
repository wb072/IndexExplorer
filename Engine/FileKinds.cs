namespace IndexExplorer.Engine;

/// <summary>파일 종류 (확장자 묶음). 엔진·화면이 함께 쓴다. 번호는 저장 파일에 기록되므로 바꾸지 않는다.</summary>
public static class FileKinds
{
    public sealed record Kind(byte Id, string Name, string SampleExt, string[] Exts, bool Hidden = false);

    public const byte None = 0;

    public static readonly Kind[] All =
    [
        new(1, "엑셀", ".xlsx", ["xls", "xlsx", "xlsm", "xlsb", "csv"]),
        new(2, "워드", ".docx", ["doc", "docx", "rtf", "odt"]),
        new(3, "파워포인트", ".pptx", ["ppt", "pptx", "pps", "ppsx"]),
        new(4, "PDF", ".pdf", ["pdf"]),
        new(5, "한글", ".hwp", ["hwp", "hwpx"]),
        new(6, "텍스트", ".txt", ["txt", "md", "log", "ini", "json", "xml", "yaml", "yml"]),
        new(7, "이미지", ".png", ["jpg", "jpeg", "png", "gif", "bmp", "webp", "heic", "tif", "tiff", "svg", "ico"]),
        new(8, "동영상", ".mp4", ["mp4", "avi", "mkv", "mov", "wmv", "webm", "m4v"]),
        new(9, "음악·소리", ".mp3", ["mp3", "wav", "m4a", "flac", "aac", "ogg", "wma"]),
        new(10, "압축", ".zip", ["zip", "7z", "rar", "gz", "tar", "alz", "egg"]),
        new(11, "프로그램", ".exe", ["exe", "msi", "bat", "cmd", "ps1"]),
        new(12, "시스템", ".dll", ["dll", "sys", "cab", "mui", "cat", "manifest", "pyc", "pyd", "lib", "pdb", "obj"], Hidden: true),
    ];

    private static readonly Dictionary<string, byte> ByExt = BuildMap();

    private static Dictionary<string, byte> BuildMap()
    {
        var map = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in All) foreach (var e in k.Exts) map[e] = k.Id;
        return map;
    }

    private static readonly Dictionary<string, byte>.AlternateLookup<ReadOnlySpan<char>> Lookup = ByExt.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>파일 이름 → 종류 번호 (모르면 0).</summary>
    public static byte Of(ReadOnlySpan<char> fileName)
    {
        var dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1) return None;
        return Lookup.TryGetValue(fileName[(dot + 1)..], out var id) ? id : None;
    }

    public static Kind? ById(byte id) => id >= 1 && id <= All.Length ? All[id - 1] : null;
    public static Kind? ByName(string name) => All.FirstOrDefault(k => k.Name == name);
}
