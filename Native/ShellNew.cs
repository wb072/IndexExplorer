using System.IO;
using Microsoft.Win32;

namespace IndexExplorer.Native;

/// <summary>탐색기 "새로 만들기" 메뉴에 등록된 문서 종류 (Word, Excel, 한글, 텍스트 …).</summary>
internal sealed record ShellNewItem(string Extension, string DisplayName, Func<string, bool> Create);

internal static class ShellNew
{
    private static List<ShellNewItem>? _cache;

    // 탐색기가 무시하는 항목
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
        { "Folder", ".lnk", ".library-ms", "Briefcase", ".contact", ".bmp", ".rtf", ".zip" };

    private static readonly string[] Fallback = [".docx", ".xlsx", ".pptx", ".hwp", ".hwpx", ".txt"];

    public static IReadOnlyList<ShellNewItem> Items()
    {
        if (_cache != null) return _cache;
        var exts = new List<string>();
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Discardable\PostSetup\ShellNew");
            if (k?.GetValue("Classes") is string[] classes) exts.AddRange(classes);
        }
        catch { }
        foreach (var f in Fallback) if (!exts.Contains(f, StringComparer.OrdinalIgnoreCase)) exts.Add(f);

        var list = new List<ShellNewItem>();
        foreach (var ext in exts)
        {
            if (!ext.StartsWith('.') || Skip.Contains(ext)) continue;
            var item = Read(ext);
            if (item != null) list.Add(item);
        }
        // 자주 쓰는 순서: 오피스 → 한글 → 나머지(이름순), 텍스트는 끝
        int Rank(ShellNewItem i) => i.Extension.ToLowerInvariant() switch
        {
            ".docx" => 0, ".xlsx" => 1, ".pptx" => 2, ".hwp" => 3, ".hwpx" => 4, ".txt" => 99, _ => 50,
        };
        _cache = list.OrderBy(Rank).ThenBy(i => i.DisplayName).ToList();
        return _cache;
    }

    private static ShellNewItem? Read(string ext)
    {
        using var extKey = Registry.ClassesRoot.OpenSubKey(ext);
        if (extKey == null) return null;
        var progId = extKey.GetValue(null) as string;
        RegistryKey? sn = null;
        if (!string.IsNullOrEmpty(progId)) sn = extKey.OpenSubKey(progId + @"\ShellNew");
        sn ??= extKey.OpenSubKey("ShellNew");
        if (sn == null)
        {
            // 하위 ProgID 아래에 숨어 있는 경우
            foreach (var sub in extKey.GetSubKeyNames())
            {
                sn = extKey.OpenSubKey(sub + @"\ShellNew");
                if (sn != null) break;
            }
        }
        if (sn == null) return null;
        using (sn)
        {
            Func<string, bool>? create = null;
            if (sn.GetValue("FileName") is string tpl)
            {
                var src = ResolveTemplate(tpl);
                if (src != null) create = p => { File.Copy(src, p, false); return true; };
            }
            if (create == null && sn.GetValue("Data") is byte[] data)
                create = p => { File.WriteAllBytes(p, data); return true; };
            if (create == null && sn.GetValueNames().Contains("NullFile", StringComparer.OrdinalIgnoreCase))
                create = p => { using (File.Create(p)) { } return true; };
            if (create == null) return null;

            var name = ShellImages.TypeName("x" + ext, false);
            return new ShellNewItem(ext, name, create);
        }
    }

    private static string? ResolveTemplate(string tpl)
    {
        tpl = Environment.ExpandEnvironmentVariables(tpl);
        if (Path.IsPathRooted(tpl)) return File.Exists(tpl) ? tpl : null;
        string[] dirs =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Templates),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonTemplates),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ShellNew"),
        ];
        foreach (var d in dirs)
        {
            var p = Path.Combine(d, tpl);
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
