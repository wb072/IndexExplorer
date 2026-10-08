using System.IO;
using System.Windows;
using System.Windows.Interop;
using static IndexExplorer.Native.NativeMethods;

namespace IndexExplorer.Native;

/// <summary>
/// 복사·이동·휴지통은 윈도우 셸에 맡긴다: 탐색기와 같은 진행 창, 덮어쓰기 확인 창,
/// "실행 취소" 가능한 휴지통 동작을 그대로 얻는다.
/// </summary>
internal static class ShellFileOps
{
    private static IntPtr Hwnd(Window? w) => w == null ? IntPtr.Zero : new WindowInteropHelper(w).Handle;

    private static string Multi(IEnumerable<string> paths) => string.Join('\0', paths) + "\0\0";

    /// <summary>각 원본을 지정한 대상 경로로 복사/이동한다 (대상 이름까지 정확히 지정). 취소되면 false.</summary>
    public static bool Transfer(Window? owner, bool move, IReadOnlyList<string> sources, IReadOnlyList<string> targets)
    {
        if (sources.Count == 0) return true;
        var op = new SHFILEOPSTRUCT
        {
            hwnd = Hwnd(owner),
            wFunc = move ? FO_MOVE : FO_COPY,
            pFrom = Multi(sources),
            pTo = Multi(targets),
            fFlags = (ushort)(FOF_MULTIDESTFILES | FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR),
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }

    /// <summary>폴더 안으로 복사/이동 (이름 충돌은 윈도우 기본 창이 묻는다).</summary>
    public static bool TransferInto(Window? owner, bool move, IReadOnlyList<string> sources, string destDir)
    {
        if (sources.Count == 0) return true;
        var op = new SHFILEOPSTRUCT
        {
            hwnd = Hwnd(owner),
            wFunc = move ? FO_MOVE : FO_COPY,
            pFrom = Multi(sources),
            pTo = destDir + "\0\0",
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR),
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }

    /// <summary>휴지통으로 보낸다 (영구 삭제는 하지 않는다). confirm=false 면 확인 창 없이.</summary>
    public static bool Recycle(Window? owner, IReadOnlyList<string> paths, bool confirm)
    {
        if (paths.Count == 0) return true;
        var flags = (ushort)(FOF_ALLOWUNDO | FOF_WANTNUKEWARNING);
        if (!confirm) flags |= FOF_NOCONFIRMATION;
        var op = new SHFILEOPSTRUCT { hwnd = Hwnd(owner), wFunc = FO_DELETE, pFrom = Multi(paths), fFlags = flags };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }

    /// <summary>탐색기처럼 겹치지 않는 이름: "보고서.docx" → "보고서 (2).docx"</summary>
    public static string UniquePath(string dir, string name, bool isDir = false)
    {
        var path = Path.Combine(dir, name);
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var stem = isDir ? name : Path.GetFileNameWithoutExtension(name);
        var ext = isDir ? "" : Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            path = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
        }
    }

    public static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && name.Trim() is not ("." or "..") && !name.EndsWith('.') ;

    /// <summary>폴더 이름으로 쓸 수 없는 글자를 _ 로 바꾼다 (색인 폴더 → 실제 폴더 이름).</summary>
    public static string SafeName(string name)
    {
        var chars = name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).TrimEnd('.', ' ');
        return s.Length == 0 ? "_" : s;
    }
}
