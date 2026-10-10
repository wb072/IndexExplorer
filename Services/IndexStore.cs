using System.IO;
using IndexExplorer.Native;
using Microsoft.Data.Sqlite;

namespace IndexExplorer.Services;

/// <summary>색인 폴더 (논리 분류). 파일은 옮기지 않고 연결만 가진다.</summary>
public sealed class IndexFolder
{
    public long Id { get; init; }
    public long? ParentId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>이 폴더에서 새로 만든 파일이 저장될 실제 위치 (null = 상위 규칙 상속 / 원본 보관함).</summary>
    public string? StoragePath { get; set; }
}

/// <summary>색인에 올라간 파일 하나 (원본은 디스크 어디에 있든 상관없다).</summary>
public sealed class FileRecord
{
    public long Id { get; init; }
    public string Path { get; set; } = "";
    public uint Volume { get; set; }
    public ulong FileId { get; set; }
    public long Size { get; set; }
    public DateTime Modified { get; set; }
    /// <summary>원본을 찾을 수 없음 (삭제되었거나 다른 드라이브로 옮겨짐).</summary>
    public bool Missing { get; set; }
    public HashSet<long> Folders { get; } = [];

    public string Name => System.IO.Path.GetFileName(Path);
    public string Dir => PathUtil.Dir(Path);
}

/// <summary>
/// 색인 저장소. 파일 수천~수만 개 규모라 전체를 메모리에 두고 (검색·목록 즉시 응답),
/// 변경은 SQLite에 바로 기록한다.
/// </summary>
public sealed class IndexStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly Dictionary<long, IndexFolder> _folders = [];
    private readonly Dictionary<long, FileRecord> _files = [];
    private readonly Dictionary<string, FileRecord> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(uint, ulong), FileRecord> _byId = [];
    private SqliteTransaction? _tx;
    private int _batchDepth;
    private bool _dirty;

    /// <summary>색인 내용이 바뀌면 (일괄 작업은 끝날 때 한 번) 알린다.</summary>
    public event Action? Changed;

    public IndexStore(string dbPath)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dbPath)!);
        _db = new SqliteConnection($"Data Source={dbPath}");
        _hashDb = new SqliteConnection($"Data Source={dbPath}"); // 중복 찾기(백그라운드)용 별도 연결
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
        Exec("""
            CREATE TABLE IF NOT EXISTS folders(
                id INTEGER PRIMARY KEY, parent_id INTEGER NULL REFERENCES folders(id) ON DELETE CASCADE,
                name TEXT NOT NULL, storage_path TEXT NULL);
            CREATE TABLE IF NOT EXISTS files(
                id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                vol INTEGER NOT NULL, fid INTEGER NOT NULL, size INTEGER NOT NULL, mtime INTEGER NOT NULL,
                missing INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS links(
                folder_id INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
                file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                PRIMARY KEY(folder_id, file_id));
            CREATE TABLE IF NOT EXISTS file_hashes(
                path TEXT NOT NULL PRIMARY KEY COLLATE NOCASE, size INTEGER NOT NULL, mtime INTEGER NOT NULL,
                partial TEXT NULL, full TEXT NULL);
            """);
        Load();
    }

    private void Exec(string sql, params (string, object?)[] args)
    {
        using var c = _db.CreateCommand();
        c.CommandText = sql;
        c.Transaction = _tx;
        foreach (var (k, v) in args) c.Parameters.AddWithValue(k, v ?? DBNull.Value);
        c.ExecuteNonQuery();
    }

    private long Insert(string sql, params (string, object?)[] args)
    {
        using var c = _db.CreateCommand();
        c.CommandText = sql + "; SELECT last_insert_rowid();";
        c.Transaction = _tx;
        foreach (var (k, v) in args) c.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return (long)c.ExecuteScalar()!;
    }

    private void Load()
    {
        using (var c = _db.CreateCommand())
        {
            c.CommandText = "SELECT id, parent_id, name, storage_path FROM folders";
            using var r = c.ExecuteReader();
            while (r.Read())
                _folders[r.GetInt64(0)] = new IndexFolder
                {
                    Id = r.GetInt64(0), ParentId = r.IsDBNull(1) ? null : r.GetInt64(1),
                    Name = r.GetString(2), StoragePath = r.IsDBNull(3) ? null : r.GetString(3),
                };
        }
        using (var c = _db.CreateCommand())
        {
            c.CommandText = "SELECT id, path, vol, fid, size, mtime, missing FROM files";
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                var f = new FileRecord
                {
                    Id = r.GetInt64(0), Path = r.GetString(1), Volume = (uint)r.GetInt64(2), FileId = (ulong)r.GetInt64(3),
                    Size = r.GetInt64(4), Modified = new DateTime(r.GetInt64(5), DateTimeKind.Utc), Missing = r.GetInt64(6) != 0,
                };
                AddToMaps(f);
            }
        }
        using (var c = _db.CreateCommand())
        {
            c.CommandText = "SELECT folder_id, file_id FROM links";
            using var r = c.ExecuteReader();
            while (r.Read())
                if (_files.TryGetValue(r.GetInt64(1), out var f)) f.Folders.Add(r.GetInt64(0));
        }
    }

    private void AddToMaps(FileRecord f)
    {
        _files[f.Id] = f;
        _byPath[f.Path] = f;
        if (f.FileId != 0) _byId[(f.Volume, f.FileId)] = f;
    }

    // ===== 일괄 처리 =====
    public void Batch(Action work)
    {
        if (_batchDepth++ == 0) _tx = _db.BeginTransaction();
        try { work(); }
        finally
        {
            if (--_batchDepth == 0)
            {
                _tx!.Commit(); _tx.Dispose(); _tx = null;
                if (_dirty) { _dirty = false; Changed?.Invoke(); }
            }
        }
    }

    private void Touch()
    {
        if (_batchDepth > 0) _dirty = true;
        else Changed?.Invoke();
    }

    // ===== 색인 폴더 =====
    public IEnumerable<IndexFolder> Folders => _folders.Values;
    public IndexFolder? Folder(long id) => _folders.GetValueOrDefault(id);

    public IEnumerable<IndexFolder> Children(long? parentId) =>
        _folders.Values.Where(f => f.ParentId == parentId).OrderBy(f => f.Name, NaturalComparer.Instance);

    public IndexFolder CreateFolder(long? parentId, string baseName = "새 폴더")
    {
        var name = UniqueFolderName(parentId, baseName);
        var id = Insert("INSERT INTO folders(parent_id, name) VALUES($p, $n)", ("$p", parentId), ("$n", name));
        var f = new IndexFolder { Id = id, ParentId = parentId, Name = name };
        _folders[id] = f;
        Touch();
        return f;
    }

    public string UniqueFolderName(long? parentId, string name, long exceptId = 0)
    {
        var used = _folders.Values.Where(f => f.ParentId == parentId && f.Id != exceptId)
            .Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(name)) return name;
        for (int i = 2; ; i++) if (!used.Contains($"{name} ({i})")) return $"{name} ({i})";
    }

    public bool NameTaken(long? parentId, string name, long exceptId) =>
        _folders.Values.Any(f => f.ParentId == parentId && f.Id != exceptId && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public void RenameFolder(long id, string name)
    {
        var f = _folders[id];
        f.Name = name;
        Exec("UPDATE folders SET name=$n WHERE id=$i", ("$n", name), ("$i", id));
        Touch();
    }

    public void MoveFolder(long id, long? newParent)
    {
        var f = _folders[id];
        f.ParentId = newParent;
        f.Name = UniqueFolderName(newParent, f.Name, id);
        Exec("UPDATE folders SET parent_id=$p, name=$n WHERE id=$i", ("$p", newParent), ("$n", f.Name), ("$i", id));
        Touch();
    }

    public void SetStoragePath(long id, string? path)
    {
        _folders[id].StoragePath = path;
        Exec("UPDATE folders SET storage_path=$s WHERE id=$i", ("$s", path), ("$i", id));
        Touch();
    }

    public void DeleteFolder(long id)
    {
        var all = Descendants(id).Append(id).ToHashSet();
        foreach (var f in _files.Values) f.Folders.ExceptWith(all);
        foreach (var fid in all) _folders.Remove(fid);
        Exec("DELETE FROM folders WHERE id=$i", ("$i", id)); // 하위 폴더·연결은 CASCADE
        Touch();
    }

    public IEnumerable<long> Descendants(long id)
    {
        foreach (var c in _folders.Values.Where(f => f.ParentId == id).ToList())
        {
            yield return c.Id;
            foreach (var d in Descendants(c.Id)) yield return d;
        }
    }

    public bool IsAncestor(long ancestor, long id)
    {
        long? cur = id;
        while (cur is long c && _folders.TryGetValue(c, out var f))
        {
            if (f.ParentId == ancestor) return true;
            cur = f.ParentId;
        }
        return false;
    }

    /// <summary>루트부터 이 폴더까지의 경로.</summary>
    public List<IndexFolder> Chain(long id)
    {
        var list = new List<IndexFolder>();
        long? cur = id;
        while (cur is long c && _folders.TryGetValue(c, out var f)) { list.Insert(0, f); cur = f.ParentId; }
        return list;
    }

    public string ChainText(long id) => string.Join(" › ", Chain(id).Select(f => f.Name));

    /// <summary>
    /// 새 파일 저장 위치 규칙: 가장 가까운 상위(자신 포함)에 지정된 저장 위치 + 나머지 하위 경로.
    /// 지정이 없으면 원본 보관함 아래에 색인과 같은 폴더 구조.
    /// </summary>
    public (string Dir, IndexFolder? PinnedBy) StorageDir(long folderId, string storageRoot)
    {
        var chain = Chain(folderId);
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(chain[i].StoragePath)) continue;
            var dir = chain.Skip(i + 1).Aggregate(chain[i].StoragePath!, (acc, f) => Path.Combine(acc, ShellFileOps.SafeName(f.Name)));
            return (dir, chain[i]);
        }
        return (chain.Aggregate(storageRoot, (acc, f) => Path.Combine(acc, ShellFileOps.SafeName(f.Name))), null);
    }

    // ===== 파일 =====
    public IEnumerable<FileRecord> Files => _files.Values;
    public int FileCount => _files.Count;
    public FileRecord? File(long id) => _files.GetValueOrDefault(id);
    public FileRecord? ByPath(string path) => _byPath.GetValueOrDefault(path);
    public FileRecord? ById(uint vol, ulong fid) => fid == 0 ? null : _byId.GetValueOrDefault((vol, fid));

    public IEnumerable<FileRecord> FilesIn(long folderId) => _files.Values.Where(f => f.Folders.Contains(folderId));
    public int CountIn(long folderId) => _files.Values.Count(f => f.Folders.Contains(folderId));

    /// <summary>파일을 색인에 올린다 (이미 있으면 정보 갱신).</summary>
    public FileRecord Upsert(string path, uint vol, ulong fid, long size, DateTime modifiedUtc)
    {
        var existing = _byPath.GetValueOrDefault(path) ?? ById(vol, fid);
        if (existing != null)
        {
            if (!existing.Path.Equals(path, StringComparison.Ordinal)) UpdatePath(existing, path);
            if (existing.Size != size || existing.Modified != modifiedUtc || existing.Missing ||
                existing.Volume != vol || existing.FileId != fid)
            {
                if (existing.FileId != 0) _byId.Remove((existing.Volume, existing.FileId));
                existing.Size = size; existing.Modified = modifiedUtc; existing.Missing = false;
                existing.Volume = vol; existing.FileId = fid;
                if (fid != 0) _byId[(vol, fid)] = existing;
                Exec("UPDATE files SET size=$s, mtime=$m, missing=0, vol=$v, fid=$f WHERE id=$i",
                    ("$s", size), ("$m", modifiedUtc.Ticks), ("$v", (long)vol), ("$f", (long)fid), ("$i", existing.Id));
                Touch();
            }
            return existing;
        }
        var id = Insert("INSERT INTO files(path, vol, fid, size, mtime) VALUES($p,$v,$f,$s,$m)",
            ("$p", path), ("$v", (long)vol), ("$f", (long)fid), ("$s", size), ("$m", modifiedUtc.Ticks));
        var rec = new FileRecord { Id = id, Path = path, Volume = vol, FileId = fid, Size = size, Modified = modifiedUtc };
        AddToMaps(rec);
        Touch();
        return rec;
    }

    /// <summary>디스크의 파일을 읽어 색인에 올린다 (없거나 폴더면 null).</summary>
    public FileRecord? UpsertFromDisk(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) return null;
        FileIdentity.TryGet(path, out var vol, out var fid);
        return Upsert(fi.FullName, vol, fid, fi.Length, fi.LastWriteTimeUtc);
    }

    public void UpdatePath(FileRecord f, string newPath)
    {
        if (_byPath.TryGetValue(newPath, out var other) && other != f) Remove(other, keepLinksInto: f);
        _byPath.Remove(f.Path);
        f.Path = newPath;
        f.Missing = false;
        _byPath[newPath] = f;
        Exec("UPDATE files SET path=$p, missing=0 WHERE id=$i", ("$p", newPath), ("$i", f.Id));
        Touch();
    }

    /// <summary>폴더 이름 변경·이동 시 그 아래 파일 경로를 한꺼번에 바꾼다.</summary>
    public void RebaseDir(string oldDir, string newDir)
    {
        Batch(() =>
        {
            foreach (var f in _files.Values.Where(f => PathUtil.IsUnder(f.Path, oldDir)).ToList())
                UpdatePath(f, newDir + f.Path[oldDir.Length..]);
        });
    }

    public void SetMissing(FileRecord f, bool missing)
    {
        if (f.Missing == missing) return;
        f.Missing = missing;
        Exec("UPDATE files SET missing=$m WHERE id=$i", ("$m", missing ? 1 : 0), ("$i", f.Id));
        Touch();
    }

    public void Remove(FileRecord f, FileRecord? keepLinksInto = null)
    {
        if (keepLinksInto != null) foreach (var fo in f.Folders) Link(fo, keepLinksInto);
        _files.Remove(f.Id);
        _byPath.Remove(f.Path);
        if (f.FileId != 0 && _byId.TryGetValue((f.Volume, f.FileId), out var x) && x == f) _byId.Remove((f.Volume, f.FileId));
        Exec("DELETE FROM files WHERE id=$i", ("$i", f.Id));
        Touch();
    }

    // ===== 연결 (색인 폴더 ↔ 파일) =====
    public void Link(long folderId, FileRecord f)
    {
        if (!f.Folders.Add(folderId)) return;
        Exec("INSERT OR IGNORE INTO links(folder_id, file_id) VALUES($a,$b)", ("$a", folderId), ("$b", f.Id));
        Touch();
    }

    public void Unlink(long folderId, FileRecord f)
    {
        if (!f.Folders.Remove(folderId)) return;
        Exec("DELETE FROM links WHERE folder_id=$a AND file_id=$b", ("$a", folderId), ("$b", f.Id));
        Touch();
    }

    // ===== 조회 =====
    /// <summary>미분류 = 감시 폴더 안에 있지만 아직 어떤 색인에도 연결되지 않은 파일.</summary>
    public IEnumerable<FileRecord> Unclassified() => _files.Values.Where(f => f.Folders.Count == 0 && !f.Missing);
    public int UnclassifiedCount => _files.Values.Count(f => f.Folders.Count == 0 && !f.Missing);

    /// <summary>공백으로 나눈 모든 단어가 이름(또는 색인 폴더 이름)에 들어 있는 파일.</summary>
    public IEnumerable<FileRecord> SearchFiles(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return _files.Values.Where(f =>
        {
            var name = f.Name;
            return words.All(w => name.Contains(w, StringComparison.OrdinalIgnoreCase)
                                  || f.Folders.Any(id => _folders.TryGetValue(id, out var fo) && fo.Name.Contains(w, StringComparison.OrdinalIgnoreCase)));
        });
    }

    public IEnumerable<IndexFolder> SearchFolders(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return _folders.Values.Where(f => words.All(w => f.Name.Contains(w, StringComparison.OrdinalIgnoreCase)));
    }

    // ===== 파일 지문 (중복 찾기) — 경로·크기·수정 시각이 같으면 다시 읽지 않는다 =====
    private readonly object _hashLock = new();
    private readonly SqliteConnection _hashDb;

    private SqliteConnection HashDb()
    {
        if (_hashDb.State != System.Data.ConnectionState.Open) _hashDb.Open();
        return _hashDb;
    }

    public (string? partial, string? full) GetHash(string path, long size, long mtimeTicks)
    {
        lock (_hashLock)
        {
            using var c = HashDb().CreateCommand();
            c.CommandText = "SELECT partial, full FROM file_hashes WHERE path=$p AND size=$s AND mtime=$m";
            c.Parameters.AddWithValue("$p", path);
            c.Parameters.AddWithValue("$s", size);
            c.Parameters.AddWithValue("$m", mtimeTicks);
            using var r = c.ExecuteReader();
            return r.Read() ? (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)) : (null, null);
        }
    }

    public void PutHash(string path, long size, long mtimeTicks, string? partial, string? full)
    {
        lock (_hashLock)
        {
            using var c = HashDb().CreateCommand();
            c.CommandText = """
                INSERT INTO file_hashes(path, size, mtime, partial, full) VALUES($p,$s,$m,$a,$b)
                ON CONFLICT(path) DO UPDATE SET size=$s, mtime=$m,
                    partial=COALESCE($a, CASE WHEN file_hashes.size=$s AND file_hashes.mtime=$m THEN file_hashes.partial END),
                    full=COALESCE($b, CASE WHEN file_hashes.size=$s AND file_hashes.mtime=$m THEN file_hashes.full END)
                """;
            c.Parameters.AddWithValue("$p", path);
            c.Parameters.AddWithValue("$s", size);
            c.Parameters.AddWithValue("$m", mtimeTicks);
            c.Parameters.AddWithValue("$a", (object?)partial ?? DBNull.Value);
            c.Parameters.AddWithValue("$b", (object?)full ?? DBNull.Value);
            c.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        _hashDb.Dispose();
        _db.Dispose();
    }
}

/// <summary>탐색기와 같은 자연 정렬 (파일2 &lt; 파일10).</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();
    public int Compare(string? x, string? y) => NativeMethods.StrCmpLogicalW(x ?? "", y ?? "");
}
