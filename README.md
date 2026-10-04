# Vantage

Vantage (formerly the RD Dashboard portal) is the corporate BI portal for Power BI and Tableau dashboards.
`CLAUDE.md` describes the components, structure, rules and conventions; `docs/requirements.md` holds the
requirements and decisions.

Everything runs in Docker: SQL Server, the API, the Admin Portal and the User Portal.

- **Admin Portal: http://localhost:8080**. Sign in as **Nimal Perera** (Super Admin).
- **User Portal: http://localhost:8081**. The home page shows the dashboards the signed-in user can open.
- **Emails: http://localhost:8025** (Mailpit). Every email Vantage sends lands here instead of a real inbox.

The sign-in picker stands in for ADFS and Cognito in this local build. Both portals share one layout: a sidebar
that collapses to icons (the menu button at the top left), with notifications and the user menu at the top right.

## What's in this step

Work through the **Setup** page in order:

1. **Roles and permissions** — the built-in roles (Super Admin, Dashboard Owner, Dashboard User) and your own roles,
   with View or Edit per module.
2. **Users** — add internal users by email (names, department and manager come from the HRMS table) and external
   users; add many by pasting emails or uploading Excel; edit roles and status; run the HRMS sync.
3. **Tenants** — add the Power BI tenant (Azure tenant ID, client ID, client secret, workspaces), then
   **Verify connection**. It checks the service principal can sign in, is allowed to use Power BI APIs, has
   Admin/Member/Contributor on each workspace, and that each workspace is on the F64 capacity.
   The secret is stored encrypted and never shown again.
4. **Categories** — the Primary / Secondary / Tertiary tree. Names are unique within their parent; a category that
   holds dashboards or sub-categories can't be deleted.
5. **Publish dashboard** — upload a .pbix into a verified workspace with its category, tags, audience,
   classification and an optional 16:9 thumbnail (PNG or JPG, at least 640 x 360, up to 1 MB). The service
   principal imports it, the published report ID is stored, and the owners go into the dashboard's default group.
6. **Dashboards Master** — every dashboard with its category, RLS flag, owners and groups. Open one to edit its
   details and thumbnail, manage its groups' RLS values, check RLS with Power BI, and preview it.
   Only dashboards with RLS can have more than the default group; on a dashboard without RLS the RLS value is kept
   but not used.

7. **Access groups** — every dashboard's groups in one list. Open a group to add people (by name, pasted emails or
   an Excel file; only existing portal users can be added), move them to another group of the same dashboard, remove them,
   copy members from another group, clone the group, change its RLS value, rename it or deactivate it. Each group
   shows its history.

8. **Access requests** — every request across all dashboards, by status. Super Admins can approve or reject on the
   owners' behalf (and are the only ones who can while a dashboard's ownership is under review).

In the User Portal, the home page shows thumbnail cards (newest first), a category view and a sortable list, with
search and up to 12 pins. The **Dashboard Catalogue** lists every dashboard the user may see; for ones they can't open
they click **Request Access** and give a reason. Owners get a bell notification and an email, and decide under
**As an Owner → Access Requests**: on dashboards with RLS they choose the access group (which decides the data the
person sees), otherwise the person joins the default group. The requester is notified and emailed either way.
Personal folders and monthly owner reviews come in later steps.

## Starting and stopping

In a terminal in this folder:

```
docker compose -f deploy/docker-compose.yml up -d --build     # start (or apply changes)
docker compose -f deploy/docker-compose.yml down              # stop, keeping all data
docker compose -f deploy/docker-compose.yml down -v           # stop and delete all data (fresh start)
```

The first start takes a few minutes while the images build. In Docker Desktop the stack shows as **vantage** with
six containers: `vantage-db`, `db-init` (runs once and stops, which is expected), `vantage-api`, `vantage-admin`,
`vantage-user` and `vantage-mail`.

## Settings

`deploy/.env` holds the SQL Server passwords and the bootstrap Super Admin's email. It's created once and is never
committed. `deploy/.env.example` shows the format.

| Port | What |
| --- | --- |
| 8080 | Admin Portal (and the API under /api) |
| 8081 | User Portal (and the API under /api) |
| 8082 | GenAI dashboards (framed by the portals; signed links only) |
| 8025 | Mailpit: the emails Vantage has sent |
| 1433 | SQL Server, for SSMS or Azure Data Studio: `localhost,1433`, login `sa` or `rd_app` with the passwords in `deploy/.env` |

If your network inspects HTTPS traffic and **Verify connection** says the certificate isn't trusted, put your
company's root certificate (.crt) in `deploy/certs` and run the start command again.

## For developers

| Path | What |
| --- | --- |
| `backend/src/Vantage.Domain` | Entities, enums, rules |
| `backend/src/Vantage.Infrastructure` | EF Core model and migrations, Power BI client, tenant verifier, services, email |
| `backend/src/Vantage.Api` | ASP.NET Core API |
| `backend/tests/Vantage.Tests` | Unit tests and SQL Server tests |
| `frontend/admin-portal` | Admin Portal (React, Vite) |
| `frontend/user-portal` | User Portal |
| `frontend/shared` | Layout, sign-in, icons and API client shared by both portals (`@vantage/shared`) |
| `deploy/` | Docker Compose, Dockerfiles, nginx config (shared by both portals), database init script |
| `docs/` | Requirements snapshot and handoff notes |

Tests: `cd backend && dotnet test Vantage.slnx`. The SQL Server tests run when `VANTAGE_TEST_SQL` is set, for
example `Server=localhost,1433;User Id=sa;Password=<MSSQL_SA_PASSWORD>;TrustServerCertificate=True`; each creates
and drops its own database. GitHub Actions (`.github/workflows/ci.yml`) runs the same on every push and pull request.
