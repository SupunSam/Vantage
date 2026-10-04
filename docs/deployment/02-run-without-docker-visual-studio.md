# Guide 2: Run Vantage without Docker, using Visual Studio Professional

Audience: a developer on Windows with Visual Studio Professional. Result: the API running under the Visual Studio
debugger, both portals running from Vite dev servers, SQL Server installed locally, and a way to run the tests.

> Status: written from the repository (project files, `Program.cs`, `launchSettings.json`, `init.sql`). It has not
> been run end to end on a Windows machine. The GenAI step in section 8 is the least certain; it is marked.

## 1. What changes compared with Docker

| Docker did this | You do this instead |
| --- | --- |
| `vantage-db` SQL Server container | Install SQL Server Developer (or use an existing server) and run `deploy\db\init.sql` once |
| `vantage-api` container | Run `Vantage.Api` from Visual Studio (http://localhost:5080) |
| Portals served by nginx on 8080/8081 | Vite dev servers on http://localhost:5173 (User) and http://localhost:5174 (Admin); they forward `/api` to 5080 |
| Mailpit container | Optional: the Mailpit Windows binary, or switch emails off |
| GenAI nginx on 8082 | Point the GenAI address at the API itself (section 8) |
| Connection string, key and file folders from compose | Set with Visual Studio **User Secrets** |

The API only starts in the **Development** environment today (that is what turns on the dev sign-in picker). The
supplied launch profile already sets it. In Development the API also applies the database migrations and seeds
reference data at start.

## 2. Install the tools

| Tool | Version | Why |
| --- | --- | --- |
| Visual Studio **2026** Professional | 18.x | The projects target **.NET 10**. Visual Studio 2022 cannot build .NET 10 projects. If you only have 2022, either upgrade or install the .NET 10 SDK and use `dotnet` from a terminal |
| VS workloads | **ASP.NET and web development**; add **Node.js development** if you want to run the portals from inside VS | Installed through the Visual Studio Installer, Modify |
| .NET 10 SDK | 10.0.x | Normally installed by the workload; check `dotnet --list-sdks` |
| Node.js | 22 LTS (20 or newer works) | Builds and runs the React portals. Includes npm |
| SQL Server | 2019, 2022 or later, Developer edition | Free for development. Express also works. Plus SQL Server Management Studio (SSMS) to run scripts |
| Git for Windows | latest | To clone |
| Mailpit (optional) | latest | Catches emails locally, as in Docker |

```powershell
winget install --id Git.Git -e
winget install --id OpenJS.NodeJS.LTS -e
winget install --id Microsoft.SQLServer.2022.Developer -e   # or download from microsoft.com
winget install --id Microsoft.SQLServerManagementStudio -e
```

Check: `node --version`, `npm --version`, `dotnet --list-sdks` (must list 10.0.x).

## 3. Get the code

```powershell
mkdir C:\Code
cd C:\Code
git clone https://github.com/SupunSam/Vantage.git
cd Vantage
```

## 4. Create the database

The script creates the `RDDashboard` database, the `rd_app` login and the dummy HRMS table with sample people.
(The database and login keep these names on purpose; do not rename them.)

Run it from PowerShell, choosing your own password for `rd_app` (8+ characters with upper case, lower case, a digit
and a symbol; avoid quotes and `$`):

```powershell
sqlcmd -S localhost -E -C -v APP_PASSWORD="<choose-a-password>" -i deploy\db\init.sql
```

`-E` means "sign in as me with Windows authentication". If SQL Server is a named instance use `-S localhost\SQLEXPRESS`.
Do not run the script by pasting it into an SSMS query window; it needs the `APP_PASSWORD` value, which `sqlcmd`
supplies. The last line it prints is `RDDashboard ready: 20 HRMS rows.`

## 5. Give the API its settings (User Secrets)

Settings with passwords must not go into files that are committed. The API project already has a User Secrets ID.

1. In Visual Studio open `C:\Code\Vantage\backend\Vantage.slnx`.
2. In Solution Explorer, right-click the **Vantage.Api** project, then **Manage User Secrets**.
3. Paste this into the `secrets.json` that opens, then adjust:

```json
{
  "ConnectionStrings": {
    "Default": "Server=localhost;Database=RDDashboard;Trusted_Connection=True;TrustServerCertificate=True"
  },
  "DataProtection": { "KeysPath": "C:\\VantageData\\keys" },
  "Storage": { "LocalPath": "C:\\VantageData\\files" },
  "Bootstrap": { "SuperAdminEmail": "nimal.perera@rrd.com" },
  "Email": {
    "SmtpHost": "localhost",
    "SmtpPort": 1025,
    "FromAddress": "vantage@rrd.com",
    "UserPortalUrl": "http://localhost:5173",
    "AdminPortalUrl": "http://localhost:5174"
  }
}
```

Notes:

- The connection string above uses your Windows login. To use the `rd_app` login instead:
  `Server=localhost;Database=RDDashboard;User Id=rd_app;Password=<the password from step 4>;TrustServerCertificate=True;Encrypt=True`
  (this needs the SQL Server to allow SQL logins, "mixed mode").
- `DataProtection:KeysPath` holds the keys that encrypt stored tenant secrets (Power BI and Tableau). Create the
  folder and **keep it**: if the keys are lost, saved tenant secrets cannot be read and must be entered again.
- `Storage:LocalPath` holds uploaded .pbix files, GenAI files and thumbnails. If you leave it out, files go to
  your Windows local application data folder under `Vantage\files`.
- The email values point at Mailpit (SMTP 1025, web screen 8025). Without Mailpit the emails stay in the outbox and
  the sender retries; either install Mailpit or switch emails off in Admin Configuration, Settings.
- Never put real secrets in `appsettings.json` or commit them. User Secrets live in your Windows profile, outside the repo.

Create the two folders: `mkdir C:\VantageData\keys, C:\VantageData\files`.

## 6. Run the API

1. Set **Vantage.Api** as the startup project (right-click, Set as Startup Project).
2. In the toolbar choose the **http** launch profile (it sets `ASPNETCORE_ENVIRONMENT=Development` and
   `http://localhost:5080`).
3. Press **F5** (debug) or **Ctrl+F5** (run without debugging).
4. The first start applies the migrations (takes a few seconds) and seeds roles, modules, settings and the bootstrap
   Super Admin. Watch the Output window.
5. Check http://localhost:5080/api/health. Expected: `{"status":"ok","database":"ok"}`.

If it fails with `ConnectionStrings:Default is not set`, the User Secrets were not saved, or the environment is not
Development.

## 7. Run the portals

The portals are plain Node projects. Run them from a terminal (the Visual Studio **Terminal** window,
View, Terminal, works; so does Windows Terminal):

```powershell
cd C:\Code\Vantage\frontend
npm ci
npm run dev:user      # User Portal  http://localhost:5173   (leave running)
```

Open a second terminal:

```powershell
cd C:\Code\Vantage\frontend
npm run dev:admin     # Admin Portal http://localhost:5174   (leave running)
```

Both forward `/api` to the API on 5080, so start the API first. Sign in: **Login as Internal User**, then
**Nimal Perera**. Each portal keeps its own session.

Editing a `.tsx` file reloads the browser automatically. Production-style build check: `npm run typecheck` then
`npm run build` from the `frontend` folder.

Optional mail catcher: download the Windows build of Mailpit, run `mailpit.exe`, and read emails at http://localhost:8025.

## 8. GenAI dashboards without the GenAI nginx (less certain)

In Docker a separate nginx on port 8082 serves GenAI files so their script cannot touch the portal. Without it,
the API itself serves the same content at `/genai/<token>`, and the portal builds the link as `<GenAI web
address>/<token>`. So set the web address to the API's own `/genai` path on a *different host name*, which the
browser treats as a different origin:

1. In the Admin Portal open **Configuration, GenAI Config**.
2. **GenAI web address**: `http://127.0.0.1:5080/genai` (the portals use `localhost`, this uses `127.0.0.1`).
3. **Portals allowed to show GenAI dashboards**: `http://localhost:5173 http://localhost:5174`.
4. Save. The change applies at once.

This is derived from reading the code; it has not been run. Never serve GenAI files through the portals' own
address in any shared environment: the security design relies on the separate origin (see `docs/genai-security.md`).
For a faithful test, run the Docker stack (Guide 1).

## 9. Run the tests

Tests that do not need a database run anywhere. About 143 tests need SQL Server and are skipped unless you set
`VANTAGE_TEST_SQL`. Each test class creates and deletes its own throwaway database, so the login needs permission to
create databases.

1. Set a user environment variable (Windows Settings, search "environment variables"), then **restart Visual Studio**:
   - Name: `VANTAGE_TEST_SQL`
   - Value (SQL login): `Server=localhost,1433;User Id=sa;Password=<sa password>;TrustServerCertificate=True`
   - Windows login may also work (`Server=localhost;Integrated Security=True;TrustServerCertificate=True`); it is
     untested.
2. In Visual Studio: **Test, Test Explorer, Run All Tests**. Or from `C:\Code\Vantage\backend`: `dotnet test Vantage.slnx`.

All tests must pass before a change is done; the CI also runs them against SQL Server.

## 10. Common tasks

| Task | How |
| --- | --- |
| New database migration | From `C:\Code\Vantage`: `dotnet tool restore` then `dotnet ef migrations add <Name> -p backend/src/Vantage.Infrastructure -s backend/src/Vantage.Api`. Never rename an existing migration |
| Start with an empty database | Stop the API, drop `RDDashboard` in SSMS, run step 4 again, start the API |
| Reset sample data only | Re-run `init.sql` (it is safe to run more than once and only fills an empty HRMS table) |
| Debug the API | F5 with breakpoints. React code: use the browser's developer tools |
| Run a background job now | Admin Portal, **Scheduled Jobs**, Run Now (the scheduler also runs inside the API while it is up) |

## 11. Troubleshooting

| Symptom | Likely cause and fix |
| --- | --- |
| Solution will not load, or "SDK not found" | Visual Studio 2022 or missing .NET 10 SDK; use VS 2026 or install the SDK |
| Login failed for user | Wrong connection string or SQL login not enabled; test it in SSMS first |
| `Cannot open database RDDashboard` | Step 4 was not run on this SQL Server instance |
| `The certificate chain was issued by an authority that is not trusted` | Add `TrustServerCertificate=True` to the connection string |
| Portal shows network errors | The API is not running on 5080, or the Vite proxy cannot reach it |
| Port 5173/5174/5080 in use | Another process uses it (Vite is set to fail rather than pick another port); close it |
| Saved tenant secrets stop working | The `DataProtection:KeysPath` folder changed or was deleted; re-enter the secrets |
