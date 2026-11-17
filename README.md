# Inventory Server

Double-click `InventoryServer.exe` in this folder (one self-contained file: no install, no .NET needed, no command line). The version (e.g. `V1.1.0`) is shown in the window title and at the bottom of every web page.

One app can host **several companies**. Each company is a *server* with its own domain, data folder, branding, users and inventory.

## Setting up a company (the “New server” wizard)
Servers tab → **New server…** (opens automatically on first launch):
1. **Company** – name and optional logo (shown on the website and labels).
2. **Data folder** – where that company's database, settings and backups live.
3. **Access** – choose how people reach it:
   * **Cloudflare Tunnel (recommended for the internet)** – public domain, HTTPS handled by Cloudflare, nothing opened in the firewall.
   * **Internal network** – plain HTTP on the company LAN/VPN, using an IP or internal DNS name.
   * **Own certificate (.pfx)** – public domain served directly over HTTPS; ports must be forwarded.
4. **Domain details** (for Cloudflare: the exact clicks, token box and the install command to copy).
5. **Admin account** – first website administrator.
6. **Review and test** – starts the server and checks it (and, for public modes, the public address).

### Cloudflare Tunnel in short
1. Put the domain on Cloudflare. Zero Trust → Networks → Tunnels → *Create a tunnel* (Cloudflared).
2. Copy the tunnel token into the wizard. Add a **Public Hostname**: your domain → service `HTTP` → `localhost:<local port the wizard shows>`.
3. On the server PC install cloudflared and run the command the wizard/“Tunnel command” button gives you (administrator, once). cloudflared runs as its own Windows service; **this app never installs or starts it**.
4. Firewall: **no inbound rule**. The PC needs outbound access to Cloudflare (TCP/UDP 7844).

## Using it
* **Items tab** (pick the server at the top): add/edit/delete items, Add stock / Use stock / Set count, history, Show QR, Print label(s).
* **Serial numbers**: tick **Serialized** when adding an item (or later via Edit) to track each unit by serial number instead of a plain count. Stock is then the number of *available* units. Each unit can be **marked in use** (with where it is used) and later **returned to stock**. Desktop: **Serial numbers…** button and the **In use** tab; website: the item page and the **In use** page (all deployed units, searchable, with Return buttons). Admins can remove lost/broken units (reason is logged) and export all serials (`/export-serials.csv`). Searching an exact serial number finds its item.
* **QR codes** contain `<public address>/i/<SKU>`. Staff scan with the normal phone camera, sign in, and record **use** (with where it was used) or **add stock**. The home page searches by keyword and shows In stock / Low / Out of stock.
* **Users…** (Servers tab): add staff/admin accounts. Admins can create items, print labels and export; staff can search, use and add stock. Passwords: 10+ characters, stored salted PBKDF2-SHA256.
* Sign-in is always required. Failed logins lock the account/IP for 15 minutes; forms carry CSRF tokens; cookies are HttpOnly/SameSite (and Secure over HTTPS).
* Changing a server's address means reprinting its labels.

## Security / EDR notes
* Internet-facing servers deserve IT/security sign-off. The tunnel mode keeps the app on 127.0.0.1 with no inbound port.
* The app does not change the firewall or add startup entries. “Firewall help” shows the rule for IT.
* **Updates**: the app checks `github.com/<repo>/releases` for a newer version (at start and every 6 hours) and shows *Install now*; nothing installs without a click. Updates must carry a signature from the release key (ECDSA P-256, public key built into the app) or they are refused. Installing replaces only the program files (backup in `%LocalAppData%\InventoryServer\update\previous-version`, log in `update.log`); company data is never touched.

## Shipping an update
1. One time: `dotnet run --project ReleaseTool -- init <github-owner>/<repo>` – creates the signing key in `%AppData%\InventoryServer-release\signing-key.pem` (**back it up, keep it private**) and writes the repo + public key into `InventoryServer\Updater.cs`. Rebuild and install that build by hand once.
2. Each release: `.\release.ps1 V1.2.0 "What changed"` – versions are `Vmajor.minor.patch` and must go up (patch = fixes, minor = new features, major = big/breaking changes). It sets the version, runs the smoke test, builds, signs, and creates the GitHub release tagged `V1.2.0` (needs `gh auth login`; the repo's releases must be public). Every running copy then offers the update.
* Tunnel tokens and certificate passwords are saved encrypted for the current Windows user (DPAPI).
* Sign the exe with a code-signing certificate so IT can allow-list it by publisher.

## Files
* Settings: `%LocalAppData%\InventoryServer\servers.json`; each server's data (database, logo, backups) in its own folder. Existing single-server installs are migrated automatically (database stays in place; the old shared password is replaced by user accounts – you are asked to create an admin on first start).
* Rebuild: `.\build.ps1` – writes the single-file `InventoryServer.exe` into this folder (needs the .NET 8 SDK; exit the app first). The version comes from `<Version>` in `InventoryServer\InventoryServer.csproj`.
* Tests: `dotnet run --project SmokeTest` (end-to-end checks, uses a temp folder); `dotnet run --project SmokeTest -- --gui <folder>` saves screenshots of the wizard pages.
