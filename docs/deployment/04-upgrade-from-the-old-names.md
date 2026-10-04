# Guide 4: Upgrade an existing install to the Vantage names (one time)

Audience: anyone who ran Vantage before 4 Oct 2026 and wants to keep their local data (decision C48).
A brand-new install does not need this guide.

> Status: written from the compose file and `deploy/db/init.sql`. The steps have not been run against a real
> pre-C48 install (the cloud workspace this was written in has no Docker or SQL Server). Your old volumes are only
> read, never changed, so you can go back (section 6).

## 1. What changed

| Before | Now |
| --- | --- |
| Database `RDDashboard` | `Vantage` |
| SQL login and user `rd_app` | `vantage_app` (same password: `APP_DB_PASSWORD` in `deploy\.env`, which does not change) |
| Docker volumes `rd-dashboard_sqldata`, `rd-dashboard_appdata` | `vantage_sqldata`, `vantage_appdata` |
| Data Protection names and User Secrets ID (code) | `Vantage`, `Vantage.Secrets.v1`, `vantage-api` |

Your data is kept in two steps: you copy the two old volumes into the new ones (section 3), and `deploy/db/init.sql`
renames the old database and login in place the first time the new version starts (it only does so when `Vantage`
does not exist yet, so running it again is harmless).

**What you will notice afterwards**
- Saved tenant secrets (Power BI client secret, Tableau Connected App secret) read as not set. They were encrypted
  under the old Data Protection names. A Super Admin enters them again (section 5).
- Each person's remembered layout (menu collapsed, Home view) and the local sign-in choice reset once.
- GenAI links issued before the upgrade stop working (they expire within the hour anyway); open the dashboard again.
- Dashboards, users, groups, requests, audit log and uploaded files are all kept.

## 2. Before you start

- Run these from PowerShell in the repository folder (for example `C:\Code\Vantage`).
- Keep `deploy\.env` as it is.
- Make sure Docker Desktop is running.

## 3. Upgrade with Docker

**Step 1. Stop the stack.** This keeps all data and makes sure SQL Server shuts down cleanly.

```powershell
docker compose -f deploy/docker-compose.yml down
```

**Step 2. Get the new code.**

```powershell
git pull
```

**Step 3. Copy the old volumes into the new ones.** The first two lines create the new volumes with the labels
Docker Compose expects (so it does not warn); the last two copy everything, keeping file ownership. The old
volumes are only read.

```powershell
docker volume create --label com.docker.compose.project=vantage --label com.docker.compose.volume=sqldata vantage_sqldata
docker volume create --label com.docker.compose.project=vantage --label com.docker.compose.volume=appdata vantage_appdata
docker run --rm -v rd-dashboard_sqldata:/from -v vantage_sqldata:/to alpine sh -c "cp -a /from/. /to/"
docker run --rm -v rd-dashboard_appdata:/from -v vantage_appdata:/to alpine sh -c "cp -a /from/. /to/"
```

If Docker says a `vantage_*` volume already exists, you started the new version before copying. Check that it holds
nothing you need (it would only contain a fresh, empty database), remove both with
`docker volume rm vantage_sqldata vantage_appdata`, and repeat step 3.

**Step 4. Start the new version.**

```powershell
docker compose -f deploy/docker-compose.yml up -d --build
```

**Step 5. Check the rename worked.**

```powershell
docker compose -f deploy/docker-compose.yml logs db-init
```

The log should end with `Vantage ready: 20 HRMS rows.` (the number can differ). Then open
http://localhost:8080/api/health: it should show `{"status":"ok","database":"ok"}`. Sign in to the Admin Portal and
check that your dashboards, users and groups are there.

## 4. Upgrade without Docker (Visual Studio, local SQL Server)

1. Stop the API.
2. Run the database script again. It renames `RDDashboard` to `Vantage` and `rd_app` to `vantage_app` in place and
   sets the login's password to the one you pass:
   ```powershell
   sqlcmd -S localhost -E -C -v APP_PASSWORD="<the same password as before>" -i deploy\db\init.sql
   ```
3. In Visual Studio, right-click **Vantage.Api**, **Manage User Secrets**, and paste your settings again (the ID is
   now `vantage-api`, so the old file is not used). Use `Database=Vantage` in the connection string, and
   `User Id=vantage_app` if you sign in with the SQL login.
4. Keep the `DataProtection:KeysPath` and `Storage:LocalPath` folders as they are, then start the API.
5. Re-enter tenant secrets (section 5).

## 5. Re-enter the tenant secrets

In the Admin Portal go to **Tenants**. For each Power BI and Tableau Server tenant: open it, enter the client
secret (Power BI) or the Connected App secret (Tableau) again, save, and click **Verify Connection**. Nothing else
about a tenant is lost: IDs, workspaces and published dashboards stay.

## 6. Going back

The old volumes were never changed, so you can return to the previous version at any time:

```powershell
docker compose -f deploy/docker-compose.yml down
git checkout <the commit you were on before the upgrade>
docker compose -f deploy/docker-compose.yml up -d --build
```

Anything you did in the new version after the copy stays only in the new volumes.

## 7. Clean up (after a few days of normal use)

When you are sure everything is fine, free the disk space:

```powershell
docker volume rm rd-dashboard_sqldata rd-dashboard_appdata
```

The file names inside SQL Server still start with the old name (`RDDashboard.mdf`). That is cosmetic and harmless.

## 8. Troubleshooting

| Symptom | Likely cause and fix |
| --- | --- |
| `db-init` fails on `ALTER DATABASE` | Something is still connected: run `docker compose -f deploy/docker-compose.yml down` and start again. Also close SSMS or Azure Data Studio connections to the old database |
| Login failed for `vantage_app` | `APP_DB_PASSWORD` in `deploy\.env` differs from the password the login got when it was renamed. Put the old value back in `deploy\.env`, or set the login's password to the current value: `docker exec vantage-db /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "<sa password>" -C -Q "ALTER LOGIN vantage_app WITH PASSWORD = N'<APP_DB_PASSWORD>'"`, then restart the API with `docker restart vantage-api` |
| The portal shows an empty database with only sample data | The copy in step 3 was skipped. Stop, remove the new volumes, repeat step 3 |
| Tenants show "secret not set" | Expected: re-enter them (section 5) |
