import { useMemo, useState, type FormEvent } from "react";
import { Link } from "react-router-dom";
import { Icon } from "../Icon";
import { Pager, usePaged } from "../Pager";
import { useFlash } from "../Toast";
import { api } from "../api";
import { errorText, Modal, Notice, Pill, useApi, when } from "./ui";
import type { GroupRow } from "./AccessGroupsPage";
import { useManage } from "./routes";

type Condition = { field: string; value: string };
type Rule = {
  id: number; name: string; action: "Add" | "Remove"; moveFromOtherGroups: boolean; isActive: boolean; lastRunAtUtc: string | null; lastRunSummary: string | null;
  groupId: number; group: string; groupStatus: string; dashboardId: number; dashboard: string; waiting: number; conditions: Condition[]; text: string;
};
type Data = { canEdit: boolean; fields: { key: string; label: string }[]; rules: Rule[] };
type Person = { email: string; name: string | null; detail: string | null };
type Preview = { matching: number; wouldPropose: Person[]; skipped: Person[] };
type Draft = { groupId: number; name: string; action: "Add" | "Remove"; moveFromOtherGroups: boolean; isActive: boolean; conditions: Condition[] };

/** The access group a panel is limited to: on a group's own page the rules shown and made are for that group only. */
export type FixedGroup = { groupId: number; group: string; dashboard: string; dashboardId: number };

const MAX_CONDITIONS = 5;
const toBody = (r: Rule): Draft => ({ groupId: r.groupId, name: r.name, action: r.action, moveFromOtherGroups: r.moveFromOtherGroups, isActive: r.isActive, conditions: r.conditions });

/**
 * The rules of every group (the Access Group Rules page) or of one group (the Rules tab of the group page).
 * A rule never changes anyone's access: what it finds goes to the dashboard's owners as a request.
 */
export function RulesPanel({ fixed, onChanged }: { fixed?: FixedGroup; onChanged?: () => void }) {
  const routes = useManage();
  const { data, error, reload } = useApi<Data>(`/api/admin/access-rules${fixed ? `?groupId=${fixed.groupId}` : ""}`);
  const groups = useApi<GroupRow[]>(fixed ? null : "/api/admin/access-groups");
  const values = useApi<Record<string, string[]>>("/api/admin/access-rules/field-values");
  const [editing, setEditing] = useState<Rule | "new" | null>(null);
  const [previewing, setPreviewing] = useState<Rule | null>(null);
  const setMessage = useFlash();
  const [busy, setBusy] = useState<number | "all" | null>(null);
  const paged = usePaged(data?.rules ?? [], 10);

  if (error) return <Notice tone="error">{error}</Notice>;
  if (!data) return <p className="muted">Loading…</p>;
  const fieldLabel = (key: string) => data.fields.find((f) => f.key === key)?.label ?? key;
  const changed = () => { reload(); onChanged?.(); };

  async function run(rule?: Rule) {
    setBusy(rule?.id ?? "all");
    setMessage(null);
    try {
      if (rule) {
        const r = await api<{ message: string }>(`/api/admin/access-rules/${rule.id}/run`, { method: "POST" });
        setMessage({ ok: true, text: `${rule.name}: ${r.message}` });
      } else {
        const r = await api<{ rule: string; proposed: number; message: string }[]>(`/api/admin/access-rules/run-all${fixed ? `?groupId=${fixed.groupId}` : ""}`, { method: "POST" });
        const sent = r.reduce((n, x) => n + x.proposed, 0);
        setMessage({ ok: true, text: r.length === 0 ? "There are no active rules to run." : sent > 0 ? `Sent ${sent} ${sent === 1 ? "person" : "people"} to the owners for approval.` : "Nothing new to propose." });
      }
      changed();
    } catch (e) { setMessage({ ok: false, text: errorText(e) }); } finally { setBusy(null); }
  }

  async function toggle(rule: Rule) {
    try { await api(`/api/admin/access-rules/${rule.id}/active`, { method: "POST", body: JSON.stringify({ active: !rule.isActive }) }); changed(); } catch (e) { setMessage({ ok: false, text: errorText(e) }); }
  }

  async function remove(rule: Rule) {
    if (!window.confirm(`Delete the rule “${rule.name}”? Requests it already sent to the owners stay as they are.`)) return;
    try { await api(`/api/admin/access-rules/${rule.id}`, { method: "DELETE" }); setMessage({ ok: true, text: `Deleted “${rule.name}”.` }); changed(); } catch (e) { setMessage({ ok: false, text: errorText(e) }); }
  }

  return (
    <>
      <div className="panel-row rules-head">
        {fixed
          ? <p className="muted small">Rules for <strong>{fixed.group}</strong>: they pick people from the HRMS data and propose adding them to this group, or removing them. The owners of {fixed.dashboard} confirm every change, and rules also run after each HRMS sync.</p>
          : <span />}
        {data.canEdit && (
          <div className="actions">
            <button className="btn" type="button" disabled={busy !== null || data.rules.length === 0} onClick={() => void run()}>{busy === "all" ? "Running…" : fixed ? "Run These Rules" : "Run All Rules"}</button>
            <button className="btn btn-primary" type="button" onClick={() => setEditing("new")}><Icon name="plus" size={18} /> New Rule</button>
          </div>
        )}
      </div>
      {!data.canEdit && <Notice>Only Super Admins can create, change or run rules. You can see them here.</Notice>}

      {data.rules.length === 0 ? (
        <div className="empty"><h2>No Rules Yet</h2><p>{data.canEdit ? "Create a rule to keep this access group in step with the HRMS data, with the owners always confirming." : "No Super Admin has created a rule yet."}</p></div>
      ) : (
        <>
          <div className="table-wrap">
            <table className="grid">
              <thead><tr><th>Rule</th><th>What it does</th><th>When</th><th>Last run</th><th /></tr></thead>
              <tbody>
                {paged.rows.map((r) => (
                  <tr key={r.id}>
                    <td>
                      <strong>{r.name}</strong>
                      <div>{r.isActive ? <Pill tone="ok">On</Pill> : <Pill tone="neutral">Off</Pill>}</div>
                    </td>
                    <td className="small">
                      <Pill tone={r.action === "Add" ? "ok" : "warn"}>{r.action === "Add" ? "Add people" : "Remove people"}</Pill>
                      {!fixed && <div><Link to={routes.group(r.groupId, "rules")}>{r.group}</Link></div>}
                      <div className="muted">{!fixed && <>{r.dashboard}</>}{r.moveFromOtherGroups ? `${fixed ? "" : " · "}may move people from other groups` : ""}</div>
                    </td>
                    <td className="small">{r.conditions.map((c) => <div key={c.field}>{fieldLabel(c.field)} equals <strong>{c.value}</strong></div>)}</td>
                    <td className="small">
                      {r.lastRunAtUtc ? <>{when(r.lastRunAtUtc)}<div className="muted">{r.lastRunSummary}</div></> : <span className="muted">Not run yet</span>}
                      {r.waiting > 0 && <div><Link to="/requests">{r.waiting} waiting for the owners</Link></div>}
                    </td>
                    <td className="cell-actions">
                      <span className="member-actions">
                        <button type="button" className="btn" onClick={() => setPreviewing(r)}>Preview</button>
                        {data.canEdit && (
                          <>
                            <button type="button" className="btn" disabled={busy !== null || !r.isActive} title={r.isActive ? "Send what the rule finds to the owners now" : "Switch the rule on first"} onClick={() => void run(r)}>{busy === r.id ? "Running…" : "Run Now"}</button>
                            <button type="button" className="btn" onClick={() => void toggle(r)}>{r.isActive ? "Switch Off" : "Switch On"}</button>
                            <button type="button" className="icon-btn icon-btn-sm" aria-label={`Edit ${r.name}`} onClick={() => setEditing(r)}><Icon name="edit" size={18} /></button>
                            <button type="button" className="icon-btn icon-btn-sm icon-btn-danger" aria-label={`Delete ${r.name}`} onClick={() => void remove(r)}><Icon name="trash" size={18} /></button>
                          </>
                        )}
                      </span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <Pager {...paged.pager} />
        </>
      )}

      {editing && (fixed || groups.data) && (
        <RuleEditor rule={editing === "new" ? null : editing} fixed={fixed} groups={groups.data ?? []} fields={data.fields} values={values.data ?? {}}
          onClose={() => setEditing(null)} onSaved={(t) => { setEditing(null); setMessage({ ok: true, text: t }); changed(); }} />
      )}
      {previewing && (
        <Modal title={`Preview: ${previewing.name}`} onClose={() => setPreviewing(null)} wide>
          <PreviewPanel load={() => api<Preview>(`/api/admin/access-rules/preview?ruleId=${previewing.id}`, { method: "POST", body: JSON.stringify(toBody(previewing)) })} action={previewing.action} autoLoad />
        </Modal>
      )}
    </>
  );
}

// ---------------------------------------------------------------- Preview

function PreviewPanel({ load, action, autoLoad }: { load: () => Promise<Preview>; action: "Add" | "Remove"; autoLoad?: boolean }) {
  const [preview, setPreview] = useState<Preview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [started, setStarted] = useState(false);

  async function go() {
    setBusy(true);
    setError(null);
    try { setPreview(await load()); } catch (e) { setError(errorText(e)); setPreview(null); } finally { setBusy(false); }
  }
  if (autoLoad && !started) { setStarted(true); void go(); }

  const verb = action === "Add" ? "added" : "removed";
  return (
    <div className="rule-preview">
      {!autoLoad && <button type="button" className="btn" onClick={() => void go()} disabled={busy}>{busy ? "Checking…" : "Preview Who Matches"}</button>}
      {autoLoad && busy && <p className="muted">Checking the HRMS data…</p>}
      {error && <Notice tone="error">{error}</Notice>}
      {preview && (
        <>
          <p>
            <strong>{preview.matching}</strong> {preview.matching === 1 ? "person matches" : "people match"} right now.{" "}
            <strong>{preview.wouldPropose.length}</strong> would be sent to the owners to be {verb}
            {preview.skipped.length > 0 && <>, and <strong>{preview.skipped.length}</strong> {preview.skipped.length === 1 ? "is" : "are"} left out</>}.
            {" "}Nothing has been sent: this is only a preview.
          </p>
          {preview.wouldPropose.length > 0 && <PersonList title={`Would be sent to the owners to be ${verb}`} people={preview.wouldPropose} />}
          {preview.skipped.length > 0 && <PersonList title="Left out" people={preview.skipped} />}
        </>
      )}
    </div>
  );
}

function PersonList({ title, people }: { title: string; people: Person[] }) {
  const paged = usePaged(people, 10);
  return (
    <div className="rule-people">
      <h3>{title} <span className="muted">({people.length})</span></h3>
      <ul>
        {paged.rows.map((p) => (
          <li key={p.email}><strong>{p.name ?? p.email}</strong> <span className="muted small">{p.email}{p.detail ? ` · ${p.detail}` : ""}</span></li>
        ))}
      </ul>
      <Pager {...paged.pager} sizes={[10, 25, 50]} />
    </div>
  );
}

// ---------------------------------------------------------------- Editor

function RuleEditor({ rule, fixed, groups, fields, values, onClose, onSaved }: {
  rule: Rule | null; fixed?: FixedGroup; groups: GroupRow[]; fields: { key: string; label: string }[]; values: Record<string, string[]>;
  onClose: () => void; onSaved: (text: string) => void;
}) {
  const usable = useMemo(() => groups.filter((g) => g.status !== "Retired" && g.dashboardStatus !== "Retired"), [groups]);
  const dashboards = useMemo(() => [...new Map(usable.map((g) => [g.dashboardId, g.dashboard])).entries()].sort((a, b) => a[1].localeCompare(b[1])), [usable]);
  const [dashboardId, setDashboardId] = useState<string>(rule ? String(rule.dashboardId) : fixed ? String(fixed.dashboardId) : "");
  const [draft, setDraft] = useState<Draft>(rule ? toBody(rule) : { groupId: fixed?.groupId ?? 0, name: "", action: "Add", moveFromOtherGroups: false, isActive: true, conditions: [{ field: "Department", value: "" }] });
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [previewKey, setPreviewKey] = useState(0);

  const groupOptions = usable.filter((g) => String(g.dashboardId) === dashboardId);
  const set = (p: Partial<Draft>) => { setDraft((d) => ({ ...d, ...p })); setPreviewKey((k) => k + 1); };
  const setCondition = (i: number, p: Partial<Condition>) => set({ conditions: draft.conditions.map((c, j) => (j === i ? { ...c, ...p } : c)) });
  const usedFields = new Set(draft.conditions.map((c) => c.field));

  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      if (rule) await api(`/api/admin/access-rules/${rule.id}`, { method: "PUT", body: JSON.stringify(draft) });
      else await api("/api/admin/access-rules", { method: "POST", body: JSON.stringify(draft) });
      onSaved(rule ? `Saved “${draft.name}”.` : `Created “${draft.name}”. Use Run Now to send what it finds to the owners.`);
    } catch (err) { setError(errorText(err)); setBusy(false); }
  }

  const ready = draft.groupId > 0 && draft.conditions.length > 0 && draft.conditions.every((c) => c.value.trim());

  return (
    <Modal title={rule ? "Edit Rule" : "New Rule"} onClose={onClose} wide>
      <form className="stack" onSubmit={save}>
        {fixed ? (
          <div className="cols cols-3">
            <p className="small"><span className="muted">Access group</span><br /><strong>{fixed.group}</strong> <span className="muted">on {fixed.dashboard}</span></p>
            <label className="field">
              <span>Rule name</span>
              <input required maxLength={100} value={draft.name} onChange={(e) => set({ name: e.target.value })} placeholder="e.g. Finance team" />
            </label>
          </div>
        ) : (
          <div className="cols cols-3">
            <label className="field">
              <span>Dashboard</span>
              <select required value={dashboardId} disabled={!!rule} onChange={(e) => { setDashboardId(e.target.value); set({ groupId: 0 }); }}>
                <option value="">Choose…</option>
                {dashboards.map(([id, name]) => <option key={id} value={id}>{name}</option>)}
              </select>
            </label>
            <label className="field">
              <span>Access group</span>
              <select required value={draft.groupId || ""} disabled={!!rule || !dashboardId} onChange={(e) => set({ groupId: Number(e.target.value) })}>
                <option value="">Choose…</option>
                {groupOptions.map((g) => <option key={g.id} value={g.id}>{g.name}{g.isDefault ? " (default)" : ""}</option>)}
              </select>
            </label>
            <label className="field">
              <span>Rule name</span>
              <input required maxLength={100} value={draft.name} onChange={(e) => set({ name: e.target.value })} placeholder="e.g. Finance team" />
            </label>
          </div>
        )}

        <fieldset className="checks">
          <legend>What should it propose?</legend>
          <label className="check"><input type="radio" name="action" checked={draft.action === "Add"} onChange={() => set({ action: "Add" })} /> Add people who match to the group</label>
          <label className="check"><input type="radio" name="action" checked={draft.action === "Remove"} onChange={() => set({ action: "Remove", moveFromOtherGroups: false })} /> Remove people who match from the group</label>
        </fieldset>

        <div className="rule-conditions">
          <h3>When a person's HRMS details match all of these</h3>
          {draft.conditions.map((c, i) => (
            <div key={i} className="rule-condition">
              <select aria-label="HRMS field" value={c.field} onChange={(e) => setCondition(i, { field: e.target.value, value: "" })}>
                {fields.map((f) => <option key={f.key} value={f.key} disabled={f.key !== c.field && usedFields.has(f.key)}>{f.label}</option>)}
              </select>
              <span className="muted">equals</span>
              <input aria-label="Value" list={`values-${i}`} value={c.value} maxLength={200} onChange={(e) => setCondition(i, { value: e.target.value })} placeholder="Type or pick a value" />
              <datalist id={`values-${i}`}>{(values[c.field] ?? []).map((v) => <option key={v} value={v} />)}</datalist>
              {draft.conditions.length > 1 && <button type="button" className="icon-btn icon-btn-sm" aria-label="Remove this condition" onClick={() => set({ conditions: draft.conditions.filter((_, j) => j !== i) })}><Icon name="close" size={16} /></button>}
            </div>
          ))}
          {draft.conditions.length < MAX_CONDITIONS && usedFields.size < fields.length && (
            <button type="button" className="link" onClick={() => set({ conditions: [...draft.conditions, { field: fields.find((f) => !usedFields.has(f.key))!.key, value: "" }] })}>Add Another Condition</button>
          )}
          <p className="muted small">Only active portal users with HRMS details are considered, and the match is not case sensitive. Owners of the dashboard are never proposed for removal.</p>
        </div>

        {draft.action === "Add" && (
          <label className="check"><input type="checkbox" checked={draft.moveFromOtherGroups} onChange={(e) => set({ moveFromOtherGroups: e.target.checked })} /> Also propose people who are in another group of this dashboard (approving moves them here)</label>
        )}
        <label className="check"><input type="checkbox" checked={draft.isActive} onChange={(e) => set({ isActive: e.target.checked })} /> Switch the rule on (it then runs after each HRMS sync, and with Run Now)</label>

        <div className="rule-preview-box">
          <PreviewPanel key={previewKey} action={draft.action}
            load={() => api<Preview>(`/api/admin/access-rules/preview${rule ? `?ruleId=${rule.id}` : ""}`, { method: "POST", body: JSON.stringify(draft) })} />
        </div>

        <p className="muted small">Nothing changes when you save. The owners of the dashboard confirm every addition and removal.</p>
        {error && <Notice tone="error">{error}</Notice>}
        <div className="actions">
          <button className="btn btn-primary" type="submit" disabled={busy || !ready || !draft.name.trim()}>{busy ? "Saving…" : rule ? "Save Rule" : "Create Rule"}</button>
          <button className="btn btn-quiet" type="button" onClick={onClose}>Cancel</button>
        </div>
      </form>
    </Modal>
  );
}
