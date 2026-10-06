import { useState, type FormEvent } from "react";
import { api, can, Icon, useSession, useFlash } from "@vantage/shared";
import { errorText, Modal, Notice, useApi } from "@vantage/shared";

type Level = "None" | "View" | "Edit";
type Role = { id: number; name: string; description: string | null; isSystem: boolean; locked: boolean; users: number; permissions: { module: string; level: Level }[] };
type Module = { key: string; name: string; portal: string };
type Data = { roles: Role[]; modules: Module[] };

export function RolesPage() {
  const { me } = useSession();
  const { data, error, reload } = useApi<Data>("/api/admin/roles");
  const [editing, setEditing] = useState<Role | "new" | null>(null);
  const setMessage = useFlash();
  const canEdit = can(me, "roles", "Edit");

  if (error) return <Notice tone="error">{error}</Notice>;
  if (!data) return <p className="muted">Loading…</p>;

  const levelOf = (r: Role, key: string): Level => r.permissions.find((p) => p.module === key)?.level ?? "None";

  async function remove(r: Role) {
    if (!confirm(`Delete the role "${r.name}"?`)) return;
    try {
      await api(`/api/admin/roles/${r.id}`, { method: "DELETE" });
      setMessage({ ok: true, text: `Deleted "${r.name}".` });
      reload();
    } catch (e) {
      setMessage({ ok: false, text: errorText(e) });
    }
  }

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Roles and Permissions</h1>
          <p>Each role grants View or Edit on modules. A user with several roles gets the highest level from any of them.</p>
        </div>
        {canEdit && <button className="btn btn-primary" type="button" onClick={() => setEditing("new")}><Icon name="plus" size={18} /> Add Role</button>}
      </div>

      <div className="perm-legend small muted">
        <span><span className="perm perm-edit"><Icon name="check" size={16} /></span> Edit: can view and change</span>
        <span><span className="perm perm-view"><Icon name="check" size={16} /></span> View only</span>
        <span><span className="perm perm-none">–</span> No access</span>
      </div>
      <div className="table-wrap">
        <table className="grid perm-grid">
          <thead>
            <tr>
              <th className="perm-module-col">Page / Module</th>
              {data.roles.map((r) => (
                <th key={r.id} className="perm-role">
                  <strong>{r.name}</strong>
                  <span className="muted small">{r.users} user{r.users === 1 ? "" : "s"}{r.isSystem ? ", built-in" : ""}</span>
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {(["Admin", "User"] as const).map((portal) => [
              <tr key={portal} className="perm-group"><td colSpan={data.roles.length + 1}>{portal} Portal</td></tr>,
              ...data.modules.filter((m) => m.portal === portal).map((m) => (
                <tr key={m.key}>
                  <th scope="row" className="perm-module-col">{m.name}</th>
                  {data.roles.map((r) => {
                    const l = levelOf(r, m.key);
                    return (
                      <td key={r.id} className="perm-cell" title={`${r.name}: ${m.name}: ${l === "None" ? "no access" : l === "View" ? "view only" : "view and edit"}`}>
                        {l === "None" ? <span className="perm perm-none" aria-label="No access">–</span>
                          : <span className={`perm perm-${l.toLowerCase()}`} aria-label={l === "Edit" ? "Edit" : "View only"}><Icon name="check" size={16} /></span>}
                      </td>
                    );
                  })}
                </tr>
              )),
            ])}
          </tbody>
          {canEdit && (
            <tfoot>
              <tr>
                <th scope="row" className="perm-module-col muted small">Actions</th>
                {data.roles.map((r) => (
                  <td key={r.id} className="perm-cell">
                    {r.locked ? <span className="muted small" title="Super Admin always has Edit everywhere">Fixed</span> : (
                      <span className="perm-actions">
                        <button type="button" className="btn btn-sm" onClick={() => setEditing(r)}><Icon name="edit" size={16} /> Edit</button>
                        {!r.isSystem && <button type="button" className="icon-btn icon-btn-sm icon-btn-danger" aria-label={`Delete ${r.name}`} title="Delete" onClick={() => void remove(r)}><Icon name="trash" size={16} /></button>}
                      </span>
                    )}
                  </td>
                ))}
              </tr>
            </tfoot>
          )}
        </table>
      </div>
      <p className="muted small" style={{ marginTop: 10 }}>
        Super Admin always has Edit everywhere and can't be changed. Built-in roles can't be renamed or deleted. A role with users can't be deleted.
      </p>

      {editing && (
        <RoleEditor
          role={editing === "new" ? null : editing}
          modules={data.modules}
          onClose={() => setEditing(null)}
          onSaved={(text) => { setEditing(null); setMessage({ ok: true, text }); reload(); }}
        />
      )}
    </>
  );
}

function RoleEditor({ role, modules, onClose, onSaved }: { role: Role | null; modules: Module[]; onClose: () => void; onSaved: (text: string) => void }) {
  const [name, setName] = useState(role?.name ?? "");
  const [description, setDescription] = useState(role?.description ?? "");
  const [perms, setPerms] = useState<Record<string, Level>>(() =>
    Object.fromEntries(modules.map((m) => [m.key, role?.permissions.find((p) => p.module === m.key)?.level ?? "None"])),
  );
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  async function save(e: FormEvent) {
    e.preventDefault();
    setSaving(true);
    setError(null);
    try {
      const body = JSON.stringify({ name, description, permissions: perms });
      if (role) await api(`/api/admin/roles/${role.id}`, { method: "PUT", body });
      else await api("/api/admin/roles", { method: "POST", body });
      onSaved(role ? `Saved "${name}".` : `Added "${name}".`);
    } catch (err) {
      setError(errorText(err));
    } finally {
      setSaving(false);
    }
  }

  return (
    <Modal title={role ? `Edit ${role.name}` : "Add Role"} onClose={onClose} wide>
      <form onSubmit={save} className="stack">
        <div className="form-grid">
          <label className="field">
            <span>Name</span>
            <input required maxLength={60} value={name} disabled={role?.isSystem} onChange={(e) => setName(e.target.value)} />
          </label>
          <label className="field span-2">
            <span>Description</span>
            <input maxLength={500} value={description} onChange={(e) => setDescription(e.target.value)} />
          </label>
        </div>
        <table className="grid perm-editor">
          <thead><tr><th>Module</th><th>Portal</th><th className="perm-cell">Can view</th><th className="perm-cell">Can edit</th></tr></thead>
          <tbody>
            {modules.map((m) => {
              const l = perms[m.key];
              return (
                <tr key={m.key}>
                  <td>{m.name}</td>
                  <td className="muted">{m.portal}</td>
                  <td className="perm-cell">
                    <input type="checkbox" aria-label={`${m.name}: can view`} checked={l !== "None"}
                      onChange={(e) => setPerms({ ...perms, [m.key]: e.target.checked ? (l === "None" ? "View" : l) : "None" })} />
                  </td>
                  <td className="perm-cell">
                    <input type="checkbox" aria-label={`${m.name}: can edit`} checked={l === "Edit"}
                      onChange={(e) => setPerms({ ...perms, [m.key]: e.target.checked ? "Edit" : l === "Edit" ? "View" : l })} />
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
        <p className="muted small">Edit includes view. Unticking "Can view" removes access to the module.</p>
        {error && <Notice tone="error">{error}</Notice>}
        <div className="actions">
          <button className="btn btn-primary" type="submit" disabled={saving}>{saving ? "Saving…" : role ? "Save Role" : "Add Role"}</button>
          <button className="btn" type="button" onClick={onClose}>Cancel</button>
        </div>
      </form>
    </Modal>
  );
}
