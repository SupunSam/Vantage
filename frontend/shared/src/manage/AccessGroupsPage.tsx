import { useMemo, useState, type FormEvent } from "react";
import { Link, useNavigate } from "react-router-dom";
import { Icon } from "../Icon";
import { Pager, usePaged } from "../Pager";
import { api } from "../api";
import { can, useGridPageSize, useSession } from "../session";
import { errorText, Modal, Notice, SortHeader, StatusPill, useApi, useSort } from "./ui";
import { useManage } from "./routes";
import { PageSkeleton, ErrorState } from "../Feedback";

export type GroupRow = {
  id: number; name: string; rlsValue: string | null; isDefault: boolean; status: string; createdAtUtc: string;
  dashboardId: number; dashboard: string; dashboardCode: string; dashboardStatus: string; rlsEnabled: boolean; members: number;
};
type DashboardOption = { id: number; name: string; code: string; rlsEnabled: boolean; status: string };
export type MemberResult = { email: string; outcome: string; detail: string | null };

/** Access Groups: every dashboard's groups in one list. Open a group to manage its members. */
export function AccessGroupsPage() {
  const routes = useManage();
  const { me } = useSession();
  const navigate = useNavigate();
  const { data, error, reload } = useApi<GroupRow[]>("/api/admin/access-groups");
  const [q, setQ] = useState("");
  const [status, setStatus] = useState("Active");
  const [dashboardId, setDashboardId] = useState("");
  const [creating, setCreating] = useState(false);
  const canEdit = can(me, "groups", "Edit");

  const dashboards = useMemo(() => [...new Map((data ?? []).map((g) => [g.dashboardId, g.dashboard])).entries()].sort((a, b) => a[1].localeCompare(b[1])), [data]);
  const rows = useMemo(() => {
    const needle = q.trim().toLowerCase();
    return (data ?? []).filter((g) =>
      (!status || g.status === status)
      && (!dashboardId || String(g.dashboardId) === dashboardId)
      && (!needle || [g.name, g.dashboard, g.dashboardCode, g.rlsValue].some((v) => v?.toLowerCase().includes(needle))));
  }, [data, q, status, dashboardId]);

  const { sorted, sort, toggle } = useSort(rows, { name: (g) => g.name, dashboard: (g) => g.dashboard, rls: (g) => g.rlsValue, members: (g) => g.members, status: (g) => g.status });
  const paged = usePaged(sorted, useGridPageSize(), `${q}|${status}|${dashboardId}|${sort?.key}|${sort?.desc}`);

  if (error) return <ErrorState onRetry={reload}>{error}</ErrorState>;
  if (!data) return <PageSkeleton kind="table" />;

  const totalMembers = data.filter((g) => g.status === "Active").reduce((n, g) => n + g.members, 0);

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Access Groups</h1>
          <p>Being in an access group is the only way to open a dashboard. Each person is in one group per dashboard, and on dashboards with row-level security the group's RLS value decides what data they see.</p>
        </div>
        {canEdit && <button className="btn btn-primary" type="button" onClick={() => setCreating(true)}><Icon name="plus" size={18} /> New Access Group</button>}
      </div>

      <div className="stat-row">
        <div><strong>{data.filter((g) => g.status === "Active").length}</strong><span>active groups</span></div>
        <div><strong>{dashboards.length}</strong><span>dashboards</span></div>
        <div><strong>{totalMembers}</strong><span>memberships</span></div>
      </div>

      <div className="filters">
        <label className="field field-search">
          <span>Search</span>
          <span className="input-icon"><Icon name="search" size={18} /><input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Group, dashboard or RLS value" /></span>
        </label>
        <label className="field">
          <span>Dashboard</span>
          <select value={dashboardId} onChange={(e) => setDashboardId(e.target.value)}>
            <option value="">All</option>
            {dashboards.map(([id, name]) => <option key={id} value={id}>{name}</option>)}
          </select>
        </label>
        <label className="field">
          <span>Status</span>
          <select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">All</option><option>Active</option><option>Inactive</option><option>Retired</option>
          </select>
        </label>
        <span className="muted small filters-count">{rows.length} of {data.length}</span>
      </div>

      <div className="table-wrap">
        <table className="grid grid-rows">
          <thead><tr><SortHeader label="Access group" k="name" sort={sort} onSort={toggle} /><SortHeader label="Dashboard" k="dashboard" sort={sort} onSort={toggle} /><SortHeader label="RLS value" k="rls" sort={sort} onSort={toggle} /><SortHeader label="Members" k="members" sort={sort} onSort={toggle} className="num" /><SortHeader label="Status" k="status" sort={sort} onSort={toggle} /></tr></thead>
          <tbody>
            {paged.rows.map((g) => (
              <tr key={g.id} onClick={() => navigate(routes.group(g.id))}>
                <td>
                  <Link to={routes.group(g.id)} className="group-name" onClick={(e) => e.stopPropagation()}>{g.name}</Link>
                  {g.isDefault && <div className="muted small">Default group</div>}
                </td>
                <td className="small">{g.dashboard}<div className="muted">{g.rlsEnabled ? "RLS on" : "No RLS"}{g.dashboardStatus !== "Active" ? `, ${g.dashboardStatus.toLowerCase()}` : ""}</div></td>
                <td>
                  {g.rlsValue ? <code className={g.rlsEnabled ? "" : "rls-ignored"}>{g.rlsValue}</code>
                    : g.rlsEnabled ? <span className="warn-text small">Needs a value</span> : <span className="muted">–</span>}
                </td>
                <td className="num">{g.members}</td>
                <td><StatusPill status={g.status} /></td>
              </tr>
            ))}
          </tbody>
        </table>
        {rows.length === 0 && <p className="pop-empty">No access groups match these filters.</p>}
      </div>
      <Pager {...paged.pager} />

      {creating && <NewGroupModal groups={data} onClose={() => setCreating(false)} onCreated={(id) => { setCreating(false); reload(); navigate(routes.group(id)); }} />}
    </>
  );
}

export function NewGroupModal({ groups, onClose, onCreated, copyFromGroupId, dashboardId: presetDashboard, title = "New Access Group" }: {
  groups: GroupRow[]; onClose: () => void; onCreated: (id: number) => void; copyFromGroupId?: number; dashboardId?: number; title?: string;
}) {
  const { data } = useApi<{ dashboards: DashboardOption[]; noRlsMessage: string }>("/api/admin/access-groups/dashboards");
  const [dashboardId, setDashboardId] = useState(presetDashboard ? String(presetDashboard) : "");
  const [name, setName] = useState("");
  const [rls, setRls] = useState("");
  const [copyFrom, setCopyFrom] = useState(copyFromGroupId ? String(copyFromGroupId) : "");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const dashboard = data?.dashboards.find((d) => String(d.id) === dashboardId);
  const blocked = dashboard && !dashboard.rlsEnabled;

  function pick(id: string) {
    setDashboardId(id);
    const d = data?.dashboards.find((x) => String(x.id) === id);
    if (d && !name) setName(`${d.code}-`);
  }

  async function create(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const r = await api<{ id: number; copied: MemberResult[] }>("/api/admin/access-groups", {
        method: "POST",
        body: JSON.stringify({ dashboardId: Number(dashboardId), name, rlsValue: rls || null, copyFromGroupId: copyFrom ? Number(copyFrom) : null }),
      });
      onCreated(r.id);
    } catch (err) {
      setError(errorText(err));
      setBusy(false);
    }
  }

  return (
    <Modal title={title} onClose={onClose}>
      {!data ? <p className="muted">Loading…</p> : (
        <form className="stack" onSubmit={create}>
          <label className="field">
            <span>Dashboard</span>
            <select required value={dashboardId} onChange={(e) => pick(e.target.value)}>
              <option value="">Choose…</option>
              {data.dashboards.map((d) => <option key={d.id} value={d.id}>{d.name}{d.rlsEnabled ? "" : " (no RLS)"}</option>)}
            </select>
          </label>
          {blocked ? (
            <Notice>{data.noRlsMessage}</Notice>
          ) : (
            <>
              <label className="field">
                <span>Group name (unique, letters, digits, - and _, up to 40)</span>
                <input required maxLength={40} pattern="[A-Za-z0-9_\-]+" value={name} onChange={(e) => setName(e.target.value)} placeholder="SALES-North" />
              </label>
              <label className="field">
                <span>RLS value (role name in the .pbix; several with commas)</span>
                <input required value={rls} onChange={(e) => setRls(e.target.value)} placeholder="Region_North" />
              </label>
              <label className="field">
                <span>Copy members from <span className="optional">(optional: clones the members, not the RLS value; they go to the owners for approval)</span></span>
                <select value={copyFrom} onChange={(e) => setCopyFrom(e.target.value)}>
                  <option value="">Start empty</option>
                  {groups.filter((g) => g.status === "Active" && g.members > 0).map((g) => (
                    <option key={g.id} value={g.id}>{g.name} ({g.dashboard}, {g.members})</option>
                  ))}
                </select>
              </label>
            </>
          )}
          {error && <Notice tone="error">{error}</Notice>}
          <div className="actions">
            <button className="btn btn-primary" type="submit" disabled={busy || !dashboardId || !!blocked}>{busy ? "Creating…" : "Create Group"}</button>
            <button className="btn btn-quiet" type="button" onClick={onClose}>Cancel</button>
          </div>
        </form>
      )}
    </Modal>
  );
}
