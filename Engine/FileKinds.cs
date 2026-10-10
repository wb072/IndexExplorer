namespace IndexExplorer.Engine;

/// <summary>
/// 파일 종류 (확장자 묶음). 엔진·화면이 함께 쓴다.
/// 기본 종류(1~12)와 골라 켜는 추가 종류(13~), 사용자가 만든 종류(64~)가 있다. 번호는 바꾸지 않는다.
/// 무엇을 켤지·확장자는 설정에서 정하고(<see cref="Services.KindSetting"/>), 엔진에는 요청마다 지금 쓰는 표(<see cref="KindDef"/>)를 보낸다.
/// </summary>
public static class FileKinds
{
    public sealed record Kind(byte Id, string Name, string SampleExt, string[] Exts, bool Hidden = false);

    public const byte None = 0;
    public const byte SystemId = 12;
    /// <summary>사용자가 만드는 종류는 이 번호부터.</summary>
    public const byte FirstCustomId = 64;

    /// <summary>처음부터 켜져 있는 기본 종류.</summary>
    public static readonly Kind[] Builtin =
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
        new(SystemId, "시스템", ".dll", ["dll", "sys", "cab", "mui", "cat", "manifest", "pyc", "pyd", "lib", "pdb", "obj"], Hidden: true),
    ];

    /// <summary>설정에서 체크하면 켜지는 추가 종류 (처음에는 꺼져 있다).</summary>
    public static readonly Kind[] Presets =
    [
        new(13, "파이썬", ".py", ["py", "pyw", "ipynb"]),
        new(14, "소스 코드", ".cs", ["cs", "js", "ts", "tsx", "jsx", "java", "kt", "c", "cpp", "cc", "h", "hpp", "go", "rs", "swift", "php", "rb", "sql", "vb", "xaml"]),
        new(15, "웹 문서", ".html", ["html", "htm", "css", "scss", "mht", "mhtml"]),
        new(16, "캐드·도면", ".dwg", ["dwg", "dxf", "dwf", "step", "stp", "igs", "iges", "stl", "ipt", "iam", "sldprt", "sldasm"]),
        new(17, "디자인", ".psd", ["psd", "ai", "sketch", "fig", "xd", "indd", "eps", "raw", "cr2", "nef"]),
        new(18, "데이터베이스", ".accdb", ["db", "sqlite", "mdb", "accdb", "dbf"]),
        new(19, "메일", ".eml", ["eml", "msg", "pst", "ost"]),
        new(20, "글꼴", ".ttf", ["ttf", "otf", "woff", "woff2", "ttc"]),
        new(21, "전자책", ".epub", ["epub", "mobi", "azw3"]),
        new(22, "디스크 이미지", ".iso", ["iso", "img", "vhd", "vhdx", "vmdk"]),
    ];

    public static IEnumerable<Kind> Defaults => Builtin.Concat(Presets);

    /// <summary>엔진이 잡동사니(프로그램 폴더) 판별에 쓰는 고정 표. 사용자 설정과 상관없다.</summary>
    public static readonly KindMap Fixed = new(Builtin.Select(k => new KindDef(k.Id, k.Exts)));

    /// <summary>화면 앱이 지금 쓰는 종류 (설정에서 켠 것, 순서대로). 엔진에도 이 표를 보낸다.</summary>
    public static IReadOnlyList<Kind> Current { get; private set; } = Builtin;
    public static KindMap CurrentMap { get; private set; } = Fixed;
    public static List<KindDef> CurrentDefs { get; private set; } = Builtin.Select(k => new KindDef(k.Id, k.Exts)).ToList();

    public static void SetCurrent(IReadOnlyList<Kind> kinds)
    {
        // 시스템 종류는 항상 (숨김, 잡동사니 판별용)
        if (!kinds.Any(k => k.Id == SystemId)) kinds = kinds.Append(Builtin[SystemId - 1]).ToList();
        Current = kinds;
        CurrentDefs = kinds.Select(k => new KindDef(k.Id, k.Exts)).ToList();
        CurrentMap = new KindMap(CurrentDefs);
    }

    /// <summary>파일 이름 → 지금 쓰는 종류 번호 (모르면 0).</summary>
    public static byte Of(ReadOnlySpan<char> fileName) => CurrentMap.Of(fileName);

    public static Kind? ById(byte id) => Current.FirstOrDefault(k => k.Id == id);

    /// <summary>"py, .ipynb  ;txt" → ["py", "ipynb", "txt"]</summary>
    public static string[] ParseExts(string text) =>
        text.Split([',', ' ', ';', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim().TrimStart('*').TrimStart('.').ToLowerInvariant())
            .Where(e => e.Length is > 0 and <= 16 && !e.Contains('.') && !e.Contains('\\'))
            .Distinct().ToArray();
}

/// <summary>엔진에 보내는 종류 하나 (번호 + 확장자).</summary>
public sealed record KindDef(byte Id, string[] Exts);

/// <summary>확장자 → 종류 번호 표. 같은 확장자가 두 종류에 있으면 뒤의 것(사용자가 추가한 종류)이 가져간다.</summary>
public sealed class KindMap
{
    private readonly Dictionary<string, byte> _byExt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte>.AlternateLookup<ReadOnlySpan<char>> _lookup;

    public KindMap(IEnumerable<KindDef> defs)
    {
        foreach (var d in defs) foreach (var e in d.Exts) _byExt[e] = d.Id;
        _lookup = _byExt.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public byte Of(ReadOnlySpan<char> fileName)
    {
        var dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1) return FileKinds.None;
        return OfExt(fileName[(dot + 1)..]);
    }

    public byte OfExt(ReadOnlySpan<char> ext) => _lookup.TryGetValue(ext, out var id) ? id : FileKinds.None;
}
