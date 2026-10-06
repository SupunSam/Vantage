import { useCallback, useEffect, useState } from "react";
import { api, Icon, useFlash } from "@vantage/shared";
import { errorText, when } from "@vantage/shared";

type DraftRow = { id: number; type: string; title: string; payload: string; updatedAtUtc: string };

/** The code a dashboard of this name will get (C60). Read-only in the forms; the server makes the final one when publishing. */
export function useGeneratedCode(name: string) {
  const [code, setCode] = useState("");
  useEffect(() => {
    const n = name.trim();
    if (!n) { setCode(""); return; }
    let cancelled = false;
    const timer = window.setTimeout(() => {
      api<{ code: string }>(`/api/publishing/code?name=${encodeURIComponent(n)}`).then((r) => !cancelled && setCode(r.code)).catch(() => !cancelled && setCode(""));
    }, 300);
    return () => { cancelled = true; window.clearTimeout(timer); };
  }, [name]);
  return code;
}

/**
 * Saved drafts of the publish form (C60). A draft keeps the details only; files and thumbnails are chosen again at publish time.
 * `snapshot` returns the values to keep, `restore` puts them back.
 */
export function useDraft(type: "PowerBi" | "Tableau" | "GenAi", title: string, snapshot: () => object, restore: (values: Record<string, unknown>) => void) {
  const [drafts, setDrafts] = useState<DraftRow[]>([]);
  const [id, setId] = useState<number | null>(null);
  const [saving, setSaving] = useState(false);
  const say = useFlash();

  const load = useCallback(() => {
    api<DraftRow[]>("/api/publishing/drafts").then((rows) => setDrafts(rows.filter((r) => r.type === type))).catch(() => {});
  }, [type]);
  useEffect(load, [load]);

  async function save() {
    setSaving(true);
    try {
      const row = await api<DraftRow>("/api/publishing/drafts", { method: "POST", body: JSON.stringify({ id, type, name: title, payload: JSON.stringify(snapshot()) }) });
      setId(row.id);
      say({ ok: true, text: `Draft saved. Choose the file again when you publish.` });
      load();
    } catch (e) { say({ ok: false, text: errorText(e) }); } finally { setSaving(false); }
  }

  function resume(draftId: number) {
    const row = drafts.find((d) => d.id === draftId);
    if (!row) return;
    try { restore(JSON.parse(row.payload) as Record<string, unknown>); setId(row.id); } catch { say({ ok: false, text: "That draft couldn't be opened." }); }
  }

  async function remove(draftId: number) {
    try {
      await api(`/api/publishing/drafts/${draftId}`, { method: "DELETE" });
      if (id === draftId) setId(null);
      load();
    } catch (e) { say({ ok: false, text: errorText(e) }); }
  }

  /** Called after a successful publish so the finished draft doesn't linger. */
  const finish = useCallback(() => { if (id !== null) void remove(id); }, [id]); // eslint-disable-line react-hooks/exhaustive-deps

  return { drafts, id, saving, save, resume, remove, finish };
}

export type DraftState = ReturnType<typeof useDraft>;

/** Resume or delete a saved draft. Shown above the publish form; hidden when there are none. */
export function DraftPicker({ draft }: { draft: DraftState }) {
  if (draft.drafts.length === 0) return null;
  return (
    <div className="draft-picker">
      <label className="field">
        <span>Resume a draft</span>
        <select value={draft.id ?? ""} onChange={(e) => e.target.value && draft.resume(Number(e.target.value))}>
          <option value="">Start a new one</option>
          {draft.drafts.map((d) => <option key={d.id} value={d.id}>{d.title}, saved {when(d.updatedAtUtc)}</option>)}
        </select>
      </label>
      {draft.id !== null && (
        <button type="button" className="icon-btn icon-btn-sm icon-btn-danger" title="Delete this draft" aria-label="Delete this draft"
          onClick={() => window.confirm("Delete this draft?") && void draft.remove(draft.id!)}><Icon name="trash" size={18} /></button>
      )}
    </div>
  );
}

export function SaveDraftButton({ draft }: { draft: DraftState }) {
  return <button className="btn" type="button" disabled={draft.saving} onClick={() => void draft.save()}>{draft.saving ? "Saving…" : draft.id !== null ? "Update Draft" : "Save Draft"}</button>;
}
