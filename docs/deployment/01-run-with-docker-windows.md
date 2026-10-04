# Guide 1: Run Vantage with Docker on a new Windows 11 PC

Audience: someone with a clean Windows 11 PC and nothing installed. Result: the whole local stack running
(SQL Server, API, Admin Portal, User Portal, GenAI origin, mail catcher) with sample data.

> Status: written from the repository's compose file and Dockerfiles. It has not been run end to end on a fresh
> Windows 11 machine (the cloud workspace this was written in has no Docker). If a step differs on your PC, tell
> us and we will correct the guide.

## 1. What you need

| Item | Minimum | Notes |
| --- | --- | --- |
| Windows 11 | Home, Pro or Enterprise, 64-bit | Docker Desktop uses WSL 2, which Home supports |
| Memory | 16 GB recommended (8 GB works, tight) | SQL Server alone wants at least 2 GB |
| Disk | 20 GB free | Images, build caches and the database |
| Virtualisation | Switched on in BIOS/UEFI | Check: Task Manager, Performance, CPU shows "Virtualization: Enabled" |
| Internet | Needed for the first build | Pulls base images and npm/NuGet packages |
| Access to the repo | GitHub `SupunSam/Vantage` | You sign in the first time you clone |

**Docker Desktop licence:** Docker Desktop is free only for small companies, education and personal use. A company the
size of RRD normally needs a paid Docker subscription. Check with IT before installing. Alternatives that run the
same compose file are Rancher Desktop (dockerd mode) or Docker Engine inside WSL 2; this guide uses Docker Desktop.

## 2. Install the tools (once)

Open **PowerShell** (Start menu, type PowerShell, open it; no need for Administrator for the winget lines, but the
Windows may ask for approval).

```powershell
winget install --id Git.Git -e
winget install --id Docker.DockerDesktop -e
```

If `winget` is not available, install both from their websites instead (git-scm.com and docker.com).

1. Restart Windows when the installer asks.
2. Start **Docker Desktop** from the Start menu. Accept the licence terms. When asked, choose the **WSL 2** backend.
   If it says WSL is not installed, run `wsl --install` in an Administrator PowerShell and restart.
3. Wait until the Docker whale icon says "Engine running".
4. Check in a new PowerShell window:
   ```powershell
   git --version
   docker --version
   docker compose version
   ```

### Optional: stop Docker taking all your memory

Create the file `C:\Users\<you>\.wslconfig` with:

```ini
[wsl2]
memory=6GB
processors=4
```

Then run `wsl --shutdown` and start Docker Desktop again.

## 3. Get the code

Use a short folder path so Windows path-length limits never get in the way.

```powershell
mkdir C:\Code
cd C:\Code
git clone https://github.com/SupunSam/Vantage.git
cd Vantage
```

A browser window opens the first time to sign in to GitHub. Do not paste passwords or tokens into any chat.

## 4. Create your settings file

The passwords are not in git. Create them once:

```powershell
copy deploy\.env.example deploy\.env
notepad deploy\.env
```

Edit the three values and save:

| Setting | What to put |
| --- | --- |
| `MSSQL_SA_PASSWORD` | A new password for the SQL Server `sa` account |
| `APP_DB_PASSWORD` | A new password for the application's `rd_app` database login |
| `SUPER_ADMIN_EMAIL` | The email of the first Super Admin. It must exist in the dummy HRMS list to get a name. The default `nimal.perera@rrd.com` is fine for testing |

Password rules: at least 8 characters with upper case, lower case, a digit and a symbol. Avoid `"` `'` `$` and
spaces, which can break the connection strings. Never commit `deploy\.env` (git already ignores it).

If your company network inspects HTTPS traffic and builds fail with certificate errors, copy the company root
certificate (a `.crt` file) into `deploy\certs\` before you start.

## 5. Start everything

From `C:\Code\Vantage`:

```powershell
docker compose -f deploy/docker-compose.yml up -d --build
```

The first run takes roughly 5 to 15 minutes (it builds the API and both portals and pulls SQL Server). Later starts
take under a minute.

Check it:

```powershell
docker compose -f deploy/docker-compose.yml ps
```

Expected: `vantage-db` healthy; `vantage-api`, `vantage-admin`, `vantage-user`, `vantage-genai` and `vantage-mail`
running. The `db-init` container shows **Exited (0)**. That is correct: it creates the database once and stops.

Quick health check: open http://localhost:8080/api/health. You should see `{"status":"ok","database":"ok"}`.
The API creates its tables and seeds roles, modules, settings and sample people the first time it starts.

## 6. Open the portals

| What | Address |
| --- | --- |
| Admin Portal | http://localhost:8080 |
| User Portal | http://localhost:8081 |
| Emails Vantage sends (Mailpit) | http://localhost:8025 |
| GenAI dashboards | http://localhost:8082 (loads inside the portals; nothing to open by hand) |
| SQL Server (SSMS or Azure Data Studio) | `localhost,1433`, login `sa` or `rd_app` with the passwords from `deploy\.env` |

Sign in: choose **Login as Internal User**, then pick **Nimal Perera** (Super Admin). This picker is the local
stand-in for ADFS and Cognito. The two portals keep separate sessions, so sign in to each one.

## 7. Everyday commands

```powershell
cd C:\Code\Vantage
docker compose -f deploy/docker-compose.yml up -d --build   # start, or apply code changes
docker compose -f deploy/docker-compose.yml logs -f api     # watch the API log (Ctrl+C to stop watching)
docker compose -f deploy/docker-compose.yml down            # stop, keep all data
docker compose -f deploy/docker-compose.yml down -v         # stop AND delete everything (fresh start)
git pull                                                    # get new code, then run the first line again
```

**Warning about `down -v`:** it deletes the database, uploaded files and the encryption keys. Tenant client secrets
(Power BI, Tableau) stored in the database become unreadable and must be entered again. The data volumes are named
`rd-dashboard_sqldata` and `rd-dashboard_appdata` on purpose; do not rename them.

Data does not travel between PCs. A new PC starts with an empty portal plus the sample data.

## 8. Optional: malware scan for GenAI uploads

```powershell
docker compose -f deploy/docker-compose.yml --profile scan up -d --build
```

ClamAV downloads signatures for a few minutes on first start. Then in the Admin Portal go to **Configuration, GenAI
Config** and set **Malware scanner host** to `clamav`. Until it is ready, uploads are refused.

## 9. Connect Power BI (needs Azure access, not part of Docker)

Publishing a Power BI dashboard needs a real tenant. In the Admin Portal, **Tenants, Add Tenant, Power BI**: the
Azure tenant ID, the service principal's client ID and client secret, and the workspaces. Click **Verify
Connection**. The service principal must be allowed to use Power BI APIs and be Admin/Member/Contributor on the
workspace, and the workspace must be on the F64 capacity. Tableau Public and GenAI dashboards need no tenant.

## 10. Troubleshooting

| Symptom | Likely cause and fix |
| --- | --- |
| Docker Desktop says "Virtualization is disabled" | Switch on Intel VT-x / AMD-V (SVM) in BIOS/UEFI |
| `docker` is not recognised | Close and reopen PowerShell after installing; make sure Docker Desktop is running |
| `Set MSSQL_SA_PASSWORD in deploy/.env` error | `deploy\.env` is missing or in the wrong folder; it must be `deploy\.env` |
| `db-init` exits with an error | Password too weak or contains special characters; fix `.env`, then `docker compose -f deploy/docker-compose.yml down -v` and start again |
| Port already in use (8080, 8081, 8082, 8025, 1433) | Another program uses it. Stop it, or a local SQL Server on 1433 (stop the "SQL Server" Windows service) |
| Build fails with certificate errors | Company HTTPS inspection: add the root `.crt` to `deploy\certs\` and rebuild |
| Portal loads but API calls fail | `docker compose -f deploy/docker-compose.yml logs api`; usually the database was not ready, run the start command again |
| Very slow or SQL Server restarts | Not enough memory for WSL; raise `memory=` in `.wslconfig` |
| Emails do not arrive | Open http://localhost:8025; nothing is sent outside your PC. Check Admin Configuration, Settings, "emails are sent" |
