namespace InventoryServer.UI;

/// <summary>
/// One input in a <see cref="FieldDialog"/>. A <paramref name="Checkbox"/> field returns "1" when ticked and "" otherwise
/// (set <paramref name="Value"/> to "1" to start ticked).
/// </summary>
public record Field(string Label, string Value = "", bool Multiline = false, bool ReadOnly = false, bool Password = false, bool Checkbox = false);

public static class FieldDialog
{
    /// <summary>Shows a simple form of labelled text boxes. Returns the entered values, or null if cancelled.</summary>
    public static string[]? Ask(IWin32Window? owner, string title, string? prompt, params Field[] fields)
    {
        using var form = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12),
        };
        var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        var inputs = new List<Control>();
        if (prompt != null)
        {
            var l = new Label { Text = prompt, AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(3, 3, 3, 10) };
            table.Controls.Add(l);
            table.SetColumnSpan(l, 2);
        }
        foreach (var f in fields)
        {
            if (f.Checkbox)
            {
                table.Controls.Add(new Label { AutoSize = true });
                var cb = new CheckBox { Text = f.Label, Checked = f.Value == "1", Enabled = !f.ReadOnly, AutoSize = true, Margin = new Padding(3, 3, 3, 6) };
                inputs.Add(cb);
                table.Controls.Add(cb);
                continue;
            }
            table.Controls.Add(new Label { Text = f.Label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(3, 7, 10, 3) });
            var tb = new TextBox
            {
                Text = f.Value, Width = 300, Multiline = f.Multiline, ReadOnly = f.ReadOnly, UseSystemPasswordChar = f.Password,
                Height = f.Multiline ? 60 : 23, Margin = new Padding(3, 3, 3, 6), ScrollBars = f.Multiline ? ScrollBars.Vertical : ScrollBars.None,
                AcceptsReturn = f.Multiline,
            };
            inputs.Add(tb);
            table.Controls.Add(tb);
        }
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 80 };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        table.Controls.Add(buttons);
        table.SetColumnSpan(buttons, 2);
        form.Controls.Add(table);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(owner) == DialogResult.OK
            ? inputs.Select(c => c is CheckBox cb ? (cb.Checked ? "1" : "") : c.Text).ToArray()
            : null;
    }
}
