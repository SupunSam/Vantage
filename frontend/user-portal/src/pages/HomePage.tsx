import { useEffect, useMemo, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { api, ApiError, Icon, Thumbnail, useSession } from "@vantage/shared";
import { FolderMenu } from "./FolderMenu";
import { FoldersPage } from "./FoldersPage";
import { Crumbs, FolderGrid, plural } from "./FolderGrid";

type MyDashboard = {
  id: number;
  code: string;
  name: string;
  description: string | null;
  type: "PowerBi" | "Tableau" | "GenAi";
  categoryId: number | null;
  categoryPath: string | null;
  /** The route from the primary category down to this dashboard's own category; empty when it has none. */
  categoryChain: { id: number; name: string; sortOrder: number }[];
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

/** Home: two tabs. My Dashboards (everything this person can open) and Personal Folders (their own groupings of those). */
export function HomePage() {
  const { me } = useSession();
  const [search, setSearch] = useSearchParams();
  const tab = search.get("tab") === "folders" ? "folders" : "dashboards";
  const firstName = me?.displayName.split(" ")[0];
  const tabs = [{ key: "dashboards", label: "My Dashboards" }, { key: "folders", label: "Personal Folders" }] as const;
  return (
    <>
      <div className="home-head">
        <h1>Home</h1>
        <p className="muted">{firstName ? `Welcome, ${firstName}. ` : ""}Your dashboards, and the folders you keep them in.</p>
      </div>
      <div className="tabs" role="tablist">
        {tabs.map((t) => (
          <button key={t.key} type="button" role="tab" aria-selected={tab === t.key} className={`tab ${tab === t.key ? "tab-on" : ""}`}
            onClick={() => setSearch(t.key === "dashboards" ? {} : { tab: t.key }, { replace: true })}>{t.label}</button>
        ))}
      </div>
      {tab === "folders" ? <FoldersPage embedded /> : <MyDashboards />}
    </>
  );
}

/** The dashboards this person can open, as thumbnail cards (newest first), by category, or as a sortable list. */
function MyDashboards() {
  const [search, setSearch] = useSearchParams();
  const cat = search.get("cat");                       // the category folder we are inside, or none for the top level
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
    if (p.grouping && p.grouping !== prefs.grouping) setSearch({}, { replace: true });   // leaving or entering category browsing starts at the top
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

  return (
    <>
      <p className="muted home-count">
        {rows.length === 0
          ? "You don't have access to any dashboards yet."
          : `${rows.length} dashboard${rows.length === 1 ? "" : "s"} you can open${pinCount ? `, ${pinCount} pinned` : ""}.`}
      </p>

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
              {pinned.length > 0 && !q && !(prefs.grouping === "category" && cat) && (
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
              ) : q ? (
                // A search looks across every category, so show the matches together instead of folders.
                filtered.length > 0 && <section className="home-section"><Cards rows={filtered} onPin={togglePin} /></section>
              ) : (
                <CategoryBrowser rows={filtered} cat={cat} onOpen={(id) => setSearch(id === null ? {} : { cat: String(id) })} onPin={togglePin} />
              )}
            </>
          )}
        </>
      )}
    </>
  );
}

type CatNode = { id: number; name: string; sortOrder: number; children: Map<number, CatNode>; direct: MyDashboard[]; total: number };
const NO_CATEGORY = -1;   // the folder for dashboards that have no category

const newNode = (id: number, name: string, sortOrder: number): CatNode => ({ id, name, sortOrder, children: new Map(), direct: [], total: 0 });

/** The categories that hold dashboards this person can open, as a tree. Each dashboard sits in the deepest category it was filed under. */
function buildCategoryTree(rows: MyDashboard[]) {
  const root = newNode(0, "All Categories", 0);
  const index = new Map<number, { node: CatNode; trail: CatNode[] }>();
  const uncategorised: MyDashboard[] = [];
  for (const r of rows) {
    if (r.categoryChain.length === 0) { uncategorised.push(r); continue; }
    let node = root;
    root.total++;
    for (const ref of r.categoryChain) {
      let child = node.children.get(ref.id);
      if (!child) { child = newNode(ref.id, ref.name, ref.sortOrder); node.children.set(ref.id, child); }
      child.total++;
      node = child;
    }
    node.direct.push(r);
  }
  if (uncategorised.length > 0) {
    const none = newNode(NO_CATEGORY, "Not Categorised", Number.MAX_SAFE_INTEGER);
    none.direct = uncategorised;
    none.total = uncategorised.length;
    root.children.set(NO_CATEGORY, none);
    root.total += uncategorised.length;
  }
  const walk = (node: CatNode, trail: CatNode[]) => {
    for (const child of node.children.values()) {
      index.set(child.id, { node: child, trail: [...trail, child] });
      walk(child, [...trail, child]);
    }
  };
  walk(root, []);
  return { root, index };
}

const byOrder = (a: CatNode, b: CatNode) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name);

/**
 * Categories as folders. The top level shows the primary categories; open one to see its secondary categories (as folders again)
 * and the dashboards filed directly in it. Where you are is kept in the address (?cat=), so Back and a refresh work.
 */
function CategoryBrowser({ rows, cat, onOpen, onPin }: { rows: MyDashboard[]; cat: string | null; onOpen: (id: number | null) => void; onPin: (d: MyDashboard) => void }) {
  const { root, index } = useMemo(() => buildCategoryTree(rows), [rows]);
  const here = cat !== null && /^-?\d+$/.test(cat) ? index.get(Number(cat)) : undefined;
  const node = here?.node ?? root;
  const trail = here?.trail ?? [];
  const folders = [...node.children.values()].sort(byOrder);
  const tile = (c: CatNode) => ({
    key: String(c.id), name: c.name, onOpen: () => onOpen(c.id),
    sub: plural(c.total, "dashboard") + (c.children.size > 0 ? `, ${plural(c.children.size, "sub-category", "sub-categories")}` : ""),
  });

  return (
    <section className="home-section">
      {trail.length > 0 && (
        <Crumbs trail={[{ label: "All Categories", onClick: () => onOpen(null) }, ...trail.map((t) => ({ label: t.name, onClick: () => onOpen(t.id) }))]} />
      )}
      {folders.length > 0 && <FolderGrid tiles={folders.map(tile)} />}
      {node.direct.length > 0 && (
        <>
          {folders.length > 0 && <h2 className="cat-inside">Dashboards in {node.name}</h2>}
          <Cards rows={[...node.direct].sort((a, b) => time(b.publishedAtUtc) - time(a.publishedAtUtc))} onPin={onPin} />
        </>
      )}
      {folders.length === 0 && node.direct.length === 0 && <p className="muted">Nothing is filed here.</p>}
    </section>
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
            <FolderMenu dashboardId={d.id} name={d.name} />
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
