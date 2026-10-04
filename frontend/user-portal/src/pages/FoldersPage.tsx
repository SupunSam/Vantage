import { useCallback, useEffect, useMemo, useState, type FormEvent } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { api, ApiError, Icon, Thumbnail } from "@vantage/shared";
import { Crumbs, FolderGrid, plural } from "./FolderGrid";

export type Folder = { id: number; name: string; dashboardIds: number[] };
type Mine = { id: number; name: string; description: string | null; type: "PowerBi" | "Tableau" | "GenAi"; categoryPath: string | null; thumbnail: string | null };

const typeLabel: Record<Mine["type"], string> = { PowerBi: "Power BI", Tableau: "Tableau", GenAi: "GenAI" };
const MAX_FOLDERS = 20;
const message = (e: unknown) => (e instanceof ApiError ? e.message : e instanceof Error ? e.message : String(e));

/** Personal Folders: the person's own one-level folders of the dashboards they can open. */
export function FoldersPage({ embedded = false }: { embedded?: boolean }) {
  const [folders, setFolders] = useState<Folder[] | null>(null);
  const [mine, setMine] = useState<Mine[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [search, setSearch] = useSearchParams();
  const [newName, setNewName] = useState("");
  const [renaming, setRenaming] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [adding, setAdding] = useState(false);
  const [q, setQ] = useState("");

  const load = useCallback(async () => {
    try {
      const [f, m] = await Promise.all([api<Folder[]>("/api/folders"), api<Mine[]>("/api/dashboards/mine")]);
      setFolders(f);
      setMine(m);
    } catch (e) { setError(message(e)); }
  }, []);
  useEffect(() => { void load(); }, [load]);

  // Which folder is open is kept in the address (?tab=folders&folder=3), so Back and a refresh work. None means the grid of all folders.
  const folderParam = Number(search.get("folder"));
  const selected = folders?.find((f) => f.id === folderParam) ?? null;
  const openFolder = (id: number | null) => { setSearch(id === null ? { tab: "folders" } : { tab: "folders", folder: String(id) }); setRenaming(null); setConfirmDelete(false); setAdding(false); setNotice(null); };
  const byId = useMemo(() => new Map((mine ?? []).map((d) => [d.id, d])), [mine]);

  async function run(action: () => Promise<unknown>, after?: () => void) {
    setNotice(null);
    try { await action(); await load(); after?.(); } catch (e) { setNotice(message(e)); }
  }

  async function createFolder(e: FormEvent) {
    e.preventDefault();
    setNotice(null);
    try {
      const f = await api<Folder>("/api/folders", { method: "POST", body: JSON.stringify({ name: newName }) });
      setNewName("");
      await load();
      openFolder(f.id);
    } catch (err) { setNotice(message(err)); }
  }

  if (error) return <p className="notice notice-error">{error}</p>;
  if (!folders || !mine) return <p className="muted">Loading your folders…</p>;

  const inFolder = selected ? selected.dashboardIds.map((id) => byId.get(id)).filter((d): d is Mine => !!d) : [];
  const needle = q.trim().toLowerCase();
  const candidates = mine.filter((d) => !selected?.dashboardIds.includes(d.id) && (!needle || [d.name, d.description, d.categoryPath].some((v) => v?.toLowerCase().includes(needle))));

  return (
    <>
      <div className={embedded ? "" : "home-head"}>
        {!embedded && <h1>Personal Folders</h1>}
        <p className="muted home-count">Your own folders for the dashboards you open often. Only you can see them, and a dashboard you lose access to leaves them automatically.</p>
      </div>
      {notice && <p className="notice notice-error">{notice}</p>}

      <div className="folders-layout">
        <aside className="folders-side" aria-label="Your folders">
          <form className="folders-new" onSubmit={createFolder}>
            <input value={newName} onChange={(e) => setNewName(e.target.value)} maxLength={60} placeholder="New folder name" aria-label="New folder name" />
            <button className="btn btn-primary" type="submit" disabled={!newName.trim() || folders.length >= MAX_FOLDERS}><Icon name="plus" size={16} /> Add Folder</button>
          </form>
          {folders.length >= MAX_FOLDERS && <p className="muted small">You have reached {MAX_FOLDERS} folders.</p>}
          {folders.length === 0 ? <p className="muted small">No folders yet. Name one above to start.</p> : (
            <ul className="folders-list">
              <li>
                <button type="button" className={`folders-item ${!selected ? "folders-item-on" : ""}`} aria-current={!selected} onClick={() => openFolder(null)}>
                  <Icon name="dashboards" size={18} /><span className="folders-name">All Folders</span><span className="folders-count">{folders.length}</span>
                </button>
              </li>
              {folders.map((f) => (
                <li key={f.id}>
                  <button type="button" className={`folders-item ${selected?.id === f.id ? "folders-item-on" : ""}`} aria-current={selected?.id === f.id}
                    onClick={() => openFolder(f.id)}>
                    <Icon name="folder" size={18} /><span className="folders-name">{f.name}</span><span className="folders-count">{f.dashboardIds.length}</span>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </aside>

        <section className="folders-main">
          {!selected ? (
            folders.length === 0
              ? <div className="state"><h2>No Folders Yet</h2><p className="muted">Name a folder on the left to create it, then add dashboards to it.</p></div>
              : (
                <>
                  <Crumbs trail={[{ label: "All Folders" }]} />
                  <FolderGrid tiles={folders.map((f) => ({ key: String(f.id), name: f.name, sub: plural(f.dashboardIds.filter((id) => byId.has(id)).length, "dashboard"), onOpen: () => openFolder(f.id) }))} />
                </>
              )
          ) : (
            <>
              <Crumbs trail={[{ label: "All Folders", onClick: () => openFolder(null) }, { label: selected.name }]} />
              <div className="folders-head">
                {renaming === null ? <h2>{selected.name}</h2> : (
                  <form className="folders-new" onSubmit={(e) => { e.preventDefault(); void run(() => api(`/api/folders/${selected.id}`, { method: "PUT", body: JSON.stringify({ name: renaming }) }), () => setRenaming(null)); }}>
                    <input autoFocus value={renaming} onChange={(e) => setRenaming(e.target.value)} maxLength={60} aria-label="Folder name" />
                    <button className="btn btn-primary" type="submit" disabled={!renaming.trim()}>Save Name</button>
                    <button className="btn" type="button" onClick={() => setRenaming(null)}>Cancel</button>
                  </form>
                )}
                {renaming === null && (
                  <div className="folders-actions">
                    <button className="btn btn-primary" type="button" onClick={() => { setAdding(!adding); setQ(""); }}><Icon name="plus" size={16} /> Add Dashboards</button>
                    <button className="btn" type="button" onClick={() => { setRenaming(selected.name); setConfirmDelete(false); }}><Icon name="edit" size={16} /> Rename</button>
                    {confirmDelete ? (
                      <>
                        <button className="btn btn-danger" type="button" onClick={() => void run(() => api(`/api/folders/${selected.id}`, { method: "DELETE" }), () => openFolder(null))}>Delete Folder</button>
                        <button className="btn" type="button" onClick={() => setConfirmDelete(false)}>Keep It</button>
                      </>
                    ) : <button className="btn" type="button" onClick={() => setConfirmDelete(true)}><Icon name="trash" size={16} /> Delete</button>}
                  </div>
                )}
              </div>
              {confirmDelete && <p className="muted small">Deleting the folder does not remove any dashboard, only the folder.</p>}

              {adding && (
                <div className="folders-add">
                  <label className="home-search">
                    <Icon name="search" size={20} />
                    <input type="search" value={q} onChange={(e) => setQ(e.target.value)} placeholder="Search your dashboards" aria-label="Search your dashboards" />
                  </label>
                  {candidates.length === 0 ? <p className="muted small">{needle ? "No dashboards match." : "Every dashboard you can open is already in this folder."}</p> : (
                    <ul className="folders-pick">
                      {candidates.map((d) => (
                        <li key={d.id}>
                          <span><strong>{d.name}</strong>{d.categoryPath && <span className="muted small"> · {d.categoryPath}</span>}</span>
                          <button className="btn" type="button" onClick={() => void run(() => api(`/api/folders/${selected.id}/dashboards/${d.id}`, { method: "POST" }))}>Add</button>
                        </li>
                      ))}
                    </ul>
                  )}
                </div>
              )}

              {inFolder.length === 0 ? (
                <div className="state"><h2>This Folder Is Empty</h2><p className="muted">Use Add Dashboards, or the folder button on any dashboard card on My Dashboards.</p></div>
              ) : (
                <ul className="cards">
                  {inFolder.map((d) => (
                    <li key={d.id} className="card">
                      <Link to={`/dashboards/${d.id}`} className="card-link">
                        <div className="card-thumb"><Thumbnail dashboardId={d.id} version={d.thumbnail} type={d.type} /></div>
                        <div className="card-body">
                          {d.categoryPath && <p className="card-cat">{d.categoryPath}</p>}
                          <h3>{d.name}</h3>
                          <p className="card-meta"><span>{typeLabel[d.type]}</span></p>
                        </div>
                      </Link>
                      <button type="button" className="card-pin" onClick={() => void run(() => api(`/api/folders/${selected.id}/dashboards/${d.id}`, { method: "DELETE" }))}
                        aria-label={`Remove ${d.name} from ${selected.name}`} title="Remove from this folder"><Icon name="close" size={18} /></button>
                    </li>
                  ))}
                </ul>
              )}
            </>
          )}
        </section>
      </div>
    </>
  );
}
