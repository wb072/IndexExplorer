using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace IndexExplorer.Native;

/// <summary>
/// 최근 문서 = 윈도우가 기록하는 "최근 항목"(%AppData%\Microsoft\Windows\Recent 의 바로 가기).
/// 탐색기 "즐겨찾기 › 최근 파일"과 같은 목록이다. 어떤 프로그램으로 열었든 모두 잡힌다.
/// </summary>
internal static class RecentFiles
{
    public sealed record Entry(string Path, DateTime UsedUtc);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink;

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, uint flags);
    }

    private static List<Entry>? _cache;
    private static DateTime _cachedAt;

    public static string Folder => Environment.GetFolderPath(Environment.SpecialFolder.Recent);

    private static Task? _loading;

    /// <summary>지금 가진 목록 (처음엔 null). 오래됐으면 백그라운드로 새로 읽고, 다 읽으면 onUpdated 를 UI 스레드에서 부른다.</summary>
    public static IReadOnlyList<Entry>? Current(Action? onUpdated = null)
    {
        var stale = _cache == null || (DateTime.UtcNow - _cachedAt).TotalSeconds > 5;
        // 이미 읽는 중이면 끝날 때 함께 알려 준다 (미리 읽기 도중에 연 경우)
        if (onUpdated != null && (stale || _loading != null)) Waiters.Add(onUpdated);
        if (stale && _loading == null)
        {
            var ui = System.Windows.Application.Current.Dispatcher;
            _loading = Task.Run(() => Read()).ContinueWith(_ => ui.BeginInvoke(() =>
            {
                _loading = null;
                var waiters = Waiters.ToList();
                Waiters.Clear();
                foreach (var w in waiters) w();
            }));
        }
        return _cache;
    }

    private static readonly List<Action> Waiters = [];

    public static bool IsLoading => _loading != null;

    /// <summary>최근에 쓴 순서, 실제로 존재하는 파일만 (폴더 제외). 바로 가기 해석·존재 확인이 느릴 수 있어 백그라운드에서.</summary>
    private static void Read(int max = 200)
    {
        var list = new List<Entry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var links = new DirectoryInfo(Folder).EnumerateFiles("*.lnk")
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(max * 2);
            foreach (var lnk in links)
            {
                var target = Resolve(lnk.FullName);
                if (target == null || !seen.Add(target) || !File.Exists(target)) continue;
                list.Add(new Entry(target, lnk.LastWriteTimeUtc));
                if (list.Count >= max) break;
            }
        }
        catch (Exception ex) { App.Log("recent: " + ex.Message); }
        _cache = list;
        _cachedAt = DateTime.UtcNow;
    }

    private static string? Resolve(string lnkPath)
    {
        object? link = null;
        try
        {
            link = new CShellLink();
            ((IPersistFile)link).Load(lnkPath, 0);
            var sb = new StringBuilder(1024);
            ((IShellLinkW)link).GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            return sb.Length > 0 ? sb.ToString() : null;
        }
        catch { return null; }
        finally { if (link != null) Marshal.ReleaseComObject(link); }
    }
}
