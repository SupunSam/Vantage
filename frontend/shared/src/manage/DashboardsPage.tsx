import { useMemo, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { Icon } from "../Icon";
import { Thumbnail } from "../Thumbnail";
import { TypeIcon } from "../TypeIcon";
import { can, useSession } from "../session";
import { Notice, StatusPill, useApi, when } from "./ui";
import { useManage } from "./routes";

type Row = {
  id: number; code: string; name: string; description: string | null; type: string; status: string; rlsEnabled: boolean; lastError: string | null;
  categoryId: number | null; categoryPath: string | null; tenant: string | null; workspace: string | null; owner: string | null; backupOwner: string | null;
  tags: string[]; audience: string; classification: string; thumbnail: string | null;
  publishedAtUtc: string | null; createdAtUtc: string; groups: number; needsRls: boolean; members: number; myGroup: string | null;
};


/** Dashboards Master: every dashboard with its category, RLS flag, owners and groups. Open one to edit it. */
export function DashboardsPage() {
  const { me } = useSession();
  const navigate = useNavigate();
  const routes = useManage();
  const owner = routes.mode === "owner";
  const { data, error } = useApi<Row[]>(routes.listApi);
  const [q, setQ] = useState("");
  const [status, setStatus] = useState("");
  const [primary, setPrimary] = useState("");

  const primaries = useMemo(() => [...new Set((data ?? []).map((d) => d.categoryPath?.split(" / ")[0]).filter(Boolean) as string[])].sort(), [data]);
  const rows = useMemo(() => {
    const needle = q.trim().toLowerCase();
    return (data ?? []).filter((d) =>
      (!status || d.status === status)
      && (!primary || (primary === "-" ? !d.categoryPath : d.categoryPath?.split(" / ")[0] === primary))
      && (!needle || [d.name, d.code, d.description, d.owner, d.backupOwner, d.categoryPath, ...d.tags].some((v) => v?.toLowerCase().includes(needle))));
  }, [data, q, status, primary]);

  if (error) return <Notice tone="error">{error}</Notice>;
  if (!data) return <p className="muted">Loading…</p>;

  const uncategorised = data.filter((d) => !d.categoryPath && d.status !== "Retired").length;

  return (
    <>
      <div className="page-head">
        <div>
          <h1>{routes.dashboardsLabel}</h1>
          <p>{owner ? "The dashboards you own, newest first. Open one to edit its details, thumbnail and groups, or to add people." : "Every dashboard in the portal, newest first. Open one to edit its details, thumbnail and groups, or to preview it."}</p>
        </div>
        {!owner && can(me, "publishing", "Edit") && (
          <Link className="btn btn-primary" to="/publish"><Icon name="upload" size={18} /> Publish Dashboard</Link>
        )}
      </div>

      {uncategorised > 0 && (
        <Notice>
          {uncategorised} dashboard{uncategorised === 1 ? " has" : "s have"} no category yet. Open {uncategorised === 1 ? "it" : "each one"} and choose one under Details, so it shows up in the Category view of the home page.
        </Notice>
      )}

      {data.length === 0 ? (
        <div className="empty"><h2>{owner ? "No Dashboards Yet" : "Nothing Published Yet"}</h2><p>{owner ? "Dashboards you are named as an owner of appear here." : "Publish a .pbix and it appears here."}</p></div>
      ) : (
        <>
          <div className="filters">
            <label className="field field-search">
              <span>Search</span>
              <span className="input-icon"><Icon name="search" size={18} /><input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Name, code, owner, category or tag" /></span>
            </label>
            <label className="field">
              <span>Category</span>
              <select value={primary} onChange={(e) => setPrimary(e.target.value)}>
                <option value="">All</option>
                {primaries.map((p) => <option key={p}>{p}</option>)}
                <option value="-">No category</option>
              </select>
            </label>
            <label className="field">
              <span>Status</span>
              <select value={status} onChange={(e) => setStatus(e.target.value)}>
                <option value="">All</option>
                {["Active", "Publishing", "Failed", "Draft", "Inactive", "Retired"].map((s) => <option key={s}>{s}</option>)}
              </select>
            </label>
            <span className="muted small filters-count">{rows.length} of {data.length}</span>
          </div>

          <div className="table-wrap">
            <table className="grid grid-rows">
              <thead>
                <tr><th className="col-thumb"><span className="visually-hidden">Thumbnail</span></th><th>Dashboard</th><th>Primary Category</th><th>Type</th><th>RLS</th><th>Owners</th><th className="num">Groups</th><th className="num">Members</th><th>Status</th><th>Published</th></tr>
              </thead>
              <tbody>
                {rows.map((d) => (
                  <tr key={d.id} onClick={() => navigate(routes.dashboard(d.id))}>
                    <td className="col-thumb"><div className="mini-thumb"><Thumbnail dashboardId={d.id} version={d.thumbnail} type={d.type} /></div></td>
                    <td>
                      <Link to={routes.dashboard(d.id)} onClick={(e) => e.stopPropagation()}>{d.name}</Link>
                      <div className="muted small">{d.code} (#{d.id})</div>
                    </td>
                    <td className="small" title={d.categoryPath ?? undefined}>{d.categoryPath ? d.categoryPath.split(" / ")[0] : <span className="warn-text">No category</span>}</td>
                    <td className="small"><TypeIcon type={d.type} /></td>
                    <td>
                      {d.rlsEnabled ? <span className="yn yn-yes">Y</span> : <span className="yn">N</span>}
                      {d.needsRls && <span className="warn-text small" title="A group on this dashboard has no RLS value"> needs a value</span>}
                    </td>
                    <td className="small">{d.owner ?? "–"}{d.backupOwner && <div className="muted">{d.backupOwner}</div>}</td>
                    <td className="num">{d.groups}</td>
                    <td className="num">{d.members}</td>
                    <td><StatusPill status={d.status} /></td>
                    <td className="small muted">{when(d.publishedAtUtc)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            {rows.length === 0 && <p className="pop-empty">No dashboards match these filters.</p>}
          </div>
        </>
      )}
    </>
  );
}
