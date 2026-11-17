using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using InventoryServer.Data;

namespace InventoryServer.UI;

/// <summary>Step-by-step setup for one server (company, data folder, access mode, domain, admin account, test).</summary>
public class SetupWizard : Form
{
    sealed class Page
    {
        public string Title = "";
        public Control Content = null!;
        public Func<string?>? Validate;
        public Action? OnEnter;
        public Func<bool> Visible = () => true;
    }

    static readonly Regex DomainPattern = new(@"^(?=.{4,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$", RegexOptions.Compiled);

    readonly ServerProfile _p;
    readonly List<ServerProfile> _others;
    readonly bool _editing;
    readonly List<Page> _pages = new();
    int _index = -1;

    readonly Label _title = new() { Dock = DockStyle.Top, Height = 40, Font = new Font("Segoe UI", 14, FontStyle.Bold), Padding = new Padding(12, 8, 0, 0) };
    readonly Panel _body = new() { Dock = DockStyle.Fill, Padding = new Padding(16, 8, 16, 8), AutoScroll = true };
    readonly Button _back = new() { Text = "< Back", Width = 90 };
    readonly Button _next = new() { Text = "Next >", Width = 90 };
    readonly Button _cancel = new() { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };

    // page state
    readonly TextBox _company = new() { Width = 320 };
    readonly TextBox _logoPath = new() { Width = 260, ReadOnly = true };
    readonly PictureBox _logoPreview = new() { Width = 120, Height = 60, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle };
    string _logoSource = "";
    readonly TextBox _dataDir = new() { Width = 360 };
    readonly Label _dataInfo = new() { AutoSize = true, ForeColor = Color.DimGray };
    bool _dataDirEdited;
    readonly RadioButton _modeTunnel = new() { Text = "Cloudflare Tunnel – public domain, HTTPS by Cloudflare, no open ports (recommended for internet)", AutoSize = true };
    readonly RadioButton _modeInternal = new() { Text = "Internal network only – phones on the company Wi-Fi / VPN, plain HTTP", AutoSize = true };
    readonly RadioButton _modeCert = new() { Text = "Own certificate (.pfx) – public domain served directly over HTTPS, inbound ports must be opened", AutoSize = true };
    readonly Panel _tunnelPanel = new() { Dock = DockStyle.Fill };
    readonly Panel _internalPanel = new() { Dock = DockStyle.Fill };
    readonly Panel _certPanel = new() { Dock = DockStyle.Fill };
    // tunnel
    readonly TextBox _tDomain = new() { Width = 300 };
    readonly TextBox _tToken = new() { Width = 420, UseSystemPasswordChar = true };
    readonly NumericUpDown _tPort = new() { Minimum = 1024, Maximum = 65535, Width = 80 };
    readonly TextBox _tCommand = new() { Width = 520, ReadOnly = true };
    // internal
    readonly ComboBox _iAddress = new() { Width = 260 };
    readonly NumericUpDown _iPort = new() { Minimum = 1, Maximum = 65535, Width = 80 };
    // certificate
    readonly TextBox _cDomain = new() { Width = 300 };
    readonly TextBox _cPath = new() { Width = 300 };
    readonly TextBox _cPassword = new() { Width = 200, UseSystemPasswordChar = true };
    readonly NumericUpDown _cHttp = new() { Minimum = 1, Maximum = 65535, Width = 80 };
    readonly NumericUpDown _cHttps = new() { Minimum = 1, Maximum = 65535, Width = 80 };
    // admin
    readonly TextBox _adminUser = new() { Width = 220, Text = "admin" };
    readonly TextBox _adminPass = new() { Width = 220, UseSystemPasswordChar = true };
    readonly TextBox _adminPass2 = new() { Width = 220, UseSystemPasswordChar = true };
    bool _hasUsers;
    // review
    readonly TextBox _summary = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Top, Height = 150, ScrollBars = ScrollBars.Vertical };
    readonly Button _test = new() { Text = "Start server and test", Width = 170, Height = 30 };
    readonly TextBox _testLog = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    bool _testing;

    public ServerProfile Result => _p;

    /// <param name="profile">A working copy to fill in (new profile, or a clone when editing).</param>
    /// <param name="others">The other existing profiles, used to avoid port and folder clashes.</param>
    public SetupWizard(ServerProfile profile, List<ServerProfile> others, bool editing)
    {
        _p = profile;
        _others = others;
        _editing = editing;
        Text = editing ? "Edit server" : "New server";
        ClientSize = new Size(760, 560);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_next);
        buttons.Controls.Add(_back);
        Controls.Add(_body);
        Controls.Add(_title);
        Controls.Add(buttons);
        CancelButton = _cancel;

        _back.Click += (_, _) => Go(-1);
        _next.Click += async (_, _) => await NextAsync();

        BuildPages();
        LoadFromProfile();
        Go(+1);
    }

    // ---------------- page construction ----------------

    static Label Info(string text, int width = 680, Color? color = null) =>
        new() { Text = text, AutoSize = true, MaximumSize = new Size(width, 0), ForeColor = color ?? SystemColors.ControlText, Margin = new Padding(3, 3, 3, 10) };

    static FlowLayoutPanel Stack() => new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };

    static Control LabelAbove(string text, Control c)
    {
        var f = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
        f.Controls.Add(new Label { Text = text, AutoSize = true, Margin = new Padding(3, 3, 3, 0) });
        f.Controls.Add(c);
        return f;
    }

    static Control Row(params Control[] cs)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
        f.Controls.AddRange(cs);
        return f;
    }

    static Button Btn(string text, EventHandler click, int w = 90) { var b = new Button { Text = text, Width = w, Height = 26 }; b.Click += click; return b; }

    void BuildPages()
    {
        // 1. Company
        var company = Stack();
        company.Controls.Add(Info("Who is this server for? The name and logo appear on the website, the login page and printed labels."));
        company.Controls.Add(LabelAbove("Company name", _company));
        company.Controls.Add(LabelAbove("Logo (optional, PNG or JPG)", Row(_logoPath, Btn("Browse…", (_, _) => BrowseLogo()), Btn("Clear", (_, _) => SetLogo("")))));
        company.Controls.Add(_logoPreview);
        _pages.Add(new Page
        {
            Title = "Company", Content = company,
            Validate = () => _company.Text.Trim().Length == 0 ? "Enter the company name." : null,
        });

        // 2. Data folder
        var data = Stack();
        data.Controls.Add(Info("Where should this company's database, settings and backups be kept? Each company needs its own folder."));
        data.Controls.Add(LabelAbove("Data folder", Row(_dataDir, Btn("Browse…", (_, _) => BrowseFolder()))));
        data.Controls.Add(_dataInfo);
        _dataDir.TextChanged += (_, _) => { if (_dataDir.Focused) _dataDirEdited = true; UpdateDataInfo(); };
        _pages.Add(new Page
        {
            Title = "Data folder", Content = data,
            OnEnter = () =>
            {
                if (!_dataDirEdited && (!_editing || _p.DataDir.Length == 0)) _dataDir.Text = ProfileStore.DefaultDataDir(_company.Text, _p.Id);
                UpdateDataInfo();
            },
            Validate = ValidateDataDir,
        });

        // 3. Access mode
        var mode = Stack();
        mode.Controls.Add(Info("How will staff reach this server?"));
        foreach (var r in new[] { _modeTunnel, _modeInternal, _modeCert }) { r.Margin = new Padding(3, 3, 3, 10); mode.Controls.Add(r); }
        mode.Controls.Add(Info(
            "Cloudflare Tunnel needs your domain on Cloudflare plus the free cloudflared connector running on this PC; the next page walks you through it. " +
            "Nothing needs to be opened in the firewall for inbound traffic.", color: Color.DimGray));
        _pages.Add(new Page { Title = "Access", Content = mode });

        // 4. Domain details (one panel per mode)
        BuildTunnelPanel();
        BuildInternalPanel();
        BuildCertPanel();
        var details = new Panel { Dock = DockStyle.Fill };
        details.Controls.AddRange(new Control[] { _tunnelPanel, _internalPanel, _certPanel });
        _pages.Add(new Page
        {
            Title = "Domain", Content = details,
            OnEnter = () =>
            {
                _tunnelPanel.Visible = _modeTunnel.Checked;
                _internalPanel.Visible = _modeInternal.Checked;
                _certPanel.Visible = _modeCert.Checked;
                _title.Text = _modeTunnel.Checked ? "Cloudflare Tunnel" : _modeInternal.Checked ? "Internal network address" : "Domain and certificate";
                UpdateTunnelCommand();
            },
            Validate = ValidateDetails,
        });

        // 5. Admin
        var admin = Stack();
        admin.Controls.Add(Info($"Create the first admin account for the website. Passwords need at least {Db.MinPasswordLength} characters. Staff accounts can be added later with “Users…”."));
        admin.Controls.Add(LabelAbove("Admin username", _adminUser));
        admin.Controls.Add(LabelAbove("Password", _adminPass));
        admin.Controls.Add(LabelAbove("Repeat password", _adminPass2));
        _pages.Add(new Page
        {
            Title = "Admin account", Content = admin,
            Visible = () => !_hasUsers,
            Validate = () =>
            {
                if (!Db.IsValidUsername(_adminUser.Text.Trim().ToLowerInvariant())) return "Username must be 2-40 characters: letters, numbers, '.', '_', '-' or '@'.";
                if (_adminPass.Text.Length < Db.MinPasswordLength) return $"Password must be at least {Db.MinPasswordLength} characters.";
                if (_adminPass.Text.Distinct().Count() < 4) return "Password is too repetitive.";
                if (_adminPass.Text.Equals(_adminUser.Text, StringComparison.OrdinalIgnoreCase)) return "Password cannot be the username.";
                return _adminPass.Text != _adminPass2.Text ? "The passwords do not match." : null;
            },
        });

        // 6. Review & test
        var review = new Panel { Dock = DockStyle.Fill };
        review.Controls.Add(_testLog);
        review.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 40, Controls = { _test } });
        review.Controls.Add(_summary);
        _summary.Font = new Font("Consolas", 9);
        _testLog.Font = new Font("Consolas", 9);
        _test.Click += async (_, _) => await RunTestAsync();
        _pages.Add(new Page { Title = "Review and test", Content = review, OnEnter = ShowSummary });
    }

    void BuildTunnelPanel()
    {
        var s = Stack();
        s.Controls.Add(Info(
            "1. In the Cloudflare dashboard open Zero Trust > Networks > Tunnels and create a tunnel (type: Cloudflared).\n" +
            "2. Copy the tunnel token it shows (the long text after “--token” / “service install”) and paste it below.\n" +
            "3. Under Public Hostname add your domain with Service type HTTP and URL  localhost:<port below>.\n" +
            "4. Run the command at the bottom once on the server PC (as administrator). The app itself never installs or starts cloudflared."));
        s.Controls.Add(Row(LabelAbove("Public domain (as added in Cloudflare)", _tDomain), LabelAbove("Local port", _tPort)));
        s.Controls.Add(LabelAbove("Tunnel token", _tToken));
        s.Controls.Add(LabelAbove("Install command for the server PC", Row(_tCommand, Btn("Copy", (_, _) => CopyCommand()))));
        s.Controls.Add(Info(
            "Firewall: no inbound rule is needed. The PC must be allowed outbound connections to Cloudflare (TCP/UDP port 7844). " +
            "The app listens on 127.0.0.1 only.", color: Color.DimGray));
        _tunnelPanel.Controls.Add(s);
        _tDomain.TextChanged += (_, _) => UpdateTunnelCommand();
        _tToken.TextChanged += (_, _) => UpdateTunnelCommand();
    }

    void BuildInternalPanel()
    {
        var s = Stack();
        s.Controls.Add(Info(
            "Use this PC's IP address, or a name your network's DNS resolves to it (for example inventory.yourcompany.local). " +
            "This address goes into every QR code, so labels must be reprinted if it changes."));
        s.Controls.Add(Row(LabelAbove("Address", _iAddress), LabelAbove("Port", _iPort)));
        s.Controls.Add(Info(
            "Firewall: Windows Firewall must allow inbound TCP on this port. After finishing, use “Firewall help” on the Server tab for the exact rule to give IT.",
            color: Color.DimGray));
        _iAddress.Items.AddRange(NetUtil.LocalAddresses().Cast<object>().ToArray());
        _internalPanel.Controls.Add(s);
    }

    void BuildCertPanel()
    {
        var s = Stack();
        s.Controls.Add(Info(
            "Serve your domain directly over HTTPS using a certificate file (.pfx) you obtained for it. " +
            "Point the domain's DNS at this server's public IP and forward the HTTP and HTTPS ports to this PC."));
        s.Controls.Add(LabelAbove("Public domain", _cDomain));
        s.Controls.Add(LabelAbove("Certificate file (.pfx)", Row(_cPath, Btn("Browse…", (_, _) => BrowseCert()))));
        s.Controls.Add(LabelAbove("Certificate password", _cPassword));
        s.Controls.Add(Row(LabelAbove("HTTP port (redirects to HTTPS)", _cHttp), LabelAbove("HTTPS port", _cHttps)));
        s.Controls.Add(Info("Firewall: inbound TCP on both ports must be allowed and forwarded. Internet-facing servers need approval from your IT/security team.", color: Color.DimGray));
        _certPanel.Controls.Add(s);
    }

    // ---------------- profile <-> controls ----------------

    void LoadFromProfile()
    {
        _company.Text = _p.CompanyName;
        if (_p.LogoFile.Length > 0 && File.Exists(_p.LogoPath)) { _logoPath.Text = _p.LogoPath; ShowLogo(_p.LogoPath); }
        _dataDir.Text = _p.DataDir;
        _dataDirEdited = _editing;
        _dataDir.ReadOnly = _editing;
        _modeTunnel.Checked = _p.Mode == AccessMode.CloudflareTunnel;
        _modeInternal.Checked = _p.Mode == AccessMode.Internal;
        _modeCert.Checked = _p.Mode == AccessMode.OwnCertificate;
        _tPort.Value = Math.Clamp(_p.LocalPort, 1024, 65535);
        _iPort.Value = Math.Clamp(_p.LocalPort, 1, 65535);
        _cHttp.Value = Math.Clamp(_p.Mode == AccessMode.OwnCertificate ? _p.LocalPort : 80, 1, 65535);
        _cHttps.Value = Math.Clamp(_p.HttpsPort, 1, 65535);
        if (_p.Domain.Length > 0)
        {
            _tDomain.Text = _cDomain.Text = _iAddress.Text = _p.Domain;
        }
        else if (_iAddress.Items.Count > 0) _iAddress.SelectedIndex = 0;
        _tToken.Text = _p.TunnelToken ?? "";
        _cPath.Text = _p.CertPath;
        _cPassword.Text = _p.CertPassword ?? "";
        if (_editing && _p.DataDir.Length > 0 && File.Exists(_p.DbPath))
        {
            try { var db = new Db(_p); db.Init(); _hasUsers = db.HasUsers(); } catch { }
        }
    }

    /// <summary>Copies the entered values into the profile (does not touch disk).</summary>
    void ApplyToProfile()
    {
        _p.CompanyName = _company.Text.Trim();
        _p.DataDir = _dataDir.Text.Trim();
        if (_modeTunnel.Checked)
        {
            _p.Mode = AccessMode.CloudflareTunnel;
            _p.Domain = _tDomain.Text.Trim().ToLowerInvariant();
            _p.LocalPort = (int)_tPort.Value;
            _p.TunnelTokenProtected = _tToken.Text.Trim().Length > 0 ? Secret.Protect(_tToken.Text.Trim()) : "";
        }
        else if (_modeInternal.Checked)
        {
            _p.Mode = AccessMode.Internal;
            _p.Domain = _iAddress.Text.Trim();
            _p.LocalPort = (int)_iPort.Value;
        }
        else
        {
            _p.Mode = AccessMode.OwnCertificate;
            _p.Domain = _cDomain.Text.Trim().ToLowerInvariant();
            _p.LocalPort = (int)_cHttp.Value;
            _p.HttpsPort = (int)_cHttps.Value;
            _p.CertPath = _cPath.Text.Trim();
            _p.CertPasswordProtected = _cPassword.Text.Length > 0 ? Secret.Protect(_cPassword.Text) : "";
        }
    }

    // ---------------- navigation ----------------

    void Go(int step)
    {
        var i = _index;
        do { i += step; } while (i >= 0 && i < _pages.Count && !_pages[i].Visible());
        if (i < 0 || i >= _pages.Count) return;
        _index = i;
        var page = _pages[i];
        _title.Text = page.Title;
        _body.Controls.Clear();
        _body.Controls.Add(page.Content);
        page.Content.Dock = DockStyle.Fill;
        page.OnEnter?.Invoke();
        _back.Enabled = _pages.Take(i).Any(pg => pg.Visible());
        var last = !_pages.Skip(i + 1).Any(pg => pg.Visible());
        _next.Text = last ? "Finish" : "Next >";
        AcceptButton = _next;
    }

    async Task NextAsync()
    {
        if (_testing) return;
        var page = _pages[_index];
        if (page.Validate?.Invoke() is { } error)
        {
            MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_pages.Skip(_index + 1).Any(pg => pg.Visible())) { Go(+1); return; }
        await FinishAsync();
    }

    async Task FinishAsync()
    {
        try
        {
            ApplyToProfile();
            Directory.CreateDirectory(_p.DataDir);
            if (_logoSource.Length > 0)
            {
                foreach (var old in Directory.GetFiles(_p.DataDir, "logo.*")) File.Delete(old);
                _p.LogoFile = "logo" + Path.GetExtension(_logoSource).ToLowerInvariant();
                File.Copy(_logoSource, Path.Combine(_p.DataDir, _p.LogoFile), overwrite: true);
            }
            else if (_logoPath.Text.Length == 0) _p.LogoFile = "";

            var db = new Db(_p);
            db.Init();
            if (!db.HasUsers() && !_hasUsers)
                db.CreateUser(_adminUser.Text, _adminPass.Text, Roles.Admin);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception e)
        {
            MessageBox.Show(this, "Could not save the server:\n" + e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        await Task.CompletedTask;
    }

    // ---------------- validation ----------------

    string? ValidateDataDir()
    {
        var dir = _dataDir.Text.Trim();
        if (dir.Length == 0) return "Choose a data folder.";
        if (!_editing) _hasUsers = false;
        string full;
        try { full = Path.GetFullPath(dir); } catch { return "That is not a valid folder path."; }
        if (_others.Any(o => string.Equals(Path.GetFullPath(o.DataDir).TrimEnd('\\'), full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            return "Another server already uses that folder. Each company needs its own.";
        try
        {
            Directory.CreateDirectory(full);
            var probe = Path.Combine(full, ".write-test");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
        }
        catch (Exception e) { return "Cannot write to that folder: " + e.Message; }
        if (File.Exists(Path.Combine(full, "inventory.db")) && !_editing)
        {
            try { var db = new Db(new ServerProfile { DataDir = full }); db.Init(); _hasUsers = db.HasUsers(); } catch { }
        }
        return null;
    }

    string? ValidateDetails()
    {
        string? PortClash(int port, string what) =>
            _others.Any(o => o.LocalPort == port || (o.Mode == AccessMode.OwnCertificate && o.HttpsPort == port))
                ? $"Another server already uses {what} {port}." : null;

        if (_modeTunnel.Checked)
        {
            var d = _tDomain.Text.Trim().ToLowerInvariant();
            if (!DomainPattern.IsMatch(d)) return "Enter the public domain exactly as it appears in Cloudflare, for example info-inventory.com.";
            var token = _tToken.Text.Trim();
            if (token.Length > 0 && (token.Length < 40 || token.Contains(' '))) return "That does not look like a tunnel token. Paste only the long token text, or leave it empty for now.";
            return PortClash((int)_tPort.Value, "port");
        }
        if (_modeInternal.Checked)
        {
            var a = _iAddress.Text.Trim();
            if (a.Length == 0 || a.Contains(' ') || a.Contains('/') || a.Contains(':')) return "Enter an IP address or host name without http:// or a port.";
            return PortClash((int)_iPort.Value, "port");
        }
        var domain = _cDomain.Text.Trim().ToLowerInvariant();
        if (!DomainPattern.IsMatch(domain)) return "Enter the public domain, for example info-inventory.com.";
        if (_cHttp.Value == _cHttps.Value) return "The HTTP and HTTPS ports must differ.";
        return PortClash((int)_cHttp.Value, "port") ?? PortClash((int)_cHttps.Value, "port") ?? CheckCertificate(domain);
    }

    string? CheckCertificate(string domain)
    {
        var path = _cPath.Text.Trim();
        if (!File.Exists(path)) return "Choose the certificate (.pfx) file.";
        try
        {
            using var cert = new X509Certificate2(path, _cPassword.Text);
            if (!cert.HasPrivateKey) return "That certificate file has no private key.";
            if (cert.NotAfter < DateTime.Now) return $"That certificate expired on {cert.NotAfter:d}.";
            var names = CertNames(cert);
            if (!names.Any(n => NameMatches(n, domain)))
                return $"The certificate is not valid for {domain}. It covers: {string.Join(", ", names)}.";
        }
        catch (Exception e) { return "Could not open the certificate (wrong password?): " + e.Message; }
        return null;
    }

    static List<string> CertNames(X509Certificate2 cert)
    {
        var names = new List<string>();
        var san = cert.Extensions.OfType<X509Extension>().FirstOrDefault(e => e.Oid?.Value == "2.5.29.17");
        if (san != null)
            foreach (Match m in Regex.Matches(san.Format(true), @"(?:DNS Name|DNS)[=:]\s*([^\s,]+)", RegexOptions.IgnoreCase))
                names.Add(m.Groups[1].Value.ToLowerInvariant());
        var cn = cert.GetNameInfo(X509NameType.DnsName, false);
        if (!string.IsNullOrEmpty(cn)) names.Add(cn.ToLowerInvariant());
        return names.Distinct().ToList();
    }

    static bool NameMatches(string certName, string domain)
    {
        if (certName == domain) return true;
        if (!certName.StartsWith("*.")) return false;
        var dot = domain.IndexOf('.');
        return dot > 0 && domain[(dot + 1)..] == certName[2..];
    }

    // ---------------- helpers ----------------

    void UpdateDataInfo()
    {
        var dir = _dataDir.Text.Trim();
        _dataInfo.Text = dir.Length > 0 && File.Exists(Path.Combine(dir, "inventory.db"))
            ? "An existing inventory database was found in this folder and will be used."
            : "A new, empty inventory will be created here.";
    }

    void UpdateTunnelCommand()
    {
        var token = _tToken.Text.Trim();
        _tCommand.Text = $"cloudflared.exe service install {(token.Length > 0 ? token : "<paste-token-here>")}";
    }

    void CopyCommand()
    {
        if (_tToken.Text.Trim().Length == 0)
        {
            MessageBox.Show(this, "Paste the tunnel token first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Clipboard.SetText(_tCommand.Text);
    }

    void BrowseLogo()
    {
        using var dlg = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg" };
        if (dlg.ShowDialog(this) == DialogResult.OK) SetLogo(dlg.FileName);
    }

    void SetLogo(string path)
    {
        _logoSource = path;
        _logoPath.Text = path;
        if (path.Length == 0) { _logoPreview.Image = null; return; }
        ShowLogo(path);
    }

    void ShowLogo(string path)
    {
        try { using var img = Image.FromFile(path); _logoPreview.Image = new Bitmap(img); }
        catch { _logoPreview.Image = null; }
    }

    void BrowseFolder()
    {
        if (_editing)
        {
            MessageBox.Show(this, "The data folder of an existing server cannot be changed here. Copy the folder and create a new server instead.", Text);
            return;
        }
        using var dlg = new FolderBrowserDialog { SelectedPath = _dataDir.Text };
        if (dlg.ShowDialog(this) == DialogResult.OK) { _dataDirEdited = true; _dataDir.Text = dlg.SelectedPath; }
    }

    void BrowseCert()
    {
        using var dlg = new OpenFileDialog { Filter = "Certificate|*.pfx;*.p12" };
        if (dlg.ShowDialog(this) == DialogResult.OK) _cPath.Text = dlg.FileName;
    }

    void ShowSummary()
    {
        ApplyToProfile();
        _summary.Text =
            $"Company:      {_p.CompanyName}\r\n" +
            $"Data folder:  {_p.DataDir}\r\n" +
            $"Access:       {_p.Mode switch { AccessMode.CloudflareTunnel => "Cloudflare Tunnel", AccessMode.OwnCertificate => "Own certificate", _ => "Internal network" }}\r\n" +
            $"QR codes use: {_p.PublicUrl}\r\n" +
            $"Listens on:   {_p.BindAddress()}:{_p.LocalPort}{(_p.Mode == AccessMode.OwnCertificate ? $" and HTTPS {_p.HttpsPort}" : "")}\r\n" +
            (_hasUsers ? "Admin:        existing users are kept\r\n" : $"Admin:        {_adminUser.Text.Trim().ToLowerInvariant()}\r\n") +
            "\r\nOptional: press “Start server and test” to check everything works, then Finish.";
    }

    // ---------------- test ----------------

    async Task RunTestAsync()
    {
        _testing = true;
        _test.Enabled = _next.Enabled = _back.Enabled = false;
        _testLog.Clear();
        void Log(string line) => _testLog.AppendText(line + "\r\n");
        ServerHost? host = null;
        try
        {
            ApplyToProfile();
            Directory.CreateDirectory(_p.DataDir);
            var db = new Db(_p);
            db.Init();
            Log($"Starting the server on {_p.BindAddress()}:{_p.LocalPort} …");
            host = new ServerHost(_p, db);
            await host.StartAsync();
            Log("  OK – the server started.");

            using var handler = new HttpClientHandler { AllowAutoRedirect = false, ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

            var localUrl = _p.Mode == AccessMode.OwnCertificate ? $"https://127.0.0.1:{_p.HttpsPort}/login" : $"http://127.0.0.1:{_p.LocalPort}/login";
            if (_p.Mode == AccessMode.Internal && !_p.BindAddress().Equals(System.Net.IPAddress.Any) && !System.Net.IPAddress.IsLoopback(_p.BindAddress()))
                localUrl = $"http://{_p.BindAddress()}:{_p.LocalPort}/login";
            Log($"Requesting {localUrl} …");
            var res = await http.GetAsync(localUrl);
            var text = await res.Content.ReadAsStringAsync();
            Log(res.IsSuccessStatusCode && text.Contains("Sign in") ? "  OK – the sign-in page responds." : $"  Problem – unexpected answer ({(int)res.StatusCode}).");

            if (_p.Mode != AccessMode.Internal)
            {
                var publicUrl = $"{_p.PublicUrl}/login";
                Log($"Requesting {publicUrl} from this PC …");
                try
                {
                    res = await http.GetAsync(publicUrl);
                    text = await res.Content.ReadAsStringAsync();
                    if (res.IsSuccessStatusCode && text.Contains("Sign in")) Log("  OK – your public address reaches this server.");
                    else Log("  Not working yet: " + ExplainPublicFailure((int)res.StatusCode, text));
                }
                catch (HttpRequestException e)
                {
                    Log("  Not working yet: " + (e.InnerException is System.Net.Sockets.SocketException
                        ? "the domain does not resolve or cannot be reached. Check the DNS record" + (_p.Mode == AccessMode.CloudflareTunnel ? " (the hostname must be added to the tunnel in Cloudflare)." : " and port forwarding.")
                        : e.Message));
                }
                catch (TaskCanceledException) { Log("  Not working yet: the request timed out."); }
                if (_p.Mode == AccessMode.CloudflareTunnel)
                    Log("  (This is expected until cloudflared is running with your token. You can still Finish and test later.)");
            }
            Log("Test finished.");
        }
        catch (Exception e)
        {
            Log("Problem: " + e.Message);
            if (e is System.Net.Sockets.SocketException or IOException or InvalidOperationException) Log("Is the port already used by another program?");
        }
        finally
        {
            if (host != null) await host.StopAsync();
            _testing = false;
            _test.Enabled = _next.Enabled = _back.Enabled = true;
        }
    }

    static string ExplainPublicFailure(int status, string body) => status switch
    {
        530 or 1033 => "Cloudflare reports the tunnel is not connected. Install and start cloudflared with the command from the previous page.",
        502 or 523 or 521 => "Cloudflare could reach the tunnel but not this server. Check that the tunnel's service URL is localhost:" + "<the local port>" + " and the server is running.",
        404 => "the address answered but this server did not (is the public hostname mapped to this tunnel?).",
        _ => $"unexpected answer ({status}).",
    };

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_testing) e.Cancel = true;
        base.OnFormClosing(e);
    }
}
