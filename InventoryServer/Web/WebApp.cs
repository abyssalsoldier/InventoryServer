using System.Net;
using System.Security.Cryptography;
using System.Text;
using InventoryServer.Data;
using Microsoft.AspNetCore.HttpOverrides;

namespace InventoryServer.Web;

public static class WebApp
{
    const string CookieName = "inv_session";

    static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
    static IResult Html(string body, int? status = null) => Results.Content(body, "text/html; charset=utf-8", statusCode: status);
    static SessionInfo? Sess(HttpContext c) => c.Items["sess"] as SessionInfo;
    static bool IsAdmin(HttpContext c) => Sess(c)?.User.Role == Roles.Admin;
    static string Csrf(HttpContext c) => $"<input type=\"hidden\" name=\"_csrf\" value=\"{H(Sess(c)?.Csrf)}\">";

    public static void Map(WebApplication app, ServerProfile p, Db db)
    {
        var ipThrottle = new LoginThrottle(maxFailures: 20);
        var userThrottle = new LoginThrottle(maxFailures: 5);

        if (p.BehindProxy)
            app.UseForwardedHeaders(new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            });

        app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "same-origin";
            h["Cache-Control"] = "no-store";
            h["Content-Security-Policy"] =
                "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
            if (p.IsSecure && ctx.Request.IsHttps) h["Strict-Transport-Security"] = "max-age=31536000";

            if (p.Mode == AccessMode.OwnCertificate && !ctx.Request.IsHttps)
            {
                ctx.Response.Redirect(p.PublicUrl + ctx.Request.Path + ctx.Request.QueryString);
                return;
            }

            var path = ctx.Request.Path;
            var isLogin = path.StartsWithSegments("/login");
            var anonymousOk = isLogin || path.StartsWithSegments("/logo");
            var sess = db.GetSession(ctx.Request.Cookies[CookieName]);
            ctx.Items["sess"] = sess;

            if (!anonymousOk && sess == null)
            {
                var back = HttpMethods.IsGet(ctx.Request.Method) ? "?next=" + Uri.EscapeDataString(path + ctx.Request.QueryString) : "";
                ctx.Response.Redirect("/login" + back);
                return;
            }

            if (HttpMethods.IsPost(ctx.Request.Method))
            {
                if (!OriginOk(ctx, p)) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync("Forbidden (origin)."); return; }
                if (!isLogin)
                {
                    var form = await ctx.Request.ReadFormAsync();
                    var sent = Encoding.UTF8.GetBytes(form["_csrf"].ToString());
                    var expected = Encoding.UTF8.GetBytes(sess!.Csrf);
                    if (!CryptographicOperations.FixedTimeEquals(sent, expected))
                    {
                        ctx.Response.StatusCode = 403;
                        await ctx.Response.WriteAsync("Forbidden (missing or invalid form token). Go back, reload the page and try again.");
                        return;
                    }
                }
            }
            await next();
        });

        // ---------- sign in / out ----------

        app.MapGet("/login", (HttpContext ctx) =>
        {
            if (Sess(ctx) != null) return Results.Redirect("/");
            var setup = db.HasUsers()
                ? ""
                : "<div class=\"banner err\">This server has no users yet. Open the Inventory Server app, choose the server, and use <b>Users…</b> to create an admin account.</div>";
            return Html(Page(ctx, p, "Sign in", $"""
                <form method="post" class="card">
                  <h2>Sign in</h2>
                  {setup}{Banner(ctx.Request)}
                  <input type="hidden" name="next" value="{H(ctx.Request.Query["next"])}">
                  <label>Username<input name="username" autocomplete="username" autocapitalize="none" required autofocus></label>
                  <label>Password<input type="password" name="password" autocomplete="current-password" required></label>
                  <button>Sign in</button>
                </form>
                """));
        });

        app.MapPost("/login", async (HttpContext ctx) =>
        {
            var f = await ctx.Request.ReadFormAsync();
            var username = f["username"].ToString().Trim().ToLowerInvariant();
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
            IResult Fail(string msg) => Results.Redirect("/login?err=" + Uri.EscapeDataString(msg) +
                (f["next"].ToString() is { Length: > 0 } n ? "&next=" + Uri.EscapeDataString(n) : ""));

            if (ipThrottle.IsLocked(ip) || userThrottle.IsLocked(username))
                return Fail("Too many failed attempts. Please wait 15 minutes and try again.");

            var user = db.Authenticate(username, f["password"].ToString());
            if (user == null)
            {
                ipThrottle.Failure(ip);
                userThrottle.Failure(username);
                return Fail("Wrong username or password.");
            }
            ipThrottle.Success(ip);
            userThrottle.Success(username);
            ctx.Response.Cookies.Append(CookieName, db.CreateSession(user.Id), new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = ctx.Request.IsHttps, MaxAge = TimeSpan.FromHours(12), Path = "/",
            });
            var next = f["next"].ToString();
            return Results.Redirect(next.StartsWith('/') && !next.StartsWith("//") && !next.StartsWith("/\\") ? next : "/");
        });

        app.MapPost("/logout", (HttpContext ctx) =>
        {
            db.DeleteSession(ctx.Request.Cookies[CookieName]);
            ctx.Response.Cookies.Delete(CookieName);
            return Results.Redirect("/login");
        });

        app.MapGet("/logo", () =>
        {
            if (p.LogoFile.Length == 0 || !File.Exists(p.LogoPath)) return Results.NotFound();
            var type = Path.GetExtension(p.LogoPath).ToLowerInvariant() is ".jpg" or ".jpeg" ? "image/jpeg" : "image/png";
            return Results.File(p.LogoPath, type);
        });

        // ---------- inventory ----------

        app.MapGet("/", (HttpContext ctx) =>
        {
            var r = ctx.Request;
            var q = r.Query["q"].ToString();
            var inStock = r.Query["instock"] == "1";
            var items = db.Search(q, inStock);
            var sb = new StringBuilder();
            sb.Append($"""
                {Banner(r)}
                <form method="get" class="search">
                  <input type="search" name="q" value="{H(q)}" placeholder="Search name, keywords, location…" autofocus>
                  <button>Search</button>
                  <label class="chk"><input type="checkbox" name="instock" value="1" {(inStock ? "checked" : "")}> In stock only</label>
                </form>
                <p class="muted">{items.Count} item(s){(q.Length > 0 ? $" matching “{H(q)}”" : "")}</p>
                """);
            foreach (var it in items)
                sb.Append($"""
                    <a class="card item" href="/i/{Uri.EscapeDataString(it.Sku)}">
                      <div><strong>{H(it.Name)}</strong> <span class="muted">{H(it.Sku)}</span>{(it.Serialized ? " <span class=\"tag\">S/N</span>" : "")}<br>
                      <span class="muted">{H(it.Location)}{(it.Description.Length > 0 ? " · " + H(Trunc(it.Description, 80)) : "")}</span></div>
                      <div class="right">{Badge(it)}<br><span class="muted">{StockText(it)}</span></div>
                    </a>
                    """);
            if (items.Count == 0) sb.Append("<p>No items found.</p>");
            if (IsAdmin(ctx))
                sb.Append($"""
                    <details class="card"><summary>Add a new item</summary>
                    <form method="post" action="/items">
                      {Csrf(ctx)}
                      <label>Name<input name="name" required></label>
                      <label>SKU / code <small>(blank = auto)</small><input name="sku" pattern="[A-Za-z0-9._\-]+" maxlength="40"></label>
                      <label>Description<input name="description"></label>
                      <label>Keywords <small>(space separated, used by search)</small><input name="keywords"></label>
                      <label>Location (shelf / bin)<input name="location"></label>
                      <label class="chk"><input type="checkbox" name="serialized" value="1"> Serialized – track each unit by serial number</label>
                      <div class="row"><label>Unit<input name="unit" value="each"></label>
                      <label class="qty-only">Starting stock<input type="number" name="stock" min="0" value="0"></label>
                      <label>Low-stock level<input type="number" name="reorder" min="0" value="0"></label></div>
                      <label class="ser-only">Serial numbers in stock <small>(one per line – leave blank to add later)</small><textarea name="serials" rows="4"></textarea></label>
                      <button>Create item</button>
                    </form></details>
                    """);
            return Html(Page(ctx, p, "Inventory", sb.ToString()));
        });

        app.MapPost("/items", async (HttpContext ctx) =>
        {
            if (!IsAdmin(ctx)) return Forbidden(ctx, p);
            var f = await ctx.Request.ReadFormAsync();
            var sku = f["sku"].ToString().Trim().ToUpperInvariant();
            try
            {
                if (sku.Length == 0) sku = db.NextSku();
                var serialized = f["serialized"] == "1";
                db.AddItem(new Item
                {
                    Sku = sku, Name = f["name"].ToString(), Description = f["description"].ToString(),
                    Keywords = f["keywords"].ToString(), Location = f["location"].ToString(),
                    Unit = f["unit"].ToString(), Stock = serialized ? 0 : ParseInt(f["stock"]), ReorderLevel = ParseInt(f["reorder"]),
                    Serialized = serialized,
                }, Sess(ctx)!.User.Username, serialized ? Db.ParseSerials(f["serials"]) : null);
                return Results.Redirect($"/i/{Uri.EscapeDataString(sku)}?msg=" + Uri.EscapeDataString("Item created."));
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return Results.Redirect("/?err=" + Uri.EscapeDataString(e.Message));
            }
        });

        app.MapGet("/i/{sku}", (string sku, HttpContext ctx) =>
        {
            var it = db.Get(sku);
            if (it == null)
                return Html(Page(ctx, p, "Not found", $"<p class=\"card\">No item with code <b>{H(sku)}</b>. <a href=\"/\">Back to search</a></p>"), 404);
            var u = Uri.EscapeDataString(it.Sku);
            var hist = new StringBuilder();
            foreach (var t in db.History(it.Sku, 20))
                hist.Append($"""
                    <tr><td>{H(t.Ts)}</td><td class="{(t.Delta < 0 ? "neg" : "pos")}">{t.Delta:+#;-#;0}</td>
                    <td>{H(t.Kind)}</td><td>{H(t.Serial)}</td><td>{H(t.Location)}</td><td>{H(t.User)}</td><td>{H(t.Note)}</td></tr>
                    """);
            var csrf = Csrf(ctx);
            var actions = it.Serialized ? SerialForms(ctx, it, db.ListUnits(it.Sku)) : $"""
                <form method="post" action="/i/{u}/use" class="card use">
                  {csrf}
                  <h3>Use stock</h3>
                  <div class="row"><label>Quantity<input type="number" name="qty" min="1" value="1" required></label>
                  <label>Where was it used?<input name="location" required placeholder="e.g. Room 214, Server rack 3"></label></div>
                  <button>Record use</button>
                </form>
                <form method="post" action="/i/{u}/add" class="card add">
                  {csrf}
                  <h3>Add stock</h3>
                  <div class="row"><label>Quantity<input type="number" name="qty" min="1" value="1" required></label>
                  <label>Note <small>(optional)</small><input name="note" placeholder="e.g. PO 1234"></label></div>
                  <button>Add to stock</button>
                </form>
                """;
            var amount = it.Serialized
                ? $"{it.Stock} <small>available · {it.InUse} in use</small>"
                : $"{it.Stock} <small>{H(it.Unit)}</small>";
            var body = $"""
                {Banner(ctx.Request)}
                <div class="card">
                  <h2>{H(it.Name)}</h2>
                  <p class="muted">{H(it.Sku)}{(it.Location.Length > 0 ? " · " + H(it.Location) : "")}{(it.Serialized ? " · tracked by serial number" : "")}</p>
                  <p class="big">{amount} {Badge(it)}</p>
                  {(it.Description.Length > 0 ? $"<p>{H(it.Description)}</p>" : "")}
                  {(it.Keywords.Length > 0 ? $"<p class=\"muted\">Keywords: {H(it.Keywords)}</p>" : "")}
                </div>
                {actions}
                <div class="card"><h3>Recent activity</h3>
                  <div class="scroll"><table><tr><th>When</th><th>±</th><th>Type</th><th>S/N</th><th>Where</th><th>Who</th><th>Note</th></tr>{hist}</table></div>
                </div>
                """;
            return Html(Page(ctx, p, it.Name, body));
        });

        app.MapPost("/i/{sku}/use", (string sku, HttpContext ctx) => Change(db, sku, ctx, -1));
        app.MapPost("/i/{sku}/add", (string sku, HttpContext ctx) => Change(db, sku, ctx, +1));

        // ---------- serial-numbered units ----------

        long UnitOf(IFormCollection f, string sku) =>
            long.TryParse(f["unit"].ToString(), out var id) && db.GetUnit(id) is { } unit && unit.Sku == sku
                ? id
                : throw new ArgumentException("Pick a serial number.");

        app.MapPost("/i/{sku}/units/add", (string sku, HttpContext ctx) => UnitAction(ctx, sku, (f, user) =>
            $"Added {db.AddUnits(sku, Db.ParseSerials(f["serials"]), f["note"].ToString(), user)} unit(s) to stock."));

        app.MapPost("/i/{sku}/units/use", (string sku, HttpContext ctx) => UnitAction(ctx, sku, (f, user) =>
        {
            var unit = db.MarkInUse(UnitOf(f, sku), f["location"].ToString(), f["note"].ToString(), user);
            return $"Serial {unit.Serial} marked in use.";
        }));

        app.MapPost("/i/{sku}/units/return", (string sku, HttpContext ctx) => UnitAction(ctx, sku, (f, user) =>
        {
            var unit = db.ReturnUnit(UnitOf(f, sku), f["note"].ToString(), user);
            return $"Serial {unit.Serial} returned to stock.";
        }));

        app.MapPost("/i/{sku}/units/remove", (string sku, HttpContext ctx) => !IsAdmin(ctx) ? Task.FromResult(Forbidden(ctx, p)) :
            UnitAction(ctx, sku, (f, user) =>
            {
                var unit = db.RemoveUnit(UnitOf(f, sku), f["reason"].ToString(), user);
                return $"Serial {unit.Serial} removed.";
            }));

        app.MapGet("/in-use", (HttpContext ctx) =>
        {
            var q = ctx.Request.Query["q"].ToString();
            var units = db.ListInUse(q);
            var csrf = Csrf(ctx);
            var sb = new StringBuilder($"""
                {Banner(ctx.Request)}
                <h2>Units in use</h2>
                <form method="get" class="search">
                  <input type="search" name="q" value="{H(q)}" placeholder="Search serial, item, where used, who…">
                  <button>Search</button>
                </form>
                <p class="muted">{units.Count} unit(s) in use{(q.Length > 0 ? $" matching “{H(q)}”" : "")}.
                  Returning a unit puts it back into available stock.
                  {(IsAdmin(ctx) ? "<a href=\"/export-serials.csv\">Download all serial numbers (CSV)</a>" : "")}</p>
                """);
            if (units.Count > 0)
            {
                sb.Append("<div class=\"card scroll\"><table><tr><th>Item</th><th>S/N</th><th>Where</th><th>Since</th><th>Who</th><th>Note</th><th></th></tr>");
                foreach (var x in units)
                    sb.Append($"""
                        <tr><td><a href="/i/{Uri.EscapeDataString(x.Sku)}">{H(x.ItemName)}</a><br><span class="muted">{H(x.Sku)}</span></td>
                        <td><b>{H(x.Serial)}</b></td><td>{H(x.Location)}</td><td>{H(x.Since)}</td><td>{H(x.User)}</td><td>{H(x.Note)}</td>
                        <td>{ReturnButton(csrf, x, fromInUsePage: true)}</td></tr>
                        """);
                sb.Append("</table></div>");
            }
            return Html(Page(ctx, p, "In use", sb.ToString()));
        });

        app.MapGet("/i/{sku}/qr.png", (string sku) =>
            db.Get(sku) == null ? Results.NotFound() : Results.File(Qr.Png(Qr.UrlFor(p, sku)), "image/png"));

        app.MapGet("/labels", (HttpContext ctx) =>
        {
            if (!IsAdmin(ctx)) return Forbidden(ctx, p);
            var logo = p.LogoFile.Length > 0 ? "<img class=\"logo\" src=\"/logo\" alt=\"\">" : "";
            var sb = new StringBuilder("<h2 class=\"noprint\">Labels</h2><p class=\"noprint muted\">Use your browser's Print (Ctrl+P).</p><div class=\"labels\">");
            foreach (var it in db.Search(null))
                sb.Append($"""
                    <div class="label"><img src="/i/{Uri.EscapeDataString(it.Sku)}/qr.png" alt="QR"><div><b>{H(it.Name)}</b><br>{H(it.Sku)}<br><small>{logo}{H(p.CompanyName)}</small></div></div>
                    """);
            sb.Append("</div>");
            return Html(Page(ctx, p, "Labels", sb.ToString()));
        });

        app.MapGet("/export.csv", (HttpContext ctx) => !IsAdmin(ctx) ? Forbidden(ctx, p) :
            Csv("items.csv", new[] { "sku", "name", "description", "keywords", "location", "unit", "stock", "reorder_level", "serialized", "in_use" },
                db.Search(null).Select(i => new[]
                {
                    i.Sku, i.Name, i.Description, i.Keywords, i.Location, i.Unit, i.Stock.ToString(), i.ReorderLevel.ToString(),
                    i.Serialized ? "yes" : "no", i.Serialized ? i.InUse.ToString() : "",
                })));

        app.MapGet("/export-transactions.csv", (HttpContext ctx) => !IsAdmin(ctx) ? Forbidden(ctx, p) :
            Csv("transactions.csv", new[] { "when", "sku", "serial", "change", "type", "where_used", "who", "note" },
                db.History(null, int.MaxValue).Select(t => new[] { t.Ts, t.Sku, t.Serial, t.Delta.ToString(), t.Kind, t.Location, t.User, t.Note })));

        app.MapGet("/export-serials.csv", (HttpContext ctx) => !IsAdmin(ctx) ? Forbidden(ctx, p) :
            Csv("serial-numbers.csv", new[] { "sku", "item", "serial", "status", "where_used", "since", "who", "note" },
                db.AllUnits().Select(x => new[] { x.Sku, x.ItemName, x.Serial, x.IsInUse ? "in use" : "available", x.Location, x.Since, x.User, x.Note })));

        async Task<IResult> UnitAction(HttpContext ctx, string sku, Func<IFormCollection, string, string> act)
        {
            var f = await ctx.Request.ReadFormAsync();
            var back = f["back"] == "in-use" ? "/in-use" : $"/i/{Uri.EscapeDataString(sku)}";
            try
            {
                return Results.Redirect($"{back}?msg=" + Uri.EscapeDataString(act(f, Sess(ctx)!.User.Username)));
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException)
            {
                return Results.Redirect($"{back}?err=" + Uri.EscapeDataString(e.Message));
            }
        }
    }

    static string StockText(Item it) => it.Serialized
        ? $"{it.Stock} available{(it.InUse > 0 ? $" · {it.InUse} in use" : "")}"
        : $"{it.Stock} {H(it.Unit)}";

    static string ReturnButton(string csrf, SerialUnit x, bool fromInUsePage = false) => $"""
        <form method="post" action="/i/{Uri.EscapeDataString(x.Sku)}/units/return" class="inline">{csrf}
          <input type="hidden" name="unit" value="{x.Id}">{(fromInUsePage ? "<input type=\"hidden\" name=\"back\" value=\"in-use\">" : "")}
          <button class="small add-btn">Return to stock</button></form>
        """;

    /// <summary>Item page actions for a serialized item: mark in use, add units, the in-use and available lists.</summary>
    static string SerialForms(HttpContext ctx, Item it, List<SerialUnit> units)
    {
        var u = Uri.EscapeDataString(it.Sku);
        var csrf = Csrf(ctx);
        var available = units.Where(x => !x.IsInUse).ToList();
        var inUse = units.Where(x => x.IsInUse).ToList();
        var sb = new StringBuilder();

        if (available.Count > 0)
            sb.Append($"""
                <form method="post" action="/i/{u}/units/use" class="card use">
                  {csrf}
                  <h3>Mark a unit in use</h3>
                  <div class="row"><label>Serial number<select name="unit" required>{string.Join("", available.Select(x => $"<option value=\"{x.Id}\">{H(x.Serial)}</option>"))}</select></label>
                  <label>Where is it used?<input name="location" required placeholder="e.g. Room 214, Server rack 3"></label></div>
                  <label>Note <small>(optional)</small><input name="note"></label>
                  <button>Mark in use</button>
                </form>
                """);
        else
            sb.Append("<p class=\"card muted\">No units available. Add serial numbers below, or return a unit that is in use.</p>");

        sb.Append($"""
            <form method="post" action="/i/{u}/units/add" class="card add">
              {csrf}
              <h3>Add units</h3>
              <label>Serial numbers <small>(one per line)</small><textarea name="serials" rows="3" required></textarea></label>
              <label>Note <small>(optional)</small><input name="note" placeholder="e.g. PO 1234"></label>
              <button>Add to stock</button>
            </form>
            """);

        sb.Append($"<div class=\"card\"><h3>In use ({inUse.Count})</h3>");
        if (inUse.Count == 0) sb.Append("<p class=\"muted\">None.</p>");
        else
        {
            sb.Append("<div class=\"scroll\"><table><tr><th>S/N</th><th>Where</th><th>Since</th><th>Who</th><th>Note</th><th></th></tr>");
            foreach (var x in inUse)
                sb.Append($"<tr><td><b>{H(x.Serial)}</b></td><td>{H(x.Location)}</td><td>{H(x.Since)}</td><td>{H(x.User)}</td><td>{H(x.Note)}</td><td>{ReturnButton(csrf, x)}</td></tr>");
            sb.Append("</table></div>");
        }
        sb.Append("</div>");

        sb.Append($"<div class=\"card\"><h3>Available ({available.Count})</h3>");
        if (available.Count == 0) sb.Append("<p class=\"muted\">None.</p>");
        else
        {
            sb.Append("<div class=\"scroll\"><table><tr><th>S/N</th><th>In stock since</th><th>Note</th></tr>");
            foreach (var x in available)
                sb.Append($"<tr><td><b>{H(x.Serial)}</b></td><td>{H(x.Since)}</td><td>{H(x.Note)}</td></tr>");
            sb.Append("</table></div>");
        }
        sb.Append("</div>");

        if (IsAdmin(ctx) && units.Count > 0)
            sb.Append($"""
                <details class="card"><summary>Remove a unit (lost, broken, entered by mistake)</summary>
                <form method="post" action="/i/{u}/units/remove">
                  {csrf}
                  <div class="row"><label>Serial number<select name="unit" required>{string.Join("", units.Select(x => $"<option value=\"{x.Id}\">{H(x.Serial)}{(x.IsInUse ? " (in use)" : "")}</option>"))}</select></label>
                  <label>Reason<input name="reason" required placeholder="e.g. Damaged, sent for disposal"></label></div>
                  <button class="danger">Remove permanently</button>
                </form></details>
                """);
        return sb.ToString();
    }

    static bool OriginOk(HttpContext ctx, ServerProfile p)
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length == 0) return true; // same-origin form posts from older browsers; SameSite=Lax still protects the session cookie
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var o)) return false;
        var authority = o.Authority;
        return authority.Equals(ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase) ||
               (Uri.TryCreate(p.PublicUrl, UriKind.Absolute, out var pub) && authority.Equals(pub.Authority, StringComparison.OrdinalIgnoreCase));
    }

    static IResult Forbidden(HttpContext ctx, ServerProfile p) =>
        Html(Page(ctx, p, "Not allowed", "<p class=\"banner err\">Your account is not allowed to do that. Ask an admin.</p>"), 403);

    static async Task<IResult> Change(Db db, string sku, HttpContext ctx, int sign)
    {
        var f = await ctx.Request.ReadFormAsync();
        var back = $"/i/{Uri.EscapeDataString(sku)}";
        try
        {
            var qty = ParseInt(f["qty"]);
            if (qty < 1) throw new ArgumentException("Quantity must be at least 1.");
            string location = f["location"].ToString();
            if (sign < 0 && string.IsNullOrWhiteSpace(location))
                throw new ArgumentException("Please say where it was used.");
            var stock = db.Adjust(sku, sign * qty, sign < 0 ? "use" : "add", location, f["note"].ToString(), Sess(ctx)!.User.Username);
            return Results.Redirect($"{back}?msg=" + Uri.EscapeDataString($"Recorded. {stock} now in stock."));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return Results.Redirect($"{back}?err=" + Uri.EscapeDataString(e.Message));
        }
    }

    static IResult Csv(string file, string[] header, IEnumerable<string[]> rows)
    {
        // Prefix cells that spreadsheets would treat as formulas.
        static string Q(string v) => "\"" + (v.Length > 0 && "=+-@\t\r".Contains(v[0]) && !double.TryParse(v, out _) ? "'" + v : v).Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder("﻿").AppendLine(string.Join(",", header));
        foreach (var row in rows) sb.AppendLine(string.Join(",", row.Select(Q)));
        return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", file);
    }

    static int ParseInt(string? v) => int.TryParse(v, out var n) ? n : 0;
    static string Trunc(string v, int n) => v.Length <= n ? v : v[..n] + "…";

    static string Banner(HttpRequest r) =>
        r.Query["err"].ToString() is { Length: > 0 } e ? $"<div class=\"banner err\">{H(e)}</div>" :
        r.Query["msg"].ToString() is { Length: > 0 } m ? $"<div class=\"banner ok\">{H(m)}</div>" : "";

    static string Badge(Item it) => it.Stock <= 0 ? "<span class=\"badge out\">Out of stock</span>"
        : it.Stock <= it.ReorderLevel ? "<span class=\"badge low\">Low</span>" : "<span class=\"badge in\">In stock</span>";

    static string Page(HttpContext ctx, ServerProfile p, string title, string body) => Layout(title, p, body, Sess(ctx), Csrf(ctx));

    static string Layout(string title, ServerProfile p, string body, SessionInfo? sess, string csrf)
    {
        var brand = (p.LogoFile.Length > 0 ? "<img src=\"/logo\" alt=\"\">" : "") + H(p.DisplayName.Length > 0 ? p.DisplayName : "Inventory");
        var nav = sess == null
            ? ""
            : "<a href=\"/in-use\">In use</a>" +
              (sess.User.Role == Roles.Admin ? "<a href=\"/labels\">Labels</a><a href=\"/export.csv\">Export</a>" : "") +
              $"<span class=\"spacer\"></span><span class=\"who\">{H(sess.User.Username)}</span><form method=\"post\" action=\"/logout\">{csrf}<button class=\"link\">Sign out</button></form>";
        return $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{H(title)}} – {{H(p.DisplayName)}}</title>
            <style>
              :root { --bg:#f4f5f7; --fg:#1b1f24; --card:#fff; --muted:#6a737d; --line:#d9dde3; --accent:#1f6feb; }
              @media (prefers-color-scheme: dark) { :root { --bg:#14171a; --fg:#e6e8eb; --card:#1d2227; --muted:#9aa4af; --line:#333a41; --accent:#58a6ff; } }
              * { box-sizing: border-box; }
              body { margin:0; font:16px/1.4 system-ui, sans-serif; background:var(--bg); color:var(--fg); }
              header { background:var(--accent); color:#fff; padding:10px 16px; display:flex; gap:16px; align-items:center; flex-wrap:wrap; }
              header a { color:#fff; text-decoration:none; font-weight:600; }
              header .brand { display:flex; align-items:center; gap:8px; font-size:1.1em; }
              header .brand img { height:28px; width:auto; background:#fff; border-radius:4px; padding:2px; }
              header .spacer { flex:1; } header form { margin:0; }
              button.link { background:none; padding:0; color:#fff; text-decoration:underline; font-weight:600; }
              main { max-width:760px; margin:0 auto; padding:16px; }
              .card { display:block; background:var(--card); border:1px solid var(--line); border-radius:10px; padding:14px; margin:0 0 12px; color:inherit; text-decoration:none; }
              .item { display:flex; justify-content:space-between; gap:12px; }
              .right { text-align:right; white-space:nowrap; }
              .muted { color:var(--muted); font-size:.9em; }
              .big { font-size:2em; font-weight:700; margin:.2em 0; }
              .big small { font-size:.5em; font-weight:400; color:var(--muted); }
              label { display:block; margin:8px 0; font-weight:600; flex:1; }
              label small { font-weight:400; color:var(--muted); }
              input, select, textarea { display:block; width:100%; padding:10px; font:inherit; font-size:1em; border:1px solid var(--line); border-radius:8px; background:var(--bg); color:var(--fg); margin-top:4px; }
              button { padding:12px 18px; font-size:1em; border:0; border-radius:8px; background:var(--accent); color:#fff; font-weight:600; cursor:pointer; }
              .use button { background:#d1421f; } .add button, button.add-btn { background:#1a7f37; } button.danger { background:#c0392b; }
              button.small { padding:6px 10px; font-size:.85em; white-space:nowrap; } form.inline { margin:0; }
              .tag { font-size:.75em; font-weight:700; border:1px solid var(--line); border-radius:4px; padding:0 4px; color:var(--muted); }
              .ser-only { display:none; }
              form:has(input[name=serialized]:checked) .ser-only { display:block; }
              form:has(input[name=serialized]:checked) .qty-only { display:none; }
              td a { color:var(--accent); }
              .row { display:flex; gap:12px; flex-wrap:wrap; } .row label { min-width:140px; }
              .search { display:flex; gap:8px; flex-wrap:wrap; align-items:center; margin-bottom:8px; }
              .search input[type=search] { flex:1; min-width:200px; margin:0; } .chk { display:flex; align-items:center; gap:6px; margin:0; flex:none; font-weight:400; }
              .chk input { width:auto; margin:0; }
              .badge { padding:2px 8px; border-radius:99px; font-size:.8em; font-weight:700; color:#fff; }
              .badge.in { background:#1a7f37; } .badge.low { background:#b7791f; } .badge.out { background:#c0392b; }
              .banner { padding:10px 14px; border-radius:8px; margin-bottom:12px; font-weight:600; }
              .banner.ok { background:#dafbe1; color:#116329; } .banner.err { background:#ffebe9; color:#a40e26; }
              table { border-collapse:collapse; width:100%; font-size:.9em; } th, td { text-align:left; padding:6px 8px; border-bottom:1px solid var(--line); }
              .scroll { overflow-x:auto; } .pos { color:#1a7f37; font-weight:700; } .neg { color:#c0392b; font-weight:700; }
              .labels { display:grid; grid-template-columns:repeat(auto-fill, minmax(220px, 1fr)); gap:10px; }
              .label { background:#fff; color:#000; border:1px dashed #888; padding:10px; text-align:center; break-inside:avoid; }
              .label img { width:150px; height:150px; image-rendering:pixelated; }
              .label small { color:#444; } .label .logo { width:auto; height:18px; vertical-align:middle; margin-right:4px; image-rendering:auto; }
              @media print { header, .noprint { display:none; } body { background:#fff; } main { max-width:none; padding:0; } }
            </style></head><body>
            <header><a class="brand" href="/">{{brand}}</a>{{nav}}</header>
            <main>{{body}}</main></body></html>
            """;
    }
}
