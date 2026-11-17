using InventoryServer.UI;

namespace InventoryServer;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Started from a downloaded update's staging folder: install it over the app, then restart the app.
        if (args.Length >= 3 && args[0] == "--apply-update")
        {
            Updater.ApplyUpdate(args);
            return;
        }

        // Restarted after an update: start the servers that were running before.
        var resume = args.Length >= 2 && args[0] == "--updated"
            ? args[1].Split(',', StringSplitOptions.RemoveEmptyEntries)
            : null;
        if (resume == null) Updater.Cleanup();
        Application.Run(new MainForm(resume));
    }
}
