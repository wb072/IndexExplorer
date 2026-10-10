using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using static IndexExplorer.Native.NativeMethods;

namespace IndexExplorer.Native;

/// <summary>
/// 윈도우 셸이 그리는 아이콘·썸네일을 그대로 가져온다 (탐색기와 같은 컬러 아이콘).
/// 아이콘은 확장자별로 캐시해 목록이 수천 개여도 즉시 그려지고,
/// 썸네일은 별도 STA 스레드에서 최근 요청부터 처리해 화면이 멈추지 않는다.
/// </summary>
internal static class ShellImages
{
    private const int SIIGBF_RESIZETOFIT = 0, SIIGBF_BIGGERSIZEOK = 1, SIIGBF_ICONONLY = 4;
    private static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    /// <summary>모니터 배율 (1.0 = 100%). 선명한 아이콘을 위해 픽셀 크기를 키워 요청한다.</summary>
    public static double Scale { get; set; } = 1.0;

    private static readonly HashSet<string> PerFileExts = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".lnk", ".ico", ".url", ".cur", ".ani", ".msc", ".scr", ".appref-ms" };

    private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>작은 아이콘 (UI 스레드에서 동기 호출, 캐시됨).</summary>
    public static ImageSource? Icon(string path, bool isDir, int size = 16, bool perPath = false)
    {
        string key;
        if (perPath) key = path;
        else if (isDir) key = "<dir>";
        else
        {
            var ext = Path.GetExtension(path);
            key = PerFileExts.Contains(ext) || ext.Length == 0 ? path : ext;
        }
        key += "|" + size;
        if (IconCache.TryGetValue(key, out var img)) return img;

        // 일반 폴더 아이콘은 특수 폴더(바탕 화면 등)가 아닌 평범한 폴더에서 얻는다
        img = Load(isDir && !perPath ? Path.GetTempPath() : path, size, SIIGBF_ICONONLY);
        if (img == null && !isDir && !File.Exists(path))
            img = Load(Path.Combine(Path.GetTempPath(), "x" + Path.GetExtension(path)), size, SIIGBF_ICONONLY, fake: true);
        IconCache[key] = img;
        return img;
    }

    private static ImageSource? Load(string path, int size, int flags, bool fake = false)
    {
        if (fake)
        {
            // 존재하지 않는 파일: 확장자만으로 아이콘을 얻는다
            var info = new SHFILEINFO();
            var r = SHGetFileInfo(path, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x100 | SHGFI_USEFILEATTRIBUTES);
            if (r == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try
            {
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DestroyIcon(info.hIcon); }
        }
        var px = (int)Math.Round(size * Scale);
        IntPtr hbm = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, IID_IShellItemImageFactory, out var obj);
            var f = (IShellItemImageFactory)obj;
            try
            {
                if (f.GetImage(new SIZE(px, px), flags, out hbm) != 0 || hbm == IntPtr.Zero) return null;
            }
            finally { Marshal.ReleaseComObject(obj); }
            return ToBitmapSource(hbm);
        }
        catch { return null; }
        finally { if (hbm != IntPtr.Zero) DeleteObject(hbm); }
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    private static BitmapSource? ToBitmapSource(IntPtr hbm)
    {
        var ds = new DIBSECTION();
        if (GetObject(hbm, Marshal.SizeOf<DIBSECTION>(), ref ds) == 0 || ds.dsBm.bmBits == IntPtr.Zero) return null;
        int w = ds.dsBm.bmWidth, h = ds.dsBm.bmHeight, stride = ds.dsBm.bmWidthBytes;
        if (ds.dsBm.bmBitsPixel != 32) return null;
        var buf = new byte[stride * h];
        var bottomUp = ds.dsBmih.biHeight > 0;
        for (int y = 0; y < h; y++)
        {
            var srcRow = bottomUp ? h - 1 - y : y;
            Marshal.Copy(ds.dsBm.bmBits + srcRow * stride, buf, y * stride, stride);
        }
        // 일부 썸네일(JPEG 등)은 알파가 모두 0으로 오므로 불투명으로 보정
        bool anyAlpha = false;
        for (int i = 3; i < buf.Length; i += 4) if (buf[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (int i = 3; i < buf.Length; i += 4) buf[i] = 255;
        var dpi = 96 * Scale;
        var bmp = BitmapSource.Create(w, h, dpi, dpi, PixelFormats.Pbgra32, null, buf, stride);
        bmp.Freeze();
        return bmp;
    }

    // ===== 썸네일 (백그라운드 STA) =====
    private static readonly ConcurrentStack<(string path, int size, Action<ImageSource?> done)> Pending = new();
    private static readonly SemaphoreSlim Signal = new(0);
    private static Thread? _worker;
    private static Dispatcher? _ui;

    public static void RequestThumbnail(string path, int size, Action<ImageSource?> done)
    {
        _ui ??= Application.Current.Dispatcher;
        if (_worker == null)
        {
            _worker = new Thread(Worker) { IsBackground = true, Name = "Thumbnails", Priority = ThreadPriority.BelowNormal };
            _worker.SetApartmentState(ApartmentState.STA);
            _worker.Start();
        }
        Pending.Push((path, size, done));
        Signal.Release();
    }

    private static void Worker()
    {
        while (true)
        {
            Signal.Wait();
            if (!Pending.TryPop(out var job)) continue;
            ImageSource? img = null;
            // 정확히 요청한 크기로 (더 큰 그림을 받아 줄이면 흐려진다)
            try { img = Load(job.path, job.size, SIIGBF_RESIZETOFIT); } catch { }
            _ui!.BeginInvoke(DispatcherPriority.Background, () => job.done(img));
        }
    }

    // ===== 유형 이름 ("Microsoft Excel 워크시트") =====
    private static readonly Dictionary<string, string> TypeNames = new(StringComparer.OrdinalIgnoreCase);

    public static string TypeName(string path, bool isDir)
    {
        if (isDir) return "파일 폴더";
        var ext = Path.GetExtension(path);
        if (TypeNames.TryGetValue(ext, out var n)) return n;
        var info = new SHFILEINFO();
        SHGetFileInfo(string.IsNullOrEmpty(ext) ? "x" : "x" + ext, FILE_ATTRIBUTE_NORMAL, ref info,
            (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);
        n = string.IsNullOrWhiteSpace(info.szTypeName) ? (ext.Length > 1 ? ext[1..].ToUpperInvariant() + " 파일" : "파일") : info.szTypeName;
        TypeNames[ext] = n;
        return n;
    }

    /// <summary>"로컬 디스크 (C:)" 처럼 셸 표시 이름.</summary>
    public static string DisplayName(string path)
    {
        var info = new SHFILEINFO();
        SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_DISPLAYNAME);
        return string.IsNullOrWhiteSpace(info.szDisplayName) ? path : info.szDisplayName;
    }
}
