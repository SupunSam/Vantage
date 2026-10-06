import { useState } from "react";
import { GroupAddReviewList, RequestReviewList, type AccessRequestRow, type GroupAddRow, useFlash, PageSkeleton, ErrorState } from "@vantage/shared";
import { useApi } from "@vantage/shared";

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
  const adds = useApi<{ pending: number; rows: GroupAddRow[] }>(`/api/admin/group-add-requests${status ? `?status=${status}` : ""}`);
  const setMessage = useFlash();

  if (error) return <ErrorState onRetry={reload}>{error}</ErrorState>;

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
            {f.label}{f.key === "Pending" && data && <span className="tab-count">{data.pending + (adds.data?.pending ?? 0)}</span>}
          </button>
        ))}
      </div>
      {adds.data && adds.data.rows.length > 0 && (
        <>
          <h2 className="requests-sub">Access Group Additions</h2>
          <p className="muted small">
            Admins' requests to add people to an access group. The dashboard's owners approve them in the User Portal, all at once or person by person.
            {data?.canDecide ? " As a Super Admin you can use Override Approval in an exception, but you must give a reason: it goes into the audit log and the group's history, and the owners are told." : ""}
          </p>
          <GroupAddReviewList rows={adds.data.rows} groupLink={(id) => `/access-groups/${id}`} onChanged={(m) => { setMessage(m); adds.reload(); reload(); }} />
          <h2 className="requests-sub">Requests From People</h2>
        </>
      )}
      {!data ? <PageSkeleton kind="table" head={false} /> : (
        <RequestReviewList rows={data.rows} canDecide={data.canDecide} onChanged={(m) => { setMessage(m); reload(); }}
          dashboardLink={(id) => `/dashboards/${id}`}
          emptyText={status === "Pending" ? "No requests are waiting." : "No requests here."} />
      )}
    </>
  );
}
