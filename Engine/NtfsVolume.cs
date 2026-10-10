using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IndexExplorer.Engine;

/// <summary>
/// NTFS 디스크 장부 접근 (관리자 권한 필요).
/// - MFT 전체 읽기: FSCTL_ENUM_USN_DATA — 폴더를 열지 않고 모든 파일 목록을 통째로 (수십만 개 수 초)
/// - 변경 기록(USN 저널) 이어 읽기: FSCTL_READ_USN_JOURNAL — 앱이 꺼져 있던 동안의 변경까지 정확히
/// 읽기만 하고, 변경 기록이 꺼진 드라이브는 켠다 (사용자 승인 사항, 최대 32MB).
/// </summary>
public sealed unsafe class NtfsVolume : IDisposable
{
    private const uint GENERIC_READ = 0x80000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    private const uint FSCTL_ENUM_USN_DATA = 0x000900B3, FSCTL_QUERY_USN_JOURNAL = 0x000900F4,
        FSCTL_READ_USN_JOURNAL = 0x000900BB, FSCTL_CREATE_USN_JOURNAL = 0x000900E7;
    public const int ERROR_JOURNAL_NOT_ACTIVE = 1179, ERROR_JOURNAL_DELETE_IN_PROGRESS = 1178, ERROR_JOURNAL_ENTRY_DELETED = 1181;

    public const uint REASON_DATA_OVERWRITE = 0x1, REASON_DATA_EXTEND = 0x2, REASON_DATA_TRUNCATION = 0x4,
        REASON_FILE_CREATE = 0x100, REASON_FILE_DELETE = 0x200, REASON_RENAME_OLD_NAME = 0x1000,
        REASON_RENAME_NEW_NAME = 0x2000, REASON_BASIC_INFO_CHANGE = 0x8000, REASON_HARD_LINK_CHANGE = 0x10000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string n, uint a, uint s, IntPtr sec, uint c, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, void* inBuf, int inSize, void* outBuf, int outSize, out int returned, IntPtr ov);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(string root, IntPtr name, int nameLen, out uint serial, out uint maxComp, out uint flags, IntPtr fs, int fsLen);

    [StructLayout(LayoutKind.Sequential)]
    public struct JournalData { public ulong JournalId; public long FirstUsn, NextUsn, LowestValidUsn, MaxUsn; public ulong MaximumSize, AllocationDelta; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_ENUM_DATA_V0 { public ulong StartFileReferenceNumber; public long LowUsn, HighUsn; }
    [StructLayout(LayoutKind.Sequential)]
    private struct READ_USN_JOURNAL_DATA_V0 { public long StartUsn; public uint ReasonMask, ReturnOnlyOnClose; public ulong Timeout, BytesToWaitFor, UsnJournalID; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CREATE_USN_JOURNAL_DATA { public ulong MaximumSize, AllocationDelta; }

    private readonly SafeFileHandle _h;
    private const int BufSize = 1 << 20;
    private readonly byte* _buf;

    public string Root { get; }
    public uint Serial { get; }

    private NtfsVolume(string root, SafeFileHandle h, uint serial)
    {
        Root = root; _h = h; Serial = serial;
        _buf = (byte*)NativeMemory.Alloc(BufSize);
    }

    /// <summary>드라이브 열기 ("D:"). 관리자가 아니면 null.</summary>
    public static NtfsVolume? Open(string root, out int error)
    {
        root = root.TrimEnd('\\');
        var h = CreateFile(@"\\.\" + root, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        error = Marshal.GetLastWin32Error();
        if (h.IsInvalid) return null;
        GetVolumeInformation(root + "\\", IntPtr.Zero, 0, out var serial, out _, out _, IntPtr.Zero, 0);
        return new NtfsVolume(root, h, serial);
    }

    public bool QueryJournal(out JournalData jd, out int error)
    {
        JournalData d;
        var ok = DeviceIoControl(_h, FSCTL_QUERY_USN_JOURNAL, null, 0, &d, sizeof(JournalData), out _, IntPtr.Zero);
        error = ok ? 0 : Marshal.GetLastWin32Error();
        jd = d;
        return ok;
    }

    /// <summary>변경 기록 켜기 (이미 켜져 있으면 크기만 맞춤). 파일 내용은 건드리지 않는다.</summary>
    public bool CreateJournal(ulong maxBytes = 32UL << 20, ulong delta = 4UL << 20)
    {
        var d = new CREATE_USN_JOURNAL_DATA { MaximumSize = maxBytes, AllocationDelta = delta };
        return DeviceIoControl(_h, FSCTL_CREATE_USN_JOURNAL, &d, sizeof(CREATE_USN_JOURNAL_DATA), null, 0, out _, IntPtr.Zero);
    }

    /// <summary>장부 전체 읽기. 각 항목마다 (파일 번호, 부모 번호, 이름, 속성).</summary>
    public void EnumerateAll(long highUsn, Action<ulong, ulong, ReadOnlySpan<char>, uint> onEntry)
    {
        var med = new MFT_ENUM_DATA_V0 { StartFileReferenceNumber = 0, LowUsn = 0, HighUsn = highUsn };
        while (DeviceIoControl(_h, FSCTL_ENUM_USN_DATA, &med, sizeof(MFT_ENUM_DATA_V0), _buf, BufSize, out var got, IntPtr.Zero) && got > 8)
        {
            med.StartFileReferenceNumber = *(ulong*)_buf;
            ParseRecords(got, (frn, parent, name, attr, _) => onEntry(frn, parent, name, attr));
        }
    }

    /// <summary>
    /// startUsn 부터 지금까지의 변경 기록을 읽는다 (기다리지 않음). 다음에 읽을 번호를 돌려준다.
    /// 기록이 지워졌거나(너무 오래됨) 저널이 바뀌었으면 error 에 이유.
    /// </summary>
    public long ReadChanges(long startUsn, ulong journalId, Action<ulong, ulong, ReadOnlySpan<char>, uint, uint> onRecord, out int error)
    {
        error = 0;
        var next = startUsn;
        while (true)
        {
            var rd = new READ_USN_JOURNAL_DATA_V0
            {
                StartUsn = next, ReasonMask = 0xFFFFFFFF, ReturnOnlyOnClose = 0, Timeout = 0, BytesToWaitFor = 0, UsnJournalID = journalId,
            };
            if (!DeviceIoControl(_h, FSCTL_READ_USN_JOURNAL, &rd, sizeof(READ_USN_JOURNAL_DATA_V0), _buf, BufSize, out var got, IntPtr.Zero))
            {
                error = Marshal.GetLastWin32Error();
                return next;
            }
            var after = *(long*)_buf;
            if (got <= 8) return after;
            ParseRecords(got, onRecord);
            if (after == next) return after;
            next = after;
        }
    }

    // USN_RECORD_V2: 길이(4) 버전(2,2) 파일번호(8) 부모번호(8) Usn(8) 시각(8) 이유(4) 출처(4) 보안(4) 속성(4) 이름길이(2) 이름위치(2)
    private void ParseRecords(int got, Action<ulong, ulong, ReadOnlySpan<char>, uint, uint> on)
    {
        int off = 8;
        while (off < got)
        {
            var rec = _buf + off;
            var len = *(uint*)rec;
            if (len == 0 || off + len > got) break;
            if (*(ushort*)(rec + 4) == 2)
            {
                var frn = *(ulong*)(rec + 8);
                var parent = *(ulong*)(rec + 16);
                var reason = *(uint*)(rec + 40);
                var attr = *(uint*)(rec + 52);
                var nLen = *(ushort*)(rec + 56) / 2;
                var nOff = *(ushort*)(rec + 58);
                on(frn, parent, new ReadOnlySpan<char>(rec + nOff, nLen), attr, reason);
            }
            off += (int)len;
        }
    }

    public void Dispose()
    {
        _h.Dispose();
        NativeMemory.Free(_buf);
    }
}
