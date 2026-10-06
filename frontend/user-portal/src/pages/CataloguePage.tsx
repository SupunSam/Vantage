import { useCallback, useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { api, ApiError, Icon, Thumbnail, whenText, useFlash, BiInactiveBadge, PageSkeleton, ErrorState, useApi } from "@vantage/shared";

type Item = {
  id: number; name: string; description: string | null; type: string; categoryPath: string | null; thumbnail: string | null;
  publishedAtUtc: string | null; owner: string | null; tags: string[];
  state: "Open" | "Requested" | "Ownerless" | "None"; pendingRequestId: number | null;
};
type MyRequest = { id: number; status: string; requestedAtUtc: string; decidedAtUtc: string | null; requesterComment: string | null; decisionNote: string | null; dashboardId: number; dashboard: string };

const typeLabel: Record<string, string> = { PowerBi: "Power BI", Tableau: "Tableau", GenAi: "GenAI" };
const errorText = (e: unknown) => (e instanceof ApiError ? e.message : String(e));

/** Dashboard Catalogue: every dashboard the user may see, with Request Access for the ones they can't open yet. */
export function CataloguePage() {
  const { data: items, error, reload: reloadItems } = useApi<Item[]>("/api/catalogue");
  const { data: mineData, reload: reloadMine } = useApi<MyRequest[]>("/api/access-requests/mine");
  const mine = mineData ?? [];
  const setNotice = useFlash();
  const [q, setQ] = useState("");
  const [primary, setPrimary] = useState("");
  const [onlyNew, setOnlyNew] = useState(false);

  const load = useCallback(() => { reloadItems(); reloadMine(); }, [reloadItems, reloadMine]);

  const primaries = useMemo(() => [...new Set((items ?? []).map((i) => i.categoryPath?.split(" / ")[0]).filter(Boolean) as string[])].sort(), [items]);
  const shown = useMemo(() => {
    const needle = q.trim().toLowerCase();
    return (items ?? []).filter((i) =>
      (!onlyNew || i.state !== "Open")
      && (!primary || i.categoryPath?.split(" / ")[0] === primary)
      && (!needle || [i.name, i.description, i.owner, i.categoryPath, ...i.tags].some((v) => v?.toLowerCase().includes(needle))));
  }, [items, q, primary, onlyNew]);

  if (error && !items) return <ErrorState onRetry={load}>{error}</ErrorState>;
  if (!items) return <PageSkeleton kind="cards" />;

  const recent = mine.filter((r) => r.status === "Pending" || (r.decidedAtUtc && Date.now() - new Date(r.decidedAtUtc + "Z").getTime() < 30 * 86_400_000));

  async function withdraw(id: number) {
    try {
      await api(`/api/access-requests/${id}/cancel`, { method: "POST" });
      setNotice({ ok: true, text: "Request withdrawn." });
      load();
    } catch (e) {
      setNotice({ ok: false, text: errorText(e) });
    }
  }

  return (
    <>
      <div className="home-head">
        <h1>Dashboard Catalogue</h1>
        <p className="muted">Every dashboard in Vantage. Open the ones you have access to, or ask the owner for access to the others.</p>
      </div>


      {recent.length > 0 && (
        <section className="home-section">
          <h2>Your Requests</h2>
          <ul className="my-requests">
            {recent.map((r) => (
              <li key={r.id}>
                <span className={`req-status req-${r.status === "Cancelled" ? "withdrawn" : r.status.toLowerCase()}`}>{r.status === "Cancelled" ? "Withdrawn" : r.status === "Pending" ? "Waiting for the owner" : r.status}</span>
                <span className="my-req-name">{r.status === "Approved" ? <Link to={`/dashboards/${r.dashboardId}`}>{r.dashboard}</Link> : r.dashboard}</span>
                <span className="muted small">{r.status === "Pending" ? `asked ${whenText(r.requestedAtUtc)}` : whenText(r.decidedAtUtc)}{r.decisionNote ? `: “${r.decisionNote}”` : ""}</span>
                {r.status === "Pending" && <button type="button" className="link" onClick={() => void withdraw(r.id)}>Withdraw</button>}
              </li>
            ))}
          </ul>
        </section>
      )}

      <div className="home-tools">
        <label className="home-search">
          <Icon name="search" size={20} />
          <input type="search" value={q} onChange={(e) => setQ(e.target.value)} placeholder="Search by name, description, owner, category or tag" aria-label="Search the catalogue" />
        </label>
        <select className="home-select" value={primary} onChange={(e) => setPrimary(e.target.value)} aria-label="Category">
          <option value="">All Categories</option>
          {primaries.map((p) => <option key={p}>{p}</option>)}
        </select>
        <label className="check"><input type="checkbox" checked={onlyNew} onChange={(e) => setOnlyNew(e.target.checked)} /> Only ones I can't open yet</label>
      </div>

      {shown.length === 0 ? <p className="muted">No dashboards match.</p> : (
        <ul className="cards">
          {shown.map((i) => <CatalogueCard key={i.id} item={i} onRequested={(text) => { setNotice({ ok: true, text }); load(); }} onError={(text) => setNotice({ ok: false, text })} />)}
        </ul>
      )}
    </>
  );
}

function CatalogueCard({ item, onRequested, onError }: { item: Item; onRequested: (t: string) => void; onError: (t: string) => void }) {
  const [asking, setAsking] = useState(false);
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);

  async function send() {
    setBusy(true);
    try {
      await api("/api/access-requests", { method: "POST", body: JSON.stringify({ dashboardId: item.id, comment: reason || null }) });
      onRequested(`Request sent. The owner of ${item.name} will approve or reject it, and you'll be notified.`);
    } catch (e) {
      onError(errorText(e));
      setBusy(false);
    }
  }

  return (
    <li className="card card-static">
      <div className="card-link">
        <div className="card-thumb">
          <Thumbnail dashboardId={item.id} version={item.thumbnail} type={item.type} />
          {item.state === "Open" && <span className="card-badge card-badge-ok">You have access</span>}
          {item.state === "Requested" && <span className="card-badge">Requested</span>}
        </div>
        <div className="card-body">
          {item.categoryPath && <p className="card-cat">{item.categoryPath}</p>}
          <h3>{item.name}</h3>
          {item.description && <p className="card-desc">{item.description}</p>}
          <p className="card-meta"><span>{typeLabel[item.type] ?? item.type}<BiInactiveBadge type={item.type} /></span>{item.owner && <span>Owner: {item.owner}</span>}</p>

          <div className="card-action">
            {item.state === "Open" && <Link className="btn btn-primary" to={`/dashboards/${item.id}`}>Open Dashboard</Link>}
            {item.state === "Requested" && <span className="muted small">Waiting for the owner to decide.</span>}
            {item.state === "Ownerless" && <span className="muted small">This dashboard has no owner right now. Please request access again in a few days.</span>}
            {item.state === "None" && !asking && <button type="button" className="btn" onClick={() => setAsking(true)}>Request Access</button>}
            {item.state === "None" && asking && (
              <div className="ask">
                <label className="field">
                  <span>Why do you need it? (helps the owner decide)</span>
                  <textarea rows={2} maxLength={1000} value={reason} onChange={(e) => setReason(e.target.value)} autoFocus />
                </label>
                <div className="actions">
                  <button type="button" className="btn btn-primary" disabled={busy} onClick={() => void send()}>{busy ? "Sending…" : "Send Request"}</button>
                  <button type="button" className="btn btn-quiet" onClick={() => setAsking(false)}>Cancel</button>
                </div>
              </div>
            )}
          </div>
        </div>
      </div>
    </li>
  );
}
