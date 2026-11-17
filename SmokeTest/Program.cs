// End-to-end smoke test for InventoryServer: profiles, migration, auth, CSRF, roles, HTTPS, tunnel headers.
// Run:  dotnet run --project SmokeTest
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using InventoryServer;
using InventoryServer.Data;
using Microsoft.Data.Sqlite;

if (args.Length == 2 && args[0] == "--gui") { GuiShots.Run(args[1]); return 0; }

// Checks the live GitHub release: downloads update.json and verifies its signature against the key built into the app.
if (args.Length == 1 && args[0] == "--check-update")
{
    try
    {
        var m = await Updater.CheckAsync();
        Console.WriteLine($"Signature OK. This build is {Updater.CurrentText}; " + (m == null ? "no newer release." : $"newer release {Updater.Display(m.Version)} available."));
        return 0;
    }
    catch (Exception e) { Console.WriteLine("Update check FAILED: " + e.Message); return 1; }
}

var root = Path.Combine(Path.GetTempPath(), "inv-smoke-" + Guid.NewGuid().ToString("N")[..6]);
Environment.SetEnvironmentVariable("INVENTORY_DATA_DIR", root);   // must happen before ProfileStore is first used
Directory.CreateDirectory(root);

var failures = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(!ok && detail.Length > 0 ? "  -> " + detail : "")}");
    if (!ok) failures++;
}

int FreePort()
{
    var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
    l.Start();
    var port = ((IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return port;
}

HttpClient Client(out CookieContainer jar, bool trustAnyCert = true)
{
    jar = new CookieContainer();
    var h = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = jar };
    if (trustAnyCert) h.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
    return new HttpClient(h) { Timeout = TimeSpan.FromSeconds(20) };
}

static string? Csrf(string html) => Regex.Match(html, "name=\"_csrf\" value=\"([^\"]+)\"").Groups[1].Value is { Length: > 0 } v ? v : null;
static FormUrlEncodedContent Form(params (string, string)[] f) => new(f.Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2)));

// ---------- 1. migrate a v1 install ----------
{
    File.WriteAllText(Path.Combine(root, "settings.json"), """{"Address":"10.1.2.3","Port":8099,"StartOnLaunch":true,"Password":"old"}""");
    using (var c = new SqliteConnection($"Data Source={Path.Combine(root, "inventory.db")}"))
    {
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE items(id INTEGER PRIMARY KEY AUTOINCREMENT, sku TEXT NOT NULL UNIQUE, name TEXT NOT NULL, description TEXT NOT NULL DEFAULT '',
              keywords TEXT NOT NULL DEFAULT '', location TEXT NOT NULL DEFAULT '', unit TEXT NOT NULL DEFAULT 'each',
              stock INTEGER NOT NULL DEFAULT 0, reorder_level INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL DEFAULT (datetime('now','localtime')));
            CREATE TABLE transactions(id INTEGER PRIMARY KEY AUTOINCREMENT, sku TEXT NOT NULL, delta INTEGER NOT NULL, kind TEXT NOT NULL,
              location TEXT NOT NULL DEFAULT '', note TEXT NOT NULL DEFAULT '', user TEXT NOT NULL DEFAULT '', ts TEXT NOT NULL DEFAULT (datetime('now','localtime')));
            INSERT INTO items(sku,name,stock) VALUES('OLD-1','Legacy cable',5);
            """;
        cmd.ExecuteNonQuery();
    }
    SqliteConnection.ClearAllPools();
    var list = ProfileStore.Load();
    Check("v1 settings migrate to one profile", list.Count == 1 && list[0].Domain == "10.1.2.3" && list[0].LocalPort == 8099 && list[0].DataDir == root && list[0].StartOnLaunch);
    var db = new Db(list[0]);
    db.Init();
    Check("v1 database keeps its items after upgrade", db.Get("OLD-1")?.Stock == 5);
    Check("upgrade adds users table", !db.HasUsers());
    File.Delete(Path.Combine(root, "servers.json"));
    SqliteConnection.ClearAllPools();
    File.Delete(Path.Combine(root, "inventory.db")); File.Delete(Path.Combine(root, "inventory.db-wal")); File.Delete(Path.Combine(root, "inventory.db-shm")); File.Delete(Path.Combine(root, "settings.json"));
}

// ---------- 2. two servers at once ----------
ServerProfile Make(string name, AccessMode mode, int port) => new()
{
    CompanyName = name, Mode = mode, LocalPort = port, Domain = mode == AccessMode.CloudflareTunnel ? $"{name.ToLower()}.example.com" : "127.0.0.1",
    DataDir = ProfileStore.DefaultDataDir(name, name.ToLower()),
};

var pa = Make("Acme", AccessMode.Internal, FreePort());
var pb = Make("Beta", AccessMode.CloudflareTunnel, FreePort());
var dbA = new Db(pa); dbA.Init();
var dbB = new Db(pb); dbB.Init();
var hostA = new ServerHost(pa, dbA);
var hostB = new ServerHost(pb, dbB);

try
{
    // password rules
    try { dbA.CreateUser("admin", "short", Roles.Admin); Check("short password rejected", false); }
    catch (ArgumentException) { Check("short password rejected", true); }
    dbA.CreateUser("admin", "correct horse battery", Roles.Admin);
    dbA.CreateUser("sam", "another long passphrase", Roles.Staff);
    dbB.CreateUser("boss", "beta boss passphrase!", Roles.Admin);
    Check("password is stored hashed", dbA.Authenticate("admin", "correct horse battery") != null && dbA.Authenticate("admin", "wrong") == null);
    try { dbA.DeleteUser(dbA.ListUsers().First(u => u.Username == "admin").Id); Check("cannot delete last admin", false); }
    catch (InvalidOperationException) { Check("cannot delete last admin", true); }

    await hostA.StartAsync();
    await hostB.StartAsync();
    Check("two servers run side by side", hostA.Running && hostB.Running);
    var a = $"http://127.0.0.1:{pa.LocalPort}";
    var b = $"http://127.0.0.1:{pb.LocalPort}";

    // anonymous access
    using var anon = Client(out _);
    var r = await anon.GetAsync(a + "/");
    Check("anonymous / redirects to login", r.StatusCode == HttpStatusCode.Redirect && r.Headers.Location!.OriginalString.StartsWith("/login"));
    r = await anon.GetAsync(a + "/i/ITM-0001");
    Check("anonymous item page redirects with next=", r.StatusCode == HttpStatusCode.Redirect && r.Headers.Location!.OriginalString.Contains("next=%2Fi%2FITM-0001"));
    r = await anon.PostAsync(a + "/i/ITM-0001/use", Form(("qty", "1"), ("location", "x")));
    Check("anonymous POST is not accepted", r.StatusCode == HttpStatusCode.Redirect && r.Headers.Location!.OriginalString.StartsWith("/login"));
    r = await anon.GetAsync(a + "/login");
    var h = r.Headers;
    Check("security headers present", h.Contains("X-Content-Type-Options") && h.Contains("X-Frame-Options") && r.Headers.Contains("Content-Security-Policy"));
    Check("no HSTS over plain HTTP", !h.Contains("Strict-Transport-Security"));

    // wrong password lockout (username throttle: 5)
    using (var bad = Client(out _))
    {
        for (var i = 0; i < 5; i++) await bad.PostAsync(a + "/login", Form(("username", "sam"), ("password", "nope" + i)));
        r = await bad.PostAsync(a + "/login", Form(("username", "sam"), ("password", "another long passphrase")));
        Check("5 failures lock the account even with the right password", r.Headers.Location!.OriginalString.Contains("Too+many") || r.Headers.Location!.OriginalString.Contains("Too%20many"), r.Headers.Location?.OriginalString ?? "");
    }

    // admin session
    using var admin = Client(out var adminJar);
    r = await admin.PostAsync(a + "/login", Form(("username", "admin"), ("password", "correct horse battery")));
    Check("admin can sign in", r.StatusCode == HttpStatusCode.Redirect && r.Headers.Location!.OriginalString == "/");
    var cookie = r.Headers.GetValues("Set-Cookie").First();
    Check("session cookie is HttpOnly + SameSite", cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase) && cookie.Contains("samesite=lax", StringComparison.OrdinalIgnoreCase));
    Check("session cookie not Secure over plain HTTP", !cookie.Contains("secure;", StringComparison.OrdinalIgnoreCase) && !cookie.EndsWith("secure", StringComparison.OrdinalIgnoreCase));
    var home = await (await admin.GetAsync(a + "/")).Content.ReadAsStringAsync();
    var csrf = Csrf(home)!;
    Check("admin sees add-item form with CSRF token", csrf != null && home.Contains("Add a new item"));
    Check("company name in header", home.Contains("Acme"));

    r = await admin.PostAsync(a + "/items", Form(("name", "Ethernet cable 6ft"), ("keywords", "cat6 patch network"), ("location", "Shelf A2"), ("stock", "10"), ("reorder", "3"), ("unit", "each")));
    Check("POST without CSRF token is rejected", r.StatusCode == HttpStatusCode.Forbidden);
    r = await admin.PostAsync(a + "/items", Form(("_csrf", "wrong"), ("name", "x")));
    Check("POST with wrong CSRF token is rejected", r.StatusCode == HttpStatusCode.Forbidden);
    var evil = new HttpRequestMessage(HttpMethod.Post, a + "/items") { Content = Form(("_csrf", csrf), ("name", "x")) };
    evil.Headers.Add("Origin", "https://evil.example");
    r = await admin.SendAsync(evil);
    Check("cross-origin POST is rejected", r.StatusCode == HttpStatusCode.Forbidden);

    r = await admin.PostAsync(a + "/items", Form(("_csrf", csrf), ("name", "Ethernet cable 6ft"), ("keywords", "cat6 patch network"), ("location", "Shelf A2"), ("stock", "10"), ("reorder", "3"), ("unit", "each")));
    Check("admin can create item", r.StatusCode == HttpStatusCode.Redirect && r.Headers.Location!.OriginalString.StartsWith("/i/ITM-0001"));
    r = await admin.PostAsync(a + "/i/ITM-0001/use", Form(("_csrf", csrf), ("qty", "3"), ("location", "Room 214")));
    Check("use stock works", dbA.Get("ITM-0001")?.Stock == 7);
    Check("transaction records the signed-in user", dbA.History("ITM-0001", 5).Any(t => t.Kind == "use" && t.User == "admin" && t.Location == "Room 214"));
    r = await admin.PostAsync(a + "/i/ITM-0001/use", Form(("_csrf", csrf), ("qty", "30"), ("location", "Room 9")));
    Check("cannot use more than in stock", dbA.Get("ITM-0001")?.Stock == 7 && r.Headers.Location!.OriginalString.Contains("err="));
    var searchHtml = await (await admin.GetAsync(a + "/?q=cat6%20pat")).Content.ReadAsStringAsync();
    Check("keyword search finds the item with stock badge", searchHtml.Contains("Ethernet cable 6ft") && searchHtml.Contains("In stock"));
    Check("admin can export", (await admin.GetAsync(a + "/export.csv")).StatusCode == HttpStatusCode.OK);
    Check("QR image served", (await admin.GetAsync(a + "/i/ITM-0001/qr.png")).Content.Headers.ContentType?.MediaType == "image/png");

    // staff session
    using var staff = Client(out _);
    r = await staff.PostAsync(a + "/login", Form(("username", "sam"), ("password", "another long passphrase")));
    Check("staff sign-in blocked by earlier lockout, as expected", r.Headers.Location!.OriginalString.Contains("err="));
    dbA.CreateUser("pat", "pats long passphrase", Roles.Staff);
    r = await staff.PostAsync(a + "/login", Form(("username", "pat"), ("password", "pats long passphrase")));
    Check("staff can sign in", r.Headers.Location!.OriginalString == "/");
    var staffHome = await (await staff.GetAsync(a + "/")).Content.ReadAsStringAsync();
    var staffCsrf = Csrf(staffHome)!;
    Check("staff does not see add-item form", !staffHome.Contains("Add a new item"));
    r = await staff.PostAsync(a + "/items", Form(("_csrf", staffCsrf), ("name", "sneaky")));
    Check("staff cannot create items", r.StatusCode == HttpStatusCode.Forbidden && dbA.Search("sneaky").Count == 0);
    Check("staff cannot export", (await staff.GetAsync(a + "/export.csv")).StatusCode == HttpStatusCode.Forbidden);
    Check("staff cannot open labels", (await staff.GetAsync(a + "/labels")).StatusCode == HttpStatusCode.Forbidden);
    await staff.PostAsync(a + "/i/ITM-0001/add", Form(("_csrf", staffCsrf), ("qty", "2"), ("note", "PO 1")));
    Check("staff can add stock", dbA.Get("ITM-0001")?.Stock == 9);

    // ---------- serial-numbered items ----------
    r = await admin.PostAsync(a + "/items", Form(("_csrf", csrf), ("sku", "LAPTOP"), ("name", "Laptop 14in"), ("serialized", "1"),
        ("serials", "SN-100\r\nSN-101\nSN-102"), ("stock", "99"), ("unit", "each")));
    var laptop = dbA.Get("LAPTOP");
    Check("serialized item created with one unit per serial", laptop is { Serialized: true, Stock: 3, InUse: 0 }, $"{laptop?.Stock}");
    r = await admin.PostAsync(a + "/items", Form(("_csrf", csrf), ("sku", "DUP"), ("name", "Dup"), ("serialized", "1"), ("serials", "A1\na1")));
    Check("duplicate serials rejected on create", dbA.Get("DUP") == null && r.Headers.Location!.OriginalString.Contains("err="));
    r = await admin.PostAsync(a + "/i/LAPTOP/use", Form(("_csrf", csrf), ("qty", "1"), ("location", "Room 1")));
    Check("plain quantity use is refused for serialized item", dbA.Get("LAPTOP")?.Stock == 3 && r.Headers.Location!.OriginalString.Contains("err="));
    try { dbA.SetCount("LAPTOP", 10); Check("set count refused for serialized item", false); }
    catch (InvalidOperationException) { Check("set count refused for serialized item", true); }

    var page = await (await admin.GetAsync(a + "/i/LAPTOP")).Content.ReadAsStringAsync();
    Check("item page lists serials and in-use form", page.Contains("SN-101") && page.Contains("Mark a unit in use") && page.Contains("Available (3)"));
    var unit101 = dbA.ListUnits("LAPTOP").First(u => u.Serial == "SN-101");
    r = await admin.PostAsync(a + "/i/LAPTOP/units/use", Form(("_csrf", csrf), ("unit", unit101.Id.ToString()), ("location", "")));
    Check("marking in use needs a location", dbA.Get("LAPTOP")?.InUse == 0 && r.Headers.Location!.OriginalString.Contains("err="));
    r = await admin.PostAsync(a + "/i/LAPTOP/units/use", Form(("_csrf", csrf), ("unit", unit101.Id.ToString()), ("location", "Reception desk")));
    laptop = dbA.Get("LAPTOP");
    Check("mark in use moves unit out of stock", laptop is { Stock: 2, InUse: 1 } && dbA.GetUnit(unit101.Id) is { IsInUse: true, Location: "Reception desk", User: "admin" });
    Check("in-use is logged with its serial", dbA.History("LAPTOP", 5).Any(t => t.Kind == "use" && t.Serial == "SN-101" && t.Location == "Reception desk"));
    r = await admin.PostAsync(a + "/i/LAPTOP/units/use", Form(("_csrf", csrf), ("unit", unit101.Id.ToString()), ("location", "Elsewhere")));
    Check("cannot mark the same unit in use twice", r.Headers.Location!.OriginalString.Contains("err=") && dbA.GetUnit(unit101.Id)!.Location == "Reception desk");
    r = await admin.PostAsync(a + "/i/ITM-0001/units/return", Form(("_csrf", csrf), ("unit", unit101.Id.ToString())));
    Check("unit id must belong to the item in the URL", r.Headers.Location!.OriginalString.Contains("err=") && dbA.GetUnit(unit101.Id)!.IsInUse);

    var inUsePage = await (await admin.GetAsync(a + "/in-use?q=recep")).Content.ReadAsStringAsync();
    Check("in-use page lists the unit", inUsePage.Contains("SN-101") && inUsePage.Contains("Laptop 14in") && inUsePage.Contains("Return to stock"));
    Check("search by serial finds the item", dbA.Search("sn-102").FirstOrDefault()?.Sku == "LAPTOP");
    var listHtml = await (await admin.GetAsync(a + "/?q=laptop")).Content.ReadAsStringAsync();
    Check("home list shows serialized item as an expandable › row with its serials",
        listHtml.Contains("<details class=\"card expand\">") && listHtml.Contains("SN-101") && listHtml.Contains("Reception desk") && listHtml.Contains("/i/LAPTOP"));

    r = await admin.PostAsync(a + "/i/LAPTOP/units/return", Form(("_csrf", csrf), ("unit", unit101.Id.ToString()), ("back", "in-use")));
    Check("return from in-use page goes back to stock", r.Headers.Location!.OriginalString.StartsWith("/in-use?msg=") && dbA.Get("LAPTOP") is { Stock: 3, InUse: 0 });
    r = await admin.PostAsync(a + "/i/LAPTOP/units/add", Form(("_csrf", csrf), ("serials", "SN-103, SN-104"), ("note", "PO 77")));
    Check("add units by serial", dbA.Get("LAPTOP")?.Stock == 5);
    r = await admin.PostAsync(a + "/i/LAPTOP/units/add", Form(("_csrf", csrf), ("serials", "sn-100")));
    Check("existing serial cannot be added again", dbA.Get("LAPTOP")?.Stock == 5 && r.Headers.Location!.OriginalString.Contains("err="));

    // staff can use/return but not remove
    using (var st = Client(out _))
    {
        dbA.CreateUser("lee", "lees long passphrase", Roles.Staff);
        await st.PostAsync(a + "/login", Form(("username", "lee"), ("password", "lees long passphrase")));
        var stCsrf = Csrf(await (await st.GetAsync(a + "/i/LAPTOP")).Content.ReadAsStringAsync())!;
        var u103 = dbA.ListUnits("LAPTOP").First(u => u.Serial == "SN-103");
        await st.PostAsync(a + "/i/LAPTOP/units/use", Form(("_csrf", stCsrf), ("unit", u103.Id.ToString()), ("location", "Lab")));
        Check("staff can mark a unit in use", dbA.GetUnit(u103.Id) is { IsInUse: true, User: "lee" });
        r = await st.PostAsync(a + "/i/LAPTOP/units/remove", Form(("_csrf", stCsrf), ("unit", u103.Id.ToString()), ("reason", "x")));
        Check("staff cannot remove units", r.StatusCode == HttpStatusCode.Forbidden && dbA.GetUnit(u103.Id) != null);
        Check("staff cannot export serials", (await st.GetAsync(a + "/export-serials.csv")).StatusCode == HttpStatusCode.Forbidden);
        r = await admin.PostAsync(a + "/i/LAPTOP/units/remove", Form(("_csrf", csrf), ("unit", u103.Id.ToString()), ("reason", "Dropped")));
        Check("admin removes an in-use unit without changing stock", dbA.GetUnit(u103.Id) == null && dbA.Get("LAPTOP") is { Stock: 4, InUse: 0 });
    }
    var serialCsv = await (await admin.GetAsync(a + "/export-serials.csv")).Content.ReadAsStringAsync();
    Check("serials export lists units", serialCsv.Contains("SN-104") && serialCsv.Contains("available"));

    // converting an existing item
    try { dbA.SetSerialized("ITM-0001", true, new[] { "only-one" }); Check("turning on serials needs one per unit in stock", false); }
    catch (ArgumentException) { Check("turning on serials needs one per unit in stock", true); }
    dbA.AddItem(new Item { Sku = "CONV", Name = "Convert me", Stock = 2 });
    dbA.SetSerialized("CONV", true, new[] { "C1", "C2" });
    Check("existing stock converts to serials", dbA.Get("CONV") is { Serialized: true, Stock: 2 } && dbA.ListUnits("CONV").Count == 2);
    dbA.MarkInUse(dbA.ListUnits("CONV")[0].Id, "Desk 4");
    try { dbA.SetSerialized("CONV", false); Check("cannot turn off serials while units are in use", false); }
    catch (InvalidOperationException) { Check("cannot turn off serials while units are in use", true); }
    dbA.ReturnUnit(dbA.ListUnits("CONV")[0].Id);
    dbA.SetSerialized("CONV", false);
    Check("turning serials off keeps the count", dbA.Get("CONV") is { Serialized: false, Stock: 2 } && dbA.ListUnits("CONV").Count == 0);
    dbA.DeleteItem("CONV");
    dbA.DeleteItem("LAPTOP");

    // logout invalidates the session
    await staff.PostAsync(a + "/logout", Form(("_csrf", staffCsrf)));
    r = await staff.GetAsync(a + "/");
    Check("logout ends the session", r.StatusCode == HttpStatusCode.Redirect);

    // isolation between companies
    Check("other company's data is separate", dbB.Search(null).Count == 0 && dbA.Search(null).Count == 1);
    using var cross = Client(out _);
    r = await cross.PostAsync(b + "/login", Form(("username", "admin"), ("password", "correct horse battery")));
    Check("users of one company cannot sign in to another", r.Headers.Location!.OriginalString.Contains("err="));

    // ---------- tunnel mode: behind proxy ----------
    using var tun = Client(out _);
    var req = new HttpRequestMessage(HttpMethod.Post, b + "/login") { Content = Form(("username", "boss"), ("password", "beta boss passphrase!")) };
    req.Headers.Add("X-Forwarded-Proto", "https");
    req.Headers.Add("X-Forwarded-For", "203.0.113.9");
    r = await tun.SendAsync(req);
    Check("tunnel: sign-in works behind proxy", r.Headers.Location?.OriginalString == "/");
    var tcookie = r.Headers.GetValues("Set-Cookie").First();
    Check("tunnel: cookie is Secure when proxy says https", tcookie.Contains("secure", StringComparison.OrdinalIgnoreCase));
    var treq = new HttpRequestMessage(HttpMethod.Get, b + "/login");
    treq.Headers.Add("X-Forwarded-Proto", "https");
    r = await anon.SendAsync(treq);
    Check("tunnel: HSTS sent for https requests", r.Headers.Contains("Strict-Transport-Security"));
    Check("tunnel: listens on loopback only", pb.BindAddress().Equals(IPAddress.Loopback));

    // ---------- own certificate: HTTPS + redirect ----------
    using (var rsa = RSA.Create(2048))
    {
        var req2 = new CertificateRequest("CN=certtest.example.com", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("certtest.example.com");
        req2.CertificateExtensions.Add(san.Build());
        using var cert = req2.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(30));
        var pfx = Path.Combine(root, "test.pfx");
        File.WriteAllBytes(pfx, cert.Export(X509ContentType.Pfx, "pfxpass"));
        var pc = new ServerProfile
        {
            CompanyName = "Gamma", Mode = AccessMode.OwnCertificate, Domain = "certtest.example.com", LocalPort = FreePort(), HttpsPort = FreePort(),
            CertPath = pfx, CertPasswordProtected = Secret.Protect("pfxpass"), DataDir = ProfileStore.DefaultDataDir("Gamma", "gamma"),
        };
        var dbC = new Db(pc); dbC.Init(); dbC.CreateUser("gadmin", "gamma admin passphrase", Roles.Admin);
        Check("secrets round-trip through DPAPI", pc.CertPassword == "pfxpass");
        var hostC = new ServerHost(pc, dbC);
        await hostC.StartAsync();
        try
        {
            r = await anon.GetAsync($"http://127.0.0.1:{pc.LocalPort}/login");
            Check("cert: HTTP redirects to the HTTPS public URL", r.StatusCode == HttpStatusCode.Redirect && r.Headers.Location!.OriginalString.StartsWith("https://certtest.example.com"), r.Headers.Location?.OriginalString ?? "");
            using var sec = Client(out _);
            r = await sec.GetAsync($"https://127.0.0.1:{pc.HttpsPort}/login");
            Check("cert: HTTPS serves the login page", r.StatusCode == HttpStatusCode.OK);
            Check("cert: HSTS header present", r.Headers.Contains("Strict-Transport-Security"));
            r = await sec.PostAsync($"https://127.0.0.1:{pc.HttpsPort}/login", Form(("username", "gadmin"), ("password", "gamma admin passphrase")));
            Check("cert: session cookie is Secure", r.Headers.GetValues("Set-Cookie").First().Contains("secure", StringComparison.OrdinalIgnoreCase));
        }
        finally { await hostC.StopAsync(); }
    }

    // ---------- profiles persist ----------
    ProfileStore.Save(new[] { pa, pb });
    var again = ProfileStore.Load();
    Check("profiles save and reload", again.Count == 2 && again[1].Mode == AccessMode.CloudflareTunnel && again[0].PublicUrl == pa.PublicUrl);
    Check("public URLs are built per mode", pb.PublicUrl == "https://beta.example.com" && pa.PublicUrl == $"http://127.0.0.1:{pa.LocalPort}");
}
finally
{
    await hostA.StopAsync();
    await hostB.StopAsync();
    SqliteConnection.ClearAllPools();
    try { Directory.Delete(root, true); } catch { }
}

Console.WriteLine(failures == 0 ? "\nAll checks passed." : $"\n{failures} check(s) FAILED.");
return failures == 0 ? 0 : 1;
