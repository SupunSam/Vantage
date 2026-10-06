import { useEffect, useMemo, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { api, ApiError, Icon, Thumbnail, useSession, useFlash, BiInactiveBadge, PageSkeleton, ErrorState } from "@vantage/shared";
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
const PREFS_KEY = "vantage.homeView";

type Sort = { key: SortKey; desc: boolean };
const SORT_KEYS: { key: SortKey; label: string }[] = [
  { key: "name", label: "Name" }, { key: "published", label: "Published" }, { key: "category", label: "Category" }, { key: "owner", label: "Owner" }, { key: "type", label: "Type" },
];
const DEFAULT_SORT: Sort = { key: "name", desc: false };

function readPrefs(): { grouping: Grouping; layout: Layout; sort: Sort } {
  try {
    const v = JSON.parse(localStorage.getItem(PREFS_KEY) ?? "{}");
    const key = SORT_KEYS.find((k) => k.key === v.sort?.key)?.key;
    return { grouping: v.grouping === "category" ? "category" : "all", layout: v.layout === "list" ? "list" : "cards", sort: key ? { key, desc: v.sort.desc === true } : DEFAULT_SORT };
  } catch {
    return { grouping: "all", layout: "cards", sort: DEFAULT_SORT };
  }
}

/** The order a dashboard sorts by for a column; missing values go last when ascending. */
function sortValue(d: MyDashboard, k: SortKey): string | number {
  return k === "name" ? d.name.toLowerCase() : k === "category" ? (d.categoryPath ?? "~").toLowerCase() : k === "owner" ? (d.owner ?? "~").toLowerCase()
    : k === "type" ? typeLabel[d.type] : time(d.publishedAtUtc);
}

function sortRows(rows: MyDashboard[], sort: Sort): MyDashboard[] {
  return [...rows].sort((a, b) => {
    const x = sortValue(a, sort.key), y = sortValue(b, sort.key);
    const c = x < y ? -1 : x > y ? 1 : 0;
    return (sort.desc ? -c : c) || a.name.localeCompare(b.name);   // equal values fall back to the name, so the order is steady
  });
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
  const setNotice = useFlash();
  const [q, setQ] = useState("");
  const [prefs, setPrefs] = useState(readPrefs);

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

  if (error) return <ErrorState onRetry={() => window.location.reload()}>{error}</ErrorState>;
  if (!rows) return <PageSkeleton kind="cards" />;

  const pinned = filtered.filter((r) => r.pinned).sort((a, b) => (a.pinOrder ?? 0) - (b.pinOrder ?? 0));
  const pinCount = rows.filter((r) => r.pinned).length;
  // Pinned dashboards sit on top, so they are left out of the list below instead of showing twice.
  const showPinned = pinned.length > 0 && !q && !(prefs.grouping === "category" && cat);
  const rest = showPinned && prefs.grouping === "all" ? filtered.filter((r) => !r.pinned) : filtered;

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
              <input type="search" value={q} onChange={(e) => setQ(e.target.value)} placeholder="Search dashboards" title="Search by name, description, owner, category or tag" aria-label="Search your dashboards by name, description, owner, category or tag" />
            </label>
            <div className="home-controls">
            {prefs.layout === "cards" && <div className="seg" role="group" aria-label="Arrange">
              <button type="button" aria-pressed={prefs.grouping === "all"} onClick={() => setPref({ grouping: "all" })}>By Name</button>
              <button type="button" aria-pressed={prefs.grouping === "category"} onClick={() => setPref({ grouping: "category" })}>By Category</button>
            </div>}
            <div className="seg seg-icons" role="group" aria-label="Layout">
              <button type="button" aria-pressed={prefs.layout === "cards"} onClick={() => setPref({ layout: "cards" })} aria-label="Cards" title="Cards"><Icon name="dashboards" size={18} /></button>
              <button type="button" aria-pressed={prefs.layout === "list"} onClick={() => setPref({ layout: "list" })} aria-label="List" title="List"><Icon name="list" size={18} /></button>
            </div>
            <div className="home-sort" role="group" aria-label="Sort">
              <label className="visually-hidden" htmlFor="home-sort-key">Sort by</label>
              <select id="home-sort-key" value={prefs.sort.key} onChange={(e) => setPref({ sort: { key: e.target.value as SortKey, desc: e.target.value === "published" } })}>
                {SORT_KEYS.map((k) => <option key={k.key} value={k.key}>Sort by {k.label}</option>)}
              </select>
              <button type="button" className="sort-dir" onClick={() => setPref({ sort: { ...prefs.sort, desc: !prefs.sort.desc } })}
                aria-label={prefs.sort.desc ? "Sorted descending. Switch to ascending" : "Sorted ascending. Switch to descending"} title={prefs.sort.desc ? "Descending" : "Ascending"}>
                <Icon name={prefs.sort.desc ? "arrowDown" : "arrowUp"} size={18} />
              </button>
            </div>
            </div>
          </div>


          {filtered.length === 0 && <p className="muted">No dashboards match “{q}”.</p>}

          {prefs.layout === "list" ? (
            <ListView rows={sortRows(filtered, prefs.sort)} sort={prefs.sort} onSort={(key) => setPref({ sort: { key, desc: prefs.sort.key === key ? !prefs.sort.desc : key === "published" } })} onPin={togglePin} />
          ) : (
            <>
              {showPinned && (
                <section className="home-section">
                  <h2>Pinned <span className="muted">{pinned.length} of {MAX_PINS}</span></h2>
                  <Cards rows={pinned} onPin={togglePin} />
                </section>
              )}
              {prefs.grouping === "all" ? (
                rest.length > 0 && (
                  <section className="home-section">
                    {showPinned && <h2>Other Dashboards</h2>}
                    <Cards rows={sortRows(rest, prefs.sort)} onPin={togglePin} />
                  </section>
                )
              ) : q ? (
                // A search looks across every category, so show the matches together instead of folders.
                filtered.length > 0 && <section className="home-section"><Cards rows={filtered} onPin={togglePin} /></section>
              ) : (
                <CategoryBrowser rows={filtered} cat={cat} sort={prefs.sort} onOpen={(id) => setSearch(id === null ? {} : { cat: String(id) })} onPin={togglePin} />
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
function CategoryBrowser({ rows, cat, sort, onOpen, onPin }: { rows: MyDashboard[]; cat: string | null; sort: Sort; onOpen: (id: number | null) => void; onPin: (d: MyDashboard) => void }) {
  const { root, index } = useMemo(() => buildCategoryTree(rows), [rows]);
  const here = cat !== null && /^-?\d+$/.test(cat) ? index.get(Number(cat)) : undefined;
  const node = here?.node ?? root;
  const trail = here?.trail ?? [];
  const folders = [...node.children.values()].sort(byOrder);
  const tile = (c: CatNode) => ({
    key: String(c.id), name: c.name, onOpen: () => onOpen(c.id),
    sub: plural(c.total, "dashboard"),
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
          <Cards rows={sortRows(node.direct, sort)} onPin={onPin} />
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
                  <span>{typeLabel[d.type]}<BiInactiveBadge type={d.type} /></span>
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

function ListView({ rows, sort, onSort, onPin }: { rows: MyDashboard[]; sort: Sort; onSort: (k: SortKey) => void; onPin: (d: MyDashboard) => void }) {
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
          {rows.map((d) => (
            <tr key={d.id}>
              <td className="col-pin">
                <button type="button" className={`pin-btn ${d.pinned ? "card-pin-on" : ""}`} onClick={() => onPin(d)} aria-pressed={d.pinned} aria-label={d.pinned ? `Unpin ${d.name}` : `Pin ${d.name}`}>
                  <Icon name="pin" size={16} />
                </button>
              </td>
              <td><Link to={`/dashboards/${d.id}`}>{d.name}</Link>{d.description && <div className="muted small list-desc">{d.description}</div>}</td>
              <td className="small">{d.categoryPath ?? <span className="muted">Not categorised</span>}</td>
              <td className="small">{d.owner ?? "–"}</td>
              <td className="small">{typeLabel[d.type]}<BiInactiveBadge type={d.type} /></td>
              <td className="small muted">{d.publishedAtUtc ? new Date(time(d.publishedAtUtc)).toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" }) : "–"}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
