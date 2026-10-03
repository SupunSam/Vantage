import { Link } from "react-router-dom";
import { useApi } from "../ui";

type Setup = { roles: number; customRoles: number; users: number; tenants: number; verifiedTenants: number; categories: number; activeDashboards: number; uncategorised: number };

/** The setup path, in order: roles, users, a verified tenant, then publishing. Each step shows where it stands. */
export function OverviewPage() {
  const { data, error } = useApi<Setup>("/api/admin/setup");
  if (error) return <p className="notice notice-error">{error}</p>;
  if (!data) return <p className="muted">Loading…</p>;

  const steps = [
    {
      title: "Roles and Permissions",
      to: "/roles",
      done: true,
      status: `${data.roles} roles, ${data.customRoles} custom`,
      text: "Check what each built-in role can do, and add roles such as a BPI publisher or ASD help desk.",
      action: "Open roles",
    },
    {
      title: "Users",
      to: "/users",
      done: data.users > 1,
      status: `${data.users} user${data.users === 1 ? "" : "s"}`,
      text: "Add internal users by email (details come from HRMS) and external users, one at a time, pasted or from Excel.",
      action: "Open users",
    },
    {
      title: "Power BI Tenant",
      to: "/tenants",
      done: data.verifiedTenants > 0,
      status: data.tenants === 0 ? "None added" : `${data.verifiedTenants} of ${data.tenants} verified`,
      text: "Add the service principal and workspace, then verify the connection and every permission publishing needs.",
      action: "Open tenants",
    },
    {
      title: "Categories",
      to: "/categories",
      done: data.categories > 0,
      status: data.categories === 0 ? "None yet" : `${data.categories} categor${data.categories === 1 ? "y" : "ies"}`,
      text: "Build the Primary, Secondary and Tertiary tree that dashboards are filed under and users browse by.",
      action: "Open categories",
    },
    {
      title: "Publish a Dashboard",
      to: "/publish",
      done: data.activeDashboards > 0,
      status: `${data.activeDashboards} live`,
      text: data.uncategorised > 0
        ? `Upload a .pbix into a verified workspace, then check it in Dashboards Master. ${data.uncategorised} dashboard${data.uncategorised === 1 ? " still needs" : "s still need"} a category.`
        : "Upload a .pbix into a verified workspace with a category and thumbnail, then check it in Dashboards Master.",
      action: "Publish a dashboard",
      blocked: data.verifiedTenants === 0 ? "Needs a verified tenant first." : data.categories === 0 ? "Needs a category first." : null,
    },
  ];

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Set Up the Portal</h1>
          <p>Work through these in order. Each step builds on the one before.</p>
        </div>
      </div>
      <ol className="steps">
        {steps.map((s, i) => (
          <li key={s.title} className={`step ${s.done ? "step-done" : ""}`}>
            <span className="step-num" aria-hidden="true">{s.done ? "✓" : i + 1}</span>
            <div className="step-body">
              <div className="step-title">
                <h2>{s.title}</h2>
                <span className="muted small">{s.status}</span>
              </div>
              <p>{s.text}</p>
              {s.blocked ? <p className="small muted">{s.blocked}</p> : <Link className="btn" to={s.to}>{s.action}</Link>}
            </div>
          </li>
        ))}
      </ol>
    </>
  );
}
