import { useEffect, useState, type FormEvent } from "react";
import { Link } from "react-router-dom";
import { api, can, getDevUser, useGridPageSize, useSession } from "@vantage/shared";
import { errorText, Modal, Notice, Pill, StatusPill, useApi, when } from "../ui";

type Row = {
  id: number; email: string; displayName: string | null; userType: string; status: string; statusSetManually: boolean;
  department: string | null; jobTitle: string | null; roles: string[]; dashboards: number; createdAtUtc: string;
};
type Page = { total: number; page: number; pageSize: number; rows: Row[] };
type RoleOption = { id: number; name: string; isSystem: boolean };

export function UsersPage() {
  const { me } = useSession();
  const gridSize = useGridPageSize();
  const canEdit = can(me, "users", "Edit");
  const [search, setSearch] = useState("");
  const [type, setType] = useState("");
  const [status, setStatus] = useState("");
  const [roleId, setRoleId] = useState("");
  const [page, setPage] = useState(1);
  const [dialog, setDialog] = useState<"add" | "bulk" | { id: number } | null>(null);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);
  const [syncing, setSyncing] = useState(false);

  const query = new URLSearchParams({ search, type, status, roleId, page: String(page), pageSize: String(gridSize) });
  const { data, error, reload } = useApi<Page>(`/api/admin/users?${query}`);
  const roles = useApi<{ roles: RoleOption[] }>("/api/admin/roles").data?.roles ?? [];

  useEffect(() => setPage(1), [search, type, status, roleId]);

  async function syncHrms() {
    setSyncing(true);
    try {
      const s = await api<{ checked: number; profilesUpdated: number; deactivated: number; skippedManual: number; notInHrms: number; deactivatedEmails: string[]; leaversRevoked: number; membershipsEnded: number; rules: { rule: string; proposed: number; message: string }[] }>(
        "/api/admin/users/hrms-sync", { method: "POST" });
      const sent = s.rules.reduce((n, r) => n + r.proposed, 0);
      setMessage({
        ok: true,
        text: `HRMS sync: ${s.checked} internal users checked, ${s.profilesUpdated} profiles refreshed, ${s.deactivated} leaver${s.deactivated === 1 ? "" : "s"} set Inactive${s.deactivatedEmails.length ? ` (${s.deactivatedEmails.join(", ")})` : ""}${s.skippedManual ? `, ${s.skippedManual} left alone because an admin set their status` : ""}${s.notInHrms ? `, ${s.notInHrms} not found in HRMS` : ""}.${s.leaversRevoked ? ` Access ended for ${s.leaversRevoked} ${s.leaversRevoked === 1 ? "leaver" : "leavers"} (${s.membershipsEnded} group ${s.membershipsEnded === 1 ? "membership" : "memberships"}).` : ""}${s.rules.length ? ` Access group rules ran: ${sent ? `${sent} ${sent === 1 ? "person was" : "people were"} sent to the owners for approval.` : "nothing new to propose."}` : ""}`,
      });
      reload();
    } catch (e) {
      setMessage({ ok: false, text: errorText(e) });
    } finally {
      setSyncing(false);
    }
  }

  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Users</h1>
          <p>Internal users (@rrd.com) are filled from HRMS. External users get an email to set up their sign-in and complete their details.</p>
        </div>
        {canEdit && (
          <div className="actions">
            <button className="btn" type="button" onClick={() => void syncHrms()} disabled={syncing}>{syncing ? "Syncing…" : "Run HRMS Sync"}</button>
            <button className="btn" type="button" onClick={() => setDialog("bulk")}>Add Many</button>
            <button className="btn btn-primary" type="button" onClick={() => setDialog("add")}>Add User</button>
          </div>
        )}
      </div>
      {message && <Notice tone={message.ok ? "ok" : "error"}>{message.text}</Notice>}

      <div className="filters">
        <label className="field grow"><span>Search</span><input value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Name, email, department or employee ID" /></label>
        <label className="field"><span>Type</span>
          <select value={type} onChange={(e) => setType(e.target.value)}><option value="">All</option><option>Internal</option><option>External</option></select>
        </label>
        <label className="field"><span>Status</span>
          <select value={status} onChange={(e) => setStatus(e.target.value)}><option value="">All</option><option>Active</option><option value="PendingSetup">Setup pending</option><option>Inactive</option></select>
        </label>
        <label className="field"><span>Role</span>
          <select value={roleId} onChange={(e) => setRoleId(e.target.value)}><option value="">All</option>{roles.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}</select>
        </label>
      </div>

      {error && <Notice tone="error">{error}</Notice>}
      {data && (
        <>
          <div className="table-wrap">
            <table className="grid">
              <thead><tr><th>Name</th><th>Type</th><th>Department</th><th>Roles</th><th className="num">Dashboards</th><th>Status</th><th>Added</th></tr></thead>
              <tbody>
                {data.rows.map((u) => (
                  <tr key={u.id}>
                    <td>
                      <button type="button" className="link" onClick={() => setDialog({ id: u.id })}>{u.displayName ?? u.email}</button>
                      <div className="muted small">{u.email}</div>
                    </td>
                    <td>{u.userType}</td>
                    <td>{u.department ?? <span className="muted">–</span>}{u.jobTitle && <div className="muted small">{u.jobTitle}</div>}</td>
                    <td className="small">{u.roles.filter((r) => r !== "Dashboard User").join(", ") || <span className="muted">Dashboard User</span>}</td>
                    <td className="num">{u.dashboards}</td>
                    <td><StatusPill status={u.status} />{u.statusSetManually && <div className="muted small">set by admin</div>}</td>
                    <td className="small muted">{when(u.createdAtUtc)}</td>
                  </tr>
                ))}
                {data.rows.length === 0 && <tr><td colSpan={7} className="muted">No users match these filters.</td></tr>}
              </tbody>
            </table>
          </div>
          <div className="pager">
            <span className="muted small">{data.total} user{data.total === 1 ? "" : "s"}</span>
            <button className="btn" type="button" disabled={page <= 1} onClick={() => setPage(page - 1)}>Previous</button>
            <span className="small">Page {page} of {pages}</span>
            <button className="btn" type="button" disabled={page >= pages} onClick={() => setPage(page + 1)}>Next</button>
          </div>
        </>
      )}

      {dialog === "add" && <AddUser roles={roles} onClose={() => setDialog(null)} onDone={(t) => { setDialog(null); setMessage({ ok: true, text: t }); reload(); }} />}
      {dialog === "bulk" && <BulkAdd roles={roles} onClose={() => { setDialog(null); reload(); }} />}
      {dialog && typeof dialog === "object" && (
        <UserDetail id={dialog.id} roles={roles} canEdit={canEdit} isSuperAdmin={!!me?.isSuperAdmin}
          onClose={() => setDialog(null)} onSaved={(t) => { setDialog(null); setMessage({ ok: true, text: t }); reload(); }} />
      )}
    </>
  );
}

function RolePicker({ roles, value, onChange }: { roles: RoleOption[]; value: number[]; onChange: (v: number[]) => void }) {
  return (
    <fieldset className="checks">
      <legend>Roles (everyone has Dashboard User; Dashboard Owner is given automatically to dashboard owners)</legend>
      {roles.filter((r) => r.name !== "Dashboard User" && r.name !== "Dashboard Owner").map((r) => (
        <label key={r.id} className="check">
          <input type="checkbox" checked={value.includes(r.id)} onChange={(e) => onChange(e.target.checked ? [...value, r.id] : value.filter((x) => x !== r.id))} />
          {r.name}
        </label>
      ))}
    </fieldset>
  );
}

type Lookup = {
  valid: boolean; email?: string; userType?: string; existingUserId?: number | null;
  hrms?: { employeeId: string; firstName: string; lastName: string; displayName: string | null; department: string | null; jobTitle: string | null; location: string | null; employmentStatus: string } | null;
};

function AddUser({ roles, onClose, onDone }: { roles: RoleOption[]; onClose: () => void; onDone: (text: string) => void }) {
  const [email, setEmail] = useState("");
  const [lookup, setLookup] = useState<Lookup | null>(null);
  const [firstName, setFirstName] = useState("");
  const [lastName, setLastName] = useState("");
  const [tableau, setTableau] = useState("");
  const [serviceNow, setServiceNow] = useState("");
  const [roleIds, setRoleIds] = useState<number[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    setLookup(null);
    if (!email.includes("@")) return;
    const t = window.setTimeout(() => {
      api<Lookup>(`/api/admin/users/hrms-lookup?email=${encodeURIComponent(email)}`).then(setLookup).catch(() => {});
    }, 350);
    return () => window.clearTimeout(t);
  }, [email]);

  async function save(e: FormEvent) {
    e.preventDefault();
    setSaving(true);
    setError(null);
    try {
      const r = await api<{ email: string; userType: string; fromHrms: boolean; status: string }>("/api/admin/users", {
        method: "POST",
        body: JSON.stringify({ email, firstName, lastName, tableauUserName: tableau, serviceNowReference: serviceNow, roleIds }),
      });
      onDone(r.userType === "External"
        ? `Added ${r.email}. They'll set up their sign-in and details at first login.`
        : `Added ${r.email}${r.fromHrms ? " with details from HRMS" : " (not found in HRMS, so names are blank)"}${r.status === "Inactive" ? ". HRMS lists them as a leaver, so they're Inactive" : ""}.`);
    } catch (err) {
      setError(errorText(err));
    } finally {
      setSaving(false);
    }
  }

  const external = lookup?.userType === "External";
  return (
    <Modal title="Add User" onClose={onClose}>
      <form onSubmit={save} className="stack">
        <label className="field"><span>Email</span><input type="email" required autoFocus value={email} onChange={(e) => setEmail(e.target.value)} placeholder="name@rrd.com or name@client.com" /></label>
        {lookup?.existingUserId && <Notice tone="error">{lookup.email} is already a user.</Notice>}
        {lookup?.valid && !lookup.existingUserId && lookup.userType === "Internal" && (
          lookup.hrms
            ? <div className="hrms-card">
                <strong>{lookup.hrms.displayName ?? `${lookup.hrms.firstName} ${lookup.hrms.lastName}`}</strong>
                <span>{[lookup.hrms.jobTitle, lookup.hrms.department, lookup.hrms.location].filter(Boolean).join(", ")}</span>
                <span className="muted small">Employee {lookup.hrms.employeeId}, HRMS status {lookup.hrms.employmentStatus}</span>
              </div>
            : <Notice>Internal email, but not in HRMS. They'll be added without HRMS details.</Notice>
        )}
        {external && (
          <div className="form-grid">
            <label className="field"><span>First name (optional)</span><input value={firstName} onChange={(e) => setFirstName(e.target.value)} /></label>
            <label className="field"><span>Last name (optional)</span><input value={lastName} onChange={(e) => setLastName(e.target.value)} /></label>
          </div>
        )}
        <div className="form-grid">
          <label className="field"><span>Tableau user name (optional)</span><input value={tableau} onChange={(e) => setTableau(e.target.value)} /></label>
          <label className="field"><span>ServiceNow ticket (optional)</span><input maxLength={64} value={serviceNow} onChange={(e) => setServiceNow(e.target.value)} placeholder="e.g. RITM0012345" /><small className="field-hint">Saved in the audit log with this change, not on the user.</small></label>
        </div>
        <RolePicker roles={roles} value={roleIds} onChange={setRoleIds} />
        {error && <Notice tone="error">{error}</Notice>}
        <div className="actions">
          <button className="btn btn-primary" type="submit" disabled={saving || !!lookup?.existingUserId}>{saving ? "Adding…" : "Add User"}</button>
          <button className="btn" type="button" onClick={onClose}>Cancel</button>
        </div>
      </form>
    </Modal>
  );
}

type BulkRow = { email: string; outcome: string; detail: string | null; userId: number | null };

function BulkAdd({ roles, onClose }: { roles: RoleOption[]; onClose: () => void }) {
  const [mode, setMode] = useState<"paste" | "excel">("paste");
  const [emails, setEmails] = useState("");
  const [file, setFile] = useState<File | null>(null);
  const [roleIds, setRoleIds] = useState<number[]>([]);
  const [serviceNow, setServiceNow] = useState("");
  const [results, setResults] = useState<BulkRow[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  async function run(e: FormEvent) {
    e.preventDefault();
    setSaving(true);
    setError(null);
    try {
      if (mode === "paste") {
        setResults(await api<BulkRow[]>("/api/admin/users/bulk", { method: "POST", body: JSON.stringify({ emails, roleIds, serviceNowReference: serviceNow || null }) }));
      } else if (file) {
        const body = new FormData();
        body.append("file", file);
        roleIds.forEach((r) => body.append("roleIds", String(r)));
        if (serviceNow) body.append("serviceNowReference", serviceNow);
        setResults(await api<BulkRow[]>("/api/admin/users/bulk-excel", { method: "POST", body }));
      }
    } catch (err) {
      setError(errorText(err));
    } finally {
      setSaving(false);
    }
  }

  async function downloadTemplate() {
    const res = await fetch("/api/admin/users/bulk-template", { headers: { "X-Dev-User": getDevUser() ?? "" } });
    const blob = await res.blob();
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = "user-upload-template.xlsx";
    a.click();
    URL.revokeObjectURL(a.href);
  }

  const added = results?.filter((r) => r.outcome === "Added").length ?? 0;
  return (
    <Modal title="Add Many Users" onClose={onClose} wide>
      {!results ? (
        <form onSubmit={run} className="stack">
          <div className="segmented" role="radiogroup" aria-label="Source">
            <label className={mode === "paste" ? "on" : ""}><input type="radio" checked={mode === "paste"} onChange={() => setMode("paste")} />Paste emails</label>
            <label className={mode === "excel" ? "on" : ""}><input type="radio" checked={mode === "excel"} onChange={() => setMode("excel")} />Excel file</label>
          </div>
          {mode === "paste" ? (
            <label className="field"><span>Emails, one per line or separated by commas</span>
              <textarea required rows={8} value={emails} onChange={(e) => setEmails(e.target.value)} placeholder={"priya.nair@rrd.com\narjun.menon@rrd.com\njane.doe@clientcompany.com"} />
            </label>
          ) : (
            <>
              <label className="field"><span>Excel file (.xlsx) with an "Email" column and an optional "Role" column</span>
                <input type="file" required accept=".xlsx" onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
              </label>
              <p className="small"><button type="button" className="link" onClick={() => void downloadTemplate()}>Download the Template</button></p>
            </>
          )}
          <RolePicker roles={roles} value={roleIds} onChange={setRoleIds} />
          <label className="field"><span>ServiceNow ticket (optional)</span><input maxLength={64} value={serviceNow} onChange={(e) => setServiceNow(e.target.value)} placeholder="e.g. RITM0012345" /><small className="field-hint">Saved in the audit log with each user added.</small></label>
          <p className="muted small">Existing users are skipped, never changed. Lists are assumed vetted, so there's no second approval.</p>
          {error && <Notice tone="error">{error}</Notice>}
          <div className="actions">
            <button className="btn btn-primary" type="submit" disabled={saving}>{saving ? "Adding…" : "Add Users"}</button>
            <button className="btn" type="button" onClick={onClose}>Cancel</button>
          </div>
        </form>
      ) : (
        <div className="stack">
          <Notice tone="ok">{added} added, {results.length - added} not added.</Notice>
          <div className="table-wrap" style={{ maxHeight: 360 }}>
            <table className="grid">
              <thead><tr><th>Email</th><th>Result</th><th>Detail</th></tr></thead>
              <tbody>
                {results.map((r, i) => (
                  <tr key={i}><td>{r.email}</td><td><Pill tone={r.outcome === "Added" ? "ok" : r.outcome === "Invalid" ? "bad" : "neutral"}>{r.outcome}</Pill></td><td className="small">{r.detail}</td></tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="actions"><button className="btn btn-primary" type="button" onClick={onClose}>Done</button></div>
        </div>
      )}
    </Modal>
  );
}

type Detail = {
  user: {
    id: number; email: string; firstName: string | null; lastName: string | null; displayName: string | null; userType: string; status: string;
    statusSetManually: boolean; contactNumber: string | null; timeZone: string; tableauUserName: string | null; 
    createdAtUtc: string; updatedAtUtc: string | null; lastLoginAtUtc: string | null;
    roles: { roleId: number; name: string; isAutomatic: boolean }[];
    hrms: null | { employeeId: string; department: string | null; division: string | null; jobTitle: string | null; jobGrade: string | null; managerEmail: string | null; location: string | null; employmentStatus: string; hireDate: string | null; exitDate: string | null; lastSyncedAtUtc: string };
  };
  access: { dashboardId: number; dashboard: string; dashboardStatus: string; group: string; rlsValue: string | null; source: string; addedAtUtc: string }[];
  history: { id: number; action: string; occurredAtUtc: string; serviceNowReference: string | null; actor: string | null }[];
};

function UserDetail({ id, roles, canEdit, isSuperAdmin, onClose, onSaved }: {
  id: number; roles: RoleOption[]; canEdit: boolean; isSuperAdmin: boolean; onClose: () => void; onSaved: (text: string) => void;
}) {
  const { data, error } = useApi<Detail>(`/api/admin/users/${id}`);
  const [form, setForm] = useState<Record<string, string>>({});
  const [roleIds, setRoleIds] = useState<number[]>([]);
  const [newEmail, setNewEmail] = useState("");
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    if (!data) return;
    const u = data.user;
    setForm({
      firstName: u.firstName ?? "", lastName: u.lastName ?? "", displayName: u.displayName ?? "", contactNumber: u.contactNumber ?? "",
      timeZone: u.timeZone, tableauUserName: u.tableauUserName ?? "", serviceNowReference: "", status: u.status,
    });
    setRoleIds(u.roles.filter((r) => !r.isAutomatic).map((r) => r.roleId));
    setNewEmail(u.email);
  }, [data]);

  if (error) return <Modal title="User" onClose={onClose}><Notice tone="error">{error}</Notice></Modal>;
  if (!data) return <Modal title="User" onClose={onClose}><p className="muted">Loading…</p></Modal>;
  const u = data.user;
  const set = (k: string) => (e: { target: { value: string } }) => setForm({ ...form, [k]: e.target.value });

  async function save(e: FormEvent) {
    e.preventDefault();
    setSaving(true);
    setSaveError(null);
    try {
      if (isSuperAdmin && newEmail.trim().toLowerCase() !== u.email) {
        await api(`/api/admin/users/${id}/email`, { method: "PUT", body: JSON.stringify({ email: newEmail }) });
      }
      await api(`/api/admin/users/${id}`, { method: "PUT", body: JSON.stringify({ ...form, roleIds }) });
      onSaved(`Saved ${newEmail}.`);
    } catch (err) {
      setSaveError(errorText(err));
    } finally {
      setSaving(false);
    }
  }

  const automatic = u.roles.filter((r) => r.isAutomatic);
  return (
    <Modal title={u.displayName ?? u.email} onClose={onClose} wide>
      <form onSubmit={save} className="stack">
        <div className="detail-head">
          <StatusPill status={u.status} />
          <span>{u.userType}</span>
          <span className="muted small">Added {when(u.createdAtUtc)}{u.lastLoginAtUtc ? `, last sign-in ${when(u.lastLoginAtUtc)}` : ", never signed in"}</span>
        </div>

        <fieldset disabled={!canEdit} className="plain">
          <div className="form-grid">
            <label className="field"><span>Email {isSuperAdmin ? "(Super Admins only)" : ""}</span><input type="email" value={newEmail} disabled={!isSuperAdmin} onChange={(e) => setNewEmail(e.target.value)} /></label>
            <label className="field"><span>Status</span>
              <select value={form.status} onChange={set("status")}><option>Active</option><option value="PendingSetup">Setup pending</option><option>Inactive</option></select>
            </label>
            <label className="field"><span>First name</span><input value={form.firstName ?? ""} onChange={set("firstName")} /></label>
            <label className="field"><span>Last name</span><input value={form.lastName ?? ""} onChange={set("lastName")} /></label>
            <label className="field"><span>Display name</span><input value={form.displayName ?? ""} onChange={set("displayName")} /></label>
            <label className="field"><span>Contact number</span><input value={form.contactNumber ?? ""} onChange={set("contactNumber")} /></label>
            <label className="field"><span>Time zone</span><input value={form.timeZone ?? ""} onChange={set("timeZone")} /></label>
            <label className="field"><span>Tableau user name</span><input value={form.tableauUserName ?? ""} onChange={set("tableauUserName")} /></label>
            <label className="field"><span>ServiceNow ticket for this change (optional)</span><input maxLength={64} value={form.serviceNowReference ?? ""} onChange={set("serviceNowReference")} placeholder="e.g. RITM0012345" /><small className="field-hint">Saved in the audit log with this change.</small></label>
          </div>
          {u.statusSetManually && <p className="muted small">Status was set by an admin, so the HRMS sync won't change it.</p>}
          <RolePicker roles={roles} value={roleIds} onChange={setRoleIds} />
          {automatic.length > 0 && <p className="muted small">Given automatically: {automatic.map((r) => r.name).join(", ")}.</p>}
        </fieldset>

        {u.hrms && (
          <section>
            <h3 className="sub">HRMS</h3>
            <dl className="facts">
              <dt>Employee ID</dt><dd>{u.hrms.employeeId}</dd>
              <dt>Job title</dt><dd>{u.hrms.jobTitle ?? "–"}{u.hrms.jobGrade ? ` (${u.hrms.jobGrade})` : ""}</dd>
              <dt>Department</dt><dd>{u.hrms.department ?? "–"}, {u.hrms.division ?? "–"}</dd>
              <dt>Manager</dt><dd>{u.hrms.managerEmail ?? "–"}</dd>
              <dt>Location</dt><dd>{u.hrms.location ?? "–"}</dd>
              <dt>HRMS status</dt><dd>{u.hrms.employmentStatus}{u.hrms.exitDate ? `, left ${u.hrms.exitDate}` : ""}</dd>
              <dt>Last synced</dt><dd>{when(u.hrms.lastSyncedAtUtc)}</dd>
            </dl>
          </section>
        )}

        <section>
          <h3 className="sub">Access</h3>
          {data.access.length === 0 ? <p className="muted small">Not in any dashboard group yet.</p> : (
            <table className="grid">
              <thead><tr><th>Dashboard</th><th>Group</th><th>RLS role</th><th>How</th></tr></thead>
              <tbody>
                {data.access.map((a) => (
                  <tr key={a.dashboardId}><td><Link to={`/dashboards/${a.dashboardId}`}>{a.dashboard}</Link></td><td>{a.group}</td><td>{a.rlsValue ?? "–"}</td><td className="small">{a.source}</td></tr>
                ))}
              </tbody>
            </table>
          )}
        </section>

        <section>
          <h3 className="sub">Change History</h3>
          {data.history.length === 0 ? <p className="muted small">No changes recorded yet.</p> : (
            <table className="grid">
              <thead><tr><th>Change</th><th>ServiceNow ticket</th><th>By</th><th>When</th></tr></thead>
              <tbody>
                {data.history.map((h) => (
                  <tr key={h.id}>
                    <td className="small">{userAction[h.action] ?? h.action}</td>
                    <td className="small">{h.serviceNowReference ? <code>{h.serviceNowReference}</code> : <span className="muted">–</span>}</td>
                    <td className="small">{h.actor ?? "System"}</td>
                    <td className="small muted">{when(h.occurredAtUtc)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </section>

        {saveError && <Notice tone="error">{saveError}</Notice>}
        {canEdit && (
          <div className="actions">
            <button className="btn btn-primary" type="submit" disabled={saving}>{saving ? "Saving…" : "Save Changes"}</button>
            <button className="btn" type="button" onClick={onClose}>Cancel</button>
          </div>
        )}
      </form>
    </Modal>
  );
}

const userAction: Record<string, string> = {
  "user.created": "Added to the portal", "user.updated": "Details or roles changed", "user.status-changed": "Status changed",
  "user.email-changed": "Email changed", "user.servicenow-reference-moved": "ServiceNow ticket moved from the old user record",
};
