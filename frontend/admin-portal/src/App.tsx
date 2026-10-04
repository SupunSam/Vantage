import { Navigate, Route, Routes } from "react-router-dom";
import { AppShell, can, DevSignIn, useSession, type NavSection } from "@vantage/shared";
import { OverviewPage } from "./pages/OverviewPage";
import { RolesPage } from "./pages/RolesPage";
import { UsersPage } from "./pages/UsersPage";
import { TenantsPage } from "./pages/TenantsPage";
import { PublishPage } from "./pages/PublishPage";
import { DashboardsPage } from "./pages/DashboardsPage";
import { DashboardDetailPage } from "./pages/DashboardDetailPage";
import { CategoriesPage } from "./pages/CategoriesPage";
import { AccessGroupsPage } from "./pages/AccessGroupsPage";
import { AccessGroupDetailPage } from "./pages/AccessGroupDetailPage";
import { AccessRequestsPage } from "./pages/AccessRequestsPage";
import { AuditLogPage } from "./pages/AuditLogPage";
import { AccessRulesPage } from "./pages/AccessRulesPage";
import { AnalyticsPage } from "./pages/AnalyticsPage";
import { ConfigPage } from "./pages/ConfigPage";
import { JobsPage } from "./pages/JobsPage";
import "./admin.css";

/** The User Portal runs next to this one: port 8081 in the local Docker build. */
const userPortalUrl = `${window.location.protocol}//${window.location.hostname}:8081`;

export function App() {
  const { me, loading } = useSession();

  if (loading) return <p className="content muted">Loading…</p>;
  if (!me) return <DevSignIn portalLabel="Admin Portal" />;

  const show = (module: string) => can(me, module);
  const sections: NavSection[] = [
    { items: [{ to: "/", label: "Overview", icon: "home", end: true }] },
    {
      title: "Dashboards",
      items: [
        ...(show("dashboard-config") ? [{ to: "/dashboards", label: "Dashboards Master", icon: "dashboards" }] : []),
        ...(show("publishing") ? [{ to: "/publish", label: "Publish Dashboard", icon: "upload" }] : []),
        ...(show("categories") ? [{ to: "/categories", label: "Categories", icon: "categories" }] : []),
      ],
    },
    {
      title: "Access",
      items: [
        ...(show("users") ? [{ to: "/users", label: "Users", icon: "users" }] : []),
        ...(show("roles") ? [{ to: "/roles", label: "Roles and Permissions", icon: "shield" }] : []),
        ...(show("groups") ? [{ to: "/access-groups", label: "Access Groups", icon: "groups" }] : []),
        ...(show("groups") ? [{ to: "/access-rules", label: "Access Group Rules", icon: "list" }] : []),
        ...(show("groups") ? [{ to: "/requests", label: "Access Requests", icon: "inbox" }] : []),
      ],
    },
    {
      title: "Platform",
      items: [
        ...(show("tenants") ? [{ to: "/tenants", label: "Tenants", icon: "server" }] : []),
        ...(show("analytics") ? [{ to: "/analytics", label: "Analytics", icon: "chart" }] : []),
        ...(show("audit") ? [{ to: "/audit", label: "Audit Log", icon: "history" }] : []),
        ...(show("scheduled-jobs") ? [{ to: "/jobs", label: "Scheduled Jobs", icon: "refresh" }] : []),
        ...(show("admin-config") ? [{ to: "/config", label: "Configuration", icon: "settings" }] : []),
      ],
    },
  ];

  return (
    <AppShell portalLabel="Admin Portal" sections={sections} otherPortal={{ label: "Open User Portal", href: userPortalUrl }}>
      <Routes>
        <Route path="/" element={<OverviewPage />} />
        {show("roles") && <Route path="/roles" element={<RolesPage />} />}
        {show("users") && <Route path="/users" element={<UsersPage />} />}
        {show("tenants") && <Route path="/tenants" element={<TenantsPage />} />}
        {show("publishing") && <Route path="/publish" element={<PublishPage />} />}
        {show("categories") && <Route path="/categories" element={<CategoriesPage />} />}
        {show("groups") && <Route path="/access-groups" element={<AccessGroupsPage />} />}
        {show("groups") && <Route path="/access-groups/:id" element={<AccessGroupDetailPage />} />}
        {show("groups") && <Route path="/access-rules" element={<AccessRulesPage />} />}
        {show("groups") && <Route path="/requests" element={<AccessRequestsPage />} />}
        {show("audit") && <Route path="/audit" element={<AuditLogPage />} />}
        {show("scheduled-jobs") && <Route path="/jobs" element={<JobsPage />} />}
        {show("analytics") && <Route path="/analytics" element={<AnalyticsPage />} />}
        {show("admin-config") && <Route path="/config" element={<ConfigPage />} />}
        {show("dashboard-config") && <Route path="/dashboards" element={<DashboardsPage />} />}
        {show("dashboard-config") && <Route path="/dashboards/:id" element={<DashboardDetailPage />} />}
        <Route path="*" element={<Navigate to="/" />} />
      </Routes>
    </AppShell>
  );
}
