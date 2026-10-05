# Guide 5: Run Vantage locally with Visual Studio Code

Audience: a developer on Windows 11 who prefers Visual Studio Code (VS Code) to Visual Studio. Result: Vantage
running on your PC, started, debugged and tested from inside VS Code. There are two routes. Pick one:

| Route | What runs where | Use it when |
| --- | --- | --- |
| **A. Docker from VS Code** | Everything in containers, like Guide 1. VS Code is only your editor and terminal | You want the whole stack exactly as the project owner runs it, with the least setup |
| **B. No Docker** | SQL Server on Windows, the API under the VS Code debugger, both portals on Vite dev servers, like Guide 2 | You want breakpoints and live reload while you change code |

> Status: written from the repository (project files, `launchSettings.json`, `init.sql`, compose file). It has not been
> run end to end on a Windows machine. This is a local guide only; it does not cover publishing to a server or AWS
> (see Guide 3 for that plan).

Unlike Visual Studio (Guide 2), VS Code does not need a particular edition to build .NET 10, so you only need the
.NET 10 SDK.

## 1. Install the tools

Open PowerShell and install what you need (or download from each vendor's website):

```powershell
winget install --id Microsoft.VisualStudioCode -e
winget install --id Git.Git -e
winget install --id OpenJS.NodeJS.LTS -e
winget install --id Microsoft.DotNet.SDK.10 -e        # .NET 10 SDK; or download from dotnet.microsoft.com
```

| Also needed for | Install |
| --- | --- |
| Route A | Docker Desktop (`winget install --id Docker.DockerDesktop -e`), with the WSL 2 backend. See Guide 1, section 1, about the Docker Desktop licence |
| Route B | SQL Server Developer edition (`winget install --id Microsoft.SQLServer.2022.Developer -e`) and, to run scripts, `sqlcmd` (installed with it, or `winget install --id Microsoft.Sqlcmd -e`) |

Check in a new PowerShell window: `git --version`, `node --version` (20 or newer; 22 is what CI uses),
`dotnet --list-sdks` (must show a 10.0.x line).

### VS Code extensions

Open VS Code, press **Ctrl+Shift+X**, and install:

| Extension | ID | Why |
| --- | --- | --- |
| C# Dev Kit (installs C# with it) | `ms-dotnettools.csdevkit` | Build, debug, Test Explorer, understands `Vantage.slnx` (use a recent version) |
| Docker | `ms-azuretools.vscode-docker` | Route A: start, stop and read logs of the containers from the sidebar |
| SQL Server (mssql) | `ms-mssql.mssql` | Route B: browse and query the database inside VS Code (optional; SSMS works too) |

## 2. Get the code and open it

```powershell
mkdir C:\Code
cd C:\Code
git clone https://github.com/SupunSam/Vantage.git
code Vantage
```

In VS Code choose **Yes, I trust the authors** when asked. Open the built-in terminal with **Ctrl+`** (PowerShell).
All commands below run from the repository folder (`C:\Code\Vantage`) unless stated.

---

## Route A: run everything in Docker from VS Code

1. Start Docker Desktop and wait for "Engine running".
2. Create the settings file once:
   ```powershell
   copy deploy\.env.example deploy\.env
   code deploy\.env
   ```
   Change `MSSQL_SA_PASSWORD` and `APP_DB_PASSWORD` (8+ characters with upper case, lower case, a digit and a symbol;
   avoid quotes, `$` and spaces). Keep `SUPER_ADMIN_EMAIL` as is for testing. The file is git-ignored; never commit it.
3. Start the stack:
   ```powershell
   docker compose -f deploy/docker-compose.yml up -d --build
   ```
   The first run takes 5 to 15 minutes. The `db-init` container ends as **Exited (0)**; that is correct.
4. Open the **Docker** icon in the VS Code sidebar. Under **Containers** you will see `vantage-db`, `vantage-api`,
   `vantage-admin`, `vantage-user`, `vantage-genai` and `vantage-mail`. Right-click a container, then **View Logs**
   to read its output, or **Stop** or **Restart** it.
5. Open the portals:

   | What | Address |
   | --- | --- |
   | Admin Portal | http://localhost:8080 |
   | User Portal | http://localhost:8081 |
   | Emails (Mailpit) | http://localhost:8025 |
   | Health check | http://localhost:8080/api/health |

   Sign in: **Login as Internal User**, then **Nimal Perera**.

Day to day: after you change code, run the `up -d --build` line again. Stop with
`docker compose -f deploy/docker-compose.yml down` (data kept) or `down -v` (everything wiped, which also loses saved
tenant secrets). More detail, optional malware scanning and troubleshooting are in Guide 1.

Route A has no live reload or debugger: the portals are production builds served by nginx. For that, use Route B.

---

## Route B: run without Docker, with the debugger

### B1. Create the database (once)

Choose a password for the application login (8+ characters, upper, lower, digit and symbol) and run the script with
`sqlcmd`, which supplies the password to it:

```powershell
sqlcmd -S localhost -E -C -v APP_PASSWORD="<choose-a-password>" -i deploy\db\init.sql
```

`-E` signs in with your Windows account. For a named instance use `-S localhost\SQLEXPRESS`. It creates the `Vantage`
database, the `vantage_app` login and the sample HRMS people, and prints `Vantage ready: 20 HRMS rows.`
If you ran an older version, see Guide 4 instead: the script renames the old database for you.

### B2. Give the API its settings

The API reads secrets from User Secrets (stored in your Windows profile, outside the repository). Create the two data
folders, then set the values from the terminal:

```powershell
mkdir C:\VantageData\keys, C:\VantageData\files
dotnet user-secrets set "ConnectionStrings:Default" "Server=localhost;Database=Vantage;Trusted_Connection=True;TrustServerCertificate=True" --project backend/src/Vantage.Api
dotnet user-secrets set "DataProtection:KeysPath" "C:\VantageData\keys" --project backend/src/Vantage.Api
dotnet user-secrets set "Storage:LocalPath" "C:\VantageData\files" --project backend/src/Vantage.Api
dotnet user-secrets set "Email:UserPortalUrl" "http://localhost:5173" --project backend/src/Vantage.Api
dotnet user-secrets set "Email:AdminPortalUrl" "http://localhost:5174" --project backend/src/Vantage.Api
```

- The connection string uses your Windows login. To use the SQL login instead:
  `Server=localhost;Database=Vantage;User Id=vantage_app;Password=<the password from B1>;TrustServerCertificate=True;Encrypt=True`
  (SQL Server must allow SQL logins, "mixed mode").
- Keep the `C:\VantageData\keys` folder: it holds the keys that encrypt saved tenant secrets (Power BI, Tableau).
- Emails default to Mailpit on port 1025. Either run the Windows build of Mailpit (`mailpit.exe`, screen at
  http://localhost:8025) or switch emails off in the Admin Portal, Configuration, Settings.
- Check what is stored: `dotnet user-secrets list --project backend/src/Vantage.Api`.

### B3. Add the VS Code run and debug files (once)

VS Code reads `.vscode\launch.json` (run and debug) and `.vscode\tasks.json` (background tasks). The repository
ignores the `.vscode` folder, so you create these two files yourself. Create the folder `C:\Code\Vantage\.vscode`
and add:

`.vscode\launch.json`
```json
{
  "version": "0.2.0",
  "configurations": [
    {
      "name": "Vantage API",
      "type": "coreclr",
      "request": "launch",
      "preLaunchTask": "build-api",
      "program": "${workspaceFolder}/backend/src/Vantage.Api/bin/Debug/net10.0/Vantage.Api.dll",
      "cwd": "${workspaceFolder}/backend/src/Vantage.Api",
      "env": {
        "ASPNETCORE_ENVIRONMENT": "Development",
        "ASPNETCORE_URLS": "http://localhost:5080"
      },
      "stopAtEntry": false
    }
  ]
}
```

`.vscode\tasks.json`
```json
{
  "version": "2.0.0",
  "tasks": [
    {
      "label": "build-api",
      "type": "process",
      "command": "dotnet",
      "args": ["build", "${workspaceFolder}/backend/src/Vantage.Api/Vantage.Api.csproj"],
      "problemMatcher": "$msCompile"
    },
    {
      "label": "dev-user",
      "type": "shell",
      "command": "npm run dev:user",
      "options": { "cwd": "${workspaceFolder}/frontend" },
      "isBackground": true,
      "problemMatcher": []
    },
    {
      "label": "dev-admin",
      "type": "shell",
      "command": "npm run dev:admin",
      "options": { "cwd": "${workspaceFolder}/frontend" },
      "isBackground": true,
      "problemMatcher": []
    },
    {
      "label": "portals",
      "dependsOn": ["dev-user", "dev-admin"],
      "problemMatcher": []
    }
  ]
}
```

`ASPNETCORE_ENVIRONMENT=Development` matters: the API only starts in Development today (that also switches on the dev
sign-in picker, applies the database migrations and seeds roles, modules and settings at start).

### B4. Start the API

1. Install the portals' packages once: in the terminal run `cd frontend`, then `npm ci`, then `cd ..`.
2. Open **Run and Debug** (**Ctrl+Shift+D**), choose **Vantage API**, press **F5**. (**Ctrl+F5** runs without the
   debugger.)
3. The first start builds, applies the migrations and seeds data. Watch the **Debug Console**.
4. Check http://localhost:5080/api/health. Expected: `{"status":"ok","database":"ok"}`.

Breakpoints work in any `.cs` file. Alternative without the debugger and with hot reload:
`dotnet watch run --project backend/src/Vantage.Api` (set `ASPNETCORE_ENVIRONMENT` to `Development` first with
`$env:ASPNETCORE_ENVIRONMENT="Development"`).

### B5. Start the portals

Press **Ctrl+Shift+P**, choose **Tasks: Run Task**, then **portals**. This starts both Vite servers in terminal panels:

| Portal | Address |
| --- | --- |
| User Portal | http://localhost:5173 |
| Admin Portal | http://localhost:5174 |

Both forward `/api` to the API on port 5080, so start the API first. Sign in with **Login as Internal User**, then
**Nimal Perera**; each portal keeps its own session. Editing a `.tsx` file reloads the browser at once. To stop a
portal, click its terminal panel and press **Ctrl+C**.

Debugging React code: use the browser's developer tools (F12).

### B6. Run the tests

Tests that do not need a database run anywhere. About 143 tests need SQL Server and are skipped unless
`VANTAGE_TEST_SQL` is set. Each test class creates and drops its own throwaway database, so the login must be allowed
to create databases. In the terminal:

```powershell
$env:VANTAGE_TEST_SQL = "Server=localhost,1433;User Id=sa;Password=<sa password>;TrustServerCertificate=True"
dotnet test backend/Vantage.slnx
```

A Windows-login connection string (`Server=localhost;Integrated Security=True;TrustServerCertificate=True`) may also
work; it is untested. You can also use the **Testing** beaker icon in the sidebar (C# Dev Kit's Test Explorer): set the
variable first in the same terminal and start VS Code from it (`code .`) so the variable is inherited. Frontend checks:
`cd frontend`, then `npm run typecheck` and `npm run build`.

All tests must pass before a change is done; CI runs the SQL ones too.

### B7. GenAI dashboards without the GenAI nginx

Follow Guide 2, section 8 (it points the GenAI web address at the API itself on a different host name,
`127.0.0.1`, so the browser still treats it as a separate origin). It was worked out from the code and is untested.
For a faithful test of GenAI dashboards use Route A.

---

## Common tasks (Route B)

| Task | How |
| --- | --- |
| New database migration | `dotnet tool restore`, then `dotnet ef migrations add <Name> -p backend/src/Vantage.Infrastructure -s backend/src/Vantage.Api`. Never rename an existing migration |
| Start with an empty database | Stop the API, drop `Vantage` (SSMS or the mssql extension), run B1 again, start the API |
| Run a background job now | Admin Portal, **Scheduled Jobs**, Run Now |
| See emails | Mailpit at http://localhost:8025 (if you started it) |
| Stop everything | Shift+F5 for the API, Ctrl+C in each portal terminal |

## Troubleshooting

| Symptom | Likely cause and fix |
| --- | --- |
| `dotnet` not recognised, or SDK 10 missing | Install the .NET 10 SDK and open a new terminal; check `dotnet --list-sdks` |
| C# Dev Kit does not load the solution | Update the extension, then **Ctrl+Shift+P**, **.NET: Open Solution** and pick `backend/Vantage.slnx`. You can always build from the terminal with `dotnet build backend/Vantage.slnx` |
| F5 says it cannot find `Vantage.Api.dll` | The build did not run or failed; run `dotnet build backend/src/Vantage.Api/Vantage.Api.csproj` and read the errors. Make sure `launch.json` points at `net10.0` |
| `ConnectionStrings:Default is not set` | User Secrets missing, or the environment is not Development (check `launch.json`) |
| `Only the Development environment is configured in this build` | `ASPNETCORE_ENVIRONMENT` is not `Development`; that is a limitation of the current build until real sign-in is built |
| Login failed / cannot open database `Vantage` | B1 was not run on this SQL Server instance, or the connection string is wrong; test it in `sqlcmd` or the mssql extension first |
| Certificate chain not trusted (SQL) | Add `TrustServerCertificate=True` to the connection string |
| Portal shows network errors | The API is not running on 5080, or start it before the portals |
| Port 5080, 5173 or 5174 in use | Another process has it. Vite is set to fail instead of picking another port; close the other process |
| Saved tenant secrets stop working | The `DataProtection:KeysPath` folder changed or was deleted; re-enter them on the Tenants page |
