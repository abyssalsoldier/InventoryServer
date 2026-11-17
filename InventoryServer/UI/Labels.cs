using System.Drawing.Printing;
using InventoryServer.Data;
using InventoryServer.Web;

namespace InventoryServer.UI;

public static class Labels
{
    public static Bitmap QrBitmap(ServerProfile p, string sku, int pixelsPerModule = 10)
    {
        using var ms = new MemoryStream(Qr.Png(Qr.UrlFor(p, sku), pixelsPerModule));
        using var img = Image.FromStream(ms);
        return new Bitmap(img);
    }

    static Bitmap? LoadLogo(ServerProfile p)
    {
        try
        {
            if (p.LogoFile.Length == 0 || !File.Exists(p.LogoPath)) return null;
            using var img = Image.FromFile(p.LogoPath);
            return new Bitmap(img);
        }
        catch { return null; }
    }

    /// <summary>Shows one item's QR code with Save and Print buttons.</summary>
    public static void ShowQr(IWin32Window owner, ServerProfile p, Item item)
    {
        using var bmp = QrBitmap(p, item.Sku, 12);
        using var form = new Form
        {
            Text = $"QR code – {item.Name}", StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            ClientSize = new Size(380, 480),
        };
        var pic = new PictureBox { Image = bmp, SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Top, Height = 340 };
        var info = new Label
        {
            Text = $"{item.Name}\n{item.Sku}\n{Qr.UrlFor(p, item.Sku)}",
            TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Top, Height = 60,
        };
        var save = new Button { Text = "Save PNG…", Left = 20, Top = 420, Width = 110 };
        var print = new Button { Text = "Print label…", Left = 140, Top = 420, Width = 110 };
        var close = new Button { Text = "Close", Left = 260, Top = 420, Width = 100, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) =>
        {
            using var dlg = new SaveFileDialog { Filter = "PNG image|*.png", FileName = $"{item.Sku}.png" };
            if (dlg.ShowDialog(form) == DialogResult.OK) bmp.Save(dlg.FileName, System.Drawing.Imaging.ImageFormat.Png);
        };
        print.Click += (_, _) => Print(form, p, new[] { item });
        form.Controls.AddRange(new Control[] { info, pic, save, print, close });
        form.CancelButton = close;
        form.ShowDialog(owner);
    }

    /// <summary>Opens a print preview (with a Print button) of labels laid out 2 across on the page.</summary>
    public static void Print(IWin32Window owner, ServerProfile p, IList<Item> items)
    {
        if (items.Count == 0) return;
        const int cols = 2, rows = 5;
        var index = 0;
        using var logo = LoadLogo(p);
        using var doc = new PrintDocument();
        doc.DocumentName = "Inventory labels";
        doc.PrintPage += (_, e) =>
        {
            var g = e.Graphics!;
            var area = e.MarginBounds;
            var cw = area.Width / cols;
            var ch = area.Height / rows;
            using var nameFont = new Font("Segoe UI", 11, FontStyle.Bold);
            using var skuFont = new Font("Segoe UI", 9);
            using var companyFont = new Font("Segoe UI", 8);
            for (var slot = 0; slot < cols * rows && index < items.Count; slot++, index++)
            {
                var it = items[index];
                var cell = new Rectangle(area.Left + slot % cols * cw, area.Top + slot / cols * ch, cw, ch);
                var qrSize = Math.Min(ch - 16, cw / 2);
                using (var bmp = QrBitmap(p, it.Sku, 8))
                    g.DrawImage(bmp, cell.Left + 8, cell.Top + 8, qrSize, qrSize);
                var text = new Rectangle(cell.Left + qrSize + 16, cell.Top + 8, cw - qrSize - 24, qrSize);
                g.DrawString(it.Name, nameFont, Brushes.Black, text);
                g.DrawString(it.Sku, skuFont, Brushes.Black, text.Left, text.Bottom - skuFont.Height);
                var companyY = text.Bottom - skuFont.Height - companyFont.Height - 2;
                var companyX = text.Left;
                if (logo != null)
                {
                    var h = companyFont.Height + 4;
                    var w = logo.Width * h / logo.Height;
                    g.DrawImage(logo, companyX, companyY - 2, w, h);
                    companyX += w + 4;
                }
                if (p.CompanyName.Length > 0) g.DrawString(p.CompanyName, companyFont, Brushes.DimGray, companyX, companyY);
                using var pen = new Pen(Color.LightGray) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
                g.DrawRectangle(pen, cell);
            }
            e.HasMorePages = index < items.Count;
        };
        using var preview = new PrintPreviewDialog { Document = doc, WindowState = FormWindowState.Maximized };
        preview.ShowDialog(owner);
    }
}
