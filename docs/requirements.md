> Snapshot of the live requirements doc (decisions C1–C23) taken on 3 Oct 2026, when the code moved to GitHub.
> The live, editable version is the Claude Doc: https://claude.ai/code/artifact/23091f2e-8231-484a-9f67-405ece572694
> When the two differ, the live doc wins; refresh this file when decisions change.

# RD Dashboard Portal — Requirements Review & Clarifications

Oct 2, 2026 · @Supun Samarakoon

## Purpose

This is version 3 of the requirements for the RD Dashboard Portal, rebuilt from the original SRS (PRF#1484) and your answers from three review rounds on 2 Oct 2026. Every decision is written into the module requirements; a final review on 3 Oct found 7 gaps; U1 to U4 are decided, U5 to U7 remain open.

To change a requirement, edit it inline or comment on it.

## Solution summary

RD Dashboard replaces the current .NET 4.8 MVC portal. It registers BI dashboards, controls who sees them through dashboard groups, and embeds them so viewers need no Power BI licence. Sizing basis: about 1,000 users and 100 to 150 dashboards, 80 to 85% of them Power BI. Scope is build only; the first milestone is a local build with dummy data for your verification.

- Two React SPAs on separate URLs (Admin Portal, User Portal), one shared .NET API on EC2, one Microsoft SQL Server database on AWS.
- Three dashboard types: Power BI (.pbix on F64, published to a workspace picked from the tenant master), Tableau Server (embedded through a Connected App), and GenAI Dashboards (single HTML built from an approved template). Sisense is dropped.
- Internal users (@rrd.com) sign in through ADFS SAML with Duo, federated into Cognito; external users are Cognito users with an authenticator-app MFA.
- Access comes only from dashboard groups. A user is in at most one group per dashboard, and that group's RLS value is the Power BI role name used in the .pbix.
- Notifications go by Amazon SES email and an in-portal notification centre.

_(The architecture diagram lives in the live doc.)_

Power BI is highlighted because licence-free embedding on F64 is the main business reason for the portal. S3 holds uploaded files, thumbnails and the last 3 versions of each dashboard.

A user can hold several roles at once, and sees every page any of them allows. Permissions per role are edited in the Role module, so further roles (for example ASD help desk) can be added without code changes.

| Role | Portal | Main responsibilities |
| --- | --- | --- |
| Super Admin | Admin | Everything: users, roles, groups, publishing, configuration, tenants, override approvals, assign owners |
| Dashboard Owner | User | Approve access requests and group changes, usage analytics for owned dashboards |
| Dashboard User | User | View, search, pin, personal folders, request access from the catalogue |
| Custom roles (e.g. ASD, BPI) | Admin | Whatever the Role module grants, e.g. publish rights for the BPI data science team |

## Requirements by module

Fifteen modules, with round-1 decisions applied. Out of scope: Sisense, Application Admin role, Switch Role, favourites (replaced by pins), direct user grants, HRMS-rule access, copying dashboards between categories, and the Directory's "Dashboard Portal" column.

### 1. Authentication

- Login page asks for the email first. `@rrd.com` users are sent to ADFS (SAML, Duo enforced there) and federated into Cognito; everyone else signs in to Cognito directly with mandatory authenticator-app MFA.
- A user with a live ADFS session who clicks a portal link in MyConnect or Metrics is signed in without a prompt.
- One Cognito session covers both portals; logging out of one logs out of both. Idle timeout is set in Admin Configuration. No domain picker.
- External onboarding: a Super Admin creates the user, SES sends a temporary password and setup steps, and at first login the user changes the password, enrols the authenticator app and fills any missing profile fields. Self-service password reset is available.

### 2. User management

- Users table (all users): User ID, Email (unique key), First, Last and Display name, User type, Status, Cognito ID, Contact number, Time zone, Tableau user name (entered by admins; Tableau is fully built but tested later).
- HRMS profile table (internal users only), one row per user, matched on email: department, division, title, manager, location, employment status, exit date.
- HRMS data reaches an HRMS table in the application database through a SQL-level sync run by your team. A monthly portal job reads it, updates profiles and sets leavers Inactive; a manual Inactive set by an admin always wins. An Admin Configuration switch decides whether leavers lose access and sessions immediately. The local build uses this table with dummy data.
- Email changes are made manually by a Super Admin.
- Add one user, paste a list of emails, or upload Excel. Lists are pre-vetted, so no second approval. Internal users are filled from HRMS; external users complete their own details at first login.
- User grid with search and filters; an Access Summary tab per user showing each dashboard and the group that grants it; free-text ServiceNow reference field (no API check).
- Monthly new-hire digest to Super Admins and BPI, as a plain list without action links; access is then granted manually.
- Re-employed users are reactivated by an admin and keep their previous group memberships.

### 3. Roles and permissions

- Built-in roles: Super Admin, Dashboard Owner, Dashboard User. Admins can create more roles and set View or Edit per module.
- A user can hold several roles; menus and pages are the union of them. Role names are unique; a role in use cannot be deleted.

### 4. Dashboard groups

- Group membership is the only way to see a dashboard, for every user including Super Admins. Each group belongs to exactly one dashboard and has a backend Group ID (used for all mappings), a name of up to 40 characters, unique across the portal, an RLS value (the Power BI role name in the .pbix), a status (Active, Inactive, Retired) and members.
- A user is in at most one group per dashboard.
- Publishing creates a default group named \<Dashboard code>-\<Dashboard ID>-default; the primary and backup owners are added to it automatically. When the dashboard's RLS flag is Y, the default group's RLS value is a required field on the publish form.
- Clone copies members only; the new group gets its own name and RLS value. Members already in another group of the target dashboard are skipped with a warning.
- During publishing, admins can create groups or search and clone existing ones; clones are mapped to the new dashboard.
- Members are added one by one, by pasted emails or by Excel; unknown emails create users automatically.
- A Group Management module lists, edits, activates and deactivates groups.
- Power BI embed tokens carry the user's email as the effective identity and their group's RLS value as the role, only when the dashboard's RLS flag is Y. Each token issue is logged in the audit trail; the token itself is never stored.

### 5. Access requests and reviews

- Dashboard Catalogue page: every active dashboard with name, owner and a Request Access button. An Admin Configuration setting decides whether external users see dashboards marked Audience = Internal.
- A shared URL opens the dashboard for members; anyone else sees a "no access" page with Request Access.
- Every request needs owner approval; there is no auto-approval. When approving, the owner picks the group; users never see or choose groups. Super Admins can decide on the owner's behalf only as a recorded override with a reason (C28), and cannot add themselves to a group without approval (C26); they preview dashboards without joining a group (C27).
- The monthly owner access review was dropped on 5 Oct in favour of Access Group Rules (C29), which keep group membership in step with the HRMS data with the owners confirming every change.
- In-portal and SES notifications for: request raised, approved or rejected; publish succeeded or failed; owner change; inactivity flag.

### 6. Ownership

- Each dashboard has a primary and a backup owner. Naming someone as an owner gives them the Dashboard Owner role automatically; it is removed when they own no dashboards.
- If the primary owner becomes Inactive, the backup becomes primary and Super Admins are alerted. Until a Super Admin confirms the owners, access changes, approvals and reviews on that dashboard are paused, and requesters see: "This dashboard has no owner right now. Please request access again in a few days."
- If both owners become Inactive, the dashboard goes Inactive immediately and Super Admins are alerted to assign owners and reactivate it.

### 7. Publishing

- Dashboard types: Power BI, Tableau, GenAI Dashboard. The form shows the fields each type needs, driven by the BI Service Master.
- Common fields: Name (unique across the portal), Dashboard Code (up to 20 characters), Primary category (required), Secondary and Tertiary category (optional; the dashboard sits in the deepest one chosen), Description (500 characters), Primary and Backup owner, up to 8 Tags (alphanumeric, 10 characters each), Thumbnail (640 x 360 px, PNG or JPG, up to 1 MB; a default image if none), Audience Internal or Client and Data classification (both informational).
- Power BI: pick the tenant, then one of its workspaces (both from the tenant master), and upload a .pbix. The service principal imports it and binds it to the existing on-premises gateway. The published report ID is the key that locates the dashboard; the dataset ID is stored with it.
- Tableau Server: pick the Tableau tenant and enter the view URL. Each view is embedded with a Connected App (direct trust) JWT; the JWT carries the viewer's Tableau user name from their profile. Tableau users are created manually, never by the portal.
- GenAI Dashboard: one self-contained HTML file that follows the approved template, up to 5 MB (warning above 2 MB). Scripts may load only from the approved CDN list. Every upload is checked against the template rules (C32); a failed check blocks publishing until the issues are fixed. It is shown in a sandbox on a separate domain with a strict Content Security Policy.
- Drafts can be saved without a file or link. Success, failure and progress are shown on screen and notified.
- Replacing a file keeps the last 3 versions in S3; Super Admins can download any of them or restore one, which overwrites the live version. Dashboards can be renamed without re-upload.
- End-user export to PDF or image is out of scope.

### 8. Dashboard configuration page

- One page per dashboard for admins: metadata, groups and members, Power BI refresh schedule (pushed to Power BI through its API) with Refresh Now and recent refresh history, versions (download, restore), audit history, inactivity flag and lifecycle status.

### 9. Lifecycle

- Statuses: Draft, Publishing, Active, Failed, Inactive, Retired. Only Super Admins change status, except the automatic Inactive when both owners leave. The Category Management active toggle sets Inactive.
- No views for 90 days (configurable): owners get an email and the dashboard is flagged in the master grid and on its configuration page.
- Retire moves the dashboard and its groups to Retired; nothing is hard-deleted in the portal, so the audit trail stays complete. The Power BI report and dataset are deleted from the workspace; Tableau content is untouched; GenAI files and their backups stay in S3 but are no longer served.

### 10. Home page and navigation

- Default: dashboard cards with thumbnails, uncategorised, newest first. Toggles switch to Category view (only categories holding dashboards the user can open) and to Personal Folders.
- List view columns sort ascending or descending. Pins (up to 12 per user) replace favourites; recent dashboards; "New" badge on newly granted dashboards.
- Global search across name, description, owner, category path and tags.
- Personal folders are one level deep; a dashboard whose access is revoked disappears from them.
- The browser address of any dashboard is shareable as a deep link.

### 11. Categories

- Primary, Secondary and Tertiary levels; names unique within their parent. Dashboards can be moved between categories; a category holding dashboards cannot be deleted.

### 12. Dashboard Directory

- Admin grid: type, name, category path, RLS (Y/N), primary and backup owner, data classification, description, SharePoint folder and sub-folder; editable by admins.

### 13. Analytics and audit

- Usage from portal events plus Power BI activity events (the service principal is granted read-only admin API access) and Tableau Server usage data. Super Admin stats page and per-owner analytics.
- Reports for "who can open dashboard X" and "what can user Y open".
- Audit log is kept permanently; the UI searches the last 12 months; older records stay in the database.

### 14. Admin configuration

- Branding (portal name, logo, primary and accent colours), inactivity threshold, HRMS sync schedule, leaver switch, idle timeout, new-hire digest frequency, default grid size, external users' catalogue visibility, approved CDN list, BI Service Master flags and connector on/off.
- Tenant master: Power BI tenants (tenant ID, client ID, one or more workspaces with name and workspace ID) and Tableau tenants (server URL, site, Connected App client ID). Secrets live in AWS Secrets Manager.
- Every change is audited.

### 15. Error handling

- A failing BI platform does not affect the others. Friendly error states with Retry, a 30-second embed timeout, and every failure logged with a correlation ID.

## Decision log

All 14 contradictions from round 1 are resolved; the requirements above already reflect them.

| # | Decision |
| --- | --- |
| C1 | Groups can be created or cloned inside the publish page |
| C2 | Owners are auto-added to the default group |
| C3 | Default group is named `<Dashboard code>-<Dashboard ID>-default` |
| C4 | No automatic inactivation; 90 days without views only flags and emails the owner |
| C5 | Retire deletes the Power BI asset immediately |
| C6 | Email is the key; NT Login is dropped from the users table |
| C7 | Access through dashboard groups only |
| C8 | Thumbnails are uploaded, with a default image if missing |
| C9 | Dashboard names are unique across the portal |
| C10 | GenAI Dashboards are single HTML uploads |
| C11 | Newest first by default; sortable columns; pins replace favourites |
| C12 | Category active toggle is the lifecycle Inactive status |
| C13 | Permissions are set per role in the Role module |
| C14 | Sisense dropped |
| C15 | Only dashboards with RLS = Y can have more than the default group. The RLS value stays visible and editable on every group; on a dashboard without RLS it is kept but not sent in the embed token. Adding a group to a non-RLS dashboard shows a message. The RLS flag is read from the Power BI model and can be re-checked from Dashboards Master (3 Oct). |
| C16 | The Dashboards page is called Dashboards Master. Details are edited there after publishing without re-uploading: name, description, category, owners, tags, audience, classification, SharePoint folders and thumbnail. The dashboard code is fixed because the default group's name is built from it (3 Oct). |
| C17 | Thumbnails accept any 16:9 PNG or JPG of at least 640 x 360 px, up to 1 MB, and are shown at 640 x 360. A primary category is required when publishing (3 Oct). |
| C18 | Both portals share one layout: a sidebar that collapses to icons with a hamburger button, plus a top bar holding the notification bell and the user menu. The User Portal home page lists the user's dashboards as thumbnail cards, by category or as a sortable list (3 Oct). |
| C19 | The portal is named Vantage (portal name in branding settings; code and database names unchanged). Menus, page titles, tabs and dialog titles use Title Case. "Groups" are called Access Groups; their name (except the default group's), RLS value and status are edited on the group's Details panel (3 Oct). |
| C20 | The ServiceNow reference is not stored on the user. It is entered with a change (adding or editing users, adding people to an access group) and saved in the audit log with that change (3 Oct). |
| C21 | Modify Dashboard uploads a new .pbix that replaces the report in its Power BI workspace; all portal details, access groups and RLS values stay. The last 3 files are kept; Super Admins can download or restore any of them (3 Oct). |
| C22 | Only existing portal users can be added to access groups; people must be added in Users first. This replaces the earlier rule that unknown emails create users automatically (3 Oct). |
| C23 | Access requests: one pending request per user per dashboard; refused while the dashboard has no confirmed owner ("request again in a few days"). The owner (or a Super Admin on their behalf) approves into a chosen RLS group, or the default group on non-RLS dashboards; approving moves the person out of any other group of that dashboard. While ownership is under review only Super Admins decide. Requester and owners get a bell notification and an email (local build: Mailpit at port 8025). |
| C24 | Audit Log (Admin Portal, module "audit", read only): search by date range, who, action, entity type, ServiceNow reference and free text; each entry has a plain-sentence Description (who did what, to what, under which ticket) and opens to show before and after values; the filtered list exports to Excel (up to 50,000 entries). It searches the last 12 months; older entries stay in the database (4 Oct). |
| C25 | Personal folders (User Portal, "Personal Folders"): one level deep, private to the user, up to 20 folders per person, names up to 60 characters and unique per person (ignoring case). A dashboard can be in several folders. Only dashboards the user can open can be added; one whose access is revoked disappears from the folders and returns if access is granted again. Folders are created, renamed and deleted on the Personal Folders page; dashboards are added there or from the folder button on any card on My Dashboards. Not audited, like pins (4 Oct). |
| C26 | Adding people to an access group needs the dashboard owners' approval (4 Oct). Whoever adds people (one by one, pasted emails, Excel, copy or clone members), Super Admins and Super Admins adding themselves included, creates ONE request per action for the chosen access group; nobody is added yet. Owners hold approve and reject only: they can't add people. The approver sees the group (and its RLS value), can open a collapsed list of the people, ticks who to approve (everyone by default, with Select All and Clear All) and approves in one go; people left unticked are rejected, and a request with nobody ticked is rejected as a whole. The people are added to that group when approved. The only exception route is on the Admin Portal's Access Requests page: a Super Admin can choose Override Approval and decide in the owners' place, but must write a reason, which is saved in the audit log and shown in the group's History, and the owners are told. There is no override on the add screens. A person already waiting in another request for the same dashboard is skipped. Moves, removals and the default group created at publishing are not part of this. Replaces the earlier rule that Super Admins add themselves without approval (C23). |
| C27 | Super Admins administer; they don't consume (4 Oct). They check dashboards from the Preview tab of Dashboards Master, choosing which access group's view to see (that group's RLS role, with the Super Admin's own email as the identity), without being a member of any group. A preview isn't a view: it doesn't count in usage or reset the inactivity flag, and each one is audited. Only Super Admins can preview. The "Add Me" shortcut (a Super Admin joining a group without approval) is removed; using a dashboard in the User Portal still needs group membership, obtained through the normal approval. |
| C28 | Whenever a Super Admin steps in to override the normal process, they must give a reason (4 Oct). Today that means deciding an access request in the owners' place: a person's request from the catalogue, or an admin's request to add people to a group. The decision controls stay hidden behind "Override Approval…" on the Admin Portal's Access Requests page; the reason is required, saved with the request, written to the audit log (and the group's History for group additions), and the owners get a bell notification and an email. Owners deciding their own dashboards, and Super Admins previewing (C27), are not overrides. |
| C29 | Access Group Rules (5 Oct). A Super Admin can write rules for an access group on the HRMS fields (Department, Division, Job Title, Job Grade, Location, Manager Email, Employment Status): when every condition of a rule is true for a person (field equals value, not case sensitive), the rule proposes adding them to the group (Add rule) or removing them from it (Remove rule). Rules are never automatic: running a rule sends what it finds to the dashboard's owners as ONE request, and people are added or removed only when the owners approve, with the same tick-to-approve card, and the same Super Admin override with a reason (C28). Rules run when a Super Admin runs them (Run Now, Run All) and after every HRMS sync; a Preview shows who would be proposed before anything is sent. Only active portal users with HRMS details are considered. People already waiting in a request, and people the owners rejected under the current version of the rule, are not proposed again (changing a rule starts it afresh); the dashboard's owners are never proposed for removal; someone who matches both an add rule and a remove rule of the same group is left for a person to decide; an Add rule can optionally also propose people who are in another group of the dashboard (approving moves them). Rules are skipped while a dashboard's owners are under review or the group is not active. Creating, changing, deleting and running rules is recorded in the audit log and the group's History. Only Super Admins manage rules; anyone with Access Groups permission can read them. |
| C30 | Admin Configuration (5 Oct) has four tabs. Branding: portal name, primary and accent colours, and an uploaded logo (PNG, JPG, WebP or SVG up to 512 KB; the type is judged by the file's content, SVGs with scripts are refused). Settings: idle timeout (5 to 480 minutes, signs people out of both portals), internal email domain, whether external users see Internal dashboards in the catalogue, default rows per page (10, 25, 50, 100: used by the Users, Access Groups, members and Audit Log lists), and whether emails are sent (off = they wait in the outbox). Approved CDNs: plain host names only, for GenAI dashboards. Dashboard Types: switch a type off and set the largest upload; at least one stays on; enforced when publishing or replacing a .pbix. Every value is validated, nothing is saved if one is wrong, and each change is audited with the old and new value. The inactivity threshold, HRMS sync schedule, leaver switch and new-hire digest are saved but take effect only once the scheduled jobs exist. The monthly-review settings were removed with the review (C29). |
| C31 | Analytics (5 Oct). Usage is the number of times people open dashboards in the User Portal (previews are not counted); Power BI activity and Tableau usage will feed the same numbers when imported. Super Admins (Analytics permission) get Overview (views, people, dashboards opened, dashboards nobody opened, views per day, most opened dashboards, most active people, views by type), Dashboards (every dashboard, sortable, Excel export, with a per-dashboard view showing who opens it and which members never did) and Access Reports: who can open a dashboard, and what a person can open, each with Excel export and a clear "can open now" mark (an inactive person, group or dashboard cannot). Owners get their own Analytics page in the User Portal limited to the dashboards they own. Ranges are 7, 30, 90 days and 12 months; long ranges show weekly columns. |
| C32 | GenAI dashboards (4 Oct). Publish Dashboard has a GenAI tab beside Power BI: one .html file (up to 5 MB, warning from 2 MB) built from the approved starter template, which authors or their AI tool download from that page. The template carries a marker (`<meta name="vantage-template" content="genai-1">`); a file without it is refused. The upload check also refuses anything that loads from outside the approved, active CDN list or over plain HTTP, links to other files, frames, plug-ins, base tags and page redirects, and warns about network calls; a file that fails creates nothing. This is a rule check, not a malware scan. A passing file is live at once (no import step), with the owners in the default group. Modify Dashboard and Restore work as for Power BI, but instantly; the last 3 files are kept and a restore is checked again against the current CDN list. Members open a GenAI dashboard through a signed link that expires (default 60 minutes), issued after the usual group check, which is the moment the view is counted; a Super Admin preview gets the same kind of link and is not a view. The link points at a separate origin (http://localhost:8082 locally, a dedicated GenAI domain in AWS) that serves only these files, with a strict Content-Security-Policy (scripts, styles, fonts and images only from the approved CDNs, no network calls, forms, plug-ins or nested frames, framed only by the two portals) and a CSP `sandbox allow-scripts`; the portals also frame it with `sandbox="allow-scripts"` and never `allow-same-origin`, so a dashboard's script can't use the viewer's session or the API. A link is a bearer URL until it expires, so the file is not public but whoever holds a live link can load it. A dashboard that is not Active is not served. |

## Open questions

A final read-through on 3 Oct 2026 found seven gaps where two requirements meet but neither says what happens. U1 to U4 are needed before the data model is built.

| ID | Gap | Proposed default | Pri |
| --- | --- | --- | --- |
| U1 | The Directory has an "Owner approval required" flag, but every request goes to the owner. What happens to a request when the flag is No? | Auto-approved into the dashboard's default group | P1 |
| U2 | When a dashboard's RLS flag is Y, the default group needs an RLS value too, or its members (including the owners) cannot open it. | RLS value for the default group is required on the publish form when RLS = Y | P1 |
| U3 | Can Super Admins open any dashboard without being in one of its groups? On RLS dashboards that would need a role chosen for them. | No: everyone, Super Admins included, needs a group; admins can add themselves | P1 |
| U4 | Does a user get the Dashboard Owner role automatically when named primary or backup owner, and lose it when they own nothing? | Yes, both automatic | P1 |
| U5 | An internal @rrd.com user who has never been added signs in through ADFS. Create them on the fly, or refuse? | Create on first login with the Dashboard User role; they see only the Catalogue until granted | P2 |
| U6 | Can a user with publish permission (e.g. BPI) see and edit every dashboard, or only the ones they published? | Every dashboard; permissions stay at module level | P2 |
| U7 | Is migration of users, categories, dashboards and mappings from the current .NET 4.8 portal in scope? The non-functional table assumes yes. | In scope; needs the current database schema | P2 |

## Non-functional requirements

Proposed targets sized for about 1,000 users and up to 150 dashboards; change any that do not fit.

| Area | Requirement |
| --- | --- |
| Capacity | 1,000 users, 150 signed in at peak (assumed), room for 300 dashboards |
| Performance | Portal pages under 2 s (95th percentile); search under 1 s; embed token under 1 s; dashboard load timeout 30 s; bulk member uploads of up to 5,000 emails processed in the background |
| Availability | 99.5% monthly; SQL Server on RDS Multi-AZ; point-in-time restore (recovery point about 5 min); recovery within 4 h |
| Security | TLS 1.2+; encryption at rest for database and S3; secrets in AWS Secrets Manager; OWASP ASVS level 2; penetration test before go-live; GenAI HTML served from a separate domain with a strict Content Security Policy; every admin action audited |
| Sessions | Idle timeout 30 min (configurable), absolute session 12 h |
| Browsers and devices | Latest two versions of Chrome, Edge, Safari and Firefox; desktop and tablet; phones best effort |
| Accessibility and language | WCAG 2.1 AA; English only; times stored in UTC and shown in the user's time zone |
| Observability | Structured logs with correlation IDs, CloudWatch metrics and alarms, a health check per BI connector, alerts to IT Ops |
| Data retention | Audit log permanent (12 months in the UI); usage events 3 years; inactive users and retired dashboards kept as records |
| Migration | Scripts to move users, categories, dashboards and existing mappings (as dashboard groups) from the current .NET 4.8 portal; assumed in scope |
| Environments | Local first (API and React apps on the developer machine, SQL Server in local Docker until the AWS dev database is ready, real Power BI, local S3 and email stand-ins, dummy HRMS table), then Dev, UAT and Prod on AWS with the API on EC2 |
| Engineering | Automated tests on API business rules, CI build on every commit, infrastructure as code for AWS |
