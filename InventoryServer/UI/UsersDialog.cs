using InventoryServer.Data;

namespace InventoryServer.UI;

/// <summary>Add, remove and reset website users for one server.</summary>
public class UsersDialog : Form
{
    readonly Db _db;
    readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = SystemColors.Window,
    };
    List<User> _users = new();

    public UsersDialog(Db db, string serverName)
    {
        _db = db;
        Text = $"Website users – {serverName}";
        ClientSize = new Size(560, 360);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;

        _grid.Columns.Add("user", "Username");
        _grid.Columns.Add("role", "Role");
        _grid.Columns.Add("created", "Created");

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6) };
        Button B(string text, EventHandler click, int w = 110) { var b = new Button { Text = text, Width = w, Height = 28 }; b.Click += click; return b; }
        buttons.Controls.AddRange(new Control[]
        {
            B("Add user", (_, _) => AddUser()), B("Reset password", (_, _) => ResetPassword(), 120),
            B("Make admin / staff", (_, _) => ToggleRole(), 130), B("Delete", (_, _) => Delete(), 80),
            B("Close", (_, _) => Close(), 80),
        });
        Controls.Add(_grid);
        Controls.Add(buttons);
        Reload();
    }

    User? Selected => _grid.CurrentRow is { } r && r.Index < _users.Count ? _users[r.Index] : null;

    void Reload()
    {
        _users = _db.ListUsers();
        _grid.Rows.Clear();
        foreach (var u in _users) _grid.Rows.Add(u.Username, u.Role == Roles.Admin ? "Admin" : "Staff", u.CreatedAt);
    }

    void Fail(Exception e) => MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    /// <summary>Asks for a new password twice; returns null if cancelled.</summary>
    public static string? AskPassword(IWin32Window owner, string title, string prompt)
    {
        while (true)
        {
            var v = FieldDialog.Ask(owner, title, prompt, new Field("Password", Password: true), new Field("Repeat password", Password: true));
            if (v == null) return null;
            if (v[0] != v[1]) { MessageBox.Show(owner, "The passwords do not match.", title, MessageBoxButtons.OK, MessageBoxIcon.Warning); continue; }
            return v[0];
        }
    }

    void AddUser()
    {
        var v = FieldDialog.Ask(this, "Add user", $"Passwords need at least {Db.MinPasswordLength} characters.",
            new Field("Username"), new Field("Role (admin or staff)", Roles.Staff), new Field("Password", Password: true), new Field("Repeat password", Password: true));
        if (v == null) return;
        try
        {
            if (v[2] != v[3]) throw new ArgumentException("The passwords do not match.");
            _db.CreateUser(v[0], v[2], v[1].Trim().ToLowerInvariant());
            Reload();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { Fail(e); }
    }

    void ResetPassword()
    {
        if (Selected is not { } u) return;
        if (AskPassword(this, "Reset password", $"New password for {u.Username}. They will be signed out.") is not { } pw) return;
        try { _db.SetPassword(u.Id, pw); }
        catch (ArgumentException e) { Fail(e); }
    }

    void ToggleRole()
    {
        if (Selected is not { } u) return;
        try { _db.SetRole(u.Id, u.Role == Roles.Admin ? Roles.Staff : Roles.Admin); Reload(); }
        catch (InvalidOperationException e) { Fail(e); }
    }

    void Delete()
    {
        if (Selected is not { } u) return;
        if (MessageBox.Show(this, $"Delete user “{u.Username}”?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { _db.DeleteUser(u.Id); Reload(); }
        catch (InvalidOperationException e) { Fail(e); }
    }
}
