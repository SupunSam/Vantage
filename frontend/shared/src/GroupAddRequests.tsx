import { useState } from "react";
import { api, ApiError } from "./api";
import { Icon } from "./Icon";
import { whenText } from "./Requests";
import { Pager, usePaged, GridFrame } from "./Pager";
import { InfoTip } from "./Tip";

export type GroupAddItem = {
  userId: number; email: string; displayName: string | null; userType: string; title: string | null; department: string | null;
  decision: "Pending" | "Approved" | "Rejected" | "Skipped"; result: string | null; currentGroup: string | null;
};
export type GroupAddRow = {
  id: number;
  status: "Pending" | "Approved" | "PartlyApproved" | "Rejected" | "Cancelled";
  /** Add people to the group, or remove them (removals only come from access rules). */
  action: "Add" | "Remove";
  /** The access rule that raised the request, when there is no person behind it. */
  ruleName: string | null;
  createdAtUtc: string; note: string | null; serviceNowReference: string | null; moveFromOtherGroups: boolean;
  decidedAtUtc: string | null; decisionNote: string | null; overrideReason: string | null; decidedBy: string | null;
  requestedBy: { id: number; email: string; displayName: string | null } | null;
  dashboardId: number; dashboard: string; rlsEnabled: boolean; ownershipPendingReview: boolean;
  groupId: number; group: string; rlsValue: string | null; groupIsDefault: boolean;
  items: GroupAddItem[];
  owners: string[];
  /** The viewer may decide this request (an owner, or a Super Admin who gives a reason). */
  canDecide: boolean;
  /** The viewer isn't the one who should normally decide, so a reason is required (Super Admin in place of the owners). */
  needsOverride: boolean;
  isRequester: boolean;
};

const statusLabel: Record<GroupAddRow["status"], string> = { Pending: "Pending", Approved: "Approved", PartlyApproved: "Partly approved", Rejected: "Rejected", Cancelled: "Withdrawn" };
const errorText = (e: unknown) => (e instanceof ApiError ? e.message : e instanceof Error ? e.message : String(e));
const people = (n: number) => `${n} ${n === 1 ? "person" : "people"}`;
const askedBy = (r: GroupAddRow) => (r.requestedBy ? r.requestedBy.displayName ?? r.requestedBy.email : `Access rule “${r.ruleName ?? "deleted rule"}”`);

/**
 * Requests to add people to an access group. One card per request however many people it holds: the approver sees the
 * group, can open the list of people, ticks who to approve (everyone by default) and approves in one go. Used by
 * owners in the User Portal and by Super Admins in the Admin Portal.
 */
export function GroupAddReviewList({ rows, onChanged, groupLink, emptyText }: {
  rows: GroupAddRow[];
  onChanged: (message: string) => void;
  groupLink?: (groupId: number) => string;
  emptyText?: string;
}) {
  const pending = rows.filter((r) => r.status === "Pending");
  const decided = rows.filter((r) => r.status !== "Pending");
  const pendingPage = usePaged(pending, 10);
  const decidedPage = usePaged(decided, 10);
  if (rows.length === 0 && !emptyText) return null;

  return (
    <div className="requests">
      {pending.length === 0 ? (
        emptyText ? <p className="requests-empty">{emptyText}</p> : null
      ) : (
        <ul className="request-cards">
          {pendingPage.rows.map((r) => <GroupAddCard key={r.id} row={r} onChanged={onChanged} groupLink={groupLink} />)}
        </ul>
      )}
      <Pager {...pendingPage.pager} sizes={[10, 25, 50]} />

      {decided.length > 0 && (
        <section className="requests-decided">
          <h2>Recent Group Changes</h2>
          <GridFrame pager={decidedPage.pager}>
            <div className="requests-table-wrap">
              <table className="requests-table">
                <thead><tr><th>Asked by</th><th>Dashboard and group</th><th>People</th><th>Decision</th><th>By</th><th>When</th></tr></thead>
                <tbody>
                  {decidedPage.rows.map((r) => {
                    const approved = r.items.filter((i) => i.decision === "Approved").length;
                    return (
                      <tr key={r.id}>
                        <td>{askedBy(r)}{r.action === "Remove" && <div className="req-muted">Removal</div>}</td>
                        <td>{r.dashboard}<div className="req-muted">{groupLink ? <a href={groupLink(r.groupId)}>{r.group}</a> : r.group}</div></td>
                        <td>{r.status === "Cancelled" ? people(r.items.length) : `${approved} of ${r.items.length}${r.action === "Remove" ? " removed" : ""}`}</td>
                        <td>
                          <span className={`req-status req-${r.status === "PartlyApproved" ? "approved" : r.status.toLowerCase()}`}>{statusLabel[r.status]}</span>
                          {r.decisionNote && <div className="req-muted">“{r.decisionNote}”</div>}
                          {r.overrideReason && <div className="req-warn">Super Admin decided in place of the owners: {r.overrideReason}</div>}
                        </td>
                        <td>{r.decidedBy ?? "–"}</td>
                        <td className="req-muted">{whenText(r.decidedAtUtc)}</td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          </GridFrame>
        </section>
      )}
    </div>
  );
}

function GroupAddCard({ row, onChanged, groupLink }: { row: GroupAddRow; onChanged: (m: string) => void; groupLink?: (id: number) => string }) {
  const [open, setOpen] = useState(false);
  // A Super Admin deciding in the owners' place is the exception: the decision controls stay hidden until they choose to override.
  const [overriding, setOverriding] = useState(false);
  const [picked, setPicked] = useState<Set<number>>(() => new Set(row.items.map((i) => i.userId)));
  const [note, setNote] = useState("");
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState<"approve" | "reject" | "cancel" | null>(null);
  const [error, setError] = useState<string | null>(null);
  const total = row.items.length;
  const who = askedBy(row);
  const removing = row.action === "Remove";
  const allPicked = picked.size === total;
  const needReason = row.needsOverride;
  const missingReason = needReason && !reason.trim();

  const toggle = (id: number) => setPicked((s) => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n; });

  async function decide(kind: "approve" | "reject") {
    if (missingReason) { setError("Give a reason for deciding in place of the owners. It is saved in the audit log and the group's history."); return; }
    setBusy(kind);
    setError(null);
    try {
      await api(`/api/group-add-requests/${row.id}/${kind}`, {
        method: "POST",
        body: JSON.stringify({ userIds: kind === "approve" ? [...picked] : undefined, note: note || null, overrideReason: needReason ? reason : null }),
      });
      onChanged(kind === "reject" ? (removing ? `Rejected. Nobody was removed from ${row.group}.` : `Rejected. Nobody was added to ${row.group}.`)
        : removing ? (allPicked ? `Approved. All ${people(total)} were removed from ${row.group}.` : `Approved ${picked.size} of ${total}. The rest keep their access to ${row.group}.`)
        : allPicked ? `Approved. All ${people(total)} were added to ${row.group}.` : `Approved ${picked.size} of ${total}. The rest were not added to ${row.group}.`);
    } catch (e) { setError(errorText(e)); setBusy(null); }
  }

  async function withdraw() {
    setBusy("cancel");
    setError(null);
    try { await api(`/api/group-add-requests/${row.id}/cancel`, { method: "POST" }); onChanged("The request was withdrawn."); } catch (e) { setError(errorText(e)); setBusy(null); }
  }

  return (
    <li className="request-card group-add-card">
      <div className="group-add-main">
        <p className="group-add-title">
          <strong>{who}</strong> wants to {removing ? "remove" : "add"} <strong>{people(total)}</strong> {removing ? "from" : "to"} the access group{" "}
          {groupLink ? <a href={groupLink(row.groupId)}><strong>{row.group}</strong></a> : <strong>{row.group}</strong>}
        </p>
        <p className="req-muted">
          On <strong>{row.dashboard}</strong>
          {!removing && row.rlsEnabled && row.rlsValue && <> · they will see the data for RLS role <code>{row.rlsValue}</code></>}
          {!removing && row.rlsEnabled && !row.rlsValue && <> · this group has no RLS value yet</>}
          {removing && <> · approving takes away their access to this dashboard</>}
          {row.groupIsDefault && <> · default group (holds the owners)</>}
          {" "}· asked {whenText(row.createdAtUtc)}
          {row.serviceNowReference && <> · ticket <code>{row.serviceNowReference}</code></>}
        </p>
        {row.note && <blockquote className="request-reason">{row.note}</blockquote>}
        {!removing && row.moveFromOtherGroups && row.items.some((i) => i.currentGroup) && <p className="req-muted">Some people would move<InfoTip label="About moving">Approving moves people who are already in another group of this dashboard.</InfoTip></p>}
        {row.ownershipPendingReview && <p className="req-warn">The owners are under review, so only a Super Admin can decide this now.</p>}

        <button type="button" className="group-add-toggle" aria-expanded={open} onClick={() => setOpen(!open)}>
          <Icon name={open ? "chevronDown" : "chevronRight"} size={16} />
          {open ? "Hide the people" : `Show the ${people(total)}`}
          <span className="req-muted"> · {picked.size} of {total} ticked{removing ? " to remove" : ""}</span>
        </button>

        {open && (
          <div className="group-add-people">
            {row.canDecide && (
              <div className="group-add-tools">
                <button type="button" className="link" onClick={() => setPicked(new Set(row.items.map((i) => i.userId)))} disabled={allPicked}>Select All</button>
                <button type="button" className="link" onClick={() => setPicked(new Set())} disabled={picked.size === 0}>Clear All</button>
              </div>
            )}
            <ul className="group-add-list">
              {row.items.map((i) => (
                <li key={i.userId}>
                  <label className={row.canDecide ? "" : "group-add-readonly"}>
                    {row.canDecide && <input type="checkbox" checked={picked.has(i.userId)} onChange={() => toggle(i.userId)} />}
                    <span className="group-add-person">
                      <strong>{i.displayName ?? i.email}</strong>
                      <span className="req-muted">{i.email}{i.userType === "External" ? ", external" : ""}{(i.title || i.department) ? ` · ${[i.title, i.department].filter(Boolean).join(", ")}` : ""}</span>
                      {!removing && i.currentGroup && row.moveFromOtherGroups && <span className="req-warn">Moves from {i.currentGroup}</span>}
                    </span>
                  </label>
                </li>
              ))}
            </ul>
          </div>
        )}
      </div>

      <div className="request-decide">
        {row.canDecide && (!row.needsOverride || overriding) ? (
          <>
            {needReason && (
              <div className="override-note">
                <strong>Override of the owners' approval<InfoTip label="About overrides">Only for exceptions. Your reason is saved in the audit log and the group's history, and the owners are told.</InfoTip></strong>
              </div>
            )}
            {needReason && (
              <label className="field">
                <span>Reason for the override (required)</span>
                <textarea rows={2} value={reason} maxLength={1000} onChange={(e) => setReason(e.target.value)} placeholder="e.g. Both owners are on leave and the month-end report is due today" />
              </label>
            )}
            <label className="field">
              <span>{row.requestedBy ? "Note to the person who asked (optional)" : "Note (optional)"}</span>
              <input value={note} maxLength={1000} onChange={(e) => setNote(e.target.value)} placeholder="Shown in their notification and email" />
            </label>
            {error && <p className="req-error">{error}</p>}
            <div className="request-buttons">
              <button type="button" className="btn btn-primary" disabled={busy !== null || picked.size === 0} onClick={() => { if (!open) setOpen(true); void decide("approve"); }}>
                {busy === "approve" ? "Approving…" : `${needReason ? (removing ? "Override and Approve Removal of" : "Override and Approve") : removing ? "Approve Removal of" : "Approve"} ${allPicked ? `All ${total}` : `${picked.size} of ${total}`}`}
              </button>
              <button type="button" className="btn" disabled={busy !== null} onClick={() => void decide("reject")}>{busy === "reject" ? "Rejecting…" : needReason ? (removing ? "Override and Keep All" : "Override and Reject All") : removing ? "Keep Everyone" : "Reject All"}</button>
            </div>
            {picked.size === 0 && <p className="req-muted">Nobody is ticked. Tick the people to {removing ? "remove" : "approve"}, or {removing ? "keep everyone" : "reject the whole request"}.</p>}
            {needReason && <button type="button" className="link" onClick={() => { setOverriding(false); setError(null); }}>Cancel the Override</button>}
          </>
        ) : (
          <>
            <p className="req-muted">
              Waiting for {row.owners.length > 0 ? <strong>{row.owners.join(" or ")}</strong> : "an owner"}
              <InfoTip label="Who decides">{row.owners.length === 0 ? "This dashboard has no owner right now. " : ""}Owners approve requests in the User Portal.</InfoTip>
            </p>
            {row.canDecide && row.needsOverride && (
              <button type="button" className="btn" onClick={() => { setOverriding(true); setOpen(true); }}>Override Approval…</button>
            )}
            {error && <p className="req-error">{error}</p>}
          </>
        )}
        {(row.isRequester || (row.requestedBy === null && row.canDecide && row.needsOverride)) && <button type="button" className="btn btn-quiet" disabled={busy !== null} onClick={() => void withdraw()}>{busy === "cancel" ? "Withdrawing…" : "Withdraw Request"}</button>}
      </div>
    </li>
  );
}
