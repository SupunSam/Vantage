import { useState, type FormEvent } from "react";
import { api, can, Icon, useSession } from "@vantage/shared";
import { errorText, Notice, useApi } from "../ui";

export type CategoryNode = { id: number; name: string; parentId: number | null; level: number; sortOrder: number; path: string; dashboardCount: number; childCount: number };

const levelName = ["", "Primary", "Secondary", "Tertiary"];

/** Category Master: the Primary / Secondary / Tertiary tree dashboards are filed under. */
export function CategoriesPage() {
  const { me } = useSession();
  const { data, error, reload } = useApi<CategoryNode[]>("/api/admin/categories");
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);
  const [adding, setAdding] = useState<number | "top" | null>(null);
  const [renaming, setRenaming] = useState<number | null>(null);
  const editable = can(me, "categories", "Edit");

  if (error) return <Notice tone="error">{error}</Notice>;
  if (!data) return <p className="muted">Loading…</p>;

  async function run(action: () => Promise<unknown>, ok?: string) {
    setMessage(null);
    try {
      await action();
      if (ok) setMessage({ ok: true, text: ok });
      setAdding(null);
      setRenaming(null);
      reload();
    } catch (e) {
      setMessage({ ok: false, text: errorText(e) });
    }
  }

  const siblings = (c: CategoryNode) => data.filter((x) => x.parentId === c.parentId);
  const primaries = data.filter((c) => c.level === 1).length;
  const total = data.reduce((n, c) => n + c.dashboardCount, 0);

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Categories</h1>
          <p>Every dashboard sits in one category: a primary category, optionally narrowed to a secondary and a tertiary one. Users browse the home page by these.</p>
        </div>
        {editable && adding !== "top" && (
          <button className="btn btn-primary" type="button" onClick={() => { setAdding("top"); setRenaming(null); }}>
            <Icon name="plus" size={18} /> Add Primary Category
          </button>
        )}
      </div>

      {message && <Notice tone={message.ok ? "ok" : "error"}>{message.text}</Notice>}

      {adding === "top" && (
        <section className="panel">
          <NameForm label="New primary category" submitLabel="Add Category" onCancel={() => setAdding(null)}
            onSubmit={(name) => run(() => api("/api/admin/categories", { method: "POST", body: JSON.stringify({ name }) }), `Added ${name}.`)} />
        </section>
      )}

      {data.length === 0 ? (
        adding !== "top" && (
          <div className="empty">
            <h2>No Categories Yet</h2>
            <p>Start with the primary categories, such as departments or business lines. Publishing needs at least one.</p>
            {editable && <button className="btn btn-primary" type="button" onClick={() => setAdding("top")}>Add Primary Category</button>}
          </div>
        )
      ) : (
        <section className="panel tree-panel">
          <div className="tree-summary muted small">
            {primaries} primary categor{primaries === 1 ? "y" : "ies"}, {data.length} in all, holding {total} dashboard{total === 1 ? "" : "s"}
          </div>
          <ul className="tree">
            {data.map((c) => {
              const sibs = siblings(c);
              const index = sibs.findIndex((s) => s.id === c.id);
              return (
                <li key={c.id} className={`tree-row tree-l${c.level}`}>
                  {renaming === c.id ? (
                    <NameForm label={`Rename ${c.name}`} initial={c.name} submitLabel="Save" onCancel={() => setRenaming(null)}
                      onSubmit={(name) => run(() => api(`/api/admin/categories/${c.id}`, { method: "PUT", body: JSON.stringify({ name }) }), `Renamed to ${name}.`)} />
                  ) : (
                    <div className="tree-line">
                      <span className="tree-name">
                        <Icon name="folder" size={18} />
                        <strong>{c.name}</strong>
                        <span className="tree-level">{levelName[c.level]}</span>
                      </span>
                      <span className="tree-count">{c.dashboardCount === 0 ? <span className="muted">No dashboards</span> : `${c.dashboardCount} dashboard${c.dashboardCount === 1 ? "" : "s"}`}</span>
                      {editable && (
                        <span className="tree-actions">
                          {c.level < 3 && (
                            <button type="button" className="icon-btn icon-btn-sm" title={`Add a ${levelName[c.level + 1]!.toLowerCase()} category under ${c.name}`} aria-label={`Add under ${c.name}`} onClick={() => { setAdding(c.id); setRenaming(null); }}>
                              <Icon name="plus" size={18} />
                            </button>
                          )}
                          <button type="button" className="icon-btn icon-btn-sm" title="Rename" aria-label={`Rename ${c.name}`} onClick={() => { setRenaming(c.id); setAdding(null); }}>
                            <Icon name="edit" size={18} />
                          </button>
                          <button type="button" className="icon-btn icon-btn-sm" title="Move up" aria-label={`Move ${c.name} up`} disabled={index === 0}
                            onClick={() => void run(() => api(`/api/admin/categories/${c.id}/move`, { method: "POST", body: JSON.stringify({ direction: -1 }) }))}>
                            <Icon name="arrowUp" size={18} />
                          </button>
                          <button type="button" className="icon-btn icon-btn-sm" title="Move down" aria-label={`Move ${c.name} down`} disabled={index === sibs.length - 1}
                            onClick={() => void run(() => api(`/api/admin/categories/${c.id}/move`, { method: "POST", body: JSON.stringify({ direction: 1 }) }))}>
                            <Icon name="arrowDown" size={18} />
                          </button>
                          <button type="button" className="icon-btn icon-btn-sm icon-btn-danger" aria-label={`Delete ${c.name}`}
                            title={c.dashboardCount > 0 || c.childCount > 0 ? "Only empty categories can be deleted" : "Delete"}
                            disabled={c.dashboardCount > 0 || c.childCount > 0}
                            onClick={() => window.confirm(`Delete the category "${c.name}"?`) && void run(() => api(`/api/admin/categories/${c.id}`, { method: "DELETE" }), `Deleted ${c.name}.`)}>
                            <Icon name="trash" size={18} />
                          </button>
                        </span>
                      )}
                    </div>
                  )}
                  {adding === c.id && (
                    <div className="tree-add">
                      <NameForm label={`New ${levelName[c.level + 1]!.toLowerCase()} category under ${c.name}`} submitLabel="Add Category" onCancel={() => setAdding(null)}
                        onSubmit={(name) => run(() => api("/api/admin/categories", { method: "POST", body: JSON.stringify({ name, parentId: c.id }) }), `Added ${name} under ${c.name}.`)} />
                    </div>
                  )}
                </li>
              );
            })}
          </ul>
        </section>
      )}
    </>
  );
}

function NameForm({ label, initial = "", submitLabel, onSubmit, onCancel }: { label: string; initial?: string; submitLabel: string; onSubmit: (name: string) => Promise<void>; onCancel: () => void }) {
  const [name, setName] = useState(initial);
  const [busy, setBusy] = useState(false);
  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    await onSubmit(name.trim());
    setBusy(false);
  }
  return (
    <form className="inline-form" onSubmit={submit}>
      <label className="field">
        <span>{label}</span>
        <input autoFocus required maxLength={100} value={name} onChange={(e) => setName(e.target.value)} onKeyDown={(e) => e.key === "Escape" && onCancel()} />
      </label>
      <button className="btn btn-primary" type="submit" disabled={busy || !name.trim()}>{submitLabel}</button>
      <button className="btn btn-quiet" type="button" onClick={onCancel}>Cancel</button>
    </form>
  );
}
