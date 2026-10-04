import { useCallback, useEffect, useState } from "react";
import { api, ApiError, GroupAddReviewList, RequestReviewList, type AccessRequestRow, type GroupAddRow } from "@vantage/shared";

/** Owner workspace: access requests for the dashboards this person owns. */
export function ApprovalsPage() {
  const [rows, setRows] = useState<AccessRequestRow[] | null>(null);
  const [groupAdds, setGroupAdds] = useState<GroupAddRow[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  const load = useCallback(() => {
    api<AccessRequestRow[]>("/api/access-requests/to-approve").then(setRows).catch((e) => setError(e instanceof ApiError ? e.message : String(e)));
    api<GroupAddRow[]>("/api/group-add-requests/to-approve").then(setGroupAdds).catch((e) => setError(e instanceof ApiError ? e.message : String(e)));
  }, []);
  useEffect(load, [load]);

  if (error) return <p className="notice notice-error">{error}</p>;
  if (!rows || !groupAdds) return <p className="muted">Loading requests…</p>;
  const pendingSingles = rows.filter((r) => r.status === "Pending").length;
  const pendingAdds = groupAdds.filter((r) => r.status === "Pending").length;
  const pending = pendingSingles + pendingAdds;

  return (
    <>
      <div className="home-head">
        <h1>Access Requests</h1>
        <p className="muted">
          {pending === 0 ? "Nothing waiting for you." : `${pending} request${pending === 1 ? "" : "s"} waiting for your decision.`}{" "}
          As an owner you decide who can open your dashboards, and on dashboards with row-level security, which data they see.
        </p>
      </div>
      {message && <p className="notice notice-ok">{message}</p>}
      {groupAdds.length > 0 && (
        <section className="home-section">
          <h2>Access Group Additions{pendingAdds > 0 && <span className="muted"> {pendingAdds} waiting</span>}</h2>
          <p className="muted small">An admin, or an access rule, asked to add people to (or remove people from) an access group of one of your dashboards. Nothing changes until you approve. Open the list to untick anyone you don't want to approve.</p>
          <GroupAddReviewList rows={groupAdds} onChanged={(m) => { setMessage(m); load(); }} />
        </section>
      )}
      <h2 className="approvals-sub">Requests From People</h2>
      <RequestReviewList rows={rows} canDecide onChanged={(m) => { setMessage(m); load(); }}
        dashboardLink={(id) => `/dashboards/${id}`} emptyText="No requests are waiting. When someone asks for one of your dashboards, it appears here and you get an email." />
    </>
  );
}
