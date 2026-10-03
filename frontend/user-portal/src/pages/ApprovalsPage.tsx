import { useCallback, useEffect, useState } from "react";
import { api, ApiError, RequestReviewList, type AccessRequestRow } from "@vantage/shared";

/** Owner workspace: access requests for the dashboards this person owns. */
export function ApprovalsPage() {
  const [rows, setRows] = useState<AccessRequestRow[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  const load = useCallback(() => {
    api<AccessRequestRow[]>("/api/access-requests/to-approve").then(setRows).catch((e) => setError(e instanceof ApiError ? e.message : String(e)));
  }, []);
  useEffect(load, [load]);

  if (error) return <p className="notice notice-error">{error}</p>;
  if (!rows) return <p className="muted">Loading requests…</p>;
  const pending = rows.filter((r) => r.status === "Pending").length;

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
      <RequestReviewList rows={rows} canDecide onChanged={(m) => { setMessage(m); load(); }}
        dashboardLink={(id) => `/dashboards/${id}`} emptyText="No requests are waiting. When someone asks for one of your dashboards, it appears here and you get an email." />
    </>
  );
}
