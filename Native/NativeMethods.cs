using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace IndexExplorer.Native;

/// <summary>Win32 / 셸 API 선언 모음.</summary>
internal static class NativeMethods
{
    // ===== 파일 고유 ID (이동·이름 변경 추적용) =====
    public const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    // FILETIME 은 DWORD 2개(4바이트 정렬)이므로 long 으로 선언하면 뒤 필드가 밀린다
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh, FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFileInformationByHandle(SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);

    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_ID_DESCRIPTOR
    {
        public uint Size;
        public int Type;       // 0 = FileIdType (64비트 파일 인덱스)
        public long FileId;
        public long Padding;   // union 크기(16바이트) 맞춤
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeFileHandle OpenFileById(SafeFileHandle volumeHint, ref FILE_ID_DESCRIPTOR id,
        uint access, uint share, IntPtr security, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetFinalPathNameByHandle(SafeFileHandle h, StringBuilder path, uint len, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetVolumeInformation(string root, StringBuilder? name, int nameLen, out uint serial,
        out uint maxComp, out uint flags, StringBuilder? fs, int fsLen);

    // ===== 정렬: 탐색기와 같은 자연 정렬 (파일2 < 파일10) =====
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int StrCmpLogicalW(string a, string b);

    // ===== 파일 정보 (유형 이름) =====
    public const uint SHGFI_TYPENAME = 0x400, SHGFI_USEFILEATTRIBUTES = 0x10, SHGFI_DISPLAYNAME = 0x200;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80, FILE_ATTRIBUTE_DIRECTORY = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);

    // ===== 셸 파일 작업 (탐색기와 같은 진행 창·충돌 창·휴지통) =====
    public const uint FO_MOVE = 1, FO_COPY = 2, FO_DELETE = 3;
    public const ushort FOF_MULTIDESTFILES = 0x1, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40,
        FOF_NOCONFIRMMKDIR = 0x200, FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    // ===== 셸 항목 / 이미지 =====
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SHCreateItemFromParsingName(string path, IntPtr bindCtx,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out object item);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHParseDisplayName(string name, IntPtr bindCtx, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    public static extern int SHGetDesktopFolder(out IShellFolder folder);

    [DllImport("shell32.dll")]
    public static extern void ILFree(IntPtr pidl);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public uint f0, f1, f2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    [DllImport("gdi32.dll")]
    public static extern int GetObject(IntPtr h, int size, ref DIBSECTION obj);

    // ===== 메뉴 (셸 컨텍스트 메뉴) =====
    public const uint MF_STRING = 0x0, MF_SEPARATOR = 0x800, MF_BYPOSITION = 0x400;
    public const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2;
    public const uint CMF_NORMAL = 0x0, CMF_EXPLORE = 0x4, CMF_CANRENAME = 0x10, CMF_EXTENDEDVERBS = 0x100;

    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool InsertMenu(IntPtr h, uint pos, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")]
    public static extern uint TrackPopupMenuEx(IntPtr h, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpTitle;
        public IntPtr lpVerbW;
        public string? lpParametersW;
        public string? lpDirectoryW;
        public string? lpTitleW;
        public POINT ptInvoke;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    public const uint CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_PTINVOKE = 0x20000000,
        CMIC_MASK_SHIFT_DOWN = 0x10000000, CMIC_MASK_CONTROL_DOWN = 0x40000000;

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT p);
}

[ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellFolder
{
    void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name, out uint eaten, out IntPtr pidl, ref uint attrs);
    void EnumObjects(IntPtr hwnd, int flags, out IntPtr enumIdList);
    void BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
    void BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
    void CreateViewObject(IntPtr hwnd, ref Guid riid, out IntPtr ppv);
    void GetAttributesOf(uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls, ref uint attrs);
    [PreserveSig] int GetUIObjectOf(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls,
        ref Guid riid, IntPtr reserved, out IntPtr ppv);
    void GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr name);
    void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr pidlOut);
}

[ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu
{
    [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint index, uint idFirst, uint idLast, uint flags);
    [PreserveSig] int InvokeCommand(ref NativeMethods.CMINVOKECOMMANDINFOEX info);
    [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, StringBuilder name, int cch);
}

[ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu2
{
    [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint index, uint idFirst, uint idLast, uint flags);
    [PreserveSig] int InvokeCommand(ref NativeMethods.CMINVOKECOMMANDINFOEX info);
    [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, StringBuilder name, int cch);
    [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
}

[ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu3
{
    [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint index, uint idFirst, uint idLast, uint flags);
    [PreserveSig] int InvokeCommand(ref NativeMethods.CMINVOKECOMMANDINFOEX info);
    [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, StringBuilder name, int cch);
    [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
    [PreserveSig] int HandleMenuMsg2(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
}

[ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemImageFactory
{
    [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
}

[StructLayout(LayoutKind.Sequential)]
internal struct SIZE
{
    public int cx, cy;
    public SIZE(int x, int y) { cx = x; cy = y; }
}
