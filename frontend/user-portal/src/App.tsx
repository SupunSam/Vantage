import { Navigate, Route, Routes } from "react-router-dom";
import { AppShell, can, SignInPage, useSession, type NavSection } from "@vantage/shared";
import { HomePage } from "./pages/HomePage";
import { ViewerPage } from "./pages/ViewerPage";
import { CataloguePage } from "./pages/CataloguePage";
import { ApprovalsPage } from "./pages/ApprovalsPage";
import { OwnerAnalyticsPage } from "./pages/OwnerAnalyticsPage";

/** The Admin Portal runs next to this one: port 8080 in the local Docker build. */
const adminPortalUrl = `${window.location.protocol}//${window.location.hostname}:8080`;
const adminModules = ["publishing", "dashboard-config", "directory", "categories", "groups", "users", "roles", "analytics", "audit", "admin-config", "tenants"];

export function App() {
  const { me, loading } = useSession();

  if (loading) return <p className="content muted">Loading…</p>;
  if (!me) return <SignInPage portalLabel="User Portal" />;

  const isOwner = me.roles.includes("Dashboard Owner");
  const sections: NavSection[] = [
    { items: [{ to: "/", label: "Home", icon: "home", end: true }] },
    {
      title: "Find More",
      items: [
        { to: "/catalogue", label: "Dashboard Catalogue", icon: "search" },
      ],
    },
    ...(isOwner ? [{ title: "Owner Workspace", items: [{ to: "/approvals", label: "Access Requests", icon: "inbox" }, { to: "/owner-analytics", label: "Analytics", icon: "chart" }] }] : []),
  ];
  const showAdmin = adminModules.some((m) => can(me, m));

  return (
    <AppShell portalLabel="Dashboards" sections={sections} otherPortal={showAdmin ? { label: "Open Admin Portal", href: adminPortalUrl } : undefined}>
      <Routes>
        <Route path="/" element={<HomePage />} />
        <Route path="/dashboards/:id" element={<ViewerPage />} />
        <Route path="/catalogue" element={<CataloguePage />} />
        <Route path="/folders" element={<Navigate to="/?tab=folders" replace />} />   {/* Personal Folders is a tab on Home now */}
        {isOwner && <Route path="/approvals" element={<ApprovalsPage />} />}
        {isOwner && <Route path="/owner-analytics" element={<OwnerAnalyticsPage />} />}
        <Route path="*" element={<Navigate to="/" />} />
      </Routes>
    </AppShell>
  );
}
