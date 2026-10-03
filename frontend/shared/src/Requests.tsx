import { useState } from "react";
import { api, ApiError } from "./api";

export type RequestGroup = { id: number; name: string; rlsValue: string | null; isDefault: boolean; members: number };
export type AccessRequestRow = {
  id: number;
  status: "Pending" | "Approved" | "Rejected" | "Cancelled";
  requestedAtUtc: string;
  comment: string | null;
  decidedAtUtc: string | null;
  decisionNote: string | null;
  decidedBy: string | null;
  assignedGroup: string | null;
  dashboard: { id: number; name: string; rlsEnabled: boolean; ownershipPendingReview: boolean; owner: string | null };
  requester: { id: number; email: string; displayName: string | null; userType: string; title: string | null; department: string | null };
  currentGroup: string | null;
  groups: RequestGroup[] | null;
};

export function whenText(iso: string | null | undefined) {
  if (!iso) return "–";
  const d = new Date(iso.endsWith("Z") || iso.includes("+") ? iso : iso + "Z");
  return d.toLocaleString(undefined, { day: "numeric", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit" });
}

const errorText = (e: unknown) => (e instanceof ApiError ? e.message : e instanceof Error ? e.message : String(e));

/**
 * Pending access requests as cards the approver works through (choose the access group, add a note, approve or
 * reject), followed by recent decisions. Used by owners in the User Portal and by Super Admins in the Admin Portal.
 */
export function RequestReviewList({ rows, canDecide, onChanged, dashboardLink, emptyText }: {
  rows: AccessRequestRow[];
  canDecide: boolean;
  onChanged: (message: string) => void;
  dashboardLink?: (id: number) => string;
  emptyText: string;
}) {
  const pending = rows.filter((r) => r.status === "Pending");
  const decided = rows.filter((r) => r.status !== "Pending");

  return (
    <div className="requests">
      {pending.length === 0 ? (
        <p className="requests-empty">{emptyText}</p>
      ) : (
        <ul className="request-cards">
          {pending.map((r) => <RequestCard key={r.id} row={r} canDecide={canDecide} onChanged={onChanged} dashboardLink={dashboardLink} />)}
        </ul>
      )}

      {decided.length > 0 && (
        <section className="requests-decided">
          <h2>Recent Decisions</h2>
          <div className="requests-table-wrap">
            <table className="requests-table">
              <thead><tr><th>Requester</th><th>Dashboard</th><th>Decision</th><th>Group</th><th>By</th><th>When</th></tr></thead>
              <tbody>
                {decided.map((r) => (
                  <tr key={r.id}>
                    <td>{r.requester.displayName ?? r.requester.email}<div className="req-muted">{r.requester.email}</div></td>
                    <td>{r.dashboard.name}</td>
                    <td><span className={`req-status req-${r.status.toLowerCase()}`}>{r.status === "Cancelled" ? "Withdrawn" : r.status}</span>{r.decisionNote && <div className="req-muted">“{r.decisionNote}”</div>}</td>
                    <td>{r.assignedGroup ?? "–"}</td>
                    <td>{r.decidedBy ?? "–"}</td>
                    <td className="req-muted">{whenText(r.decidedAtUtc)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      )}
    </div>
  );
}

function RequestCard({ row, canDecide, onChanged, dashboardLink }: { row: AccessRequestRow; canDecide: boolean; onChanged: (m: string) => void; dashboardLink?: (id: number) => string }) {
  const groups = row.groups ?? [];
  const needsChoice = row.dashboard.rlsEnabled;
  const usable = needsChoice ? groups.filter((g) => g.rlsValue) : groups.filter((g) => g.isDefault);
  const [groupId, setGroupId] = useState<string>(needsChoice ? "" : String(usable[0]?.id ?? ""));
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState<"approve" | "reject" | null>(null);
  const [error, setError] = useState<string | null>(null);
  const who = row.requester.displayName ?? row.requester.email;

  async function decide(kind: "approve" | "reject") {
    if (kind === "approve" && needsChoice && !groupId) {
      setError("Choose the access group that gives them the right data.");
      return;
    }
    setBusy(kind);
    setError(null);
    try {
      await api(`/api/access-requests/${row.id}/${kind}`, { method: "POST", body: JSON.stringify({ groupId: groupId ? Number(groupId) : null, note: note || null }) });
      onChanged(kind === "approve"
        ? `Approved. ${who} can now open ${row.dashboard.name}.`
        : `Rejected. ${who} has been told${note ? ", with your note" : ""}.`);
    } catch (e) {
      setError(errorText(e));
      setBusy(null);
    }
  }

  const initials = who.split(/[\s.@]+/).filter(Boolean).slice(0, 2).map((p) => p[0]!.toUpperCase()).join("");
  return (
    <li className="request-card">
      <div className="request-who">
        <span className="avatar" aria-hidden="true">{initials}</span>
        <div>
          <strong>{who}</strong>
          <div className="req-muted">{row.requester.email}{row.requester.userType === "External" ? ", external" : ""}</div>
          {(row.requester.title || row.requester.department) && <div className="req-muted">{[row.requester.title, row.requester.department].filter(Boolean).join(", ")}</div>}
        </div>
      </div>

      <div className="request-what">
        <p>
          wants to open{" "}
          {dashboardLink ? <a href={dashboardLink(row.dashboard.id)}>{row.dashboard.name}</a> : <strong>{row.dashboard.name}</strong>}
          <span className="req-muted">, asked {whenText(row.requestedAtUtc)}</span>
        </p>
        {row.comment ? <blockquote className="request-reason">{row.comment}</blockquote> : <p className="req-muted">No reason given.</p>}
        {row.currentGroup && <p className="req-muted">Currently in {row.currentGroup}; approving moves them.</p>}
        {row.dashboard.ownershipPendingReview && <p className="req-warn">The owners are under review, so only a Super Admin can decide this now.</p>}
      </div>

      {canDecide && (
        <div className="request-decide">
          {needsChoice ? (
            <label className="field">
              <span>Access group (decides which data they see)</span>
              <select value={groupId} onChange={(e) => setGroupId(e.target.value)}>
                <option value="">Choose…</option>
                {usable.map((g) => <option key={g.id} value={g.id}>{g.name}: {g.rlsValue}{g.isDefault ? " (default, owners' group)" : ""}</option>)}
              </select>
            </label>
          ) : (
            <p className="req-muted">No row-level security on this dashboard, so they join {usable[0]?.name ?? "the default group"}.</p>
          )}
          <label className="field">
            <span>Note to the requester (optional)</span>
            <input value={note} maxLength={1000} onChange={(e) => setNote(e.target.value)} placeholder="Shown in their notification and email" />
          </label>
          {error && <p className="req-error">{error}</p>}
          <div className="request-buttons">
            <button type="button" className="btn btn-primary" disabled={busy !== null} onClick={() => void decide("approve")}>{busy === "approve" ? "Approving…" : "Approve"}</button>
            <button type="button" className="btn" disabled={busy !== null} onClick={() => void decide("reject")}>{busy === "reject" ? "Rejecting…" : "Reject"}</button>
          </div>
        </div>
      )}
    </li>
  );
}
