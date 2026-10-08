using System.Collections.Concurrent;
using System.IO;
using System.Text;
using static IndexExplorer.Native.NativeMethods;

namespace IndexExplorer.Native;

/// <summary>
/// NTFS 파일 고유 ID. 파일을 옮기거나 이름을 바꿔도 같은 볼륨 안에서는 변하지 않으므로
/// 색인 연결을 경로가 아닌 이 ID로 추적한다. 관리자 권한이 필요 없다.
/// </summary>
internal static class FileIdentity
{
    public static bool TryGet(string path, out uint volume, out ulong fileId)
    {
        volume = 0; fileId = 0;
        using var h = CreateFile(path, 0, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid || !GetFileInformationByHandle(h, out var info)) return false;
        volume = info.VolumeSerialNumber;
        fileId = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        return true;
    }

    private static readonly ConcurrentDictionary<uint, string?> VolumeRoots = new();

    private static string? RootOf(uint serial)
    {
        if (VolumeRoots.TryGetValue(serial, out var cached) && cached != null) return cached;
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            if (GetVolumeInformation(d.RootDirectory.FullName, null, 0, out var s, out _, out _, null, 0))
                VolumeRoots[s] = d.RootDirectory.FullName;
        }
        return VolumeRoots.TryGetValue(serial, out var r) ? r : null;
    }

    /// <summary>파일 ID로 현재 경로를 찾는다 (색인 감시 범위 밖으로 옮겨진 파일 추적). 휴지통에 있으면 null.</summary>
    public static string? Resolve(uint volume, ulong fileId)
    {
        var root = RootOf(volume);
        if (root == null) return null;
        using var hint = CreateFile(root, 0, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (hint.IsInvalid) return null;
        var desc = new FILE_ID_DESCRIPTOR { Size = 24, Type = 0, FileId = (long)fileId };
        using var h = OpenFileById(hint, ref desc, 0, FILE_SHARE_ALL, IntPtr.Zero, FILE_FLAG_BACKUP_SEMANTICS);
        if (h.IsInvalid) return null;
        var sb = new StringBuilder(1024);
        var n = GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, 0);
        if (n == 0 || n >= sb.Capacity) return null;
        var p = sb.ToString();
        if (p.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) p = @"\\" + p[8..];
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
        if (p.Contains(@"\$Recycle.Bin\", StringComparison.OrdinalIgnoreCase)) return null;
        return p;
    }
}
