import { useState } from "react";
import { RequestReviewList, type AccessRequestRow } from "@vantage/shared";
import { Notice, useApi } from "../ui";

type Data = { pending: number; canDecide: boolean; rows: AccessRequestRow[] };
const filters = [
  { key: "Pending", label: "Pending" },
  { key: "Approved", label: "Approved" },
  { key: "Rejected", label: "Rejected" },
  { key: "Cancelled", label: "Withdrawn" },
  { key: "", label: "All" },
] as const;

/** Every access request. Owners decide in the User Portal; Super Admins can decide here on their behalf. */
export function AccessRequestsPage() {
  const [status, setStatus] = useState("Pending");
  const { data, error, reload } = useApi<Data>(`/api/admin/access-requests${status ? `?status=${status}` : ""}`);
  const [message, setMessage] = useState<string | null>(null);

  if (error) return <Notice tone="error">{error}</Notice>;

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Access Requests</h1>
          <p>
            Requests from the catalogue and the "no access" page. Each one goes to the dashboard's owners, who approve it in the User Portal and choose the access group.
            {data?.canDecide ? " As a Super Admin you can decide here on their behalf." : ""}
          </p>
        </div>
      </div>
      <div className="tabs" role="tablist">
        {filters.map((f) => (
          <button key={f.key} type="button" role="tab" aria-selected={status === f.key} className={`tab ${status === f.key ? "tab-on" : ""}`} onClick={() => { setStatus(f.key); setMessage(null); }}>
            {f.label}{f.key === "Pending" && data && <span className="tab-count">{data.pending}</span>}
          </button>
        ))}
      </div>
      {message && <Notice tone="ok">{message}</Notice>}
      {!data ? <p className="muted">Loading…</p> : (
        <RequestReviewList rows={data.rows} canDecide={data.canDecide} onChanged={(m) => { setMessage(m); reload(); }}
          dashboardLink={(id) => `/dashboards/${id}`}
          emptyText={status === "Pending" ? "No requests are waiting." : "No requests here."} />
      )}
    </>
  );
}
