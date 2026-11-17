using System.Diagnostics;
using InventoryServer.Data;
using Microsoft.Data.Sqlite;

namespace InventoryServer.UI;

public class MainForm : Form
{
    readonly List<ServerProfile> _profiles = ProfileStore.Load();
    readonly Dictionary<string, Db> _dbs = new();
    readonly Dictionary<string, ServerHost> _hosts = new();
    static readonly string Me = Environment.UserName;

    // Items tab
    readonly ComboBox _picker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    readonly TextBox _search = new() { Width = 220, PlaceholderText = "Search name, keywords, location…" };
    readonly CheckBox _inStockOnly = new() { Text = "In stock only", AutoSize = true };
    readonly DataGridView _grid = NewGrid();
    readonly DataGridView _history = NewGrid();

    // In use tab (serial-numbered units across all items of the selected server)
    readonly TextBox _inUseSearch = new() { Width = 260, PlaceholderText = "Search serial, item, where used, who…" };
    readonly Label _inUseServer = new() { AutoSize = true, Margin = new Padding(12, 7, 3, 3) };
    readonly DataGridView _inUseGrid = NewGrid();
    List<SerialUnit> _inUseUnits = new();

    // Server tab
    readonly DataGridView _servers = NewGrid();
    readonly CheckBox _startOnLaunch = new() { Text = "Start this server when the app opens", AutoSize = true };
    readonly Button _startStop = new() { Text = "Start", Width = 90, Height = 28 };
    readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(900, 0) };
    bool _loadingServers;

    readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    readonly NotifyIcon _tray = new() { Text = "Inventory Server", Icon = SystemIcons.Application };
    readonly System.Windows.Forms.Timer _refresh = new() { Interval = 5000 };
    bool _exiting;

    // Updates
    readonly Panel _updateBar = new() { Dock = DockStyle.Top, Height = 40, Visible = false, BackColor = Color.FromArgb(255, 244, 206), Padding = new Padding(8, 6, 8, 6) };
    readonly Label _updateText = new() { AutoSize = true, Margin = new Padding(3, 8, 12, 3) };
    readonly Button _updateInstall = new() { Text = "Install now", Width = 100, Height = 28 };
    readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = (int)TimeSpan.FromHours(6).TotalMilliseconds };
    UpdateManifest? _available;
    bool _updating;

    public MainForm(string[]? resumeServerIds = null)
    {
        Text = $"Inventory Server {Updater.CurrentText}";
        ClientSize = new Size(1000, 660);
        MinimumSize = new Size(800, 520);
        StartPosition = FormStartPosition.CenterScreen;

        var items = new TabPage("Items");
        var inUse = new TabPage("In use");
        var server = new TabPage("Servers");
        BuildItemsTab(items);
        BuildInUseTab(inUse);
        BuildServerTab(server);
        _tabs.TabPages.AddRange(new[] { items, inUse, server });
        _tabs.SelectedIndexChanged += (_, _) => { if (_tabs.SelectedTab == inUse) LoadInUse(); };
        Controls.Add(_tabs);
        BuildUpdateBar();

        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Exit", null, (_, _) => { _exiting = true; Close(); });
        _tray.ContextMenuStrip = menu;

        _refresh.Tick += (_, _) =>
        {
            if (_tabs.SelectedTab == items && !_search.Focused && Current != null) LoadItems(keepSelection: true);
            if (_tabs.SelectedTab == inUse && !_inUseSearch.Focused && Current != null) LoadInUse();
            RefreshServerList();
        };
        _refresh.Start();

        Load += async (_, _) =>
        {
            FillPicker();
            RefreshServerList();
            if (_profiles.Count == 0)
            {
                _tabs.SelectedTab = server;
                MessageBox.Show(this, "Welcome! Let's set up your first server.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                NewServer();
            }
            foreach (var p in _profiles.Where(p => p.StartOnLaunch || (resumeServerIds?.Contains(p.Id) ?? false)).ToList())
                await StartAsync(p);

            if (resumeServerIds != null)
            {
                _tray.ShowBalloonTip(5000, "Inventory Server", $"Updated to version {Updater.CurrentText}.", ToolTipIcon.Info);
                await Task.Delay(TimeSpan.FromSeconds(15));
                Updater.Cleanup();   // the installer copy has exited by now
            }
            _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(userAsked: false);
            _updateTimer.Start();
            await Task.Delay(TimeSpan.FromSeconds(20));
            await CheckForUpdatesAsync(userAsked: false);
        };
    }

    // ---------- updates ----------

    void BuildUpdateBar()
    {
        var later = new Button { Text = "Later", Width = 70, Height = 28 };
        later.Click += (_, _) => _updateBar.Visible = false;
        _updateInstall.Click += async (_, _) => await InstallUpdateAsync();
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        flow.Controls.AddRange(new Control[] { _updateText, _updateInstall, later });
        _updateBar.Controls.Add(flow);
        Controls.Add(_updateBar);
    }

    async Task CheckForUpdatesAsync(bool userAsked)
    {
        if (_updating) return;
        if (!Updater.Configured)
        {
            if (userAsked)
                MessageBox.Show(this, "Updates are not set up in this build (no release repository / signing key).", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            _available = await Updater.CheckAsync();
        }
        catch (Exception e)
        {
            if (userAsked) Error("Could not check for updates:\n" + e.Message);
            return;
        }
        if (_available == null)
        {
            _updateBar.Visible = false;
            if (userAsked) MessageBox.Show(this, $"You have the latest version ({Updater.CurrentText}).", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var notes = _available.Notes.Length > 0 ? " – " + _available.Notes.Split('\n')[0].Trim() : "";
        _updateText.Text = $"Version {_available.Version} is available (you have {Updater.CurrentText}){notes}";
        var wasHidden = !_updateBar.Visible;
        _updateBar.Visible = true;
        if (wasHidden && !Visible)
            _tray.ShowBalloonTip(5000, "Inventory Server", $"Version {_available.Version} is available. Open the app to install it.", ToolTipIcon.Info);
        if (userAsked) await InstallUpdateAsync();
    }

    async Task InstallUpdateAsync()
    {
        if (_available is not { } m || _updating) return;
        if (!Updater.CanWriteAppFolder())
        {
            Error($"This app cannot update itself because it cannot write to its own folder:\n{AppContext.BaseDirectory}\n\n" +
                  "Move the app folder somewhere you can write to (not Program Files), or run it once as administrator to update.");
            return;
        }
        var running = _profiles.Where(p => _hosts.TryGetValue(p.Id, out var h) && h.Running).ToList();
        var question = $"Install version {m.Version} now?" +
            (m.Notes.Length > 0 ? $"\n\nWhat's new:\n{m.Notes}" : "") +
            (running.Count > 0 ? $"\n\nThe running server(s) ({string.Join(", ", running.Select(p => p.DisplayName))}) stop for a few seconds and start again automatically." : "") +
            "\n\nYour inventory data is not affected.";
        if (MessageBox.Show(this, question, "Update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        _updating = true;
        _updateInstall.Enabled = false;
        try
        {
            var exe = await Updater.DownloadAsync(m, new Progress<string>(s => _updateText.Text = s));
            _updateText.Text = "Restarting…";
            Updater.LaunchInstaller(exe, running.Select(p => p.Id));
            _exiting = true;
            Close();
        }
        catch (Exception e)
        {
            Error("The update could not be downloaded:\n" + e.Message);
            _updateText.Text = $"Version {m.Version} is available (you have {Updater.CurrentText})";
            _updateInstall.Enabled = true;
            _updating = false;
        }
    }

    // ---------- layout ----------

    static DataGridView NewGrid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = SystemColors.Window,
    };

    static Button Btn(string text, EventHandler onClick, int width = 100)
    {
        var b = new Button { Text = text, Width = width, Height = 28 };
        b.Click += onClick;
        return b;
    }

    void BuildItemsTab(TabPage page)
    {
        _grid.Columns.Add("sku", "SKU");
        _grid.Columns.Add("name", "Name");
        _grid.Columns.Add("stock", "In stock");
        _grid.Columns.Add("inuse", "In use");
        _grid.Columns.Add("unit", "Unit");
        _grid.Columns.Add("loc", "Location");
        _grid.Columns.Add("status", "Status");
        _grid.Columns["sku"]!.FillWeight = 70;
        _grid.Columns["name"]!.FillWeight = 160;
        _grid.Columns["stock"]!.FillWeight = 50;
        _grid.Columns["inuse"]!.FillWeight = 45;
        _grid.Columns["unit"]!.FillWeight = 40;
        _grid.SelectionChanged += (_, _) => LoadHistory();
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) EditItem(); };

        foreach (var (n, h) in new[] { ("when", "When"), ("change", "±"), ("kind", "Type"), ("serial", "S/N"), ("where", "Where used"), ("who", "Who"), ("note", "Note") })
            _history.Columns.Add(n, h);
        _history.Columns["change"]!.FillWeight = 30;

        _search.TextChanged += (_, _) => LoadItems();
        _inStockOnly.CheckedChanged += (_, _) => LoadItems();
        _picker.SelectedIndexChanged += (_, _) => LoadItems();

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), WrapContents = true };
        top.Controls.AddRange(new Control[]
        {
            new Label { Text = "Server:", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, _picker, _search, _inStockOnly,
            Btn("Add item", (_, _) => AddItem()), Btn("Edit", (_, _) => EditItem(), 70), Btn("Delete", (_, _) => DeleteItem(), 70),
        });
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), WrapContents = true };
        actions.Controls.AddRange(new Control[]
        {
            Btn("Add stock", (_, _) => ChangeStock(+1)), Btn("Use stock", (_, _) => ChangeStock(-1)),
            Btn("Set count", (_, _) => SetCount()), Btn("Serial numbers…", (_, _) => ManageSerials(), 120), Btn("Show QR", (_, _) => ShowQr()),
            Btn("Print label", (_, _) => PrintLabels(selectedOnly: true)), Btn("Print all labels", (_, _) => PrintLabels(false), 120),
        });

        var historyBox = new GroupBox { Text = "Activity for selected item", Dock = DockStyle.Bottom, Height = 190 };
        historyBox.Controls.Add(_history);

        page.Controls.Add(_grid);
        page.Controls.Add(historyBox);
        page.Controls.Add(actions);
        page.Controls.Add(top);
        _grid.BringToFront();
    }

    void BuildInUseTab(TabPage page)
    {
        foreach (var (n, h, w) in new[] { ("item", "Item", 150), ("sku", "SKU", 70), ("serial", "Serial number", 110), ("where", "Where used", 140), ("since", "Since", 90), ("who", "Who", 60), ("note", "Note", 100) })
        {
            _inUseGrid.Columns.Add(n, h);
            _inUseGrid.Columns[n]!.FillWeight = w;
        }
        _inUseGrid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) ReturnInUse(); };
        _inUseSearch.TextChanged += (_, _) => LoadInUse();
        _picker.SelectedIndexChanged += (_, _) => { if (_tabs.SelectedTab == page) LoadInUse(); };

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), WrapContents = true };
        top.Controls.AddRange(new Control[]
        {
            _inUseSearch, Btn("Return to stock", (_, _) => ReturnInUse(), 120), Btn("Go to item", (_, _) => GoToInUseItem(), 90), _inUseServer,
        });
        var hint = new Label
        {
            Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8), ForeColor = SystemColors.GrayText,
            Text = "Serial-numbered units that are currently in use. Returning a unit puts it back into available stock. The server is chosen on the Items tab.",
        };
        page.Controls.Add(_inUseGrid);
        page.Controls.Add(hint);
        page.Controls.Add(top);
        _inUseGrid.BringToFront();
    }

    void BuildServerTab(TabPage page)
    {
        foreach (var (n, h, w) in new[] { ("name", "Company", 120), ("url", "Address (in QR codes)", 200), ("mode", "Access", 100), ("port", "Local port", 50), ("state", "Status", 70) })
        {
            _servers.Columns.Add(n, h);
            _servers.Columns[n]!.FillWeight = w;
        }
        _servers.SelectionChanged += (_, _) => OnServerSelected();
        _servers.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) EditServer(); };

        var row1 = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), WrapContents = true };
        row1.Controls.AddRange(new Control[]
        {
            Btn("New server…", (_, _) => NewServer(), 110), Btn("Edit…", (_, _) => EditServer(), 80), Btn("Remove…", (_, _) => RemoveServer(), 90),
            _startStop, _startOnLaunch,
        });
        var row2 = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), WrapContents = true };
        row2.Controls.AddRange(new Control[]
        {
            Btn("Open in browser", (_, _) => OpenSelected(), 120), Btn("Copy URL", (_, _) => CopyUrl(), 90),
            Btn("Users…", (_, _) => ManageUsers(), 80), Btn("Firewall help", (_, _) => FirewallHelp(), 100),
            Btn("Tunnel command", (_, _) => CopyTunnelCommand(), 120), Btn("Open data folder", (_, _) => OpenSelectedFolder(), 120),
            Btn("Backup now", (_, _) => Backup(), 100),
            Btn("Check for updates", async (_, _) => await CheckForUpdatesAsync(userAsked: true), 130),
        });
        var note = new Panel { Dock = DockStyle.Bottom, Height = 90, Padding = new Padding(8) };
        note.Controls.Add(_status);

        _startStop.Click += async (_, _) => { if (SelectedServer is { } p) { if (Host(p).Running) await StopAsync(p); else await StartAsync(p); } };
        _startOnLaunch.CheckedChanged += (_, _) =>
        {
            if (_loadingServers || SelectedServer is not { } p) return;
            p.StartOnLaunch = _startOnLaunch.Checked;
            ProfileStore.Save(_profiles);
        };

        page.Controls.Add(_servers);
        page.Controls.Add(note);
        page.Controls.Add(row2);
        page.Controls.Add(row1);
        _servers.BringToFront();
    }

    // ---------- profiles / databases ----------

    Db DbFor(ServerProfile p)
    {
        if (!_dbs.TryGetValue(p.Id, out var db))
        {
            db = new Db(p);
            db.Init();
            _dbs[p.Id] = db;
        }
        return db;
    }

    ServerHost Host(ServerProfile p)
    {
        if (!_hosts.TryGetValue(p.Id, out var h)) _hosts[p.Id] = h = new ServerHost(p, DbFor(p));
        return h;
    }

    ServerProfile? SelectedServer => _servers.CurrentRow?.Tag is string id ? _profiles.FirstOrDefault(p => p.Id == id) : null;

    ServerProfile? Current => _picker.SelectedIndex >= 0 && _picker.SelectedIndex < _profiles.Count ? _profiles[_picker.SelectedIndex] : null;

    void FillPicker()
    {
        var keep = Current?.Id;
        _picker.Items.Clear();
        foreach (var p in _profiles) _picker.Items.Add(p.DisplayName);
        var idx = keep == null ? 0 : Math.Max(0, _profiles.FindIndex(p => p.Id == keep));
        if (_profiles.Count > 0) _picker.SelectedIndex = idx;
        else { _grid.Rows.Clear(); _history.Rows.Clear(); }
    }

    void RefreshServerList()
    {
        var keep = SelectedServer?.Id;
        _loadingServers = true;
        _servers.Rows.Clear();
        foreach (var p in _profiles)
        {
            var running = _hosts.TryGetValue(p.Id, out var h) && h.Running;
            var i = _servers.Rows.Add(p.DisplayName, p.PublicUrl, ModeName(p.Mode), p.LocalPort, running ? "Running" : "Stopped");
            _servers.Rows[i].Tag = p.Id;
            _servers.Rows[i].DefaultCellStyle.ForeColor = running ? Color.SeaGreen : SystemColors.ControlText;
            if (p.Id == keep) _servers.CurrentCell = _servers.Rows[i].Cells[0];
        }
        _loadingServers = false;
        OnServerSelected();
    }

    static string ModeName(AccessMode m) => m switch
    {
        AccessMode.CloudflareTunnel => "Cloudflare Tunnel",
        AccessMode.OwnCertificate => "Own certificate",
        _ => "Internal",
    };

    void OnServerSelected()
    {
        var p = SelectedServer;
        _loadingServers = true;
        _startOnLaunch.Checked = p?.StartOnLaunch ?? false;
        _loadingServers = false;
        var running = p != null && _hosts.TryGetValue(p.Id, out var h) && h.Running;
        _startStop.Text = running ? "Stop" : "Start";
        _startStop.Enabled = p != null;
        _status.Text = p == null ? "No server selected." : p.Mode switch
        {
            AccessMode.CloudflareTunnel => running
                ? $"Running on 127.0.0.1:{p.LocalPort}. Public address {p.PublicUrl} works once cloudflared is running with this server's tunnel token (“Tunnel command” copies the install command). No inbound firewall rule is needed."
                : "Stopped.",
            AccessMode.OwnCertificate => running ? $"Running. HTTP {p.LocalPort} redirects to HTTPS {p.HttpsPort}. Open both ports in the firewall / router (“Firewall help”)." : "Stopped.",
            _ => running ? $"Running at {p.PublicUrl}. Other devices need an inbound firewall rule for port {p.LocalPort} (“Firewall help”)." : "Stopped.",
        };
    }

    // ---------- server actions ----------

    void NewServer()
    {
        var p = new ServerProfile { LocalPort = ProfileStore.NextFreePort(_profiles) };
        using var wiz = new SetupWizard(p, _profiles.ToList(), editing: false);
        if (wiz.ShowDialog(this) != DialogResult.OK) return;
        _profiles.Add(wiz.Result);
        ProfileStore.Save(_profiles);
        FillPicker();
        RefreshServerList();
    }

    void EditServer()
    {
        if (SelectedServer is not { } p) return;
        if (Host(p).Running) { MessageBox.Show(this, "Stop the server before editing it.", Text); return; }
        using var wiz = new SetupWizard(p.Clone(), _profiles.Where(o => o.Id != p.Id).ToList(), editing: true);
        if (wiz.ShowDialog(this) != DialogResult.OK) return;
        var index = _profiles.IndexOf(p);
        _profiles[index] = wiz.Result;
        _hosts.Remove(p.Id);
        _dbs.Remove(p.Id);
        ProfileStore.Save(_profiles);
        FillPicker();
        RefreshServerList();
    }

    async void RemoveServer()
    {
        if (SelectedServer is not { } p) return;
        var answer = MessageBox.Show(this,
            $"Remove “{p.DisplayName}” from this app?\n\nYes = remove it and also DELETE its data folder (inventory, users, backups).\nNo = remove it but keep the data folder on disk.",
            "Remove server", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer == DialogResult.Cancel) return;
        if (answer == DialogResult.Yes &&
            MessageBox.Show(this, $"This permanently deletes:\n{p.DataDir}\n\nContinue?", "Delete data", MessageBoxButtons.YesNo, MessageBoxIcon.Stop, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        await StopAsync(p);
        _profiles.Remove(p);
        _hosts.Remove(p.Id);
        _dbs.Remove(p.Id);
        ProfileStore.Save(_profiles);
        if (answer == DialogResult.Yes)
        {
            try
            {
                SqliteConnection.ClearAllPools();
                if (p.DataDir != ProfileStore.Root) Directory.Delete(p.DataDir, recursive: true);
                else foreach (var f in Directory.GetFiles(p.DataDir, "inventory.db*").Concat(Directory.GetFiles(p.DataDir, "logo.*"))) File.Delete(f);
            }
            catch (Exception e) { Error("Could not delete the data folder:\n" + e.Message); }
        }
        FillPicker();
        RefreshServerList();
    }

    /// <summary>Makes sure the website has at least one user, offering to create the admin account.</summary>
    bool EnsureAdmin(ServerProfile p)
    {
        var db = DbFor(p);
        if (db.HasUsers()) return true;
        MessageBox.Show(this, $"“{p.DisplayName}” has no website users yet. Create the admin account now.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        var name = "admin";
        while (true)
        {
            var v = FieldDialog.Ask(this, "Create admin account", $"Passwords need at least {Db.MinPasswordLength} characters.",
                new Field("Username", name), new Field("Password", Password: true), new Field("Repeat password", Password: true));
            if (v == null) return false;
            name = v[0];
            try
            {
                if (v[1] != v[2]) throw new ArgumentException("The passwords do not match.");
                db.CreateUser(v[0], v[1], Roles.Admin);
                return true;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { Error(e.Message); }
        }
    }

    async Task StartAsync(ServerProfile p)
    {
        _startStop.Enabled = false;
        try
        {
            if (!EnsureAdmin(p)) return;
            await Host(p).StartAsync();
        }
        catch (Exception e)
        {
            Error($"Could not start “{p.DisplayName}”:\n{e.Message}\n\nIs the port already in use?");
        }
        finally
        {
            _startStop.Enabled = true;
            RefreshServerList();
        }
    }

    async Task StopAsync(ServerProfile p)
    {
        if (_hosts.TryGetValue(p.Id, out var h) && h.Running)
        {
            try { await h.StopAsync(); }
            catch (Exception e) { Error(e.Message); }
        }
        RefreshServerList();
    }

    void ManageUsers()
    {
        if (SelectedServer is not { } p) return;
        using var dlg = new UsersDialog(DbFor(p), p.DisplayName);
        dlg.ShowDialog(this);
    }

    void OpenSelected()
    {
        if (SelectedServer is { } p) OpenUrl(p.PublicUrl);
    }

    void CopyUrl()
    {
        if (SelectedServer is { } p) Clipboard.SetText(p.PublicUrl);
    }

    void CopyTunnelCommand()
    {
        if (SelectedServer is not { } p) return;
        if (p.Mode != AccessMode.CloudflareTunnel) { MessageBox.Show(this, "This server does not use a Cloudflare Tunnel.", Text); return; }
        var token = p.TunnelToken;
        if (string.IsNullOrEmpty(token)) { MessageBox.Show(this, "No tunnel token saved. Edit the server and paste it.", Text); return; }
        Clipboard.SetText($"cloudflared.exe service install {token}");
        MessageBox.Show(this,
            "Copied. Run it once in an Administrator command prompt on the server PC (cloudflared must be installed from Cloudflare).\n\n" +
            "The clipboard now contains your secret tunnel token – paste it only where needed and clear it afterwards.",
            "Tunnel command", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void OpenSelectedFolder()
    {
        if (SelectedServer is { } p) OpenUrl(p.DataDir);
    }

    void FirewallHelp()
    {
        if (SelectedServer is not { } p) return;
        if (p.Mode == AccessMode.CloudflareTunnel)
        {
            MessageBox.Show(this,
                "Cloudflare Tunnel mode needs NO inbound firewall rule: the app listens on 127.0.0.1 only and cloudflared reaches it locally.\n\n" +
                "The PC must be allowed OUTBOUND connections to Cloudflare (TCP and UDP port 7844).",
                "Firewall help", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var ports = p.Mode == AccessMode.OwnCertificate ? $"{p.LocalPort},{p.HttpsPort}" : p.LocalPort.ToString();
        var cmd = $"netsh advfirewall firewall add rule name=\"Inventory Server {p.DisplayName}\" dir=in action=allow protocol=TCP localport={ports}";
        Clipboard.SetText(cmd);
        MessageBox.Show(this,
            $"Other devices can only connect if Windows Firewall allows inbound TCP port(s) {ports}.\n\n" +
            "Ask IT to add that rule, or run this in an Administrator command prompt (copied to your clipboard):\n\n" + cmd +
            "\n\nThis app does not change firewall settings itself.",
            "Firewall help", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void Backup()
    {
        if (SelectedServer is not { } p) return;
        var dir = Path.Combine(p.DataDir, "backups");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"inventory-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        try
        {
            DbFor(p).Backup(path);
            MessageBox.Show(this, $"Backup saved to:\n{path}", "Backup", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception e) { Error(e.Message); }
    }

    static void OpenUrl(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    // ---------- items ----------

    string? SelectedSku => _grid.CurrentRow?.Cells["sku"].Value as string;

    void LoadItems(bool keepSelection = false)
    {
        if (Current is not { } p) return;
        var sku = keepSelection ? SelectedSku : null;
        List<Item> items;
        try { items = DbFor(p).Search(_search.Text, _inStockOnly.Checked); }
        catch (Exception e) { Error(e.Message); return; }

        _grid.SuspendLayout();
        _grid.Rows.Clear();
        foreach (var it in items)
        {
            var i = _grid.Rows.Add(it.Sku, it.Name, it.Stock, it.Serialized ? it.InUse.ToString() : "", it.Unit, it.Location, it.Status);
            _grid.Rows[i].DefaultCellStyle.ForeColor = it.Stock <= 0 ? Color.Firebrick : it.Status == "Low" ? Color.DarkGoldenrod : SystemColors.ControlText;
            if (sku != null && it.Sku == sku) _grid.CurrentCell = _grid.Rows[i].Cells[0];
        }
        _grid.ResumeLayout();
        LoadHistory();
    }

    void LoadHistory()
    {
        _history.Rows.Clear();
        if (Current is not { } p || SelectedSku is not { } sku) return;
        foreach (var t in DbFor(p).History(sku, 100))
            _history.Rows.Add(t.Ts, t.Delta.ToString("+#;-#;0"), t.Kind, t.Serial, t.Location, t.User, t.Note);
    }

    void LoadInUse()
    {
        _inUseServer.Text = Current is { } cur ? $"Server: {cur.DisplayName}" : "No server selected.";
        var keep = _inUseGrid.CurrentRow is { } row && row.Index < _inUseUnits.Count ? _inUseUnits[row.Index].Id : (long?)null;
        _inUseGrid.Rows.Clear();
        _inUseUnits = new();
        if (Current is not { } p) return;
        try { _inUseUnits = DbFor(p).ListInUse(_inUseSearch.Text); }
        catch (Exception e) { Error(e.Message); return; }
        foreach (var u in _inUseUnits)
        {
            var i = _inUseGrid.Rows.Add(u.ItemName, u.Sku, u.Serial, u.Location, u.Since, u.User, u.Note);
            if (u.Id == keep) _inUseGrid.CurrentCell = _inUseGrid.Rows[i].Cells[0];
        }
    }

    SerialUnit? SelectedInUse => _inUseGrid.CurrentRow is { } r && r.Index < _inUseUnits.Count ? _inUseUnits[r.Index] : null;

    void ReturnInUse()
    {
        if (Current is not { } p || SelectedInUse is not { } u) { MessageBox.Show(this, "Select a unit first.", Text); return; }
        var v = FieldDialog.Ask(this, "Return to stock", $"{u.ItemName} – serial {u.Serial} (in use at {u.Location}) goes back into available stock.",
            new Field("Note (optional)"));
        if (v == null) return;
        try { DbFor(p).ReturnUnit(u.Id, v[0], Me); }
        catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException) { Error(e.Message); }
        LoadInUse();
    }

    void GoToInUseItem()
    {
        if (SelectedInUse is not { } u) return;
        _tabs.SelectedIndex = 0;
        _search.Text = "";
        LoadItems();
        SelectSku(u.Sku);
    }

    void ManageSerials()
    {
        if (RequireSelected() is not var (p, db, item)) return;
        if (!item.Serialized)
        {
            MessageBox.Show(this, $"“{item.Name}” is not tracked by serial number.\n\nTo turn it on, choose Edit and tick “Serialized”.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new SerialsDialog(db, item.Sku, Me);
        dlg.ShowDialog(this);
        LoadItems(keepSelection: true);
    }

    (ServerProfile, Db, Item)? RequireSelected()
    {
        if (Current is { } p && SelectedSku is { } sku && DbFor(p).Get(sku) is { } item) return (p, DbFor(p), item);
        MessageBox.Show(this, "Select an item first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return null;
    }

    void Error(string message) => MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    void AddItem()
    {
        if (Current is not { } p) { Error("Create a server first (Servers tab)."); return; }
        var db = DbFor(p);
        var item = new Item();
        var opening = "0";
        var serials = "";
        while (true)
        {
            var v = FieldDialog.Ask(this, "Add item",
                "Leave SKU blank to generate one automatically.\nFor a serialized item, list its serial numbers instead of a starting stock (or add them later).",
                new Field("SKU / code", item.Sku), new Field("Name", item.Name), new Field("Description", item.Description, Multiline: true),
                new Field("Keywords", item.Keywords), new Field("Location", item.Location), new Field("Unit", item.Unit),
                new Field("Low-stock level", item.ReorderLevel.ToString()),
                new Field("Serialized – track each unit by serial number", item.Serialized ? "1" : "", Checkbox: true),
                new Field("Starting stock", opening), new Field("Serial numbers (one per line)", serials, Multiline: true));
            if (v == null) return;
            item = new Item
            {
                Sku = v[0].Trim().ToUpperInvariant(), Name = v[1], Description = v[2], Keywords = v[3], Location = v[4], Unit = v[5],
                Serialized = v[7] == "1",
            };
            opening = v[8];
            serials = v[9];
            try
            {
                if (!int.TryParse(v[6], out var rl)) throw new ArgumentException("Low-stock level must be a whole number.");
                var st = 0;
                if (!item.Serialized && !int.TryParse(v[8], out st)) throw new ArgumentException("Starting stock must be a whole number.");
                if (!item.Serialized && Db.ParseSerials(serials).Count > 0)
                    throw new ArgumentException("You entered serial numbers – tick “Serialized” to track them, or clear that box.");
                item.ReorderLevel = rl;
                item.Stock = st;
                if (item.Sku.Length == 0) item.Sku = db.NextSku();
                db.AddItem(item, Me, item.Serialized ? Db.ParseSerials(serials) : null);
                LoadItems();
                SelectSku(item.Sku);
                return;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { Error(e.Message); }
        }
    }

    void EditItem()
    {
        if (RequireSelected() is not var (p, db, item)) return;
        while (true)
        {
            var v = FieldDialog.Ask(this, "Edit item",
                item.Serialized ? "Use “Serial numbers…” to add, use or return units." : "Use Add/Use stock or Set count to change the quantity.",
                new Field("SKU / code", item.Sku, ReadOnly: true), new Field("Name", item.Name), new Field("Description", item.Description, Multiline: true),
                new Field("Keywords", item.Keywords), new Field("Location", item.Location), new Field("Unit", item.Unit),
                new Field("Low-stock level", item.ReorderLevel.ToString()),
                new Field("Serialized – track each unit by serial number", item.Serialized ? "1" : "", Checkbox: true));
            if (v == null) return;
            item.Name = v[1]; item.Description = v[2]; item.Keywords = v[3]; item.Location = v[4]; item.Unit = v[5];
            var wantSerialized = v[7] == "1";
            try
            {
                if (!int.TryParse(v[6], out var rl) || rl < 0) throw new ArgumentException("Low-stock level must be a whole number.");
                item.ReorderLevel = rl;
                if (wantSerialized != item.Serialized && !ChangeSerialTracking(db, item, wantSerialized)) continue;
                db.UpdateItem(item);
                LoadItems(keepSelection: true);
                return;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { Error(e.Message); }
        }
    }

    /// <summary>Turns serial tracking on (asking for a serial per unit in stock) or off. Returns false if the user backed out.</summary>
    bool ChangeSerialTracking(Db db, Item item, bool on)
    {
        if (!on)
        {
            if (MessageBox.Show(this,
                    $"Stop tracking “{item.Name}” by serial number?\n\nThe {item.Stock} available unit(s) stay in stock as a plain count, but their serial numbers are discarded.",
                    "Serial numbers", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return false;
            db.SetSerialized(item.Sku, false, user: Me);
            item.Serialized = false;
            return true;
        }
        var serials = new List<string>();
        if (item.Stock > 0)
        {
            var v = FieldDialog.Ask(this, "Serial numbers for current stock",
                $"There are {item.Stock} {item.Unit} in stock. Enter one serial number for each (one per line).",
                new Field("Serial numbers", Multiline: true));
            if (v == null) return false;
            serials = Db.ParseSerials(v[0]);
        }
        db.SetSerialized(item.Sku, true, serials, Me);
        item.Serialized = true;
        return true;
    }

    void DeleteItem()
    {
        if (RequireSelected() is not var (p, db, item)) return;
        var answer = MessageBox.Show(this, $"Delete “{item.Name}” ({item.Sku}) and its activity history?\n\nIts printed QR label will stop working.",
            "Delete item", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;
        db.DeleteItem(item.Sku);
        LoadItems();
    }

    void ChangeStock(int sign)
    {
        if (RequireSelected() is not var (p, db, item)) return;
        if (item.Serialized) { ManageSerials(); return; }
        var using_ = sign < 0;
        while (true)
        {
            var v = FieldDialog.Ask(this, using_ ? "Use stock" : "Add stock", $"{item.Name} – currently {item.Stock} {item.Unit}",
                using_
                    ? new[] { new Field("Quantity", "1"), new Field("Where used", "") }
                    : new[] { new Field("Quantity", "1"), new Field("Note (optional)") });
            if (v == null) return;
            try
            {
                if (!int.TryParse(v[0], out var qty) || qty < 1) throw new ArgumentException("Quantity must be a whole number of at least 1.");
                if (using_ && string.IsNullOrWhiteSpace(v[1])) throw new ArgumentException("Please say where it was used.");
                db.Adjust(item.Sku, sign * qty, using_ ? "use" : "add", using_ ? v[1] : "", using_ ? "" : v[1], Me);
                LoadItems(keepSelection: true);
                return;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { Error(e.Message); }
        }
    }

    void SetCount()
    {
        if (RequireSelected() is not var (p, db, item)) return;
        if (item.Serialized) { ManageSerials(); return; }
        while (true)
        {
            var v = FieldDialog.Ask(this, "Set count", $"{item.Name} – currently {item.Stock} {item.Unit}. Use this after a physical recount.",
                new Field("Actual count", item.Stock.ToString()), new Field("Note (optional)"));
            if (v == null) return;
            try
            {
                if (!int.TryParse(v[0], out var n) || n < 0) throw new ArgumentException("Count must be a whole number, 0 or more.");
                db.SetCount(item.Sku, n, v[1], Me);
                LoadItems(keepSelection: true);
                return;
            }
            catch (ArgumentException e) { Error(e.Message); }
        }
    }

    void ShowQr()
    {
        if (RequireSelected() is var (p, _, item)) Labels.ShowQr(this, p, item);
    }

    void PrintLabels(bool selectedOnly)
    {
        if (selectedOnly)
        {
            if (RequireSelected() is var (p, _, item)) Labels.Print(this, p, new[] { item });
        }
        else if (Current is { } cur) Labels.Print(this, cur, DbFor(cur).Search(null));
    }

    void SelectSku(string sku)
    {
        foreach (DataGridViewRow row in _grid.Rows)
            if ((string?)row.Cells["sku"].Value == sku) { _grid.CurrentCell = row.Cells[0]; return; }
    }

    // ---------- window lifetime ----------

    bool AnyRunning => _hosts.Values.Any(h => h.Running);

    void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && AnyRunning) Hide();
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && AnyRunning && e.CloseReason == CloseReason.UserClosing)
        {
            var answer = MessageBox.Show(this, "One or more servers are running. Stop them and exit?\n\nChoose No to keep them running in the system tray.",
                Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) { e.Cancel = true; return; }
            if (answer == DialogResult.No) { e.Cancel = true; Hide(); return; }
        }
        _tray.Visible = false;
        foreach (var h in _hosts.Values.Where(h => h.Running).ToList())
            await h.StopAsync();
        base.OnFormClosing(e);
    }
}
