using System.IO;
using System.Text;

namespace IndexExplorer.Engine;

/// <summary>
/// 드라이브 하나의 전체 파일 목록 (NTFS 장부를 그대로 옮긴 압축 구조).
/// 파일마다 경로를 저장하지 않고 "파일 번호·부모 번호·이름"만 담아, 경로는 필요할 때 부모를 따라 조립한다.
/// 이름은 한 덩어리 문자 배열에 이어 붙인다. 70만 개 기준 수십 MB.
/// 읽기(조회)는 여러 스레드, 쓰기(장부·변경 기록 반영)는 엔진 스레드 하나.
/// </summary>
public sealed class VolumeIndex
{
    public const uint AttrReadOnly = 0x1, AttrHidden = 0x2, AttrSystem = 0x4, AttrDirectory = 0x10;

    public string Root { get; }          // "D:"
    public uint Serial { get; set; }
    public ulong JournalId { get; set; }
    public long NextUsn { get; set; }
    /// <summary>바뀔 때마다 1씩 늘어난다 (화면이 다시 물어볼지 판단).</summary>
    public long Version => Interlocked.Read(ref _version);

    private ulong[] _frn = new ulong[1024];
    private ulong[] _parent = new ulong[1024];
    private int[] _nameOff = new int[1024];
    private ushort[] _nameLen = new ushort[1024];
    private uint[] _attr = new uint[1024];
    private byte[] _kind = new byte[1024];   // 고정 표 기준 종류 (잡동사니 판별용)
    private ushort[] _ext = new ushort[1024]; // 확장자 번호 (사용자가 정한 종류는 요청마다 이 번호로 판정)
    private readonly Dictionary<string, ushort> _extId = new(StringComparer.OrdinalIgnoreCase) { [""] = 0 };
    private readonly List<string> _exts = [""];
    private int _slots;                       // 쓴 칸 수 (지운 칸 포함)
    private readonly Stack<int> _free = new();
    private readonly Dictionary<ulong, int> _slotOf = new(1 << 16);
    private char[] _pool = new char[1 << 16];
    private int _poolUsed;
    private long _poolGarbage;
    private long _version;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly Dictionary<int, bool> _systemDir = [];   // 폴더 칸 → 시스템 폴더 아래인가 (캐시)
    /// <summary>폴더(파일 번호)별 바로 안의 시스템 파일(dll 등) 수 — 프로그램이 설치된 폴더를 알아보는 데 쓴다.</summary>
    private readonly Dictionary<ulong, int> _sysFilesIn = [];
    /// <summary>폴더(파일 번호)별 바로 안의 개발 프로젝트 파일(.csproj, package.json …) 수 — 개발 프로젝트는 빌드 결과물만 숨긴다.</summary>
    private readonly Dictionary<ulong, int> _projFilesIn = [];

    private static readonly HashSet<string> ProjectMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "package.json", "pom.xml", "build.gradle", "build.gradle.kts", "CMakeLists.txt", "Cargo.toml", "pyproject.toml", "go.mod", "Makefile",
    };
    private static readonly HashSet<string> ProjectExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj", ".sln", ".slnx", ".vcxproj", ".fsproj", ".vbproj", ".xcodeproj", ".uproject",
    };

    private static bool IsProjectMarker(ReadOnlySpan<char> name)
    {
        var dot = name.LastIndexOf('.');
        return (dot >= 0 && ProjectExts.Contains(name[dot..].ToString())) || ProjectMarkers.Contains(name.ToString());
    }

    private void CountProj(ulong parent, int delta)
    {
        var after = _projFilesIn.GetValueOrDefault(parent) + delta;
        if (after <= 0) _projFilesIn.Remove(parent); else _projFilesIn[parent] = after;
        InvalidateDirs();
    }
    private const byte SystemKind = 12;
    public const int ProgramFolderThreshold = 3;

    private long _dirStamp;                    // 폴더 구조·프로그램 폴더 판별이 바뀔 때마다 늘어난다
    private HashSet<int>? _programRoots;
    private long _programRootsStamp = -1;

    private void InvalidateDirs()
    {
        lock (_systemDir)
        {
            _systemDir.Clear();
            _dirStamp++;
        }
    }

    private static readonly HashSet<string> UserPlaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "Users", "사용자", "Desktop", "바탕 화면", "Documents", "문서", "Downloads", "다운로드", "Pictures", "사진",
        "Videos", "동영상", "Music", "음악", "OneDrive", "Public", "공용",
    };

    /// <summary>
    /// 프로그램이 설치된 폴더의 "꼭대기": dll 이 3개 이상인 폴더에서 두 단계 위까지.
    /// (Git\ucrt64\bin 의 dll → Git 전체, 게임\Mad\Binaries\Win64 → Mad 전체)
    /// 드라이브 바로 아래 폴더와 사용자 폴더(바탕 화면·문서·다운로드 …)는 절대 넣지 않는다.
    /// </summary>
    private HashSet<int> ProgramRoots()
    {
        long stamp;
        lock (_systemDir)
        {
            if (_programRoots != null && _programRootsStamp == _dirStamp) return _programRoots;
            stamp = _dirStamp;
        }
        var set = new HashSet<int>();
        foreach (var (dirFrn, n) in _sysFilesIn)
        {
            if (n < ProgramFolderThreshold || !_slotOf.TryGetValue(dirFrn, out var d)) continue;
            var cur = d;
            for (int up = 0; up <= 2 && cur >= 0; up++)
            {
                var parent = _slotOf.TryGetValue(_parent[cur], out var p) && p != cur ? p : -1;
                var isTop = parent < 0; // 이 폴더가 드라이브 바로 아래 (D:\개발, D:\게임 …) → 지킨다
                var name = _pool.AsSpan(_nameOff[cur], _nameLen[cur]).ToString();
                if (isTop || UserPlaces.Contains(name) || (parent >= 0 && UserPlaces.Contains(_pool.AsSpan(_nameOff[parent], _nameLen[parent]).ToString()) && up > 0))
                    break;
                set.Add(cur);
                // 부모가 개발 프로젝트(.csproj, package.json …)면 빌드 결과물(bin 등)까지만 숨기고 프로젝트는 지킨다
                if (parent >= 0 && _projFilesIn.ContainsKey(_frn[parent])) break;
                cur = parent;
            }
        }
        lock (_systemDir)
        {
            if (_dirStamp == stamp) { _programRoots = set; _programRootsStamp = stamp; }
        }
        return set;
    }

    private void CountSys(ulong parent, int delta)
    {
        var before = _sysFilesIn.GetValueOrDefault(parent);
        var after = before + delta;
        if (after <= 0) _sysFilesIn.Remove(parent); else _sysFilesIn[parent] = after;
        // 프로그램 폴더 기준을 넘나들면 판별 캐시를 버린다
        if ((before >= ProgramFolderThreshold) != (after >= ProgramFolderThreshold)) InvalidateDirs();
    }

    public VolumeIndex(string root) { Root = root.TrimEnd('\\'); }

    public int Count
    {
        get { _lock.EnterReadLock(); try { return _slotOf.Count; } finally { _lock.ExitReadLock(); } }
    }

    // ===================== 쓰기 =====================

    /// <summary>일괄 쓰기 (장부 전체 읽기 등). 그동안 조회는 기다린다.</summary>
    public void Write(Action<VolumeIndex> work, bool bump = true)
    {
        _lock.EnterWriteLock();
        try { work(this); }
        finally { _lock.ExitWriteLock(); if (bump) Interlocked.Increment(ref _version); }
    }

    /// <summary>바뀌었음을 알린다 (변경 기록을 반영한 뒤).</summary>
    public void Touch() => Interlocked.Increment(ref _version);

    /// <summary>쓰기 잠금 안에서만 부른다.</summary>
    public void Upsert(ulong frn, ulong parent, ReadOnlySpan<char> name, uint attr)
    {
        if (_slotOf.TryGetValue(frn, out var s))
        {
            var wasDir = (_attr[s] & AttrDirectory) != 0;
            var sameName = name.SequenceEqual(_pool.AsSpan(_nameOff[s], _nameLen[s]));
            if (!sameName) SetName(s, name);
            if (wasDir && (_parent[s] != parent || !sameName)) InvalidateDirs(); // 폴더가 옮겨지거나 이름이 바뀌면 판별 캐시를 버린다
            var oldKind = _kind[s];
            var oldParent = _parent[s];
            _parent[s] = parent;
            _attr[s] = attr;
            _kind[s] = (attr & AttrDirectory) != 0 ? FileKinds.None : FileKinds.Fixed.Of(name);
            _ext[s] = (attr & AttrDirectory) != 0 ? (ushort)0 : ExtId(name);
            if (!sameName || oldParent != parent)
            {
                // 이름이 바뀌기 전 판단은 기록이 없으므로, 새 이름 기준으로만 다시 센다 (드묾)
                if ((attr & AttrDirectory) == 0 && IsProjectMarker(name)) { if (oldParent != parent || !sameName) CountProj(parent, +1); }
            }
            if (oldKind == SystemKind && (_kind[s] != SystemKind || oldParent != parent)) CountSys(oldParent, -1);
            if (_kind[s] == SystemKind && (oldKind != SystemKind || oldParent != parent)) CountSys(parent, +1);
            return;
        }
        s = _free.Count > 0 ? _free.Pop() : _slots++;
        EnsureCapacity(s + 1);
        _frn[s] = frn;
        _parent[s] = parent;
        _attr[s] = attr;
        _kind[s] = (attr & AttrDirectory) != 0 ? FileKinds.None : FileKinds.Fixed.Of(name);
        _ext[s] = (attr & AttrDirectory) != 0 ? (ushort)0 : ExtId(name);
        _nameLen[s] = 0;
        SetName(s, name);
        _slotOf[frn] = s;
        if (_kind[s] == SystemKind) CountSys(parent, +1);
        if ((attr & AttrDirectory) == 0 && IsProjectMarker(name)) CountProj(parent, +1);
    }

    /// <summary>확장자 번호 (처음 보는 확장자는 새 번호). 쓰기 잠금 안에서. 너무 길거나 번호가 바닥나면 0(없음).</summary>
    private ushort ExtId(ReadOnlySpan<char> name)
    {
        var dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1 || name.Length - dot - 1 > 16) return 0;
        var ext = name[(dot + 1)..];
        var lookup = _extId.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(ext, out var id)) return id;
        if (_exts.Count >= ushort.MaxValue) return 0;
        var s = ext.ToString().ToLowerInvariant();
        id = (ushort)_exts.Count;
        _exts.Add(s);
        _extId[s] = id;
        return id;
    }

    /// <summary>확장자 번호 → 종류 번호 표 (요청마다, 읽기 잠금 안에서). 확장자는 이 PC 전체에 수천 가지라 즉시 만든다.</summary>
    private byte[] KindTable(KindMap map)
    {
        var table = new byte[_exts.Count];
        for (int i = 1; i < table.Length; i++) table[i] = map.OfExt(_exts[i]);
        return table;
    }

    public void Remove(ulong frn)
    {
        if (!_slotOf.Remove(frn, out var s)) return;
        if ((_attr[s] & AttrDirectory) != 0) InvalidateDirs();
        if (_kind[s] == SystemKind) CountSys(_parent[s], -1);
        if ((_attr[s] & AttrDirectory) == 0 && IsProjectMarker(_pool.AsSpan(_nameOff[s], _nameLen[s]))) CountProj(_parent[s], -1);
        _poolGarbage += _nameLen[s];
        _frn[s] = 0;
        _nameLen[s] = 0;
        _kind[s] = 0;
        _ext[s] = 0;
        _free.Push(s);
    }

    public void Clear()
    {
        _slotOf.Clear();
        _free.Clear();
        InvalidateDirs();
        _sysFilesIn.Clear();
        _projFilesIn.Clear();
        _slots = 0;
        _poolUsed = 0;
        _poolGarbage = 0;
    }

    private void SetName(int s, ReadOnlySpan<char> name)
    {
        // 같은 길이 이하면 제자리, 길면 뒤에 새로 붙인다 (남은 자리는 나중에 압축)
        if (name.Length <= _nameLen[s] && _nameLen[s] > 0)
        {
            _poolGarbage += _nameLen[s] - name.Length;
            name.CopyTo(_pool.AsSpan(_nameOff[s]));
            _nameLen[s] = (ushort)name.Length;
            return;
        }
        _poolGarbage += _nameLen[s];
        if (_poolUsed + name.Length > _pool.Length)
        {
            if (_poolGarbage > _poolUsed / 3) CompactPool();
            if (_poolUsed + name.Length > _pool.Length) Array.Resize(ref _pool, Math.Max(_pool.Length * 2, _poolUsed + name.Length));
        }
        name.CopyTo(_pool.AsSpan(_poolUsed));
        _nameOff[s] = _poolUsed;
        _nameLen[s] = (ushort)name.Length;
        _poolUsed += name.Length;
    }

    private void CompactPool()
    {
        var fresh = new char[Math.Max(_pool.Length, 1 << 16)];
        int used = 0;
        foreach (var s in _slotOf.Values)
        {
            _pool.AsSpan(_nameOff[s], _nameLen[s]).CopyTo(fresh.AsSpan(used));
            _nameOff[s] = used;
            used += _nameLen[s];
        }
        _pool = fresh;
        _poolUsed = used;
        _poolGarbage = 0;
    }

    private void EnsureCapacity(int n)
    {
        if (n <= _frn.Length) return;
        var size = Math.Max(n, _frn.Length * 2);
        Array.Resize(ref _frn, size);
        Array.Resize(ref _parent, size);
        Array.Resize(ref _nameOff, size);
        Array.Resize(ref _nameLen, size);
        Array.Resize(ref _attr, size);
        Array.Resize(ref _kind, size);
        Array.Resize(ref _ext, size);
    }

    // ===================== 조회 =====================

    private static readonly HashSet<string> SystemTop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin", "System Volume Information",
        "Recovery", "$WinREAgent", "PerfLogs", "$SysReset", "$Windows.~BT", "$Windows.~WS", "MSOCache", "Config.Msi",
    };

    private static readonly HashSet<string> SystemAnywhere = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData", "node_modules", ".git", ".svn", ".vs", "__pycache__", "site-packages", "$RECYCLE.BIN",
    };

    /// <summary>시스템·프로그램 폴더 아래인가 (사용자 파일만 볼 때 뺀다). 읽기 잠금 안에서.</summary>
    private bool UnderSystem(int s)
    {
        // 부모 폴더 사슬을 따라 올라가며 판별, 폴더별 결과는 캐시
        var p = _slotOf.TryGetValue(_parent[s], out var ps) && ps != s ? ps : -1;
        if (p < 0) return false;
        return DirIsSystem(p, 0);
    }

    private bool DirIsSystem(int d, int depth)
    {
        lock (_systemDir) { if (_systemDir.TryGetValue(d, out var cached)) return cached; }
        bool result;
        var name = _pool.AsSpan(_nameOff[d], _nameLen[d]).ToString();
        var parent = depth < 128 && _slotOf.TryGetValue(_parent[d], out var ps) && ps != d ? ps : -1;
        if (SystemAnywhere.Contains(name)) result = true;
        else if (name.Length > 1 && name[0] == '.') result = true;                               // .eclipse, .vscode, .cache … 프로그램 설정 폴더
        else if (_sysFilesIn.GetValueOrDefault(_frn[d]) >= ProgramFolderThreshold) result = true; // dll 이 여럿 = 프로그램이 설치된 폴더
        else if (ProgramRoots().Contains(d)) result = true;                                       // 그 두 단계 위까지 (프로그램의 꼭대기)
        else if (parent < 0) result = SystemTop.Contains(name);          // 드라이브 바로 아래 폴더
        else result = DirIsSystem(parent, depth + 1);
        lock (_systemDir) _systemDir[d] = result;
        return result;
    }

    /// <summary>사용자가 직접 뺀 폴더 (요청마다): 폴더 칸 집합 + 판별 기억.</summary>
    private sealed class Excludes
    {
        public readonly HashSet<int> Dirs = [];
        public readonly Dictionary<int, bool> Memo = [];
    }

    /// <summary>뺀 폴더 경로를 이 드라이브의 폴더 칸으로 바꾼다 (읽기 잠금 안에서). 이름이 같은 폴더만 경로를 맞춰 본다.</summary>
    private Excludes? ResolveExcludes(IReadOnlyCollection<string>? paths)
    {
        if (paths == null || paths.Count == 0) return null;
        var want = paths.Select(p => Path.TrimEndingDirectorySeparator(p))
            .Where(p => p.StartsWith(Root + "\\", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (want.Count == 0) return null;
        var names = want.Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ex = new Excludes();
        foreach (var s in _slotOf.Values)
        {
            if ((_attr[s] & AttrDirectory) == 0) continue;
            if (!names.Contains(_pool.AsSpan(_nameOff[s], _nameLen[s]).ToString())) continue;
            if (want.Contains(PathOfUnlocked(s))) ex.Dirs.Add(s);
        }
        return ex.Dirs.Count > 0 ? ex : null;
    }

    private bool InExcluded(int s, Excludes ex)
    {
        var cur = _slotOf.TryGetValue(_parent[s], out var p) && p != s ? p : -1;
        Span<int> path = stackalloc int[128];
        int n = 0;
        bool result = false;
        while (cur >= 0 && n < path.Length)
        {
            if (ex.Memo.TryGetValue(cur, out var known)) { result = known; break; }
            if (ex.Dirs.Contains(cur)) { result = true; path[n++] = cur; break; }
            path[n++] = cur;
            cur = _slotOf.TryGetValue(_parent[cur], out var pp) && pp != cur ? pp : -1;
        }
        for (int i = 0; i < n; i++) ex.Memo[path[i]] = result;
        return result;
    }

    /// <summary>includeSystem = 모두 보기 (자동으로 숨긴 것·직접 뺀 폴더 모두 보임).</summary>
    private bool Visible(int s, bool includeSystem, Excludes? ex) =>
        includeSystem || ((_attr[s] & (AttrHidden | AttrSystem)) == 0 && !UnderSystem(s) && (ex == null || !InExcluded(s, ex)));

    /// <summary>종류별 파일 수.</summary>
    public int[] CountByKind(bool includeSystem, IReadOnlyCollection<string>? excludes = null, KindMap? map = null)
    {
        var counts = new int[256];
        _lock.EnterReadLock();
        try
        {
            var ex = includeSystem ? null : ResolveExcludes(excludes);
            var table = KindTable(map ?? FileKinds.Fixed);
            foreach (var s in _slotOf.Values)
            {
                var k = table[_ext[s]];
                if (k == FileKinds.None || (_attr[s] & AttrDirectory) != 0) continue;
                if (Visible(s, includeSystem, ex)) counts[k]++;
            }
        }
        finally { _lock.ExitReadLock(); }
        return counts;
    }

    public sealed record Hit(ulong Frn, string Path, uint Attr);

    /// <summary>종류(0이면 전체)와 이름 검색어(공백으로 나눈 단어 모두 포함)로 파일 찾기.</summary>
    public List<Hit> Query(byte kind, string? text, bool includeSystem, int max, IReadOnlyCollection<string>? excludes = null, KindMap? map = null)
    {
        var words = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = new List<Hit>();
        _lock.EnterReadLock();
        try
        {
            var ex = includeSystem ? null : ResolveExcludes(excludes);
            var table = KindTable(map ?? FileKinds.Fixed);
            foreach (var s in _slotOf.Values)
            {
                if ((_attr[s] & AttrDirectory) != 0) continue;
                if (kind != FileKinds.None && table[_ext[s]] != kind) continue;
                if (words.Length > 0)
                {
                    var name = _pool.AsSpan(_nameOff[s], _nameLen[s]);
                    var ok = true;
                    foreach (var w in words) if (name.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0) { ok = false; break; }
                    if (!ok) continue;
                }
                if (!Visible(s, includeSystem, ex)) continue;
                hits.Add(new Hit(_frn[s], PathOfUnlocked(s), _attr[s]));
                if (hits.Count >= max) break;
            }
        }
        finally { _lock.ExitReadLock(); }
        return hits;
    }

    public string? PathOf(ulong frn)
    {
        _lock.EnterReadLock();
        try { return _slotOf.TryGetValue(frn, out var s) ? PathOfUnlocked(s) : null; }
        finally { _lock.ExitReadLock(); }
    }

    private string PathOfUnlocked(int s)
    {
        Span<int> chain = stackalloc int[128];
        int n = 0, cur = s;
        while (cur >= 0 && n < chain.Length)
        {
            chain[n++] = cur;
            cur = _slotOf.TryGetValue(_parent[cur], out var p) && p != cur ? p : -1;
        }
        var sb = new StringBuilder(Root, 128);
        for (int i = n - 1; i >= 0; i--)
        {
            sb.Append('\\');
            sb.Append(_pool, _nameOff[chain[i]], _nameLen[chain[i]]);
        }
        return sb.ToString();
    }

    /// <summary>메모리 사용량 추정 (바이트).</summary>
    public long EstimatedBytes
    {
        get
        {
            _lock.EnterReadLock();
            try { return (long)_frn.Length * (8 + 8 + 4 + 2 + 4 + 1 + 2) + _pool.Length * 2L + _slotOf.Count * 24L; }
            finally { _lock.ExitReadLock(); }
        }
    }

    // ===================== 저장 / 복원 =====================

    private const uint Magic = 0x31565849; // "IXV1"

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        _lock.EnterReadLock();
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var w = new BinaryWriter(fs, Encoding.Unicode))
            {
                w.Write(Magic);
                w.Write(Serial);
                w.Write(JournalId);
                w.Write(NextUsn);
                w.Write(_slotOf.Count);
                foreach (var s in _slotOf.Values)
                {
                    w.Write(_frn[s]);
                    w.Write(_parent[s]);
                    w.Write(_attr[s]);
                    w.Write(_nameLen[s]);
                    w.Write(_pool, _nameOff[s], _nameLen[s]);
                }
            }
        }
        finally { _lock.ExitReadLock(); }
        File.Move(tmp, path, overwrite: true);
    }

    public bool Load(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            using var r = new BinaryReader(fs, Encoding.Unicode);
            if (r.ReadUInt32() != Magic) return false;
            var serial = r.ReadUInt32();
            var journal = r.ReadUInt64();
            var next = r.ReadInt64();
            var count = r.ReadInt32();
            var name = new char[ushort.MaxValue];
            Write(v =>
            {
                v.Clear();
                v.EnsureCapacity(count);
                for (int i = 0; i < count; i++)
                {
                    var frn = r.ReadUInt64();
                    var parent = r.ReadUInt64();
                    var attr = r.ReadUInt32();
                    var len = r.ReadUInt16();
                    r.Read(name, 0, len);
                    v.Upsert(frn, parent, name.AsSpan(0, len), attr);
                }
                v.Serial = serial;
                v.JournalId = journal;
                v.NextUsn = next;
            });
            return true;
        }
        catch { Write(v => v.Clear()); return false; }
    }
}
