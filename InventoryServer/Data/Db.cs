using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace InventoryServer.Data;

public class Item
{
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Keywords { get; set; } = "";
    public string Location { get; set; } = "";
    public string Unit { get; set; } = "each";
    public int Stock { get; set; }
    public int ReorderLevel { get; set; }
    /// <summary>Tracked by serial number: <see cref="Stock"/> is then the number of available units.</summary>
    public bool Serialized { get; set; }
    /// <summary>Serialized items only: units currently marked in use.</summary>
    public int InUse { get; set; }

    public string Status => Stock <= 0 ? "Out of stock" : Stock <= ReorderLevel ? "Low" : "In stock";
}

public class Txn
{
    public long Id { get; set; }
    public string Sku { get; set; } = "";
    public int Delta { get; set; }
    public string Kind { get; set; } = "";
    public string Location { get; set; } = "";
    public string Note { get; set; } = "";
    public string User { get; set; } = "";
    public string Ts { get; set; } = "";
    public string Serial { get; set; } = "";
}

/// <summary>One serial-numbered unit of a serialized item.</summary>
public class SerialUnit
{
    public const string Available = "available";
    public const string InUseStatus = "in_use";

    public long Id { get; set; }
    public string Sku { get; set; } = "";
    public string ItemName { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Status { get; set; } = Available;
    /// <summary>Where it is used (in-use units only).</summary>
    public string Location { get; set; } = "";
    public string Note { get; set; } = "";
    /// <summary>Who last changed its status, and when.</summary>
    public string User { get; set; } = "";
    public string Since { get; set; } = "";

    public bool IsInUse => Status == InUseStatus;
}

public class User
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string Role { get; set; } = Roles.Staff;
    public string CreatedAt { get; set; } = "";
}

public static class Roles
{
    public const string Admin = "admin";
    public const string Staff = "staff";
}

public record SessionInfo(User User, string Csrf);

/// <summary>One company's SQLite database (items, stock history, users, sessions).</summary>
public class Db
{
    public const int MinPasswordLength = 10;
    const int Pbkdf2Iterations = 600_000;
    static readonly TimeSpan SessionIdle = TimeSpan.FromHours(12);
    static readonly Regex SkuPattern = new(@"^[A-Za-z0-9._-]{1,40}$", RegexOptions.Compiled);
    static readonly Regex UserPattern = new(@"^[a-z0-9._@-]{2,40}$", RegexOptions.Compiled);

    readonly string _dataDir;
    readonly string _dbPath;

    public Db(ServerProfile profile)
    {
        _dataDir = profile.DataDir;
        _dbPath = profile.DbPath;
    }

    public static bool IsValidSku(string sku) => SkuPattern.IsMatch(sku);
    public static bool IsValidUsername(string name) => UserPattern.IsMatch(name);

    SqliteConnection Open()
    {
        var c = new SqliteConnection($"Data Source={_dbPath}");
        c.Open();
        Exec(c, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        return c;
    }

    static void Exec(SqliteConnection c, string sql, params (string, object?)[] p)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    static object? Scalar(SqliteConnection c, string sql, params (string, object?)[] p)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd.ExecuteScalar();
    }

    /// <summary>Creates or upgrades the schema. Safe to run on every start.</summary>
    public void Init()
    {
        Directory.CreateDirectory(_dataDir);
        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, """
            CREATE TABLE IF NOT EXISTS items(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                sku TEXT NOT NULL UNIQUE,
                name TEXT NOT NULL,
                description TEXT NOT NULL DEFAULT '',
                keywords TEXT NOT NULL DEFAULT '',
                location TEXT NOT NULL DEFAULT '',
                unit TEXT NOT NULL DEFAULT 'each',
                stock INTEGER NOT NULL DEFAULT 0 CHECK(stock >= 0),
                reorder_level INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))
            );
            CREATE TABLE IF NOT EXISTS transactions(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                sku TEXT NOT NULL REFERENCES items(sku) ON DELETE CASCADE ON UPDATE CASCADE,
                delta INTEGER NOT NULL,
                kind TEXT NOT NULL,
                location TEXT NOT NULL DEFAULT '',
                note TEXT NOT NULL DEFAULT '',
                user TEXT NOT NULL DEFAULT '',
                ts TEXT NOT NULL DEFAULT (datetime('now','localtime'))
            );
            CREATE INDEX IF NOT EXISTS ix_txn_sku ON transactions(sku, id DESC);
            CREATE VIRTUAL TABLE IF NOT EXISTS items_fts USING fts5(
                sku, name, description, keywords, location, content='items', content_rowid='id');
            CREATE TRIGGER IF NOT EXISTS items_ai AFTER INSERT ON items BEGIN
                INSERT INTO items_fts(rowid, sku, name, description, keywords, location)
                VALUES (new.id, new.sku, new.name, new.description, new.keywords, new.location);
            END;
            CREATE TRIGGER IF NOT EXISTS items_ad AFTER DELETE ON items BEGIN
                INSERT INTO items_fts(items_fts, rowid, sku, name, description, keywords, location)
                VALUES ('delete', old.id, old.sku, old.name, old.description, old.keywords, old.location);
            END;
            CREATE TRIGGER IF NOT EXISTS items_au AFTER UPDATE OF sku, name, description, keywords, location ON items BEGIN
                INSERT INTO items_fts(items_fts, rowid, sku, name, description, keywords, location)
                VALUES ('delete', old.id, old.sku, old.name, old.description, old.keywords, old.location);
                INSERT INTO items_fts(rowid, sku, name, description, keywords, location)
                VALUES (new.id, new.sku, new.name, new.description, new.keywords, new.location);
            END;
            CREATE TABLE IF NOT EXISTS users(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                username TEXT NOT NULL UNIQUE,
                password_hash TEXT NOT NULL,
                role TEXT NOT NULL CHECK(role IN ('admin','staff')),
                created_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))
            );
            CREATE TABLE IF NOT EXISTS sessions(
                token_hash TEXT PRIMARY KEY,
                user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                csrf TEXT NOT NULL,
                last_seen INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS serial_units(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                sku TEXT NOT NULL REFERENCES items(sku) ON DELETE CASCADE ON UPDATE CASCADE,
                serial TEXT NOT NULL COLLATE NOCASE,
                status TEXT NOT NULL DEFAULT 'available' CHECK(status IN ('available','in_use')),
                location TEXT NOT NULL DEFAULT '',
                note TEXT NOT NULL DEFAULT '',
                user TEXT NOT NULL DEFAULT '',
                since TEXT NOT NULL DEFAULT (datetime('now','localtime')),
                UNIQUE(sku, serial)
            );
            CREATE INDEX IF NOT EXISTS ix_units_status ON serial_units(status, sku);
            """);
        // Columns added after the first release.
        if (!HasColumn(c, "items", "serialized"))
            Exec(c, "ALTER TABLE items ADD COLUMN serialized INTEGER NOT NULL DEFAULT 0");
        if (!HasColumn(c, "transactions", "serial"))
            Exec(c, "ALTER TABLE transactions ADD COLUMN serial TEXT NOT NULL DEFAULT ''");
    }

    static bool HasColumn(SqliteConnection c, string table, string column)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = $c";
        cmd.Parameters.AddWithValue("$c", column);
        return cmd.ExecuteScalar() != null;
    }

    // ---------------- items ----------------

    const string ItemCols = """
        i.sku, i.name, i.description, i.keywords, i.location, i.unit, i.stock, i.reorder_level, i.serialized,
        (SELECT COUNT(*) FROM serial_units u WHERE u.sku = i.sku AND u.status = 'in_use')
        """;

    static Item ReadItem(SqliteDataReader r) => new()
    {
        Sku = r.GetString(0), Name = r.GetString(1), Description = r.GetString(2), Keywords = r.GetString(3),
        Location = r.GetString(4), Unit = r.GetString(5), Stock = r.GetInt32(6), ReorderLevel = r.GetInt32(7),
        Serialized = r.GetInt64(8) != 0, InUse = r.GetInt32(9),
    };

    /// <summary>Turns "stainless bol" into an FTS5 prefix query: "stainless"* "bol"*</summary>
    static string BuildMatch(string q) =>
        string.Join(" ", Regex.Matches(q, @"[\p{L}\p{N}]+").Select(m => $"\"{m.Value}\"*"));

    public List<Item> Search(string? query, bool inStockOnly = false)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var match = BuildMatch(query ?? "");
        var stockFilter = inStockOnly ? "i.stock > 0" : "1=1";
        if (match.Length == 0)
            cmd.CommandText = $"SELECT {ItemCols} FROM items i WHERE {stockFilter} ORDER BY i.name COLLATE NOCASE";
        else
        {
            cmd.CommandText = $"""
                SELECT {ItemCols} FROM items_fts f JOIN items i ON i.id = f.rowid
                WHERE items_fts MATCH $m AND {stockFilter} ORDER BY f.rank
                """;
            cmd.Parameters.AddWithValue("$m", match);
        }
        var list = new List<Item>();
        using (var r = cmd.ExecuteReader())
            while (r.Read()) list.Add(ReadItem(r));

        // An exact serial number finds the item it belongs to (listed first).
        var serial = (query ?? "").Trim();
        if (serial.Length > 0)
        {
            using var sc = c.CreateCommand();
            sc.CommandText = $"""
                SELECT {ItemCols} FROM items i
                WHERE {stockFilter} AND EXISTS (SELECT 1 FROM serial_units u WHERE u.sku = i.sku AND u.serial = $q)
                ORDER BY i.name COLLATE NOCASE
                """;
            sc.Parameters.AddWithValue("$q", serial);
            var bySerial = new List<Item>();
            using (var r = sc.ExecuteReader())
                while (r.Read()) bySerial.Add(ReadItem(r));
            if (bySerial.Count > 0)
                list = bySerial.Concat(list.Where(i => bySerial.All(s => s.Sku != i.Sku))).ToList();
        }
        return list;
    }

    public Item? Get(string sku)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {ItemCols} FROM items i WHERE i.sku = $s";
        cmd.Parameters.AddWithValue("$s", sku);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadItem(r) : null;
    }

    public string NextSku()
    {
        using var c = Open();
        var n = Convert.ToInt32(Scalar(c, "SELECT COALESCE(MAX(id), 0) + 1 FROM items"));
        while (true)
        {
            var sku = $"ITM-{n:0000}";
            if (Get(sku) == null) return sku;
            n++;
        }
    }

    /// <summary>
    /// Creates an item; any opening stock is recorded as an 'add' transaction.
    /// For a serialized item the opening stock is <paramref name="serials"/> (one unit each) and <see cref="Item.Stock"/> is ignored.
    /// </summary>
    public void AddItem(Item item, string user = "", IEnumerable<string>? serials = null)
    {
        if (!IsValidSku(item.Sku)) throw new ArgumentException("SKU may only contain letters, numbers, '.', '_' and '-'.");
        if (string.IsNullOrWhiteSpace(item.Name)) throw new ArgumentException("Name is required.");
        if (item.Stock < 0 || item.ReorderLevel < 0) throw new ArgumentException("Numbers cannot be negative.");
        var units = item.Serialized ? CleanSerials(serials ?? Array.Empty<string>()) : new List<string>();
        using var c = Open();
        using var tx = c.BeginTransaction();
        try
        {
            Exec(c, """
                INSERT INTO items(sku, name, description, keywords, location, unit, stock, reorder_level, serialized)
                VALUES($sku, $name, $desc, $kw, $loc, $unit, $stock, $rl, $ser)
                """,
                ("$sku", item.Sku), ("$name", item.Name.Trim()), ("$desc", item.Description.Trim()),
                ("$kw", item.Keywords.Trim()), ("$loc", item.Location.Trim()),
                ("$unit", string.IsNullOrWhiteSpace(item.Unit) ? "each" : item.Unit.Trim()),
                ("$stock", item.Serialized ? 0 : item.Stock), ("$rl", item.ReorderLevel), ("$ser", item.Serialized ? 1 : 0));
            if (item.Serialized)
                InsertUnits(c, item.Sku, units, "Opening stock", user);
            else if (item.Stock > 0)
                Exec(c, "INSERT INTO transactions(sku, delta, kind, note, user) VALUES($s, $d, 'add', 'Opening stock', $u)",
                    ("$s", item.Sku), ("$d", item.Stock), ("$u", user));
            tx.Commit();
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"An item with SKU '{item.Sku}' already exists.");
        }
    }

    /// <summary>Updates descriptive fields only; stock changes go through <see cref="Adjust"/>.</summary>
    public void UpdateItem(Item item)
    {
        if (string.IsNullOrWhiteSpace(item.Name)) throw new ArgumentException("Name is required.");
        using var c = Open();
        Exec(c, """
            UPDATE items SET name=$name, description=$desc, keywords=$kw, location=$loc, unit=$unit, reorder_level=$rl
            WHERE sku=$sku
            """,
            ("$sku", item.Sku), ("$name", item.Name.Trim()), ("$desc", item.Description.Trim()),
            ("$kw", item.Keywords.Trim()), ("$loc", item.Location.Trim()),
            ("$unit", string.IsNullOrWhiteSpace(item.Unit) ? "each" : item.Unit.Trim()), ("$rl", item.ReorderLevel));
    }

    public void DeleteItem(string sku)
    {
        using var c = Open();
        Exec(c, "DELETE FROM items WHERE sku=$s", ("$s", sku));
    }

    /// <summary>Changes stock by <paramref name="delta"/> and logs it. Throws if stock would go below zero.</summary>
    public int Adjust(string sku, int delta, string kind, string location = "", string note = "", string user = "")
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        if (Convert.ToInt64(Scalar(c, "SELECT serialized FROM items WHERE sku = $s", ("$s", sku)) ?? 0L) != 0)
            throw new InvalidOperationException("This item is tracked by serial number. Add, use or return individual units instead.");
        int rows;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "UPDATE items SET stock = stock + $d WHERE sku = $s AND stock + $d >= 0";
            cmd.Parameters.AddWithValue("$d", delta);
            cmd.Parameters.AddWithValue("$s", sku);
            rows = cmd.ExecuteNonQuery();
        }
        if (rows == 0)
        {
            var item = Get(sku) ?? throw new KeyNotFoundException($"No item with SKU '{sku}'.");
            throw new InvalidOperationException($"Only {item.Stock} {item.Unit} in stock.");
        }
        Exec(c, "INSERT INTO transactions(sku, delta, kind, location, note, user) VALUES($s,$d,$k,$l,$n,$u)",
            ("$s", sku), ("$d", delta), ("$k", kind), ("$l", location.Trim()), ("$n", note.Trim()), ("$u", user.Trim()));
        var stock = Convert.ToInt32(Scalar(c, "SELECT stock FROM items WHERE sku = $s", ("$s", sku)));
        tx.Commit();
        return stock;
    }

    /// <summary>Sets the stock to an exact count (e.g. after a physical count), logged as an 'adjust'.</summary>
    public void SetCount(string sku, int newCount, string note = "", string user = "")
    {
        if (newCount < 0) throw new ArgumentException("Count cannot be negative.");
        var item = Get(sku) ?? throw new KeyNotFoundException($"No item with SKU '{sku}'.");
        var delta = newCount - item.Stock;
        if (delta != 0) Adjust(sku, delta, "adjust", note: string.IsNullOrWhiteSpace(note) ? "Recount" : note, user: user);
    }

    public List<Txn> History(string? sku, int limit = 50)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, sku, delta, kind, location, note, user, ts, serial FROM transactions
            {(sku == null ? "" : "WHERE sku = $s")} ORDER BY id DESC LIMIT $n
            """;
        if (sku != null) cmd.Parameters.AddWithValue("$s", sku);
        cmd.Parameters.AddWithValue("$n", limit);
        var list = new List<Txn>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Txn
            {
                Id = r.GetInt64(0), Sku = r.GetString(1), Delta = r.GetInt32(2), Kind = r.GetString(3),
                Location = r.GetString(4), Note = r.GetString(5), User = r.GetString(6), Ts = r.GetString(7), Serial = r.GetString(8),
            });
        return list;
    }

    // ---------------- serial numbers ----------------

    public const int MaxSerialLength = 100;

    /// <summary>Splits pasted text into serial numbers (one per line, or comma separated).</summary>
    public static List<string> ParseSerials(string? text) =>
        (text ?? "").Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    static List<string> CleanSerials(IEnumerable<string> serials)
    {
        var list = new List<string>();
        foreach (var raw in serials)
        {
            var s = raw.Trim();
            if (s.Length == 0) continue;
            if (s.Length > MaxSerialLength) throw new ArgumentException($"Serial numbers can be at most {MaxSerialLength} characters.");
            if (list.Contains(s, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException($"Serial number '{s}' is listed twice.");
            list.Add(s);
        }
        return list;
    }

    /// <summary>Makes a serialized item's stock equal to its number of available units.</summary>
    static void SyncStock(SqliteConnection c, string sku) =>
        Exec(c, "UPDATE items SET stock = (SELECT COUNT(*) FROM serial_units WHERE sku = $s AND status = 'available') WHERE sku = $s", ("$s", sku));

    static void LogUnit(SqliteConnection c, string sku, string serial, int delta, string kind, string location, string note, string user) =>
        Exec(c, "INSERT INTO transactions(sku, delta, kind, location, note, user, serial) VALUES($s,$d,$k,$l,$n,$u,$sn)",
            ("$s", sku), ("$d", delta), ("$k", kind), ("$l", location.Trim()), ("$n", note.Trim()), ("$u", user.Trim()), ("$sn", serial));

    /// <summary>Inserts available units and logs each one. Caller owns the transaction.</summary>
    static void InsertUnits(SqliteConnection c, string sku, List<string> serials, string note, string user)
    {
        foreach (var s in serials)
        {
            if (Scalar(c, "SELECT serial FROM serial_units WHERE sku = $k AND serial = $s", ("$k", sku), ("$s", s)) is string existing)
                throw new InvalidOperationException($"Serial number '{existing}' is already recorded for this item.");
            Exec(c, "INSERT INTO serial_units(sku, serial, note, user) VALUES($k, $s, $n, $u)",
                ("$k", sku), ("$s", s), ("$n", note.Trim()), ("$u", user.Trim()));
            LogUnit(c, sku, s, +1, "add", "", note, user);
        }
        SyncStock(c, sku);
    }

    void RequireSerialized(SqliteConnection c, string sku)
    {
        var flag = Scalar(c, "SELECT serialized FROM items WHERE sku = $s", ("$s", sku)) ?? throw new KeyNotFoundException($"No item with SKU '{sku}'.");
        if (Convert.ToInt64(flag) == 0) throw new InvalidOperationException("This item is not tracked by serial number.");
    }

    /// <summary>Adds new available units to a serialized item. Returns the number added.</summary>
    public int AddUnits(string sku, IEnumerable<string> serials, string note = "", string user = "")
    {
        var list = CleanSerials(serials);
        if (list.Count == 0) throw new ArgumentException("Enter at least one serial number.");
        using var c = Open();
        using var tx = c.BeginTransaction();
        RequireSerialized(c, sku);
        InsertUnits(c, sku, list, note, user);
        tx.Commit();
        return list.Count;
    }

    const string UnitCols = "u.id, u.sku, i.name, u.serial, u.status, u.location, u.note, u.user, u.since";

    static SerialUnit ReadUnit(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), Sku = r.GetString(1), ItemName = r.GetString(2), Serial = r.GetString(3), Status = r.GetString(4),
        Location = r.GetString(5), Note = r.GetString(6), User = r.GetString(7), Since = r.GetString(8),
    };

    static List<SerialUnit> ReadUnits(SqliteCommand cmd)
    {
        var list = new List<SerialUnit>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadUnit(r));
        return list;
    }

    public SerialUnit? GetUnit(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {UnitCols} FROM serial_units u JOIN items i ON i.sku = u.sku WHERE u.id = $i";
        cmd.Parameters.AddWithValue("$i", id);
        return ReadUnits(cmd).FirstOrDefault();
    }

    /// <summary>All units of one item: in use first, then by serial number.</summary>
    public List<SerialUnit> ListUnits(string sku)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT {UnitCols} FROM serial_units u JOIN items i ON i.sku = u.sku
            WHERE u.sku = $s ORDER BY u.status DESC, u.serial
            """;
        cmd.Parameters.AddWithValue("$s", sku);
        return ReadUnits(cmd);
    }

    /// <summary>Every unit currently in use, across all items, optionally filtered by text.</summary>
    public List<SerialUnit> ListInUse(string? filter = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var f = (filter ?? "").Trim();
        const string textFilter = """
            AND (u.serial LIKE $f ESCAPE '\' OR u.location LIKE $f ESCAPE '\' OR u.note LIKE $f ESCAPE '\'
                 OR i.name LIKE $f ESCAPE '\' OR i.sku LIKE $f ESCAPE '\' OR u.user LIKE $f ESCAPE '\')
            """;
        cmd.CommandText = $"""
            SELECT {UnitCols} FROM serial_units u JOIN items i ON i.sku = u.sku
            WHERE u.status = 'in_use' {(f.Length == 0 ? "" : textFilter)}
            ORDER BY u.location COLLATE NOCASE, i.name COLLATE NOCASE, u.serial
            """;
        if (f.Length > 0) cmd.Parameters.AddWithValue("$f", "%" + f.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%");
        return ReadUnits(cmd);
    }

    /// <summary>Every unit of every item (for export).</summary>
    public List<SerialUnit> AllUnits()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {UnitCols} FROM serial_units u JOIN items i ON i.sku = u.sku ORDER BY i.name COLLATE NOCASE, u.serial";
        return ReadUnits(cmd);
    }

    SerialUnit RequireUnit(SqliteConnection c, long id)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {UnitCols} FROM serial_units u JOIN items i ON i.sku = u.sku WHERE u.id = $i";
        cmd.Parameters.AddWithValue("$i", id);
        return ReadUnits(cmd).FirstOrDefault() ?? throw new KeyNotFoundException("That serial number no longer exists.");
    }

    /// <summary>Takes an available unit out of stock and records where it is used.</summary>
    public SerialUnit MarkInUse(long unitId, string location, string note = "", string user = "")
    {
        if (string.IsNullOrWhiteSpace(location)) throw new ArgumentException("Please say where it is used.");
        using var c = Open();
        using var tx = c.BeginTransaction();
        var u = RequireUnit(c, unitId);
        if (u.IsInUse) throw new InvalidOperationException($"Serial {u.Serial} is already in use at {u.Location}.");
        Exec(c, """
            UPDATE serial_units SET status = 'in_use', location = $l, note = $n, user = $u, since = datetime('now','localtime')
            WHERE id = $i
            """, ("$l", location.Trim()), ("$n", note.Trim()), ("$u", user.Trim()), ("$i", unitId));
        LogUnit(c, u.Sku, u.Serial, -1, "use", location, note, user);
        SyncStock(c, u.Sku);
        tx.Commit();
        return u;
    }

    /// <summary>Puts an in-use unit back into available stock.</summary>
    public SerialUnit ReturnUnit(long unitId, string note = "", string user = "")
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var u = RequireUnit(c, unitId);
        if (!u.IsInUse) throw new InvalidOperationException($"Serial {u.Serial} is already in stock.");
        Exec(c, """
            UPDATE serial_units SET status = 'available', location = '', note = $n, user = $u, since = datetime('now','localtime')
            WHERE id = $i
            """, ("$n", note.Trim()), ("$u", user.Trim()), ("$i", unitId));
        LogUnit(c, u.Sku, u.Serial, +1, "return", u.Location, note, user);
        SyncStock(c, u.Sku);
        tx.Commit();
        return u;
    }

    /// <summary>Deletes a unit (lost, broken, entered by mistake). Logged with the reason.</summary>
    public SerialUnit RemoveUnit(long unitId, string reason, string user = "")
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Please give a reason for removing it.");
        using var c = Open();
        using var tx = c.BeginTransaction();
        var u = RequireUnit(c, unitId);
        Exec(c, "DELETE FROM serial_units WHERE id = $i", ("$i", unitId));
        LogUnit(c, u.Sku, u.Serial, u.IsInUse ? 0 : -1, "remove", u.Location, reason, user);
        SyncStock(c, u.Sku);
        tx.Commit();
        return u;
    }

    /// <summary>
    /// Turns serial tracking on or off for an existing item.
    /// Turning it on needs one serial number per unit currently in stock; turning it off needs every unit back in stock.
    /// </summary>
    public void SetSerialized(string sku, bool on, IEnumerable<string>? serials = null, string user = "")
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var item = Get(sku) ?? throw new KeyNotFoundException($"No item with SKU '{sku}'.");
        if (item.Serialized == on) return;
        if (on)
        {
            var list = CleanSerials(serials ?? Array.Empty<string>());
            if (list.Count != item.Stock)
                throw new ArgumentException($"There are {item.Stock} {item.Unit} in stock, so enter exactly {item.Stock} serial number(s) (you entered {list.Count}).");
            Exec(c, "UPDATE items SET serialized = 1 WHERE sku = $s", ("$s", sku));
            foreach (var s in list)
            {
                Exec(c, "INSERT INTO serial_units(sku, serial, user) VALUES($k, $s, $u)", ("$k", sku), ("$s", s), ("$u", user.Trim()));
                LogUnit(c, sku, s, 0, "adjust", "", "Serial number recorded for existing stock", user);
            }
            SyncStock(c, sku);
        }
        else
        {
            if (item.InUse > 0)
                throw new InvalidOperationException($"{item.InUse} unit(s) are still marked in use. Return or remove them first.");
            Exec(c, "DELETE FROM serial_units WHERE sku = $s", ("$s", sku));
            Exec(c, "UPDATE items SET serialized = 0 WHERE sku = $s", ("$s", sku));
            Exec(c, "INSERT INTO transactions(sku, delta, kind, note, user) VALUES($s, 0, 'adjust', 'Serial number tracking turned off', $u)",
                ("$s", sku), ("$u", user.Trim()));
        }
        tx.Commit();
    }

    /// <summary>Writes a consistent copy of the database to <paramref name="path"/>.</summary>
    public void Backup(string path)
    {
        using var c = Open();
        Exec(c, "VACUUM INTO $p", ("$p", path));
    }

    // ---------------- users ----------------

    static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Pbkdf2Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    static void ValidatePassword(string password)
    {
        if (password.Length < MinPasswordLength)
            throw new ArgumentException($"Password must be at least {MinPasswordLength} characters.");
        if (password.Distinct().Count() < 4)
            throw new ArgumentException("Password is too repetitive.");
    }

    public bool HasUsers()
    {
        using var c = Open();
        return Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM users")) > 0;
    }

    public List<User> ListUsers()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, username, role, created_at FROM users ORDER BY username";
        var list = new List<User>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new User { Id = r.GetInt64(0), Username = r.GetString(1), Role = r.GetString(2), CreatedAt = r.GetString(3) });
        return list;
    }

    public void CreateUser(string username, string password, string role)
    {
        username = username.Trim().ToLowerInvariant();
        if (!IsValidUsername(username)) throw new ArgumentException("Username must be 2-40 characters: letters, numbers, '.', '_', '-' or '@'.");
        if (role != Roles.Admin && role != Roles.Staff) throw new ArgumentException("Unknown role.");
        ValidatePassword(password);
        if (username.Equals(password, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Password cannot be the username.");
        using var c = Open();
        try
        {
            Exec(c, "INSERT INTO users(username, password_hash, role) VALUES($u,$h,$r)",
                ("$u", username), ("$h", HashPassword(password)), ("$r", role));
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"User '{username}' already exists.");
        }
    }

    public void SetPassword(long userId, string password)
    {
        ValidatePassword(password);
        using var c = Open();
        Exec(c, "UPDATE users SET password_hash=$h WHERE id=$i", ("$h", HashPassword(password)), ("$i", userId));
        Exec(c, "DELETE FROM sessions WHERE user_id=$i", ("$i", userId));
    }

    public void SetRole(long userId, string role)
    {
        using var c = Open();
        if (role != Roles.Admin && Scalar(c, "SELECT role FROM users WHERE id=$i", ("$i", userId)) as string == Roles.Admin &&
            Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM users WHERE role='admin'")) <= 1)
            throw new InvalidOperationException("There must be at least one admin.");
        Exec(c, "UPDATE users SET role=$r WHERE id=$i", ("$r", role), ("$i", userId));
    }

    public void DeleteUser(long userId)
    {
        using var c = Open();
        if (Scalar(c, "SELECT role FROM users WHERE id=$i", ("$i", userId)) as string == Roles.Admin &&
            Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM users WHERE role='admin'")) <= 1)
            throw new InvalidOperationException("There must be at least one admin.");
        Exec(c, "DELETE FROM users WHERE id=$i", ("$i", userId));
    }

    static readonly string DummyHash = HashPassword("not-a-real-password");

    /// <summary>Checks credentials. Takes about the same time whether or not the user exists.</summary>
    public User? Authenticate(string username, string password)
    {
        username = username.Trim().ToLowerInvariant();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, username, role, created_at, password_hash FROM users WHERE username=$u";
        cmd.Parameters.AddWithValue("$u", username);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
        {
            VerifyPassword(password, DummyHash);
            return null;
        }
        return VerifyPassword(password, r.GetString(4))
            ? new User { Id = r.GetInt64(0), Username = r.GetString(1), Role = r.GetString(2), CreatedAt = r.GetString(3) }
            : null;
    }

    // ---------------- sessions ----------------

    static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Creates a login session and returns the secret cookie token (only its hash is stored).</summary>
    public string CreateSession(long userId)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var csrf = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        using var c = Open();
        Exec(c, "DELETE FROM sessions WHERE last_seen < $old", ("$old", Now() - (long)SessionIdle.TotalSeconds));
        Exec(c, "INSERT INTO sessions(token_hash, user_id, csrf, last_seen) VALUES($t,$u,$c,$n)",
            ("$t", HashToken(token)), ("$u", userId), ("$c", csrf), ("$n", Now()));
        return token;
    }

    public SessionInfo? GetSession(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var hash = HashToken(token);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT u.id, u.username, u.role, u.created_at, s.csrf, s.last_seen
            FROM sessions s JOIN users u ON u.id = s.user_id WHERE s.token_hash = $t
            """;
        cmd.Parameters.AddWithValue("$t", hash);
        SessionInfo? info = null;
        long lastSeen = 0;
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) return null;
            info = new SessionInfo(new User { Id = r.GetInt64(0), Username = r.GetString(1), Role = r.GetString(2), CreatedAt = r.GetString(3) }, r.GetString(4));
            lastSeen = r.GetInt64(5);
        }
        var now = Now();
        if (now - lastSeen > SessionIdle.TotalSeconds)
        {
            Exec(c, "DELETE FROM sessions WHERE token_hash=$t", ("$t", hash));
            return null;
        }
        if (now - lastSeen > 60) Exec(c, "UPDATE sessions SET last_seen=$n WHERE token_hash=$t", ("$n", now), ("$t", hash));
        return info;
    }

    public void DeleteSession(string? token)
    {
        if (string.IsNullOrEmpty(token)) return;
        using var c = Open();
        Exec(c, "DELETE FROM sessions WHERE token_hash=$t", ("$t", HashToken(token)));
    }
}
