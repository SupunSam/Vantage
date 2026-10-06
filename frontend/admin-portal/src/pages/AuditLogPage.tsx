import { useEffect, useState } from "react";
import { api, apiObjectUrl, Icon, useGridPageSize, PageSkeleton, ErrorState } from "@vantage/shared";
import { errorText, Modal, Notice, useApi, when } from "@vantage/shared";

type Row = {
  id: number; occurredAtUtc: string; actorUserId: number | null; actor: string | null; action: string; entityType: string; entityId: string | null;
  dashboardId: number | null; dashboard: string | null; serviceNowReference: string | null; ipAddress: string | null; correlationId: string | null; details: string | null; description: string;
};
type Page = { total: number; page: number; pageSize: number; searchableFromUtc: string; rows: Row[] };
type Options = { entityTypes: string[]; actions: string[] };
type Filters = { from: string; to: string; actor: string; entityType: string; action: string; serviceNow: string; search: string };

const empty: Filters = { from: "", to: "", actor: "", entityType: "", action: "", serviceNow: "", search: "" };

function queryString(f: Filters) {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(f)) if (v.trim()) p.set(k, v.trim());
  return p;
}

/** Waits for typing to pause before the filters are sent. */
function useDebounced<T>(value: T, ms = 350) {
  const [v, setV] = useState(value);
  useEffect(() => {
    const t = setTimeout(() => setV(value), ms);
    return () => clearTimeout(t);
  }, [value, ms]);
  return v;
}

/** Audit Log: every change made in the portal, searchable for the last 12 months, with an Excel export. */
export function AuditLogPage() {
  const [filters, setFilters] = useState<Filters>(empty);
  const [page, setPage] = useState(1);
  const [open, setOpen] = useState<number | null>(null);
  const [exporting, setExporting] = useState(false);
  const [exportError, setExportError] = useState<string | null>(null);
  const applied = useDebounced(filters);
  const gridSize = useGridPageSize();
  const options = useApi<Options>("/api/admin/audit-log/options");

  const qs = queryString(applied);
  qs.set("page", String(page));
  qs.set("pageSize", String(gridSize));
  const { data, error, loading } = useApi<Page>(`/api/admin/audit-log?${qs}`);

  const set = (k: keyof Filters, v: string) => { setFilters((f) => ({ ...f, [k]: v })); setPage(1); };
  const filtered = Object.values(filters).some((v) => v.trim());
  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;

  async function exportExcel() {
    setExporting(true);
    setExportError(null);
    try {
      const url = await apiObjectUrl(`/api/admin/audit-log/export?${queryString(applied)}`);
      const a = document.createElement("a");
      a.href = url;
      a.download = `audit-log-${new Date().toISOString().slice(0, 10)}.xlsx`;
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
    } catch (e) { setExportError(errorText(e)); } finally { setExporting(false); }
  }

  if (error) return <ErrorState>{error}</ErrorState>;

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Audit Log</h1>
          <p>
            Every change made in the portal, newest first, described in plain words. The log is kept permanently; this page searches the last 12 months
            {data ? `, from ${new Date(data.searchableFromUtc).toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" })}` : ""}.
            The export uses the filters below (up to 50,000 entries).
          </p>
        </div>
        <button className="btn" type="button" disabled={exporting || !data || data.total === 0} onClick={() => void exportExcel()}>
          <Icon name="download" size={18} /> {exporting ? "Preparing…" : "Export to Excel"}
        </button>
      </div>
      {exportError && <Notice tone="error">{exportError}</Notice>}

      <div className="filters">
        <label className="field"><span>From</span><input type="date" value={filters.from} max={filters.to || undefined} onChange={(e) => set("from", e.target.value)} /></label>
        <label className="field"><span>To</span><input type="date" value={filters.to} min={filters.from || undefined} onChange={(e) => set("to", e.target.value)} /></label>
        <label className="field"><span>Who</span><input value={filters.actor} onChange={(e) => set("actor", e.target.value)} placeholder="Name or email" /></label>
        <label className="field">
          <span>Action</span>
          <select value={filters.action} onChange={(e) => set("action", e.target.value)}>
            <option value="">All actions</option>
            {options.data?.actions.map((a) => <option key={a} value={a}>{a}</option>)}
          </select>
        </label>
        <label className="field">
          <span>Entity type</span>
          <select value={filters.entityType} onChange={(e) => set("entityType", e.target.value)}>
            <option value="">All types</option>
            {options.data?.entityTypes.map((t) => <option key={t} value={t}>{t}</option>)}
          </select>
        </label>
        <label className="field"><span>ServiceNow reference</span><input value={filters.serviceNow} onChange={(e) => set("serviceNow", e.target.value)} placeholder="e.g. INC0012345" /></label>
        <label className="field field-search">
          <span>Search</span>
          <span className="input-icon"><Icon name="search" size={18} /><input value={filters.search} onChange={(e) => set("search", e.target.value)} placeholder="Entity id or text in the details" /></span>
        </label>
        {filtered && <button className="btn btn-quiet" type="button" onClick={() => { setFilters(empty); setPage(1); }}>Clear Filters</button>}
        <span className="muted small filters-count">{data ? `${data.total.toLocaleString()} ${data.total === 1 ? "entry" : "entries"}` : ""}</span>
      </div>

      {!data ? <PageSkeleton kind="table" head={false} /> : data.rows.length === 0 ? (
        <p className="muted">{filtered ? "No entries match these filters." : "Nothing has been logged yet."}</p>
      ) : (
        <div className="table-wrap" aria-busy={loading}>
          <table className="grid grid-rows audit-grid">
            <thead><tr><th>When</th><th>Who</th><th>Description</th></tr></thead>
            <tbody>
              {data.rows.map((r) => (
                <tr key={r.id} tabIndex={0} onClick={() => setOpen(r.id)} onKeyDown={(e) => { if (e.key === "Enter") setOpen(r.id); }}>
                  <td>{when(r.occurredAtUtc)}</td>
                  <td>{r.actor ?? <span className="muted">System</span>}</td>
                  <td>{r.description}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {data && data.total > data.pageSize && (
        <div className="pager">
          <span className="muted small">Page {page} of {pages}</span>
          <button className="btn" type="button" disabled={page <= 1} onClick={() => setPage(page - 1)}>Previous</button>
          <button className="btn" type="button" disabled={page >= pages} onClick={() => setPage(page + 1)}>Next</button>
        </div>
      )}

      {open != null && <EntryModal id={open} onClose={() => setOpen(null)} />}
    </>
  );
}

type Change = { label: string; before: unknown; after: unknown };

/** Pairs up fields named xBefore/xAfter or from/to; everything else is returned as "other". */
function splitDetails(json: string | null): { changes: Change[]; other: Record<string, unknown> | null; raw: string | null } {
  if (!json) return { changes: [], other: null, raw: null };
  let parsed: unknown;
  try { parsed = JSON.parse(json); } catch { return { changes: [], other: null, raw: json }; }
  if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) return { changes: [], other: null, raw: JSON.stringify(parsed, null, 2) };
  const obj = { ...(parsed as Record<string, unknown>) };
  const changes: Change[] = [];
  const take = (a: string, b: string, label: string) => {
    if (a in obj && b in obj) { changes.push({ label, before: obj[a], after: obj[b] }); delete obj[a]; delete obj[b]; }
  };
  take("from", "to", "Value");
  for (const k of Object.keys(obj)) {
    const m = /^(.*)Before$/.exec(k);
    if (m && `${m[1]}After` in obj) take(k, `${m[1]}After`, m[1].replace(/([A-Z])/g, " $1").replace(/^./, (c) => c.toUpperCase()));
  }
  return { changes, other: Object.keys(obj).length ? obj : null, raw: null };
}

const show = (v: unknown) => (v === null || v === undefined || v === "" ? "–" : typeof v === "string" ? v : JSON.stringify(v));

function EntryModal({ id, onClose }: { id: number; onClose: () => void }) {
  const [row, setRow] = useState<Row | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { api<Row>(`/api/admin/audit-log/${id}`).then(setRow).catch((e) => setError(errorText(e))); }, [id]);

  const d = row ? splitDetails(row.details) : null;
  return (
    <Modal title="Audit Entry" onClose={onClose} wide>
      {error && <Notice tone="error">{error}</Notice>}
      {!row && !error && <p className="muted">Loading…</p>}
      {row && d && (
        <>
          <p className="audit-description">{row.description}</p>
          <dl className="facts">
            <dt>When</dt><dd>{when(row.occurredAtUtc)}</dd>
            <dt>Who</dt><dd>{row.actor ?? "System"}</dd>
            <dt>Action</dt><dd><code>{row.action}</code></dd>
            <dt>Entity</dt><dd>{row.entityType}{row.entityId ? ` #${row.entityId}` : ""}</dd>
            <dt>Dashboard</dt><dd>{row.dashboard ?? "–"}</dd>
            <dt>ServiceNow</dt><dd>{row.serviceNowReference ?? "–"}</dd>
            <dt>IP address</dt><dd>{row.ipAddress ?? "–"}</dd>
            <dt>Correlation id</dt><dd>{row.correlationId ?? "–"}</dd>
          </dl>

          {d.changes.length > 0 && (
            <>
              <h3>Before and After</h3>
              <div className="table-wrap">
                <table className="grid">
                  <thead><tr><th>Field</th><th>Before</th><th>After</th></tr></thead>
                  <tbody>{d.changes.map((c) => <tr key={c.label}><td>{c.label}</td><td>{show(c.before)}</td><td>{show(c.after)}</td></tr>)}</tbody>
                </table>
              </div>
            </>
          )}
          {(d.other || d.raw) && (
            <>
              <h3>{d.changes.length ? "Other Details" : "Details"}</h3>
              <pre className="json-box">{d.raw ?? JSON.stringify(d.other, null, 2)}</pre>
            </>
          )}
          {!row.details && <p className="muted">No further details were recorded for this entry.</p>}
        </>
      )}
    </Modal>
  );
}
