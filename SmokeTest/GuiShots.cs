using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using InventoryServer;
using InventoryServer.UI;

static class GuiShots
{
    /// <summary>Opens the wizard and saves a PNG of each page (layout check). Usage: SmokeTest --gui &lt;folder&gt;</summary>
    public static void Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var t = new Thread(() =>
        {
            ApplicationConfiguration.Initialize();
            var p = new ServerProfile { LocalPort = 8081, Domain = "info-inventory.com", CompanyName = "Info Inventory" };
            using var wiz = new SetupWizard(p, new List<ServerProfile>(), editing: false);
            wiz.Show();
            Application.DoEvents();
            var go = typeof(SetupWizard).GetMethod("Go", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var pages = (System.Collections.IList)typeof(SetupWizard).GetField("_pages", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(wiz)!;
            for (var i = 0; i < pages.Count; i++)
            {
                Application.DoEvents();
                using var bmp = new Bitmap(wiz.Width, wiz.Height);
                wiz.DrawToBitmap(bmp, new Rectangle(0, 0, wiz.Width, wiz.Height));
                bmp.Save(Path.Combine(outDir, $"wizard-{i + 1}.png"));
                if (i < pages.Count - 1) go.Invoke(wiz, new object[] { +1 });
            }
            wiz.Close();
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
    }
}
