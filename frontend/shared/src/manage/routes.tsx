import { createContext, useContext, type ReactNode } from "react";

/**
 * The dashboard and access group screens are used by two portals: Dashboards Master and Access Groups in the Admin Portal,
 * and My Dashboards in the User Portal (C59). This says where their links go and which list they show.
 */
export type ManageRoutes = {
  mode: "admin" | "owner";
  /** The list page, for "back" links. */
  dashboards: string;
  dashboardsLabel: string;
  dashboard: (id: number) => string;
  group: (id: number, tab?: string) => string;
  /** The group list page; owners have none, they reach groups from their dashboard. */
  groups: string | null;
  /** The API route behind the dashboards grid. */
  listApi: string;
};

const adminRoutes: ManageRoutes = {
  mode: "admin",
  dashboards: "/dashboards",
  dashboardsLabel: "Dashboards Master",
  dashboard: (id) => `/dashboards/${id}`,
  group: (id, tab) => `/access-groups/${id}${tab ? `?tab=${tab}` : ""}`,
  groups: "/access-groups",
  listApi: "/api/admin/dashboards",
};

export const ownerRoutes: ManageRoutes = {
  mode: "owner",
  dashboards: "/my-dashboards",
  dashboardsLabel: "My Dashboards",
  dashboard: (id) => `/my-dashboards/${id}`,
  group: (id, tab) => `/my-dashboards/groups/${id}${tab ? `?tab=${tab}` : ""}`,
  groups: null,
  listApi: "/api/admin/dashboards/mine",
};

const ManageContext = createContext<ManageRoutes>(adminRoutes);

export const useManage = () => useContext(ManageContext);

/** Wraps the screens that use the management styles (`.manage`) and tells them where their links go. */
export function ManageScope({ routes = adminRoutes, children }: { routes?: ManageRoutes; children: ReactNode }) {
  return <ManageContext.Provider value={routes}><div className="manage">{children}</div></ManageContext.Provider>;
}
