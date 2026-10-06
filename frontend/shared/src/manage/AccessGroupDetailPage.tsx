import { useEffect, useMemo, useState, type FormEvent } from "react";
import { Link, useNavigate, useParams, useSearchParams } from "react-router-dom";
import { Icon } from "../Icon";
import { GridFrame, Pager, usePaged } from "../Pager";
import { useFlash } from "../Toast";
import { api, apiObjectUrl } from "../api";
import { useGridPageSize } from "../session";
import { errorText, MenuButton, Modal, Notice, StatusPill, useApi, when, FilePicker } from "./ui";
import { NewGroupModal, type GroupRow, type MemberResult } from "./AccessGroupsPage";
import { RulesPanel } from "./RulesPanel";
import { useManage } from "./routes";
import { Avatar } from "./ui";
import { PageSkeleton, ErrorState } from "../Feedback";
import { useConfirm } from "../Feedback";

type Member = { userId: number; email: string; displayName: string | null; userType: string; userStatus: string; source: string; addedAtUtc: string; addedBy: string | null };
type Sibling = { id: number; name: string; rlsValue: string | null; status: string; isDefault: boolean };
type Detail = {
  group: {
    id: number; name: string; rlsValue: string | null; isDefault: boolean; status: string; createdAtUtc: string; clonedFrom: string | null; createdBy: string | null; ruleCount: number;
    dashboard: { id: number; name: string; code: string; status: string; rlsEnabled: boolean; primaryOwnerId: number | null; backupOwnerId: number | null; ownershipPendingReview: boolean };
    members: Member[];
    siblings: Sibling[];
  };
  pendingRequests: { id: number; createdAtUtc: string; serviceNowReference: string | null; note: string | null; requestedById: number | null; action: "Add" | "Remove"; requestedBy: string; count: number; people: string[] }[];
  myId: number;
  canEdit: boolean;
  isSuperAdmin: boolean;
  noRlsMessage: string;
};
/** What adding people did: sent to the owners (Requested), or nothing could be added. */
type AddOutcome = { mode: "Requested" | "Added" | "Nothing"; requestId: number | null; results: MemberResult[] };

const tabs = [
  { key: "members", label: "Members" },
  { key: "rules", label: "Rules" },
  { key: "history", label: "History" },
] as const;
type Tab = (typeof tabs)[number]["key"];

const sourceLabel: Record<string, string> = {
  Manual: "Added by an admin", BulkUpload: "Added from a list", Clone: "Copied from another group", AccessRequest: "Access request",
  OwnerAuto: "Owner", SuperAdminSelf: "Added themselves", Migration: "Migrated", AccessRule: "Proposed by an access rule",
};

/** One access group: its settings, then tabs for its members (add, move, remove), its rules and its history. */
export function AccessGroupDetailPage() {
  const routes = useManage();
  const confirm = useConfirm();
  const { id } = useParams();
  const navigate = useNavigate();
  const { data, error, reload } = useApi<Detail>(`/api/admin/access-groups/${id}`);
  const { data: allGroups } = useApi<GroupRow[]>("/api/admin/access-groups");
  const setMessage = useFlash();
  const [results, setResults] = useState<MemberResult[] | null>(null);
  const [adding, setAdding] = useState(false);
  const [dialog, setDialog] = useState<"copy" | "clone" | null>(null);
  const [q, setQ] = useState("");
  const [search, setSearch] = useSearchParams();
  const tab: Tab = search.get("tab") === "rules" ? "rules" : search.get("tab") === "history" ? "history" : "members";

  useEffect(() => { setMessage(null); setResults(null); setAdding(false); }, [id]);

  const members = useMemo(() => {
    const needle = q.trim().toLowerCase();
    return (data?.group.members ?? []).filter((m) => !needle || `${m.displayName ?? ""} ${m.email}`.toLowerCase().includes(needle));
  }, [data, q]);
  const memberPage = usePaged(members, useGridPageSize(), q);

  if (error) return <ErrorState>{error}</ErrorState>;
  if (!data) return <PageSkeleton kind="detail" />;
  const g = data.group;
  const d = g.dashboard;
  const editable = data.canEdit && g.status !== "Retired" && d.status !== "Retired";
  const live = editable && g.status === "Active";
  const ok = (text: string) => { setMessage({ ok: true, text }); reload(); };
  const fail = (e: unknown) => setMessage({ ok: false, text: errorText(e) });
  const isOwner = (userId: number) => userId === d.primaryOwnerId || userId === d.backupOwnerId;
  const moveTargets = g.siblings.filter((s) => s.status === "Active");

  async function remove(m: Member) {
    if (!(await confirm({ title: "Remove Member", message: `Remove ${m.displayName ?? m.email} from ${g.name}? They lose access to ${d.name}.`, confirmLabel: "Remove", danger: true }))) return;
    try {
      await api(`/api/admin/access-groups/${g.id}/members/${m.userId}`, { method: "DELETE" });
      ok(`Removed ${m.displayName ?? m.email}.`);
    } catch (e) { fail(e); }
  }

  function handleOutcome(o: AddOutcome) {
    const sent = o.results.filter((r) => r.outcome === "Requested").length;
    const added = o.results.filter((r) => r.outcome === "Added" || r.outcome === "Moved").length;
    if (o.mode === "Added") setMessage({ ok: true, text: `Added ${added} ${added === 1 ? "person" : "people"} to ${g.name}.` });
    else if (o.mode === "Requested") setMessage({ ok: true, text: `Sent to the owners of ${d.name} as one request for ${sent} ${sent === 1 ? "person" : "people"}. They are added only when the owners approve.` });
    else setMessage({ ok: false, text: "Nobody could be added. See the list below for why." });
    setResults(o.results);
    reload();
  }

  async function withdraw(requestId: number) {
    try { await api(`/api/group-add-requests/${requestId}/cancel`, { method: "POST" }); ok("The request was withdrawn."); } catch (e) { fail(e); }
  }

  async function move(m: Member, target: Sibling) {
    try {
      await api(`/api/admin/access-groups/${g.id}/members/${m.userId}/move`, { method: "POST", body: JSON.stringify({ targetGroupId: target.id }) });
      ok(`Moved ${m.displayName ?? m.email} to ${target.name}.`);
    } catch (e) { fail(e); }
  }

  return (
    <>
      <div className="page-head">
        <div>
          <p className="crumb">{routes.groups ? <Link to={routes.groups}>Access Groups</Link> : <Link to={routes.dashboard(d.id)}>{routes.dashboardsLabel}</Link>}</p>
          <h1 className="group-title">{g.name}</h1>
          <p>
            Gives access to <Link to={routes.dashboard(d.id)}>{d.name}</Link>
            {g.isDefault ? ", as its default group (holds the owners)." : "."}
            {g.clonedFrom && <> Members first copied from {g.clonedFrom}.</>}
          </p>
          <div className="detail-badges">
            <StatusPill status={g.status} />
            <span className={`pill ${d.rlsEnabled ? "pill-brand" : "pill-neutral"}`}>{d.rlsEnabled ? "RLS on" : "No RLS"}</span>
            {g.rlsValue
              ? <span className="pill pill-neutral" title={d.rlsEnabled ? "Sent as the Power BI role" : "Not used: the dashboard has no RLS"}>RLS value <code className={d.rlsEnabled ? "" : "rls-ignored"}>{g.rlsValue}</code></span>
              : d.rlsEnabled && <span className="pill pill-bad">No RLS value: members can't open the dashboard</span>}
          </div>
        </div>
        {editable && (
          <MenuButton label="More Actions" items={[
            { label: "Copy Members from Another Group", hint: "Adds that group's people here", disabled: g.status !== "Active", onSelect: () => setDialog("copy") },
            { label: "Clone into a New Group", hint: "A new group with these members", disabled: g.members.length === 0, onSelect: () => setDialog("clone") },
          ]} />
        )}
      </div>

      {d.ownershipPendingReview && <Notice>{d.name}'s owners are under review, so access changes are paused{data.isSuperAdmin ? " for everyone except Super Admins" : " until a Super Admin confirms them"}.</Notice>}
      {g.status === "Inactive" && <Notice>This group is inactive: its members keep their place but can't open {d.name}. Set it to Active under Details.</Notice>}

      <GroupDetails data={data} editable={editable} onSaved={ok} />

      <div className="tabs" role="tablist">
        {tabs.filter((t) => t.key !== "rules" || routes.mode === "admin").map((t) => (
          <button key={t.key} type="button" role="tab" aria-selected={tab === t.key} className={`tab ${tab === t.key ? "tab-on" : ""}`}
            onClick={() => { setMessage(null); setSearch(t.key === "members" ? {} : { tab: t.key }, { replace: true }); }}>
            {t.label}
            {t.key === "members" && <span className="tab-count">{g.members.length}</span>}
            {t.key === "rules" && g.ruleCount > 0 && <span className="tab-count">{g.ruleCount}</span>}
          </button>
        ))}
      </div>

      {tab === "members" && (
        <>
        <section className="panel">
          <div className="panel-row">
            <h2>Members <span className="muted count">{g.members.length}</span></h2>
            <div className="actions">
              {g.members.length > 8 && (
                <span className="input-icon input-compact"><Icon name="search" size={16} /><input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Find a member" aria-label="Find a member" /></span>
              )}
              {live && !adding && <button className="btn btn-primary" type="button" onClick={() => { setAdding(true); setResults(null); }}><Icon name="plus" size={18} /> Add People</button>}
            </div>
          </div>

          {adding && (
            <AddPeople groupId={g.id} hasSiblings={g.siblings.length > 0}
              onCancel={() => setAdding(false)}
              onDone={(o) => { handleOutcome(o); setAdding(false); }}
              onError={fail} />
          )}
          {results && <ResultSummary results={results} onClose={() => setResults(null)} />}
          {data.pendingRequests.length > 0 && (
            <div className="pending-adds" aria-label="Waiting for the owners' approval">
              {data.pendingRequests.map((p) => (
                <div key={p.id} className="pending-add">
                  <span>
                    <strong>{p.count} {p.count === 1 ? "person" : "people"}</strong> waiting for the owners' approval to {p.action === "Remove" ? "be removed" : "be added"}
                    <span className="muted small block">{p.people.join(", ")}{p.count > p.people.length ? " and others" : ""} · asked by {p.requestedBy}, {when(p.createdAtUtc)}{p.serviceNowReference && <>, ticket <code>{p.serviceNowReference}</code></>}</span>
                  </span>
                  {(p.requestedById === data.myId || data.isSuperAdmin) && <button type="button" className="btn btn-quiet" onClick={() => void withdraw(p.id)}>Withdraw Request</button>}
                </div>
              ))}
            </div>
          )}

          {g.members.length === 0 ? (
            <p className="pop-empty">No members yet.{live ? " Add people by name, a pasted list of emails, or an Excel file." : ""}</p>
          ) : (
            <GridFrame pager={memberPage.pager}>
            <div className="table-wrap">
            <table className="grid member-grid">
              <thead><tr><th>Person</th><th>Type</th><th>How they were added</th><th>Added</th>{live && <th />}</tr></thead>
              <tbody>
                {memberPage.rows.map((m) => (
                  <tr key={m.userId}>
                    <td>
                      <span className="person">
                        <Avatar name={m.displayName} email={m.email} />
                        <span><strong>{m.displayName ?? m.email}</strong>{isOwner(m.userId) && <span className="pill pill-brand pill-xs">Owner</span>}<span className="muted small block">{m.displayName ? m.email : "Name not filled in yet"}</span></span>
                      </span>
                    </td>
                    <td className="small">{m.userType}{m.userStatus === "PendingSetup" && <div className="muted">Setup pending</div>}{m.userStatus === "Inactive" && <div className="warn-text">Inactive user</div>}</td>
                    <td className="small">{sourceLabel[m.source] ?? m.source}{m.addedBy && m.source !== "SuperAdminSelf" && <div className="muted">by {m.addedBy}</div>}</td>
                    <td className="small muted">{when(m.addedAtUtc)}</td>
                    {live && (
                      <td className="cell-actions"><span className="member-actions">
                        {moveTargets.length > 0 && (
                          <MenuButton label="Move" items={moveTargets.map((s) => ({ label: s.name, hint: s.rlsValue ? `RLS ${s.rlsValue}` : s.isDefault ? "Default group" : undefined, onSelect: () => void move(m, s) }))} />
                        )}
                        <button type="button" className="icon-btn icon-btn-sm icon-btn-danger" aria-label={`Remove ${m.displayName ?? m.email}`}
                          title={isOwner(m.userId) ? "Owners keep access. Change the owner in Dashboards Master first." : "Remove from this group"}
                          disabled={isOwner(m.userId)} onClick={() => void remove(m)}>
                          <Icon name="trash" size={18} />
                        </button>
                      </span></td>
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
            </div>
            </GridFrame>
          )}
        </section>
        </>
      )}

      {tab === "rules" && (
        <section className="panel">
          <RulesPanel fixed={{ groupId: g.id, group: g.name, dashboard: d.name, dashboardId: d.id }} onChanged={reload} />
        </section>
      )}

      {tab === "history" && <HistoryPanel groupId={g.id} created={`Created ${when(g.createdAtUtc)}${g.createdBy ? ` by ${g.createdBy}` : ""}.`} />}

      {dialog === "copy" && allGroups && (
        <CopyDialog groups={allGroups.filter((x) => x.id !== g.id && x.status === "Active" && x.members > 0)} onClose={() => setDialog(null)}
          onCopy={async (sourceId) => {
            const o = await api<AddOutcome>(`/api/admin/access-groups/${g.id}/copy-members`, { method: "POST", body: JSON.stringify({ sourceGroupId: sourceId }) });
            setDialog(null); handleOutcome(o);
          }} />
      )}
      {dialog === "clone" && allGroups && (
        <NewGroupModal title={`Clone ${g.name}`} groups={allGroups} copyFromGroupId={g.id} dashboardId={d.rlsEnabled ? d.id : undefined}
          onClose={() => setDialog(null)} onCreated={(newId) => { setDialog(null); navigate(routes.group(newId)); }} />
      )}
    </>
  );
}

// ---------------------------------------------------------------- Details

function GroupDetails({ data, editable, onSaved }: { data: Detail; editable: boolean; onSaved: (text: string) => void }) {
  const confirm = useConfirm();
  const routes = useManage();
  const g = data.group;
  const d = g.dashboard;
  const [editing, setEditing] = useState(false);
  const [name, setName] = useState(g.name);
  const [rls, setRls] = useState(g.rlsValue ?? "");
  const [active, setActive] = useState(g.status === "Active");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  function start() {
    setName(g.name);
    setRls(g.rlsValue ?? "");
    setActive(g.status === "Active");
    setError(null);
    setEditing(true);
  }

  async function save(e: FormEvent) {
    e.preventDefault();
    if (!active && g.status === "Active" && !(await confirm({ title: "Set Group Inactive", message: `Set ${g.name} to Inactive? Its ${g.members.length} member(s) lose access to ${d.name} until it's active again.`, confirmLabel: "Set Inactive", danger: true }))) return;
    setBusy(true);
    setError(null);
    try {
      await api(`/api/admin/access-groups/${g.id}`, { method: "PUT", body: JSON.stringify({ name, rlsValue: rls || null, active }) });
      setEditing(false);
      onSaved("Details saved.");
    } catch (err) {
      setError(errorText(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="panel">
      <div className="panel-row">
        <h2>Details</h2>
        {editable && !editing && <button className="btn" type="button" onClick={start}><Icon name="edit" size={18} /> Edit Details</button>}
      </div>
      {!editing ? (
        <dl className="facts facts-wide">
          <dt>Group name</dt><dd>{g.name}{g.isDefault && <span className="muted small"> (default group: the name is fixed)</span>}</dd>
          <dt>Dashboard</dt><dd><Link to={routes.dashboard(d.id)}>{d.name}</Link> <span className="muted small">{d.rlsEnabled ? "uses row-level security" : "has no row-level security"}</span></dd>
          <dt>RLS value</dt>
          <dd>
            {g.rlsValue ? <code className={d.rlsEnabled ? "" : "rls-ignored"}>{g.rlsValue}</code> : <span className={d.rlsEnabled ? "warn-text" : "muted"}>None{d.rlsEnabled ? ": members can't open the dashboard" : ""}</span>}
            {!d.rlsEnabled && g.rlsValue && <span className="muted small"> not used</span>}
          </dd>
          <dt>Status</dt><dd><StatusPill status={g.status} /></dd>
          <dt>Created</dt><dd className="muted">{when(g.createdAtUtc)}{g.createdBy ? ` by ${g.createdBy}` : ""}{g.clonedFrom ? `, members copied from ${g.clonedFrom}` : ""}</dd>
        </dl>
      ) : (
        <form className="form-sections" onSubmit={save}>
          <div className="cols cols-3">
            <label className="field">
              <span>Group name {g.isDefault ? <span className="optional">(fixed for the default group)</span> : <span className="optional">(unique, up to 40)</span>}</span>
              <input required maxLength={40} pattern="[A-Za-z0-9_\-]+" title="Letters, digits, hyphens and underscores" value={name} disabled={g.isDefault} onChange={(e) => setName(e.target.value)} />
            </label>
            <label className="field">
              <span>RLS value {d.rlsEnabled ? "(required)" : <span className="optional">(not used: no RLS)</span>}</span>
              <input required={d.rlsEnabled} maxLength={100} value={rls} onChange={(e) => setRls(e.target.value)} placeholder="Role name from the .pbix, e.g. Region_North" />
            </label>
            <label className="field">
              <span>Status {g.isDefault && <span className="optional">(the default group stays active)</span>}</span>
              <select value={active ? "Active" : "Inactive"} disabled={g.isDefault} onChange={(e) => setActive(e.target.value === "Active")}>
                <option>Active</option>
                <option>Inactive</option>
              </select>
            </label>
          </div>
          <p className="muted small">
            {d.rlsEnabled ? "The RLS value must match a role in the report exactly; list several roles with commas. " : ""}
            An inactive group keeps its members, but they can't open the dashboard.
          </p>
          {error && <Notice tone="error">{error}</Notice>}
          <div className="actions">
            <button className="btn btn-primary" type="submit" disabled={busy}>{busy ? "Saving…" : "Save Details"}</button>
            <button className="btn btn-quiet" type="button" onClick={() => setEditing(false)}>Cancel</button>
          </div>
        </form>
      )}
    </section>
  );
}

// ---------------------------------------------------------------- Adding people

type Found = { id: number; email: string; displayName: string | null; userType: string; status: string };

function AddPeople({ groupId, hasSiblings, onCancel, onDone, onError }: { groupId: number; hasSiblings: boolean; onCancel: () => void; onDone: (o: AddOutcome) => void; onError: (e: unknown) => void }) {
  const [mode, setMode] = useState<"one" | "paste" | "excel">("one");
  const [text, setText] = useState("");
  const [file, setFile] = useState<File | null>(null);
  const [move, setMove] = useState(false);
  const [ticket, setTicket] = useState("");
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [q, setQ] = useState("");
  const [found, setFound] = useState<Found[]>([]);

  useEffect(() => {
    if (mode !== "one" || q.trim().length < 2) { setFound([]); return; }
    const t = window.setTimeout(() => {
      api<Found[]>(`/api/admin/access-groups/user-search?q=${encodeURIComponent(q.trim())}`).then(setFound).catch(() => setFound([]));
    }, 200);
    return () => window.clearTimeout(t);
  }, [q, mode]);

  async function submit(emails: string) {
    setBusy(true);
    try {
      onDone(await api<AddOutcome>(`/api/admin/access-groups/${groupId}/members`, { method: "POST", body: JSON.stringify({ emails, moveFromOtherGroups: move, serviceNowReference: ticket || null, note: note || null }) }));
    } catch (e) { onError(e); } finally { setBusy(false); }
  }

  async function submitExcel(e: FormEvent) {
    e.preventDefault();
    if (!file) return;
    setBusy(true);
    const body = new FormData();
    body.append("file", file);
    body.append("moveFromOtherGroups", String(move));
    if (ticket) body.append("serviceNowReference", ticket);
    if (note) body.append("note", note);
    try {
      onDone(await api<AddOutcome>(`/api/admin/access-groups/${groupId}/members/excel`, { method: "POST", body }));
    } catch (err) { onError(err); } finally { setBusy(false); }
  }

  async function template() {
    const url = await apiObjectUrl("/api/admin/access-groups/members-template");
    const a = document.createElement("a");
    a.href = url;
    a.download = "access-group-members.xlsx";
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
  }

  const emailLike = /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(q.trim());
  const pasted = text.split(/[\s,;]+/).filter((x) => x.includes("@")).length;

  return (
    <div className="add-people">
      <div className="seg-tabs" role="tablist">
        {([["one", "One Person"], ["paste", "Paste Emails"], ["excel", "Excel File"]] as const).map(([k, l]) => (
          <button key={k} type="button" role="tab" aria-selected={mode === k} onClick={() => setMode(k)}>{l}</button>
        ))}
      </div>

      {mode === "one" && (
        <div className="stack">
          <label className="field">
            <span>Name or email</span>
            <span className="input-icon"><Icon name="search" size={18} /><input autoFocus value={q} onChange={(e) => setQ(e.target.value)} placeholder="Start typing a name or email" /></span>
          </label>
          {found.length > 0 && (
            <ul className="pick-list">
              {found.map((u) => (
                <li key={u.id}>
                  <button type="button" disabled={busy || u.status === "Inactive"} onClick={() => void submit(u.email)}>
                    <span className="avatar avatar-sm" aria-hidden="true">{initials(u.displayName ?? u.email)}</span>
                    <span className="pick-text"><strong>{u.displayName ?? u.email}</strong><span className="muted small">{u.email}, {u.userType.toLowerCase()}{u.status === "Inactive" ? ", inactive" : ""}</span></span>
                    <span className="pick-add">{u.status === "Inactive" ? "Inactive" : "Request"}</span>
                  </button>
                </li>
              ))}
            </ul>
          )}
          {q.trim().length >= 2 && found.length === 0 && (
            emailLike
              ? <p className="small">{q.trim()} isn't a portal user yet. Only portal users can join an access group: a Super Admin adds people to the portal first (Users in the Admin Portal), then you can add them here.</p>
              : <p className="muted small">No portal user matches. People must be added by a Super Admin before they can join a group.</p>
          )}
        </div>
      )}

      {mode === "paste" && (
        <div className="stack">
          <label className="field">
            <span>Emails, one per line or separated by commas <span className="optional">({pasted} found, up to 5,000)</span></span>
            <textarea rows={6} value={text} onChange={(e) => setText(e.target.value)} placeholder={"priya.nair@rrd.com\njane.doe@clientcompany.com"} />
          </label>
          <p className="muted small">Only portal users can be added. Anyone who isn't a user yet is listed so you can add them in Users first.</p>
          <div className="actions"><button className="btn btn-primary" type="button" disabled={busy || pasted === 0} onClick={() => void submit(text)}>{busy ? "Sending…" : `Send for Approval${pasted ? `: ${pasted} ${pasted === 1 ? "Person" : "People"}` : ""}`}</button></div>
        </div>
      )}

      {mode === "excel" && (
        <form className="stack" onSubmit={submitExcel}>
          <div className="field">
            <span>Excel workbook (.xlsx) with emails under an "Email" heading</span>
            <FilePicker file={file} accept=".xlsx" label="Choose the Excel workbook" onChange={setFile} />
          </div>
          <p className="small"><button type="button" className="link" onClick={() => void template()}>Download a Template</button></p>
          <div className="actions"><button className="btn btn-primary" type="submit" disabled={busy || !file}>{busy ? "Sending…" : "Send File for Approval"}</button></div>
        </form>
      )}

      <p className="muted small">Nobody is added straight away. Everyone on your list goes to the owners of this dashboard as <strong>one request</strong>; they approve everyone or tick only some, and people are added when they do.</p>
      <label className="field ticket-field">
        <span>Note for the owners (optional)</span>
        <input maxLength={1000} value={note} onChange={(e) => setNote(e.target.value)} placeholder="e.g. New starters in the Colombo sales team" />
      </label>
      <label className="field ticket-field">
        <span>ServiceNow ticket (optional, saved in the audit log)</span>
        <input maxLength={64} value={ticket} onChange={(e) => setTicket(e.target.value)} placeholder="e.g. RITM0012345" />
      </label>

      <div className="add-people-foot">
        {hasSiblings
          ? <label className="check"><input type="checkbox" checked={move} onChange={(e) => setMove(e.target.checked)} /> Move people who are already in another group of this dashboard</label>
          : <span />}
        <button className="btn btn-quiet" type="button" onClick={onCancel}>Done</button>
      </div>
    </div>
  );
}

const outcomeText: Record<string, string> = {
  Added: "Added", Moved: "Moved here", AlreadyHere: "Already in this group",
  Requested: "Sent for approval", AlreadyWaiting: "Skipped: already waiting for approval",
  InOtherGroup: "Skipped: in another group", NotAUser: "Skipped: not portal users yet", Inactive: "Skipped: inactive", Invalid: "Not valid emails",
};

function ResultSummary({ results, onClose }: { results: MemberResult[]; onClose: () => void }) {
  const [open, setOpen] = useState(false);
  const counts = Object.entries(results.reduce<Record<string, number>>((acc, r) => ({ ...acc, [r.outcome]: (acc[r.outcome] ?? 0) + 1 }), {}));
  const problems = results.filter((r) => ["InOtherGroup", "NotAUser", "Inactive", "Invalid", "AlreadyWaiting"].includes(r.outcome));
  return (
    <div className={`result-box ${problems.length ? "result-warn" : "result-ok"}`}>
      <div className="panel-row">
        <p className="result-counts">{counts.map(([k, n]) => <span key={k}><strong>{n}</strong> {(outcomeText[k] ?? k).toLowerCase()}</span>)}</p>
        <span className="actions">
          <button type="button" className="link" onClick={() => setOpen(!open)}>{open ? "Hide Details" : "Show Each Email"}</button>
          <button type="button" className="icon-btn icon-btn-sm" aria-label="Close" onClick={onClose}><Icon name="close" size={16} /></button>
        </span>
      </div>
      {(open || (problems.length > 0 && problems.length <= 5)) && (
        <table className="grid">
          <tbody>
            {(open ? results : problems).map((r, i) => (
              <tr key={i}><td>{r.email}</td><td className="small">{outcomeText[r.outcome] ?? r.outcome}</td><td className="small muted">{r.detail}</td></tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- Dialogs

function CopyDialog({ groups, onClose, onCopy }: { groups: GroupRow[]; onClose: () => void; onCopy: (sourceId: number) => Promise<void> }) {
  const [source, setSource] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  async function copy(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    try { await onCopy(Number(source)); } catch (err) { setError(errorText(err)); setBusy(false); }
  }
  return (
    <Modal title="Copy Members from Another Group" onClose={onClose}>
      <form className="stack" onSubmit={copy}>
        <label className="field">
          <span>Group to copy from (any dashboard)</span>
          <select required value={source} onChange={(e) => setSource(e.target.value)}>
            <option value="">Choose…</option>
            {groups.map((g) => <option key={g.id} value={g.id}>{g.name} ({g.dashboard}, {g.members})</option>)}
          </select>
        </label>
        <p className="muted small">Only the people are copied, and they go to the owners for approval as one request. Anyone already in a group of this dashboard is skipped and listed.</p>
        {error && <Notice tone="error">{error}</Notice>}
        <div className="actions"><button className="btn btn-primary" type="submit" disabled={busy || !source}>{busy ? "Sending…" : "Send for Approval"}</button><button className="btn btn-quiet" type="button" onClick={onClose}>Cancel</button></div>
      </form>
    </Modal>
  );
}

function initials(name: string) {
  return name.split(/[\s.@]+/).filter(Boolean).slice(0, 2).map((p) => p[0]!.toUpperCase()).join("");
}

// ---------------------------------------------------------------- History

type HistoryPage = { total: number; page: number; pageSize: number; rows: { id: number; occurredAtUtc: string; actor: string | null; description: string; serviceNowReference: string | null }[] };

/** Everything that has changed in this group, newest first, a page at a time. */
function HistoryPanel({ groupId, created }: { groupId: number; created: string }) {
  const [page, setPage] = useState(1);
  const [size, setSize] = useState(20);
  const { data, error } = useApi<HistoryPage>(`/api/admin/access-groups/${groupId}/history?page=${page}&pageSize=${size}`);
  if (error) return <ErrorState>{error}</ErrorState>;
  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;
  return (
    <section className="panel">
      <div className="panel-row">
        <h2>History {data && <span className="muted count">{data.total}</span>}</h2>
        <span className="muted small">{created}</span>
      </div>
      {!data ? <p className="muted">Loading…</p> : data.rows.length === 0 ? <p className="pop-empty">No changes recorded yet.</p> : (
        <ol className="history">
          {data.rows.map((h) => (
            <li key={h.id}>
              <span className="history-dot" aria-hidden="true" />
              <div>
                <p>{h.description}</p>
                <p className="muted small">{h.actor ?? "System"}, {when(h.occurredAtUtc)}</p>
              </div>
            </li>
          ))}
        </ol>
      )}
      {data && <Pager page={data.page} pages={pages} size={data.pageSize} total={data.total} onPage={setPage} onSize={(n) => { setSize(n); setPage(1); }} sizes={[10, 20, 50, 100]} />}
    </section>
  );
}
