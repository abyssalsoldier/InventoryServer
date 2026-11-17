# Inventory Server

Double-click `publish\InventoryServer.exe` (no install, no command line).

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
* The app does not change the firewall, add startup entries or download anything. “Firewall help” shows the rule for IT.
* Tunnel tokens and certificate passwords are saved encrypted for the current Windows user (DPAPI).
* Sign the exe with a code-signing certificate so IT can allow-list it by publisher.

## Files
* Settings: `%LocalAppData%\InventoryServer\servers.json`; each server's data (database, logo, backups) in its own folder. Existing single-server installs are migrated automatically (database stays in place; the old shared password is replaced by user accounts – you are asked to create an admin on first start).
* Rebuild: `dotnet publish InventoryServer -c Release -r win-x64 --self-contained -o publish` (needs the .NET 8 SDK).
* Tests: `dotnet run --project SmokeTest` (end-to-end checks, uses a temp folder); `dotnet run --project SmokeTest -- --gui <folder>` saves screenshots of the wizard pages.
