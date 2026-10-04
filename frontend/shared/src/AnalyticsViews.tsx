import { useEffect, useMemo, useState } from "react";
import { api, ApiError, apiObjectUrl } from "./api";
import { Icon } from "./Icon";
import { Pager, usePaged } from "./Pager";
import { TrendChart, type TrendPoint } from "./TrendChart";

export type TopDashboard = { id: number; name: string; type: string; views: number; users: number; lastViewedAtUtc: string | null };
export type TopUser = { id: number; name: string; email: string; views: number; dashboards: number; lastViewedAtUtc: string | null };
export type Overview = {
  days: number; fromUtc: string; views: number; uniqueUsers: number; inScope: number; viewed: number; unused: number;
  daily: TrendPoint[]; topDashboards: TopDashboard[]; topUsers: TopUser[];
  byType: { type: string; views: number; dashboards: number }[];
  unusedList: { id: number; name: string; type: string; owner: string | null; lastViewedAtUtc: string | null; members: number }[];
};
export type DashboardUsage = {
  id: number; code: string; name: string; type: string; status: string; owner: string | null; backupOwner: string | null;
  views: number; users: number; lastViewedAtUtc: string | null; members: number; flagged: boolean; daysSinceView: number | null;
};
export type UsageDetail = { id: number; name: string; type: string; status: string; days: number; views: number; users: number; members: number; membersNeverOpened: number; daily: TrendPoint[]; topUsers: TopUser[] };

export const RANGES = [{ days: 7, label: "7 days" }, { days: 30, label: "30 days" }, { days: 90, label: "90 days" }, { days: 365, label: "12 months" }] as const;
const typeLabel: Record<string, string> = { PowerBi: "Power BI", Tableau: "Tableau", GenAi: "GenAI" };
const errorText = (e: unknown) => (e instanceof ApiError ? e.message : e instanceof Error ? e.message : String(e));
const when = (iso: string | null) => (iso ? new Date(iso.endsWith("Z") ? iso : iso + "Z").toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" }) : "Never");

/** Downloads a file the API builds (Excel), sending the same sign-in header as every other call. */
export async function downloadFile(path: string, fileName: string) {
  const url = await apiObjectUrl(path);
  const a = document.createElement("a");
  a.href = url;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}

/** The date range switcher that sits above every analytics view and scopes all of it. */
export function RangePicker({ days, onChange }: { days: number; onChange: (d: number) => void }) {
  return (
    <div className="seg" role="group" aria-label="Date range">
      {RANGES.map((r) => <button key={r.days} type="button" aria-pressed={days === r.days} onClick={() => onChange(r.days)}>{r.label}</button>)}
    </div>
  );
}

function Tile({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return <div className="an-tile"><span className="an-tile-label">{label}</span><strong className="an-tile-value">{value}</strong>{hint && <span className="an-tile-hint">{hint}</span>}</div>;
}

// ---------------------------------------------------------------- Overview

export function OverviewPanel({ o, scopeLabel, onOpenDashboard }: { o: Overview; scopeLabel: string; onOpenDashboard?: (id: number) => void }) {
  const unusedPage = usePaged(o.unusedList, 10);
  const days = RANGES.find((r) => r.days === o.days)?.label ?? `${o.days} days`;
  const open = (id: number, name: string) => (onOpenDashboard ? <button type="button" className="link" onClick={() => onOpenDashboard(id)}>{name}</button> : <>{name}</>);
  return (
    <>
      <div className="an-tiles">
        <Tile label="Views" value={o.views.toLocaleString()} hint={`last ${days}`} />
        <Tile label="People who opened a dashboard" value={o.uniqueUsers.toLocaleString()} />
        <Tile label="Dashboards opened" value={`${o.viewed} of ${o.inScope}`} hint={scopeLabel} />
        <Tile label="Nobody opened" value={o.unused.toLocaleString()} hint={o.unused > 0 ? "candidates to retire or promote" : "every dashboard was used"} />
      </div>

      <section className="panel">
        <TrendChart points={o.daily} title={`Views per ${o.daily.length > 120 ? "week" : "day"}, last ${days}`} />
      </section>

      <div className="an-cols">
        <section className="panel">
          <h2>Most Opened Dashboards</h2>
          {o.topDashboards.length === 0 ? <p className="muted">No views in this period.</p> : (
            <table className="requests-table">
              <thead><tr><th>Dashboard</th><th className="num">Views</th><th className="num">People</th></tr></thead>
              <tbody>{o.topDashboards.map((d) => <tr key={d.id}><td>{open(d.id, d.name)}<div className="req-muted">{typeLabel[d.type] ?? d.type}</div></td><td className="num">{d.views.toLocaleString()}</td><td className="num">{d.users.toLocaleString()}</td></tr>)}</tbody>
            </table>
          )}
        </section>
        <section className="panel">
          <h2>Most Active People</h2>
          {o.topUsers.length === 0 ? <p className="muted">No views in this period.</p> : (
            <table className="requests-table">
              <thead><tr><th>Person</th><th className="num">Views</th><th className="num">Dashboards</th></tr></thead>
              <tbody>{o.topUsers.map((u) => <tr key={u.id}><td>{u.name}<div className="req-muted">{u.email}</div></td><td className="num">{u.views.toLocaleString()}</td><td className="num">{u.dashboards}</td></tr>)}</tbody>
            </table>
          )}
        </section>
      </div>

      {o.byType.length > 1 && (
        <section className="panel">
          <h2>Views by Dashboard Type</h2>
          <table className="requests-table">
            <thead><tr><th>Type</th><th className="num">Views</th><th className="num">Dashboards opened</th></tr></thead>
            <tbody>{o.byType.map((t) => <tr key={t.type}><td>{typeLabel[t.type] ?? t.type}</td><td className="num">{t.views.toLocaleString()}</td><td className="num">{t.dashboards}</td></tr>)}</tbody>
          </table>
        </section>
      )}

      {o.unusedList.length > 0 && (
        <section className="panel">
          <h2>Nobody Opened These in the Last {days} <span className="muted count">{o.unused}</span></h2>
          <p className="muted small">Active dashboards with no views in this period, longest unused first. Consider asking the owner whether they are still needed.{o.unused > o.unusedList.length ? ` Showing ${o.unusedList.length} of ${o.unused}.` : ""}</p>
          <table className="requests-table">
            <thead><tr><th>Dashboard</th><th>Owner</th><th>Last opened</th><th className="num">Members</th></tr></thead>
            <tbody>{unusedPage.rows.map((d) => <tr key={d.id}><td>{open(d.id, d.name)}</td><td>{d.owner ?? "–"}</td><td>{when(d.lastViewedAtUtc)}</td><td className="num">{d.members}</td></tr>)}</tbody>
          </table>
          <Pager {...unusedPage.pager} sizes={[10, 20]} />
        </section>
      )}
    </>
  );
}

// ---------------------------------------------------------------- Dashboards table

type SortKey = "name" | "views" | "users" | "lastViewedAtUtc" | "members";

export function DashboardsPanel({ base, days, exportPath, onOpen }: { base: string; days: number; exportPath?: string; onOpen: (id: number) => void }) {
  const [rows, setRows] = useState<DashboardUsage[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [q, setQ] = useState("");
  const [sort, setSort] = useState<{ key: SortKey; desc: boolean }>({ key: "views", desc: true });

  useEffect(() => {
    setRows(null);
    api<DashboardUsage[]>(`${base}/dashboards?days=${days}`).then(setRows).catch((e) => setError(errorText(e)));
  }, [base, days]);

  const sorted = useMemo(() => {
    const needle = q.trim().toLowerCase();
    const list = (rows ?? []).filter((r) => !needle || [r.name, r.code, r.owner, r.backupOwner].some((v) => v?.toLowerCase().includes(needle)));
    const value = (r: DashboardUsage) => (sort.key === "name" ? r.name.toLowerCase() : sort.key === "lastViewedAtUtc" ? (r.lastViewedAtUtc ?? "") : r[sort.key]);
    return [...list].sort((a, b) => { const x = value(a), y = value(b); const c = x < y ? -1 : x > y ? 1 : 0; return sort.desc ? -c : c; });
  }, [rows, q, sort]);
  const paged = usePaged(sorted, 25, `${q}|${days}`);

  if (error) return <p className="notice notice-error">{error}</p>;
  if (!rows) return <p className="muted">Loading…</p>;

  const Th = ({ k, label, num }: { k: SortKey; label: string; num?: boolean }) => (
    <th className={num ? "num" : ""} aria-sort={sort.key === k ? (sort.desc ? "descending" : "ascending") : "none"}>
      <button type="button" className="th-sort" onClick={() => setSort((s) => ({ key: k, desc: s.key === k ? !s.desc : k !== "name" }))}>
        {label}{sort.key === k && <Icon name={sort.desc ? "arrowDown" : "arrowUp"} size={14} />}
      </button>
    </th>
  );

  return (
    <section className="panel">
      <div className="panel-row">
        <h2>Dashboards <span className="muted count">{sorted.length}</span></h2>
        <div className="actions">
          <span className="input-icon input-compact"><Icon name="search" size={16} /><input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Find a dashboard or owner" aria-label="Find a dashboard or owner" /></span>
          {exportPath && <button className="btn" type="button" onClick={() => void downloadFile(`${exportPath}?days=${days}`, `dashboard-usage-${new Date().toISOString().slice(0, 10)}.xlsx`).catch((e) => setError(errorText(e)))}><Icon name="download" size={18} /> Export to Excel</button>}
        </div>
      </div>
      <div className="requests-table-wrap">
        <table className="requests-table">
          <thead><tr><Th k="name" label="Dashboard" /><th>Owner</th><Th k="views" label="Views" num /><Th k="users" label="People" num /><Th k="lastViewedAtUtc" label="Last opened" /><Th k="members" label="Members" num /></tr></thead>
          <tbody>
            {paged.rows.map((r) => (
              <tr key={r.id}>
                <td>
                  <button type="button" className="link" onClick={() => onOpen(r.id)}>{r.name}</button>
                  <div className="req-muted">{typeLabel[r.type] ?? r.type}{r.status !== "Active" ? ` · ${r.status.toLowerCase()}` : ""}{r.flagged ? " · flagged as unused" : ""}</div>
                </td>
                <td className="small">{r.owner ?? <span className="muted">No owner</span>}{r.backupOwner && <div className="req-muted">{r.backupOwner}</div>}</td>
                <td className="num">{r.views.toLocaleString()}</td>
                <td className="num">{r.users.toLocaleString()}</td>
                <td className="small">{when(r.lastViewedAtUtc)}{r.daysSinceView != null && r.daysSinceView > 0 && <div className="req-muted">{r.daysSinceView} days ago</div>}</td>
                <td className="num">{r.members.toLocaleString()}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {sorted.length === 0 && <p className="pop-empty">No dashboards match.</p>}
      <Pager {...paged.pager} />
    </section>
  );
}

// ---------------------------------------------------------------- One dashboard

export function DashboardDetailPanel({ base, id, days, onClose }: { base: string; id: number; days: number; onClose: () => void }) {
  const [d, setD] = useState<UsageDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    setD(null);
    setError(null);
    api<UsageDetail>(`${base}/dashboards/${id}?days=${days}`).then(setD).catch((e) => setError(errorText(e)));
  }, [base, id, days]);
  const label = RANGES.find((r) => r.days === days)?.label ?? `${days} days`;

  return (
    <section className="panel an-detail">
      <div className="panel-row">
        <h2>{d?.name ?? "Dashboard"}</h2>
        <button type="button" className="btn btn-quiet" onClick={onClose}>Close</button>
      </div>
      {error && <p className="notice notice-error">{error}</p>}
      {!d && !error && <p className="muted">Loading…</p>}
      {d && (
        <>
          <div className="an-tiles">
            <Tile label="Views" value={d.views.toLocaleString()} hint={`last ${label}`} />
            <Tile label="People who opened it" value={d.users.toLocaleString()} />
            <Tile label="Members" value={d.members.toLocaleString()} />
            <Tile label="Members who never opened it" value={d.membersNeverOpened.toLocaleString()} hint={`in the last ${label}`} />
          </div>
          <TrendChart points={d.daily} title={`Views per ${d.daily.length > 120 ? "week" : "day"}, last ${label}`} />
          <h3>Who Opens It Most</h3>
          {d.topUsers.length === 0 ? <p className="muted">Nobody opened it in this period.</p> : (
            <table className="requests-table">
              <thead><tr><th>Person</th><th className="num">Views</th><th>Last opened</th></tr></thead>
              <tbody>{d.topUsers.map((u) => <tr key={u.id}><td>{u.name}<div className="req-muted">{u.email}</div></td><td className="num">{u.views.toLocaleString()}</td><td>{when(u.lastViewedAtUtc)}</td></tr>)}</tbody>
            </table>
          )}
        </>
      )}
    </section>
  );
}
