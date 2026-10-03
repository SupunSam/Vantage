import { useEffect, useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { api, ApiError, Icon, Thumbnail, useSession } from "@vantage/shared";

type MyDashboard = {
  id: number;
  code: string;
  name: string;
  description: string | null;
  type: "PowerBi" | "Tableau" | "GenAi";
  categoryId: number | null;
  categoryPath: string | null;
  owner: string | null;
  tags: string[];
  thumbnail: string | null;
  publishedAtUtc: string | null;
  grantedAtUtc: string;
  linked: boolean;
  pinned: boolean;
  pinOrder: number | null;
  lastViewedAtUtc: string | null;
};

type Grouping = "all" | "category";
type Layout = "cards" | "list";
type SortKey = "name" | "category" | "owner" | "type" | "published";

const typeLabel: Record<MyDashboard["type"], string> = { PowerBi: "Power BI", Tableau: "Tableau", GenAi: "GenAI" };
const NEW_DAYS = 14;
const MAX_PINS = 12;
const PREFS_KEY = "rd.homeView";

function readPrefs(): { grouping: Grouping; layout: Layout } {
  try {
    const v = JSON.parse(localStorage.getItem(PREFS_KEY) ?? "{}");
    return { grouping: v.grouping === "category" ? "category" : "all", layout: v.layout === "list" ? "list" : "cards" };
  } catch {
    return { grouping: "all", layout: "cards" };
  }
}

const time = (iso: string | null) => (iso ? new Date(iso.endsWith("Z") ? iso : iso + "Z").getTime() : 0);

/** Home page: the dashboards this person can open, as thumbnail cards (newest first), by category, or as a sortable list. */
export function HomePage() {
  const { me } = useSession();
  const [rows, setRows] = useState<MyDashboard[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [q, setQ] = useState("");
  const [prefs, setPrefs] = useState(readPrefs);
  const [sort, setSort] = useState<{ key: SortKey; desc: boolean }>({ key: "published", desc: true });

  useEffect(() => {
    api<MyDashboard[]>("/api/dashboards/mine").then(setRows).catch((e) => setError(e.message));
  }, []);

  function setPref(p: Partial<typeof prefs>) {
    const next = { ...prefs, ...p };
    setPrefs(next);
    try {
      localStorage.setItem(PREFS_KEY, JSON.stringify(next));
    } catch {
      /* kept for this visit only */
    }
  }

  async function togglePin(d: MyDashboard) {
    setNotice(null);
    try {
      await api(`/api/dashboards/${d.id}/pin`, { method: d.pinned ? "DELETE" : "POST" });
      setRows((rs) => rs?.map((r) => (r.id === d.id ? { ...r, pinned: !d.pinned, pinOrder: d.pinned ? null : Date.now() } : r)) ?? null);
    } catch (e) {
      setNotice(e instanceof ApiError ? e.message : String(e));
    }
  }

  const filtered = useMemo(() => {
    const needle = q.trim().toLowerCase();
    if (!rows) return [];
    if (!needle) return rows;
    return rows.filter((d) => [d.name, d.description, d.owner, d.categoryPath, d.code, ...d.tags].some((v) => v?.toLowerCase().includes(needle)));
  }, [rows, q]);

  if (error) return <p className="notice notice-error">{error}</p>;
  if (!rows) return <p className="muted">Loading your dashboards…</p>;

  const pinned = filtered.filter((r) => r.pinned).sort((a, b) => (a.pinOrder ?? 0) - (b.pinOrder ?? 0));
  const pinCount = rows.filter((r) => r.pinned).length;
  const firstName = me?.displayName.split(" ")[0];

  return (
    <>
      <div className="home-head">
        <div>
          <h1>{firstName ? `${firstName}'s Dashboards` : "My Dashboards"}</h1>
          <p className="muted">
            {rows.length === 0
              ? "You don't have access to any dashboards yet."
              : `${rows.length} dashboard${rows.length === 1 ? "" : "s"} you can open${pinCount ? `, ${pinCount} pinned` : ""}.`}
          </p>
        </div>
      </div>

      {rows.length === 0 ? (
        <div className="state">
          <h2>Nothing Here Yet</h2>
          <p className="muted">Access to a dashboard comes from its owner. Once the catalogue is open you can request access there; for now, ask the dashboard's owner or a Super Admin to add you.</p>
        </div>
      ) : (
        <>
          <div className="home-tools">
            <label className="home-search">
              <Icon name="search" size={20} />
              <input type="search" value={q} onChange={(e) => setQ(e.target.value)} placeholder="Search by name, description, owner, category or tag" aria-label="Search your dashboards" />
            </label>
            {prefs.layout === "cards" && <div className="seg" role="group" aria-label="Arrange">
              <button type="button" aria-pressed={prefs.grouping === "all"} onClick={() => setPref({ grouping: "all" })}>Newest First</button>
              <button type="button" aria-pressed={prefs.grouping === "category"} onClick={() => setPref({ grouping: "category" })}>By Category</button>
            </div>}
            <div className="seg seg-icons" role="group" aria-label="Layout">
              <button type="button" aria-pressed={prefs.layout === "cards"} onClick={() => setPref({ layout: "cards" })} aria-label="Cards" title="Cards"><Icon name="dashboards" size={18} /></button>
              <button type="button" aria-pressed={prefs.layout === "list"} onClick={() => setPref({ layout: "list" })} aria-label="List" title="List"><Icon name="list" size={18} /></button>
            </div>
          </div>

          {notice && <p className="notice notice-error">{notice}</p>}

          {filtered.length === 0 && <p className="muted">No dashboards match “{q}”.</p>}

          {prefs.layout === "list" ? (
            <ListView rows={filtered} sort={sort} onSort={(key) => setSort((s) => ({ key, desc: s.key === key ? !s.desc : key === "published" }))} onPin={togglePin} />
          ) : (
            <>
              {pinned.length > 0 && !q && (
                <section className="home-section">
                  <h2>Pinned <span className="muted">{pinned.length} of {MAX_PINS}</span></h2>
                  <Cards rows={pinned} onPin={togglePin} />
                </section>
              )}
              {prefs.grouping === "all" ? (
                filtered.length > 0 && (
                  <section className="home-section">
                    {pinned.length > 0 && !q && <h2>All Dashboards</h2>}
                    <Cards rows={[...filtered].sort((a, b) => time(b.publishedAtUtc) - time(a.publishedAtUtc))} onPin={togglePin} />
                  </section>
                )
              ) : (
                <CategoryView rows={filtered} onPin={togglePin} />
              )}
            </>
          )}
        </>
      )}
    </>
  );
}

function CategoryView({ rows, onPin }: { rows: MyDashboard[]; onPin: (d: MyDashboard) => void }) {
  // One section per primary category; each card shows the rest of its path. Not categorised goes last.
  const groups = useMemo(() => {
    const map = new Map<string, MyDashboard[]>();
    for (const r of rows) {
      const key = r.categoryPath?.split(" / ")[0] ?? "";
      map.set(key, [...(map.get(key) ?? []), r]);
    }
    return [...map.entries()]
      .sort(([a], [b]) => (a === "" ? 1 : b === "" ? -1 : a.localeCompare(b)))
      .map(([key, items]) => [key, items.sort((x, y) => (x.categoryPath ?? "").localeCompare(y.categoryPath ?? "") || time(y.publishedAtUtc) - time(x.publishedAtUtc))] as const);
  }, [rows]);

  return (
    <>
      {groups.map(([primary, items]) => (
        <section key={primary || "-"} className="home-section">
          <h2 className="cat-head">
            <span className="cat-primary">{primary || "Not Categorised"}</span>
            <span className="muted cat-count">{items.length} dashboard{items.length === 1 ? "" : "s"}</span>
          </h2>
          <Cards rows={items} onPin={onPin} />
        </section>
      ))}
    </>
  );
}

function Cards({ rows, onPin }: { rows: MyDashboard[]; onPin: (d: MyDashboard) => void }) {
  return (
    <ul className="cards">
      {rows.map((d) => {
        const isNew = Date.now() - time(d.grantedAtUtc) < NEW_DAYS * 86_400_000 && !d.lastViewedAtUtc;
        return (
          <li key={d.id} className="card">
            <Link to={`/dashboards/${d.id}`} className="card-link">
              <div className="card-thumb">
                <Thumbnail dashboardId={d.id} version={d.thumbnail} type={d.type} />
                {isNew && <span className="card-new">New</span>}
              </div>
              <div className="card-body">
                {d.categoryPath && <p className="card-cat">{d.categoryPath}</p>}
                <h3>{d.name}</h3>
                {d.description && <p className="card-desc">{d.description}</p>}
                <p className="card-meta">
                  <span>{typeLabel[d.type]}</span>
                  {d.owner && <span>{d.owner}</span>}
                </p>
                {!d.linked && <p className="card-warn">Not linked to a report yet</p>}
              </div>
            </Link>
            <button type="button" className={`card-pin ${d.pinned ? "card-pin-on" : ""}`} onClick={() => onPin(d)}
              aria-pressed={d.pinned} aria-label={d.pinned ? `Unpin ${d.name}` : `Pin ${d.name}`} title={d.pinned ? "Unpin" : "Pin to the top"}>
              <Icon name="pin" size={18} />
            </button>
          </li>
        );
      })}
    </ul>
  );
}

function ListView({ rows, sort, onSort, onPin }: { rows: MyDashboard[]; sort: { key: SortKey; desc: boolean }; onSort: (k: SortKey) => void; onPin: (d: MyDashboard) => void }) {
  const value = (d: MyDashboard, k: SortKey): string | number =>
    k === "name" ? d.name.toLowerCase() : k === "category" ? (d.categoryPath ?? "~").toLowerCase() : k === "owner" ? (d.owner ?? "~").toLowerCase()
      : k === "type" ? typeLabel[d.type] : time(d.publishedAtUtc);
  const sorted = [...rows].sort((a, b) => {
    const x = value(a, sort.key), y = value(b, sort.key);
    const c = x < y ? -1 : x > y ? 1 : 0;
    return sort.desc ? -c : c;
  });
  const Th = ({ k, label }: { k: SortKey; label: string }) => (
    <th aria-sort={sort.key === k ? (sort.desc ? "descending" : "ascending") : "none"}>
      <button type="button" className="th-sort" onClick={() => onSort(k)}>
        {label}{sort.key === k && <Icon name={sort.desc ? "arrowDown" : "arrowUp"} size={14} />}
      </button>
    </th>
  );
  return (
    <div className="table-wrap">
      <table className="grid list-grid">
        <thead><tr><th className="col-pin"><span className="visually-hidden">Pinned</span></th><Th k="name" label="Dashboard" /><Th k="category" label="Category" /><Th k="owner" label="Owner" /><Th k="type" label="Type" /><Th k="published" label="Published" /></tr></thead>
        <tbody>
          {sorted.map((d) => (
            <tr key={d.id}>
              <td className="col-pin">
                <button type="button" className={`pin-btn ${d.pinned ? "card-pin-on" : ""}`} onClick={() => onPin(d)} aria-pressed={d.pinned} aria-label={d.pinned ? `Unpin ${d.name}` : `Pin ${d.name}`}>
                  <Icon name="pin" size={16} />
                </button>
              </td>
              <td><Link to={`/dashboards/${d.id}`}>{d.name}</Link>{d.description && <div className="muted small list-desc">{d.description}</div>}</td>
              <td className="small">{d.categoryPath ?? <span className="muted">Not categorised</span>}</td>
              <td className="small">{d.owner ?? "–"}</td>
              <td className="small">{typeLabel[d.type]}</td>
              <td className="small muted">{d.publishedAtUtc ? new Date(time(d.publishedAtUtc)).toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" }) : "–"}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
