import { useEffect, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { api, DashboardDetailPanel, DashboardsPanel, downloadFile, Icon, OverviewPanel, Pager, RangePicker, TypePicker, typeParam, usePaged, type Overview } from "@vantage/shared";
import { errorText, Notice, Pill, useApi, when } from "@vantage/shared";

const BASE = "/api/admin/analytics";
const tabs = [
  { key: "overview", label: "Overview" },
  { key: "dashboards", label: "Dashboards" },
  { key: "reports", label: "Access Reports" },
] as const;
type Tab = (typeof tabs)[number]["key"];

/**
 * Analytics for Super Admins: how the dashboards are used (from the views the portal records when someone opens one),
 * and two access reports: who can open a dashboard, and what a person can open.
 */
export function AnalyticsPage() {
  const [search, setSearch] = useSearchParams();
  const tab: Tab = search.get("tab") === "dashboards" ? "dashboards" : search.get("tab") === "reports" ? "reports" : "overview";
  const [days, setDays] = useState(30);
  const [type, setType] = useState("");   // all BI types by default
  const [selected, setSelected] = useState<number | null>(null);
  const overview = useApi<Overview>(tab === "overview" ? `${BASE}/overview?days=${days}${typeParam(type)}` : null);

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Analytics</h1>
          <p>
            How dashboards are used, and who can open what. Usage counts each time someone opens a dashboard in the User Portal; previews by Super Admins are not counted.
            Power BI activity and Tableau usage will be added to the same numbers when those imports are connected.
          </p>
        </div>
        {tab !== "reports" && <div className="actions"><TypePicker type={type} onChange={setType} /><RangePicker days={days} onChange={setDays} /></div>}
      </div>

      <div className="tabs" role="tablist">
        {tabs.map((t) => (
          <button key={t.key} type="button" role="tab" aria-selected={tab === t.key} className={`tab ${tab === t.key ? "tab-on" : ""}`}
            onClick={() => { setSelected(null); setSearch(t.key === "overview" ? {} : { tab: t.key }, { replace: true }); }}>{t.label}</button>
        ))}
      </div>

      {tab !== "reports" && selected !== null && <DashboardDetailPanel base={BASE} id={selected} days={days} onClose={() => setSelected(null)} />}

      {tab === "overview" && (
        overview.error ? <Notice tone="error">{overview.error}</Notice>
          : !overview.data ? <p className="muted">Loading…</p>
          : <OverviewPanel o={overview.data} scopeLabel="active dashboards" onOpenDashboard={setSelected} />
      )}
      {tab === "dashboards" && <DashboardsPanel base={BASE} days={days} type={type} exportPath={`${BASE}/dashboards/export`} onOpen={setSelected} />}
      {tab === "reports" && <Reports />}
    </>
  );
}

// ---------------------------------------------------------------- Access reports

type Person = { userId: number; email: string; name: string | null; userType: string; userStatus: string; groupId: number; group: string; groupStatus: string; rlsValue: string | null; addedAtUtc: string; canOpenNow: boolean };
type Who = { dashboardId: number; dashboard: string; status: string; rlsEnabled: boolean; people: Person[] };
type Dash = { dashboardId: number; dashboard: string; code: string; type: string; status: string; rlsEnabled: boolean; groupId: number; group: string; groupStatus: string; rlsValue: string | null; addedAtUtc: string; canOpenNow: boolean };
type What = { userId: number; email: string; name: string | null; userStatus: string; dashboards: Dash[] };
type Found = { id: number; email: string; displayName: string | null; status: string };

const typeLabel: Record<string, string> = { PowerBi: "Power BI", Tableau: "Tableau", GenAi: "GenAI" };

function Reports() {
  return (
    <>
      <WhoCanOpenReport />
      <WhatCanOpenReport />
    </>
  );
}

function CanPill({ yes, why }: { yes: boolean; why?: string }) {
  return yes ? <Pill tone="ok">Yes</Pill> : <span title={why}><Pill tone="warn">Not now</Pill></span>;
}

function WhoCanOpenReport() {
  const options = useApi<{ dashboards: { id: number; name: string; code: string; status: string }[] }>(`${BASE}/report-options`);
  const [id, setId] = useState("");
  const [data, setData] = useState<Who | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [q, setQ] = useState("");
  const rows = (data?.people ?? []).filter((p) => !q.trim() || `${p.name ?? ""} ${p.email} ${p.group}`.toLowerCase().includes(q.trim().toLowerCase()));
  const paged = usePaged(rows, 25, `${id}|${q}`);

  useEffect(() => {
    setData(null);
    setError(null);
    if (!id) return;
    api<Who>(`${BASE}/reports/who-can-open/${id}`).then(setData).catch((e) => setError(errorText(e)));
  }, [id]);

  const canNow = data?.people.filter((p) => p.canOpenNow).length ?? 0;
  return (
    <section className="panel">
      <div className="panel-row">
        <h2>Who Can Open a Dashboard</h2>
        {data && <button className="btn" type="button" onClick={() => void downloadFile(`${BASE}/reports/who-can-open/${data.dashboardId}/export`, `who-can-open-${data.dashboard}.xlsx`).catch((e) => setError(errorText(e)))}><Icon name="download" size={18} /> Export to Excel</button>}
      </div>
      <div className="filters">
        <label className="field">
          <span>Dashboard</span>
          <select value={id} onChange={(e) => setId(e.target.value)}>
            <option value="">Choose…</option>
            {options.data?.dashboards.map((d) => <option key={d.id} value={d.id}>{d.name}{d.status !== "Active" ? ` (${d.status.toLowerCase()})` : ""}</option>)}
          </select>
        </label>
        {data && data.people.length > 8 && (
          <label className="field field-search"><span>Find</span><span className="input-icon"><Icon name="search" size={18} /><input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Name, email or group" /></span></label>
        )}
      </div>
      {error && <Notice tone="error">{error}</Notice>}
      {data && (
        <>
          <p className="muted small">
            <strong>{canNow}</strong> {canNow === 1 ? "person can" : "people can"} open <strong>{data.dashboard}</strong> right now
            {data.people.length > canNow ? `, and ${data.people.length - canNow} more ${data.people.length - canNow === 1 ? "has" : "have"} a place in a group but can't at the moment (inactive person, group or dashboard)` : ""}.
            {data.status !== "Active" ? ` The dashboard is ${data.status.toLowerCase()}, so nobody can open it.` : ""}
          </p>
          {data.people.length === 0 ? <p className="pop-empty">Nobody is in a group of this dashboard.</p> : (
            <>
              <div className="requests-table-wrap">
                <table className="requests-table">
                  <thead><tr><th>Person</th><th>Access group</th>{data.rlsEnabled && <th>RLS value</th>}<th>In the group since</th><th>Can open now</th></tr></thead>
                  <tbody>
                    {paged.rows.map((p) => (
                      <tr key={p.userId}>
                        <td>{p.name ?? p.email}<div className="req-muted">{p.email} · {p.userType.toLowerCase()}{p.userStatus === "Inactive" ? " · inactive" : ""}</div></td>
                        <td>{p.group}{p.groupStatus !== "Active" && <div className="req-muted">{p.groupStatus.toLowerCase()}</div>}</td>
                        {data.rlsEnabled && <td>{p.rlsValue ? <code>{p.rlsValue}</code> : "–"}</td>}
                        <td className="small">{when(p.addedAtUtc)}</td>
                        <td><CanPill yes={p.canOpenNow} why="The person, the group or the dashboard is inactive" /></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <Pager {...paged.pager} />
            </>
          )}
        </>
      )}
    </section>
  );
}

function WhatCanOpenReport() {
  const [q, setQ] = useState("");
  const [found, setFound] = useState<Found[]>([]);
  const [data, setData] = useState<What | null>(null);
  const [error, setError] = useState<string | null>(null);
  const paged = usePaged(data?.dashboards ?? [], 25, data?.userId);

  useEffect(() => {
    if (q.trim().length < 2) { setFound([]); return; }
    const t = window.setTimeout(() => api<Found[]>(`${BASE}/users?q=${encodeURIComponent(q.trim())}`).then(setFound).catch(() => setFound([])), 200);
    return () => window.clearTimeout(t);
  }, [q]);

  async function pick(u: Found) {
    setQ("");
    setFound([]);
    setError(null);
    try { setData(await api<What>(`${BASE}/reports/what-can-open/${u.id}`)); } catch (e) { setError(errorText(e)); }
  }

  const canNow = data?.dashboards.filter((d) => d.canOpenNow).length ?? 0;
  return (
    <section className="panel">
      <div className="panel-row">
        <h2>What a Person Can Open</h2>
        {data && <button className="btn" type="button" onClick={() => void downloadFile(`${BASE}/reports/what-can-open/${data.userId}/export`, `what-can-open-${data.email}.xlsx`).catch((e) => setError(errorText(e)))}><Icon name="download" size={18} /> Export to Excel</button>}
      </div>
      <div className="filters">
        <label className="field field-search">
          <span>Find a person</span>
          <span className="input-icon"><Icon name="search" size={18} /><input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Name or email" /></span>
        </label>
      </div>
      {found.length > 0 && (
        <ul className="pick-list">
          {found.map((u) => (
            <li key={u.id}>
              <button type="button" onClick={() => void pick(u)}>
                <span className="pick-text"><strong>{u.displayName ?? u.email}</strong><span className="muted small">{u.email}{u.status === "Inactive" ? ", inactive" : ""}</span></span>
                <span className="pick-add">Show</span>
              </button>
            </li>
          ))}
        </ul>
      )}
      {error && <Notice tone="error">{error}</Notice>}
      {data && (
        <>
          <p className="muted small">
            <strong>{data.name ?? data.email}</strong> can open <strong>{canNow}</strong> {canNow === 1 ? "dashboard" : "dashboards"} right now
            {data.userStatus === "Inactive" ? ". They are an inactive user, so they can't open any" : ""}.
          </p>
          {data.dashboards.length === 0 ? <p className="pop-empty">They aren't in any access group.</p> : (
            <>
              <div className="requests-table-wrap">
                <table className="requests-table">
                  <thead><tr><th>Dashboard</th><th>Access group</th><th>RLS value</th><th>In the group since</th><th>Can open now</th></tr></thead>
                  <tbody>
                    {paged.rows.map((d) => (
                      <tr key={`${d.dashboardId}-${d.groupId}`}>
                        <td>{d.dashboard}<div className="req-muted">{typeLabel[d.type] ?? d.type}{d.status !== "Active" ? ` · ${d.status.toLowerCase()}` : ""}</div></td>
                        <td>{d.group}{d.groupStatus !== "Active" && <div className="req-muted">{d.groupStatus.toLowerCase()}</div>}</td>
                        <td>{d.rlsValue ? <code>{d.rlsValue}</code> : <span className="muted">{d.rlsEnabled ? "none" : "not used"}</span>}</td>
                        <td className="small">{when(d.addedAtUtc)}</td>
                        <td><CanPill yes={d.canOpenNow} why="The person, the group or the dashboard is inactive" /></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <Pager {...paged.pager} />
            </>
          )}
        </>
      )}
    </section>
  );
}
