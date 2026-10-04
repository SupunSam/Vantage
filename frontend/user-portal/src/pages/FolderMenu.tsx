import { useEffect, useRef, useState, type FormEvent } from "react";
import { api, ApiError, Icon } from "@vantage/shared";
import type { Folder } from "./FoldersPage";

/** The folder button on a dashboard card: tick the folders the dashboard should be in, or start a new one. */
export function FolderMenu({ dashboardId, name }: { dashboardId: number; name: string }) {
  const [open, setOpen] = useState(false);
  const [folders, setFolders] = useState<Folder[] | null>(null);
  const [newName, setNewName] = useState("");
  const [error, setError] = useState<string | null>(null);
  const ref = useRef<HTMLDivElement>(null);

  const fail = (e: unknown) => setError(e instanceof ApiError ? e.message : String(e));
  const load = () => api<Folder[]>("/api/folders").then(setFolders).catch(fail);

  useEffect(() => {
    if (!open) return;
    setError(null);
    void load();
    const down = (e: MouseEvent) => !ref.current?.contains(e.target as Node) && setOpen(false);
    const key = (e: KeyboardEvent) => e.key === "Escape" && setOpen(false);
    document.addEventListener("mousedown", down);
    document.addEventListener("keydown", key);
    return () => { document.removeEventListener("mousedown", down); document.removeEventListener("keydown", key); };
  }, [open]);

  async function toggle(f: Folder) {
    setError(null);
    const has = f.dashboardIds.includes(dashboardId);
    try { await api(`/api/folders/${f.id}/dashboards/${dashboardId}`, { method: has ? "DELETE" : "POST" }); await load(); } catch (e) { fail(e); }
  }

  async function create(e: FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      const f = await api<Folder>("/api/folders", { method: "POST", body: JSON.stringify({ name: newName }) });
      await api(`/api/folders/${f.id}/dashboards/${dashboardId}`, { method: "POST" });
      setNewName("");
      await load();
    } catch (err) { fail(err); }
  }

  return (
    <div className="card-folder-wrap" ref={ref}>
      <button type="button" className="card-pin card-folder" aria-haspopup="dialog" aria-expanded={open} aria-label={`Add ${name} to a folder`} title="Add to a folder" onClick={() => setOpen(!open)}>
        <Icon name="folder" size={18} />
      </button>
      {open && (
        <div className="pop-panel folder-pop" role="dialog" aria-label={`Folders for ${name}`}>
          <p className="folder-pop-title">Add to Folder</p>
          {!folders ? <p className="muted small folder-pop-pad">Loading…</p> : folders.length === 0 ? <p className="muted small folder-pop-pad">No folders yet.</p> : (
            <ul className="folder-pop-list">
              {folders.map((f) => (
                <li key={f.id}>
                  <label><input type="checkbox" checked={f.dashboardIds.includes(dashboardId)} onChange={() => void toggle(f)} /> <span>{f.name}</span></label>
                </li>
              ))}
            </ul>
          )}
          <form className="folder-pop-new" onSubmit={create}>
            <input value={newName} onChange={(e) => setNewName(e.target.value)} maxLength={60} placeholder="New folder" aria-label="New folder name" />
            <button className="btn" type="submit" disabled={!newName.trim()}>Add</button>
          </form>
          {error && <p className="folder-pop-error">{error}</p>}
        </div>
      )}
    </div>
  );
}
