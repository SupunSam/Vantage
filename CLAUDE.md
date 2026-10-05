# CLAUDE.md: Vantage

Guidance for Claude (and people) working in this repo. Read this first, then `docs/requirements.md` before
changing behaviour.

## What Vantage is

Vantage (formerly RRD Dashboards, PRF#1484) is the corporate BI portal. It replaces a .NET 4.8 MVC portal.
- It registers Power BI, Tableau Server and GenAI (single HTML) dashboards.
- It controls who sees each dashboard through access groups.
- It embeds Power BI on F64 capacity, so viewers need no Power BI licence.

Sizing: about 1,000 users and 100 to 150 dashboards, 80 to 85% of them Power BI.

Requirements:
- The live requirements and decisions log is a Claude Doc:
  https://claude.ai/code/artifact/23091f2e-8231-484a-9f67-405ece572694
- `docs/requirements.md` is a snapshot of it (decisions C1–C48).
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
| 10 | **GenAI origin** | `deploy/genai.nginx.conf` | nginx, passes signed links to the API's `/genai/{token}` | `vantage-genai` at http://localhost:8082 |
| 11 | **Background workers** | `Infrastructure/Email/EmailSenderWorker`, `Infrastructure/Jobs/JobScheduler` | `BackgroundService`s in the API | the first sends the email outbox every 10 s; the second runs the scheduled jobs (C35) |

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
    user-portal/src/        App.tsx, pages/ (Home, Catalogue, Viewer, Approvals, Folders)
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
```

## Running it

The project owner runs everything through Docker only. Don't ask them to run other CLI commands.

```
docker compose -f deploy/docker-compose.yml up -d --build      # start or apply changes
docker compose -f deploy/docker-compose.yml down               # stop, keep data
docker compose -f deploy/docker-compose.yml down -v            # stop and wipe data
```

- Admin Portal: http://localhost:8080. User Portal: http://localhost:8081. Emails: http://localhost:8025. GenAI dashboards load from http://localhost:8082 inside the portals' frame (nothing to open by hand).
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
own database. All tests must pass before a change is done (CI runs the SQL ones).

## Names that must NOT change

Everything is named Vantage (C48, 4 Oct 2026). These identifiers now stay as they are because data depends on them:
- **Database `Vantage` and login `vantage_app`.** Used in the compose connection string and `init.sql`.
- **Docker volumes `vantage_sqldata` and `vantage_appdata`.** Created by the compose project name `vantage`.
  They hold the database, uploaded files and Data Protection keys.
- **Data Protection.** `SetApplicationName("Vantage")` in `DependencyInjection.cs` and the protector purpose
  `"Vantage.Secrets.v1"` in `SecretStore.cs`. Changing either makes stored tenant secrets unreadable.
- **EF migration IDs** (the `__EFMigrationsHistory` rows). Never rename existing migrations.

An install made before C48 (database `RDDashboard`, login `rd_app`, volumes `rd-dashboard_*`) upgrades once:
`init.sql` renames the database and login in place, and `docs/deployment/04-upgrade-from-the-old-names.md` has the
volume copy. Those old names appear only in that script and that guide. Don't introduce them anywhere else.

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
- **Adding people to a group needs owner approval** (C26).
  - `GroupAddRequestService` turns "add these people" (add, copy members, clone) into ONE `GroupAddRequest` per action. Nobody is added until approved.
  - Owners hold approve and reject only (they have no Access Groups permission). They tick who to approve; unticked people are rejected.
  - The only override is a Super Admin deciding in the owners' place from the Admin Portal's Access Requests page ("Override Approval"). A reason is required. It goes into the audit log (`overrideReason`) and the group's History, and the owners are notified. The add screens have no override.
  - `AccessGroupService.AddMembersAsync` is the direct primitive behind it; controllers must not call it for admin adds.
  - Moves, removals and the default group at publishing stay direct.
- **Configuration is data, validated and audited** (C30). Settings live in `SystemSettings`, defined in `ConfigService` (label, kind, limits, whether anything uses it yet). Add a new setting there and in `SettingKeys.Defaults`; never read a setting without a default. Dashboard type on/off and size limits are enforced with `ServiceTypes.EnsureAllowedAsync`.
- **Analytics counts views, not previews** (C31). A view is a `DashboardViews` row written when a member's embed token is issued. Never write one for a Super Admin preview or any other administrative action.
- **Access group rules** (C29).
  - A rule (HRMS field equals value, all conditions) proposes adding people to, or removing people from, ONE access group. It never changes membership.
  - `AccessGroupRuleService.RunAsync` sends what it finds to the owners as one `GroupAddRequest` (`Action` Add or Remove, `RuleId`, `RuleName`, no requester). Owners approve in part or in full; only Super Admins manage rules.
  - Rules run on demand and after each HRMS sync (`UsersController.HrmsSync`). People already waiting, people the owners rejected under the current rule version, owners (for removal) and add/remove conflicts are not proposed.
  - Anything that changes group membership because of a rule must go through a request, never `AddMembersAsync` or `RemoveMemberAsync` directly.
- **Every override needs a reason** (C28). A Super Admin deciding an access request, or a group-add request, in the owners' place must give a reason.
  - It is stored in `OverrideReason`, written to the audit log (`overrideReason`), and the owners are notified (bell and email).
  - Any new Super Admin step-in that bypasses the normal process must follow the same pattern: required reason, audited, owners told, and hidden behind an explicit "Override…" button in the UI.
- **Super Admins administer, they don't consume** (C27).
  - They preview from Dashboards Master (`EmbedService.PreviewAsync`, `GET /api/admin/dashboards/{id}/preview-embed?groupId=`) as a chosen group's members would see it, without membership.
  - A preview is audited (`dashboard.previewed`) and is not a view: no `DashboardViews` row, no `LastViewedAtUtc`.
  - There is no way for anyone to join a group without approval. Opening a dashboard in the User Portal (`GetEmbedAsync`) always needs a live membership.
- **Access requests** (C23).
  - One pending request per user per dashboard. Every request needs owner approval.
  - On RLS dashboards the owner picks the group; otherwise the person joins the default group.
  - Super Admins can decide on the owner's behalf. (A Super Admin adding themselves to a group goes through C26 approval too.)
  - While `OwnershipPendingReview` is set, only Super Admins can change access, and requests are refused with
    "This dashboard has no owner right now. Please request access again in a few days."
- **Ownership.** Naming someone owner grants the Dashboard Owner role automatically.
- **Publishing and versions.**
  - Power BI imports run as the service principal with `nameConflict=CreateOrOverwrite` on the dataset name `<code>-<id>.pbix`.
  - Modify Dashboard replaces the report in place (C21). The last 3 files are kept; Super Admins can download or restore them.
- **ServiceNow reference.** Optional on admin changes. It is stored on the audit log row, never on the user (C20).
- **Limits.**
  - 12 pins, 20 personal folders (names ≤60 characters) and 8 tags (alphanumeric, ≤10 characters each).
  - Thumbnails: 16:9 PNG/JPG, ≥640x360, ≤1 MB.
  - Dashboard name unique; group name ≤40 characters and unique.
- **GenAI dashboards** (C32).
  - One `.html` file from the approved starter template (marker `vantage-template`/`genai-1`), 5 MB max, warning from 2 MB.
  - Only Super Admins publish, modify or restore a GenAI file (C33); the API refuses anyone else.
  - `GenAiChecker` is the upload check: approved active CDNs over HTTPS at an exact version with an integrity hash, nothing relative, no links to other sites, no frames/objects/base/redirects, and banned code patterns. `IFileScanner` (ClamAV when a scanner host is set, fail closed) scans every upload and restore. Publishing, Modify and Restore all run both; a failure changes nothing. `GenAiService` keeps the versions and signs the links; `GenAiPublisher` creates the dashboard.
  - GenAI values (web address, frame ancestors, link lifetime, warn size, scanner host/port) are settings on the Admin Configuration **GenAI Config** tab (C34), read through `GenAiSettings` on each use: Admin Configuration first, then the environment's `GenAi:*` for blank addresses and scanner host. Only Super Admins change them or the CDN list. The file-check rules and CSP directives stay in code. Don't read `IOptions<GenAiOptions>` directly.
  - Keep the starter template passing the checker (a test enforces it). Rule words also trip the checker inside comments.
  - They are served only from the separate origin (the GenAI web address) via `GenAiContentController`, with the strict CSP and `sandbox allow-scripts` from `GenAiService.PolicyFor`. Never serve the file from the portals' origin or through `/api`.
  - The portals frame it with `GenAiFrame` (`sandbox="allow-scripts"`, never `allow-same-origin`). Don't widen the sandbox or the CSP without a recorded decision.
  - A link is issued only after the normal membership check (or Super Admin preview) and is the only way in. The view row is written when the link is issued, as for Power BI. Not Active means not served.
  - Security write-up, including what the check does NOT do and the open hardening options: `docs/genai-security.md`. Keep it in step with any change here.
- **Scheduled jobs** (C35 to C37).
  - A job implements `IScheduledJob` (`Infrastructure/Jobs`), is registered in `DependencyInjection.cs`, and never records its own run. `JobRunner` does: claim, `JobRun` row, audit (`job.run`), failure notice to Super Admins, next run. The scheduler and Run Now share that path. Job state (`JobStates`) is changed only with direct updates.
  - A job's schedule is a cron (UTC) read from its setting (`CronSchedule`, pure and unit tested); the digest's "Never" means no schedule. A new job needs a stable `Name` that is never renamed.
  - The HRMS job only updates users; it never creates them (U5 open). With `hrms.leaverRevokeImmediately` on it ends a leaver's live memberships directly, which is a revocation and not a rule proposal. Owners keep theirs.
  - The inactivity check flags and emails once; opening a dashboard clears the flag (`EmbedService`). Previews are not views.
  - Keep the decisions in `JobRules` (pure) so they stay testable without a database.
- **Tableau dashboards** (C38).
  - Two modes: a Tableau Server tenant (Connected App JWT for the viewer's own Tableau user name) or Tableau Public (no tenant, no token). `TableauViewUrl` (pure) reads and cleans every address, on publish, on change and again on every open; the stored address is always the embed form.
  - A Tableau Public dashboard must be classified Public (`TableauPublisher.EnsureClassificationFits`, also checked when editing details and changing the view). Never relax this: anyone with the address can open it on Tableau Public.
  - No portal RLS for Tableau (Tableau owns it), so only the default group. Access still needs a group membership; the view row is written when the embed is issued.
  - `TableauPublisher` publishes (live at once, no import); `DashboardMasterService.SetTableauViewAsync` changes the view. The shared `TableauViz` component renders it in both portals.
- **Users, publishers and sessions** (C39 to C41).
  - Only Super Admins create users (Add User, Add Many, Excel; `RequireSuperAdminAsync`). Nobody is created at sign-in or by the HRMS job; an unknown person who signs in is refused.
  - Only Super Admins publish, modify and restore dashboards of any type. There is no publisher role, and role permissions can't grant it. Owners see only the dashboards they own, in the Owner Workspace.
  - The Admin Portal and User Portal never share a session. Don't build anything that signs one in or out through the other.
- **Owners leaving** (C43). `OwnerDepartureService.ReviewAsync` makes an Active dashboard Inactive (and sets `OwnershipPendingReview`) when neither owner is an Active user, tells the Super Admins and audits it. It runs after the HRMS job and when a user is set Inactive by hand. A Super Admin naming a new active owner in Dashboards Master (`DashboardMasterService.UpdateAsync`) reactivates it. The portal never picks owners.
- **Sign-in, Home and log out** (C45). Both portals show `SignInPage` (Login as Internal User / External User) before sign-in and land on Home after it. `signOut` ends only that portal's session and reloads at `/`; don't route a sign-out through the other portal. The Admin Home is `AdminHomeService` (`GET /api/admin/home`), which fills only the parts the role may see; its charts use a fixed colour per dashboard type. Personal Folders is a tab on the User Portal Home (`/folders` redirects there). By Category and Personal Folders both browse as a folder grid with breadcrumbs (C46): `FolderGrid`/`Crumbs`, the place is kept in the address (`?cat=`, `?folder=`), and the category tree is built in the browser from each dashboard's `categoryChain` (`CategoryService.BuildChains`). Home sorts through one `Sort` preference (`sortRows`) used by the flat cards, the list and category folders (C47); dashboard types show as `TypeIcon` (icon plus name, never colour alone).
- **Analytics type filter** (C42). Admin and owner analytics take an optional `type` (blank = all). Build the scope with `AnalyticsService.ScopeAsync` and never parse the type yourself (`ParseType` refuses unknown values).
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
- **Long lists are paged.** Use `Pager` and `usePaged` from `@vantage/shared` for lists held in the browser, and a `page`/`pageSize` API for lists that can grow without limit (audit log, group history, users). Never render an unbounded list.
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
- Record new decisions in the requirements doc's decision log (next number C49) and refresh `docs/requirements.md`.

## Status (4 Oct 2026)

Done:
- Roles, Users (HRMS lookup and sync), Tenants with Verify.
- Categories, Publishing (.pbix, thumbnail, tags, audience).
- Dashboards Master (edit details, RLS check, preview, Modify Dashboard, versions).
- Access Groups (members, move, copy, clone, details, history).
- User Portal home (cards, categories, list, pins), Dashboard Catalogue, Request Access.
- Owner approvals, Admin Access Requests, notifications (bell) and the email outbox with Mailpit.
- Admin Configuration (C30) and Analytics (C31), with owner analytics in the User Portal.
- Access Group Rules (C29): HRMS-based add and remove proposals that the owners confirm.
- Owner approval for adding people to access groups (C26): one request per add, approve in part or in full, Super Admin override (Access Requests page only) with a reason.
- Super Admins preview dashboards without joining any group (C27).
- Admin **Audit Log** viewer (filters, plain-sentence descriptions, before and after values, Excel export).
- User Portal **Personal Folders** (page plus a folder button on cards).
- Scheduled jobs (C35 to C37): HRMS sync, inactivity check and new-hire digest on one scheduler, with an Admin Portal Scheduled Jobs page (schedule, last and next run, Run Now, Pause, history).
- Tableau dashboards (C38): publish from the Tableau tab (Tableau Server tenant or Tableau Public), Tableau View panel in Dashboards Master, shared `TableauViz`. Tableau Server is untested until a test server exists.
- Sign-in page, Home pages, Log Out (C45): internal/external login choice, Admin Home with stats and charts, User Portal Home with a Personal Folders tab, Owner Workspace menu.
- Follow-ups (C39 to C44): Super Admin-only user creation and publishing, separate portal sessions, Analytics dashboard-type filter, dashboards go Inactive when their owners leave.
- GenAI dashboards (C32, C33): starter template, vetted upload (rule check plus optional ClamAV scan, Super Admin only), publish/modify/restore, signed links on a separate origin, sandboxed viewer in both portals.
- Names unified (C48): database `Vantage`, login `vantage_app`, volumes `vantage_*`, Data Protection names, browser storage keys. One-time upgrade for older installs in `docs/deployment/04-upgrade-from-the-old-names.md`.
- Deployment guides (`docs/deployment/`): Docker on Windows 11, Visual Studio without Docker, Visual Studio Code (Docker or not), and Azure DevOps to AWS Dev with a proposed Dev costing. The AWS deploy is a plan only: the API still starts only in Development (real sign-in, migrations on deploy, S3 and durable keys come first).

Next (parked by the owner for now): Tableau Server test site, real ADFS/Cognito sign-in with separate sessions per portal, the AWS environments, and the migration from the .NET 4.8 portal (U7, in scope, last).

Open questions: none. U5 to U7 were answered on 4 Oct (C39, C40, C44).
