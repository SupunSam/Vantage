import { useMemo, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { Icon } from "../Icon";
import { Thumbnail } from "../Thumbnail";
import { TypeIcon } from "../TypeIcon";
import { can, useGridPageSize, useSession } from "../session";
import { GridFrame, usePaged } from "../Pager";
import { Notice, SortHeader, StatusPill, useApi, useSort, when } from "./ui";
import { useManage } from "./routes";
import { PageSkeleton, ErrorState, EmptyState } from "../Feedback";

type Row = {
  id: number; code: string; name: string; description: string | null; type: string; status: string; rlsEnabled: boolean; lastError: string | null;
  categoryId: number | null; categoryPath: string | null; tenant: string | null; workspace: string | null; owner: string | null; backupOwner: string | null;
  tags: string[]; audience: string; classification: string; thumbnail: string | null;
  publishedAtUtc: string | null; createdAtUtc: string; groups: number; needsRls: boolean; members: number; myGroup: string | null;
  /** Set on a saved draft of the publish form (not a dashboard yet). */
  draftId?: number;
};
type DraftRow = { id: number; type: string; title: string; payload: string; updatedAtUtc: string };

/** A saved draft shown as a row, so it can be found, filtered by status Draft and opened to carry on publishing. */
function draftAsRow(d: DraftRow): Row {
  let p: { description?: string; tags?: string[]; audience?: string; classification?: string } = {};
  try { p = JSON.parse(d.payload); } catch { /* an unreadable draft still shows */ }
  return {
    id: -d.id, draftId: d.id, code: "", name: d.title, description: p.description ?? null, type: d.type, status: "Draft", rlsEnabled: false, lastError: null,
    categoryId: null, categoryPath: null, tenant: null, workspace: null, owner: null, backupOwner: null, tags: p.tags ?? [], audience: p.audience ?? "Internal",
    classification: p.classification ?? "Internal", thumbnail: null, publishedAtUtc: null, createdAtUtc: d.updatedAtUtc, groups: 0, needsRls: false, members: 0, myGroup: null,
  };
}
const draftLink = (d: Row) => `/publish?type=${d.type.toLowerCase()}&draft=${d.draftId}`;


/** Dashboards Master: every dashboard with its category, RLS flag, owners and groups. Open one to edit it. */
export function DashboardsPage() {
  const { me } = useSession();
  const navigate = useNavigate();
  const routes = useManage();
  const owner = routes.mode === "owner";
  const { data, error } = useApi<Row[]>(routes.listApi);
  // Saved drafts of the publish form sit in the same grid, with the status Draft (only the person's own, and only for people who publish).
  const drafts = useApi<DraftRow[]>(!owner && can(me, "publishing", "Edit") ? "/api/publishing/drafts" : null).data;
  const all = useMemo(() => [...(data ?? []), ...(drafts ?? []).map(draftAsRow)], [data, drafts]);
  const [q, setQ] = useState("");
  const [status, setStatus] = useState("");
  const [primary, setPrimary] = useState("");

  const primaries = useMemo(() => [...new Set(all.map((d) => d.categoryPath?.split(" / ")[0]).filter(Boolean) as string[])].sort(), [all]);
  const rows = useMemo(() => {
    const needle = q.trim().toLowerCase();
    return all.filter((d) =>
      (!status || d.status === status)
      && (!primary || (primary === "-" ? !d.categoryPath : d.categoryPath?.split(" / ")[0] === primary))
      && (!needle || [d.name, d.code, d.description, d.owner, d.backupOwner, d.categoryPath, ...d.tags].some((v) => v?.toLowerCase().includes(needle))));
  }, [all, q, status, primary]);
  const { sorted, sort, toggle } = useSort(rows, {
    name: (d) => d.name, category: (d) => d.categoryPath?.split(" / ")[0], type: (d) => d.type, rls: (d) => (d.rlsEnabled ? 1 : 0), owner: (d) => d.owner,
    groups: (d) => d.groups, members: (d) => d.members, status: (d) => d.status, published: (d) => d.publishedAtUtc,
  });
  const paged = usePaged(sorted, useGridPageSize(), `${q}|${status}|${primary}|${sort?.key}|${sort?.desc}`);

  if (error) return <ErrorState>{error}</ErrorState>;
  if (!data) return <PageSkeleton kind="table" />;

  const uncategorised = all.filter((d) => !d.categoryPath && d.status !== "Retired" && !d.draftId).length;

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

      {all.length === 0 ? (
        <EmptyState icon="dashboards" title={owner ? "No Dashboards Yet" : "Nothing Published Yet"}>{owner ? "Dashboards you are named as an owner of appear here." : "Publish a .pbix and it appears here."}</EmptyState>
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
          </div>

          <GridFrame pager={paged.pager}>
            <div className="table-wrap">
              <table className="grid grid-rows">
                <thead>
                  <tr><th className="col-thumb"><span className="visually-hidden">Thumbnail</span></th><SortHeader label="Dashboard" k="name" sort={sort} onSort={toggle} /><SortHeader label="Primary Category" k="category" sort={sort} onSort={toggle} /><SortHeader label="Type" k="type" sort={sort} onSort={toggle} /><SortHeader label="RLS" k="rls" sort={sort} onSort={toggle} /><SortHeader label="Owners" k="owner" sort={sort} onSort={toggle} /><SortHeader label="Groups" k="groups" sort={sort} onSort={toggle} className="num" /><SortHeader label="Members" k="members" sort={sort} onSort={toggle} className="num" /><SortHeader label="Status" k="status" sort={sort} onSort={toggle} /><SortHeader label="Published" k="published" sort={sort} onSort={toggle} /></tr>
                </thead>
                <tbody>
                  {paged.rows.map((d) => (
                    <tr key={d.id} onClick={() => navigate(d.draftId ? draftLink(d) : routes.dashboard(d.id))}>
                      <td className="col-thumb"><div className="mini-thumb"><Thumbnail dashboardId={d.id} version={d.thumbnail} type={d.type} /></div></td>
                      <td>
                        <Link to={d.draftId ? draftLink(d) : routes.dashboard(d.id)} onClick={(e) => e.stopPropagation()}>{d.name}</Link>
                        <div className="muted small">{d.draftId ? "Saved draft, not published yet" : `${d.code} (#${d.id})`}</div>
                      </td>
                      <td className="small" title={d.categoryPath ?? undefined}>{d.categoryPath ? d.categoryPath.split(" / ")[0] : d.draftId ? <span className="muted">–</span> : <span className="warn-text">No category</span>}</td>
                      <td className="small"><TypeIcon type={d.type} /></td>
                      <td>
                        {d.draftId ? <span className="muted">–</span> : d.rlsEnabled ? <span className="yn yn-yes">Y</span> : <span className="yn">N</span>}
                        {d.needsRls && <span className="warn-text small" title="A group on this dashboard has no RLS value"> needs a value</span>}
                      </td>
                      <td className="small">{d.owner ?? "–"}{d.backupOwner && <div className="muted">{d.backupOwner}</div>}</td>
                      <td className="num">{d.groups}</td>
                      <td className="num">{d.members}</td>
                      <td><StatusPill status={d.status} /></td>
                      <td className="small muted">{d.draftId ? `Saved ${when(d.createdAtUtc)}` : when(d.publishedAtUtc)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              {rows.length === 0 && <p className="pop-empty">No dashboards match these filters.</p>}
            </div>
          </GridFrame>
        </>
      )}
    </>
  );
}
