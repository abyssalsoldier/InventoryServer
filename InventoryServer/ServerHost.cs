using System.Security.Cryptography.X509Certificates;
using InventoryServer.Data;
using InventoryServer.Web;

namespace InventoryServer;

/// <summary>Starts and stops one profile's embedded Kestrel web server.</summary>
public class ServerHost
{
    readonly ServerProfile _profile;
    readonly Db _db;
    WebApplication? _app;

    public ServerHost(ServerProfile profile, Db db)
    {
        _profile = profile;
        _db = db;
    }

    public bool Running => _app != null;

    public async Task StartAsync()
    {
        if (_app != null) return;
        var p = _profile;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();

        X509Certificate2? cert = null;
        if (p.Mode == AccessMode.OwnCertificate)
            cert = new X509Certificate2(p.CertPath, p.CertPassword);

        var bind = p.BindAddress();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.AddServerHeader = false;
            o.Listen(bind, p.LocalPort);
            if (cert != null) o.Listen(bind, p.HttpsPort, lo => lo.UseHttps(cert));
        });

        var app = builder.Build();
        WebApp.Map(app, p, _db);
        await app.StartAsync();
        _app = app;
    }

    public async Task StopAsync()
    {
        if (_app == null) return;
        var app = _app;
        _app = null;
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
