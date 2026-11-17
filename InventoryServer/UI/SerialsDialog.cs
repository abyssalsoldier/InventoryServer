using InventoryServer.Data;

namespace InventoryServer.UI;

/// <summary>Manage the serial-numbered units of one item: add, mark in use, return to stock, remove.</summary>
public class SerialsDialog : Form
{
    readonly Db _db;
    readonly string _sku;
    readonly string _user;
    readonly Label _summary = new() { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(6), Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) };
    readonly DataGridView _available = Grid();
    readonly DataGridView _inUse = Grid();
    List<SerialUnit> _availableUnits = new();
    List<SerialUnit> _inUseUnits = new();

    /// <summary>True when anything was changed, so the caller can refresh.</summary>
    public bool Changed { get; private set; }

    static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = SystemColors.Window,
    };

    static Button Btn(string text, EventHandler click, int w = 120)
    {
        var b = new Button { Text = text, Width = w, Height = 28 };
        b.Click += click;
        return b;
    }

    public SerialsDialog(Db db, string sku, string user)
    {
        _db = db;
        _sku = sku;
        _user = user;
        Text = "Serial numbers";
        ClientSize = new Size(820, 560);
        MinimumSize = new Size(640, 420);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;

        foreach (var (n, h) in new[] { ("serial", "Serial number"), ("since", "In stock since"), ("note", "Note") })
            _available.Columns.Add(n, h);
        foreach (var (n, h) in new[] { ("serial", "Serial number"), ("where", "Where used"), ("since", "Since"), ("who", "Who"), ("note", "Note") })
            _inUse.Columns.Add(n, h);
        _available.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) MarkInUse(); };
        _inUse.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Return(); };

        var availButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(3) };
        availButtons.Controls.AddRange(new Control[]
        {
            Btn("Add serials…", (_, _) => AddSerials()), Btn("Mark in use…", (_, _) => MarkInUse()),
            Btn("Remove…", (_, _) => Remove(_available, _availableUnits), 90),
        });
        var availBox = new GroupBox { Text = "Available (in stock)", Dock = DockStyle.Fill };
        availBox.Controls.Add(_available);
        availBox.Controls.Add(availButtons);

        var useButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(3) };
        useButtons.Controls.AddRange(new Control[]
        {
            Btn("Return to stock", (_, _) => Return()), Btn("Remove…", (_, _) => Remove(_inUse, _inUseUnits), 90),
        });
        var useBox = new GroupBox { Text = "In use", Dock = DockStyle.Fill };
        useBox.Controls.Add(_inUse);
        useBox.Controls.Add(useButtons);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
        split.Panel1.Controls.Add(availBox);
        split.Panel2.Controls.Add(useBox);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        bottom.Controls.Add(Btn("Close", (_, _) => Close(), 80));

        Controls.Add(split);
        Controls.Add(bottom);
        Controls.Add(_summary);
        Load += (_, _) => split.SplitterDistance = split.Width * 2 / 5;
        Reload();
    }

    void Reload()
    {
        var item = _db.Get(_sku);
        if (item == null) { Close(); return; }
        Text = $"Serial numbers – {item.Name} ({item.Sku})";
        _summary.Text = $"{item.Stock} available · {item.InUse} in use";
        var units = _db.ListUnits(_sku);
        _availableUnits = units.Where(u => !u.IsInUse).ToList();
        _inUseUnits = units.Where(u => u.IsInUse).ToList();
        _available.Rows.Clear();
        foreach (var u in _availableUnits) _available.Rows.Add(u.Serial, u.Since, u.Note);
        _inUse.Rows.Clear();
        foreach (var u in _inUseUnits) _inUse.Rows.Add(u.Serial, u.Location, u.Since, u.User, u.Note);
    }

    static SerialUnit? Selected(DataGridView grid, List<SerialUnit> units) =>
        grid.CurrentRow is { } r && r.Index < units.Count ? units[r.Index] : null;

    void Fail(string message) => MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    void Run(Action action)
    {
        try { action(); Changed = true; Reload(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException) { Fail(e.Message); Reload(); }
    }

    void AddSerials()
    {
        var v = FieldDialog.Ask(this, "Add serials", "One serial number per line (or comma separated). Each becomes one unit in stock.",
            new Field("Serial numbers", Multiline: true), new Field("Note (optional)"));
        if (v == null) return;
        Run(() => _db.AddUnits(_sku, Db.ParseSerials(v[0]), v[1], _user));
    }

    void MarkInUse()
    {
        if (Selected(_available, _availableUnits) is not { } u) { Fail("Select an available serial number first."); return; }
        var v = FieldDialog.Ask(this, "Mark in use", $"Serial {u.Serial} will leave available stock.",
            new Field("Where used"), new Field("Note (optional)"));
        if (v == null) return;
        Run(() => _db.MarkInUse(u.Id, v[0], v[1], _user));
    }

    void Return()
    {
        if (Selected(_inUse, _inUseUnits) is not { } u) { Fail("Select a serial number that is in use first."); return; }
        var v = FieldDialog.Ask(this, "Return to stock", $"Serial {u.Serial} (in use at {u.Location}) goes back into available stock.",
            new Field("Note (optional)"));
        if (v == null) return;
        Run(() => _db.ReturnUnit(u.Id, v[0], _user));
    }

    void Remove(DataGridView grid, List<SerialUnit> units)
    {
        if (Selected(grid, units) is not { } u) { Fail("Select a serial number first."); return; }
        var v = FieldDialog.Ask(this, "Remove serial number",
            $"Permanently remove serial {u.Serial}? Use this for lost or broken units, or one entered by mistake. The reason is kept in the history.",
            new Field("Reason"));
        if (v == null) return;
        Run(() => _db.RemoveUnit(u.Id, v[0], _user));
    }
}
