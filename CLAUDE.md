# CLAUDE.md: Vantage

Guidance for Claude (and people) working in this repo. Read this first, then `docs/requirements.md` before
changing behaviour.

## What Vantage is

Vantage (formerly "RD Dashboard", PRF#1484) is the corporate BI portal. It replaces a .NET 4.8 MVC portal.
- It registers Power BI, Tableau Server and GenAI (single HTML) dashboards.
- It controls who sees each dashboard through access groups.
- It embeds Power BI on F64 capacity, so viewers need no Power BI licence.

Sizing: about 1,000 users and 100 to 150 dashboards, 80 to 85% of them Power BI.

Requirements:
- The live requirements and decisions log is a Claude Doc:
  https://claude.ai/code/artifact/23091f2e-8231-484a-9f67-405ece572694
- `docs/requirements.md` is a snapshot of it (decisions C1–C23).
- The original SRS is "SRS_RD_Dashboard - Dev Version.pdf", kept in the claude.ai project "DashboardRevamp", not in git.

## Components

| # | Component | Folder | Tech | Runs as (local Docker) |
| --- | --- | --- | --- | --- |
| 1 | **API** | `backend/src/Vantage.Api` | ASP.NET Core (.NET 10), controllers | `vantage-api` (internal port 8080; reached through the portals' `/api`) |
| 2 | **Domain** | `backend/src/Vantage.Domain` | Entities, enums, rules (no dependencies) | part of the API |
| 3 | **Infrastructure** | `backend/src/Vantage.Infrastructure` | EF Core 10 (SQL Server), services, Power BI/Tableau clients, email, storage | part of the API |
| 4 | **Tests** | `backend/tests/Vantage.Tests` | xUnit, real SQL Server | CI / locally |
| 5 | **Admin Portal** | `frontend/admin-portal` | React 19 + Vite + TypeScript | `vantage-admin` at http://localhost:8080 |
| 6 | **User Portal** | `frontend/user-portal` | React 19 + Vite + TypeScript, powerbi-client | `vantage-user` at http://localhost:8081 |
| 7 | **Shared web library** | `frontend/shared` (`@vantage/shared`) | Layout shell, icons, API client, session, request review UI | built into both portals |
| 8 | **Database** | `deploy/db/init.sql` and EF migrations | SQL Server 2022, schema `app` (+ `hrms`) | `vantage-db` on localhost:1433 |
| 9 | **Mail catcher** | `deploy/docker-compose.yml` | Mailpit | `vantage-mail` at http://localhost:8025 |
| 10 | **Background worker** | `Infrastructure/Email/EmailSenderWorker` | `BackgroundService` in the API | sends the email outbox every 10 s |

Target production (not built yet): both SPAs plus the API on AWS. The API runs on EC2, SQL Server on RDS
Multi-AZ, files on S3, email through SES, secrets in Secrets Manager. Sign-in is ADFS SAML with Duo federated
into Cognito for @rrd.com users, and Cognito with authenticator-app MFA for externals.

## Repository layout

```
vantage/
  CLAUDE.md                 this file
  README.md                 how to run it (for people)
  backend/
    Vantage.slnx            solution (run dotnet commands from backend/)
    dotnet-tools.json       dotnet-ef
    src/Vantage.Api/        Program.cs, Auth/ (dev auth, CurrentUser), Controllers/
    src/Vantage.Domain/     Entities/ (Identity, Catalog, Dashboards, Access, Operations), Enums, Rules, AppModules
    src/Vantage.Infrastructure/
      Data/                 AppDbContext (30 tables), DbSeeder, Migrations/
      Services/             one service per module (see below)
      Embedding/            PowerBiClient, TableauTokenService, embed models
      Email/                EmailOutboxService, EmailSenderWorker, EmailOptions
      Secrets/ Settings/ Storage/ Tenants/
    tests/Vantage.Tests/    xUnit; most tests are [SqlFact] against a throwaway database
  frontend/
    package.json            npm workspaces: shared, user-portal, admin-portal
    shared/src/             Shell (sidebar + top bar), Icon, Thumbnail, api.ts, session, Requests (review cards)
    admin-portal/src/       App.tsx (routes + sidebar), ui.tsx, fields.tsx, pages/*
    user-portal/src/        App.tsx, pages/ (Home, Catalogue, Viewer, Approvals)
  deploy/
    docker-compose.yml      the whole local stack (project name "vantage")
    api.Dockerfile admin.Dockerfile user.Dockerfile nginx.conf
    db/init.sql             creates the database, the app login and the dummy HRMS table
    certs/                  optional company root .crt for HTTPS-inspecting networks (git-ignored)
    .env.example            copy to deploy/.env (git-ignored) for the SQL passwords and Super Admin email
  docs/
    requirements.md         snapshot of the requirements and decisions
    handoff.md              where the build stands and what's next
  .github/workflows/ci.yml  backend build and tests (with SQL Server), frontend typecheck and build
                            (docs/ci.yml is the copy to move there if this folder is missing)
```

## Running it

The project owner runs everything through Docker only. Don't ask them to run other CLI commands.

```
docker compose -f deploy/docker-compose.yml up -d --build      # start or apply changes
docker compose -f deploy/docker-compose.yml down               # stop, keep data
docker compose -f deploy/docker-compose.yml down -v            # stop and wipe data
```

- Admin Portal: http://localhost:8080. User Portal: http://localhost:8081. Emails: http://localhost:8025.
- Local sign-in is a user picker (the `X-Dev-User` header, `DevAuthHandler`), standing in for ADFS/Cognito.
  Nimal Perera is the bootstrap Super Admin.
- Migrations apply automatically when the API starts (`Database:MigrateOnStartup`). The seeder adds roles,
  modules, settings, the BI service types and dummy HRMS people.

Developer loop without Docker (from the repo root):
```
cd backend && dotnet build Vantage.slnx
VANTAGE_TEST_SQL="Server=localhost,1433;User Id=sa;Password=...;TrustServerCertificate=True" dotnet test Vantage.slnx
cd backend/src/Vantage.Api && dotnet run          # http://localhost:5080
cd frontend && npm ci && npm run dev:user           # Vite 5173, proxies /api to 5080
cd frontend && npm run dev:admin                    # Vite 5174
cd frontend && npm run typecheck && npm run build
dotnet ef migrations add <Name> -p backend/src/Vantage.Infrastructure -s backend/src/Vantage.Api
```
Tests that need SQL Server are skipped when `VANTAGE_TEST_SQL` is unset. Each test class creates and drops its
own database. All 80 tests must pass before a change is done.

## Names that must NOT change

The repo was renamed from RdDashboard to Vantage on 3 Oct 2026. These identifiers stay as they were because data
depends on them:
- **Database `RDDashboard` and login `rd_app`.** Used in the compose connection string and `init.sql`.
- **Docker volumes `rd-dashboard_sqldata` and `rd-dashboard_appdata`.** Pinned with `name:` in the compose file.
  They hold the database, uploaded files and Data Protection keys.
- **Data Protection.** `SetApplicationName("RdDashboard")` in `DependencyInjection.cs` and the protector purpose
  `"RdDashboard.Secrets.v1"` in `SecretStore.cs`. Changing either makes stored tenant secrets unreadable.
- **EF migration IDs** (the `__EFMigrationsHistory` rows). Never rename existing migrations.

## Domain rules (enforced in services, covered by tests)

- **Access only through access groups.** Every user, Super Admins included, needs a membership.
  - A user has one live group per dashboard (filtered unique index). Memberships are soft-ended via `RemovedAtUtc`.
  - Each group belongs to one dashboard.
- **Default group.** Named `<code>-<id>-default`, created when publishing, always active.
  - The primary and backup owners are in it and can't be removed.
  - The dashboard code is fixed after publishing because the name depends on it.
- **RLS.**
  - Only dashboards with RLS = Y can have more than the default group (C15).
  - Every group keeps an editable RLS value. It is ignored and not sent in the embed token when the dashboard has no RLS.
  - On RLS dashboards every group needs one.
  - The value is the Power BI role name (comma-separated for several).
  - The effective identity is sent only when the model requires it (`RlsIdentityBuilder`).
- **Access Groups only take existing portal users** (C22). Unknown emails are reported, never created.
- **Access requests** (C23).
  - One pending request per user per dashboard. Every request needs owner approval.
  - On RLS dashboards the owner picks the group; otherwise the person joins the default group.
  - Super Admins can decide on the owner's behalf.
  - While `OwnershipPendingReview` is set, only Super Admins can change access, and requests are refused with
    "This dashboard has no owner right now. Please request access again in a few days."
- **Ownership.** Naming someone owner grants the Dashboard Owner role automatically.
- **Publishing and versions.**
  - Power BI imports run as the service principal with `nameConflict=CreateOrOverwrite` on the dataset name `<code>-<id>.pbix`.
  - Modify Dashboard replaces the report in place (C21). The last 3 files are kept; Super Admins can download or restore them.
- **ServiceNow reference.** Optional on admin changes. It is stored on the audit log row, never on the user (C20).
- **Limits.**
  - 12 pins and 8 tags (alphanumeric, ≤10 characters each).
  - Thumbnails: 16:9 PNG/JPG, ≥640x360, ≤1 MB.
  - Dashboard name unique; group name ≤40 characters and unique.
- **Retire** soft-deletes in the portal and deletes the Power BI asset. Nothing is hard-deleted in the portal.

## Conventions

- **Every change is audited** with `AuditWriter`. Save the entity first so `EntityId` is set, then audit.
  Optionally pass the `serviceNowReference`.
- **Notifications.** User-facing events notify through `NotificationService` (the bell) and queue an email with
  `EmailOutboxService.QueueAsync` in the same unit of work. Never send SMTP inline.
- **Services and controllers.**
  - Services throw `RuleException` with a plain-English message. Controllers map it to `400 { message }`.
  - Admin endpoints derive from `AdminControllerBase` and check `RequireAsync(AppModules.X, PermissionLevel.View|Edit)`.
- **Validate before mutating**, so a refused request leaves nothing half-saved.
- **C# style.**
  - Primary constructors and records for request bodies.
  - Anonymous projection properties in PascalCase (serialized camelCase).
  - Time comes from `TimeProvider`.
- **Frontend.**
  - Call the API with `api<T>()` from `@vantage/shared`. Fetch images and files with `apiObjectUrl` (it adds the dev header).
  - Keep shared UI in `frontend/shared`.
- **UI text.**
  - **Title Case** for menus, page titles, tabs, dialog titles and buttons ("Add Group", "Save Changes", "Request Access").
  - Messages and help text are plain sentences.
  - The portal name comes from the `branding.portalName` setting (Vantage).
- **Layout.** Both portals share one layout:
  - a collapsible sidebar (hamburger at the top left);
  - a top bar with the notification bell and the user menu;
  - a professional, consistent look.
- **Secrets.** Tenant client secrets are encrypted in the database and never returned. Never put secrets in
  code, config, chat or commits; `deploy/.env` is git-ignored.

## Working with the project owner

- Go step by step. Give a short plan for a module, then build it end to end: back end, tests, both portals where relevant.
- Keep the tests passing.
- Finish with exact click-by-click steps to test in the browser.
- Explain any Power BI or Azure setup in plain steps.
- Never ask for secrets in chat.
- Record new decisions in the requirements doc's decision log (next number C24) and refresh `docs/requirements.md`.

## Status (3 Oct 2026)

Done:
- Roles, Users (HRMS lookup and sync), Tenants with Verify.
- Categories, Publishing (.pbix, thumbnail, tags, audience).
- Dashboards Master (edit details, RLS check, preview, Modify Dashboard, versions).
- Access Groups (members, move, copy, clone, details, history).
- User Portal home (cards, categories, list, pins), Dashboard Catalogue, Request Access.
- Owner approvals, Admin Access Requests, notifications (bell) and the email outbox with Mailpit.

Next, in order:
1. Admin **Audit Log** viewer: filters, ServiceNow reference, before and after values, Excel export.
2. User Portal **personal folders**.
3. Owner **monthly access reviews**.
4. **Admin Configuration**: branding, settings, approved CDNs.
5. Analytics.

Then Tableau and GenAI embedding end to end, then real ADFS/Cognito and the AWS environments.

Open questions (still unanswered):
- U5: internal users who sign in before they've been added.
- U6: publisher scope.
- U7: migration from the .NET 4.8 portal.
