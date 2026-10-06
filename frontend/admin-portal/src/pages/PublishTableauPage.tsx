import { useEffect, useState, type FormEvent } from "react";
import { Link } from "react-router-dom";
import { api, can, useSession, useFlash, PageSkeleton, ErrorState, EmptyState } from "@vantage/shared";
import { DraftPicker, SaveDraftButton, useDraft, useGeneratedCode } from "../drafts";
import { errorText, Notice, useApi } from "@vantage/shared";
import { CategoryPicker, TagInput, ThumbnailPicker } from "@vantage/shared";
import type { CategoryNode } from "@vantage/shared";

type Options = {
  tableauTenants: { id: number; name: string; serverUrl: string | null; siteContentUrl: string | null; verified: boolean }[];
  categories: CategoryNode[];
  users: { id: number; email: string; displayName: string | null }[];
  limits: { nameMax: number; codeMax: number; descriptionMax: number; tagsMax: number; tagMax: number };
};
type ViewCheck = { isPublic: boolean; src: string; host: string; workbook: string; view: string; site: string | null; warning: string | null };
type PublishStatus = { dashboardId: number; name: string; status: string; defaultGroup: string | null; warning: string | null };

/** Waits for typing to pause before the address is checked. */
function useDebounced<T>(value: T, ms = 400) {
  const [v, setV] = useState(value);
  useEffect(() => {
    const t = setTimeout(() => setV(value), ms);
    return () => clearTimeout(t);
  }, [value, ms]);
  return v;
}

/**
 * Publish a Tableau dashboard: a view on a Tableau Server tenant (each person opens it as their own Tableau user), or a view on
 * Tableau Public (open to anyone with the address, so only for public data). It is live at once, with the owners in the default group.
 */
export function PublishTableauPage({ resumeId }: { resumeId?: number }) {
  const { me } = useSession();
  const { data: options, error } = useApi<Options>("/api/publishing/options");

  const [tenantId, setTenantId] = useState("");
  const [viewUrl, setViewUrl] = useState("");
  const [check, setCheck] = useState<ViewCheck | null>(null);
  const [checkError, setCheckError] = useState<string | null>(null);
  const [name, setName] = useState("");
  const code = useGeneratedCode(name);
  const [description, setDescription] = useState("");
  const [categoryId, setCategoryId] = useState<number | null>(null);
  const [tags, setTags] = useState<string[]>([]);
  const [audience, setAudience] = useState("Internal");
  const [classification, setClassification] = useState("Internal");
  const [thumbnail, setThumbnail] = useState<File | null>(null);
  const [primaryOwnerId, setPrimaryOwnerId] = useState("");
  const [backupOwnerId, setBackupOwnerId] = useState("");
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState<PublishStatus | null>(null);
  const draft = useDraft("Tableau", name, () => ({ name, description, categoryId, tags, audience, classification, primaryOwnerId, backupOwnerId, viewUrl, tenantId }), (v) => {
    setName(String(v.name ?? "")); setDescription(String(v.description ?? "")); setCategoryId((v.categoryId as number | null) ?? null);
    setTags((v.tags as string[]) ?? []); setAudience(String(v.audience ?? "Internal")); setClassification(String(v.classification ?? "Internal"));
    setPrimaryOwnerId(String(v.primaryOwnerId ?? "")); setBackupOwnerId(String(v.backupOwnerId ?? ""));
    setViewUrl(String(v.viewUrl ?? "")); setTenantId(String(v.tenantId ?? ""));
  }, resumeId);
  useEffect(() => { if (status?.status === "Active") draft.finish(); }, [status?.status]); // eslint-disable-line react-hooks/exhaustive-deps
  const setMessage = useFlash();

  useEffect(() => {
    if (me && !primaryOwnerId) setPrimaryOwnerId(String(me.id));
  }, [me, primaryOwnerId]);

  // Check the address the way publishing will, as the person types.
  const typed = useDebounced(`${tenantId}|${viewUrl.trim()}`);
  useEffect(() => {
    const [tenant, url] = [typed.split("|")[0], typed.slice(typed.indexOf("|") + 1)];
    if (!url) { setCheck(null); setCheckError(null); return; }
    let cancelled = false;
    api<ViewCheck>("/api/publishing/tableau/check", { method: "POST", body: JSON.stringify({ tenantId: tenant ? Number(tenant) : null, viewUrl: url }) })
      .then((r) => { if (!cancelled) { setCheck(r); setCheckError(null); } })
      .catch((e) => { if (!cancelled) { setCheck(null); setCheckError(errorText(e)); } });
    return () => { cancelled = true; };
  }, [typed]);

  // Tableau Public can only carry public data.
  const isPublic = tenantId === "";
  useEffect(() => { if (isPublic) setClassification("Public"); }, [isPublic]);

  if (error) return <ErrorState>{error}</ErrorState>;
  if (!options || !me) return <PageSkeleton kind="page" />;

  if (options.categories.length === 0) {
    return (
      <EmptyState icon="categories" title="Add a Category First" action={<>{can(me, "categories", "Edit") ? <Link className="btn btn-primary" to="/categories">Go to Categories</Link> : <p className="small">Ask a Super Admin to add categories.</p>}</>}>Every dashboard is filed under at least a primary category, so publishing needs one to exist.</EmptyState>
    );
  }

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessage(null);
    setStatus(null);
    const body = new FormData();
    body.append("name", name);
    body.append("code", code);
    body.append("description", description);
    if (tenantId) body.append("tenantId", tenantId);
    body.append("viewUrl", viewUrl);
    body.append("primaryOwnerId", primaryOwnerId);
    if (backupOwnerId) body.append("backupOwnerId", backupOwnerId);
    if (categoryId) body.append("categoryId", String(categoryId));
    body.append("tags", tags.join(","));
    body.append("audience", audience);
    body.append("dataClassification", classification);
    if (thumbnail) body.append("thumbnail", thumbnail);
    try {
      setStatus(await api<PublishStatus>("/api/publishing/tableau", { method: "POST", body }));
    } catch (err) {
      setMessage(errorText(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Publish a Tableau Dashboard</h1>
          <p>Point the portal at a Tableau view. It goes live for the people in its groups straight away; the owners are put in its default group automatically.</p>
        </div>
      </div>

      <section className="panel">
        <DraftPicker draft={draft} />
        <form className="form-sections" onSubmit={submit}>
          <fieldset className="form-section">
            <legend>Tableau View</legend>
            <div className="cols cols-3">
              <label className="field">
                <span>Runs on</span>
                <select value={tenantId} onChange={(e) => setTenantId(e.target.value)}>
                  <option value="">Tableau Public</option>
                  {options.tableauTenants.map((t) => <option key={t.id} value={t.id}>Tableau Server: {t.name}{t.siteContentUrl ? ` (site ${t.siteContentUrl})` : ""}{t.verified ? "" : " – not verified yet"}</option>)}
                </select>
              </label>
              <label className="field span-2">
                <span>View address</span>
                <input required value={viewUrl} onChange={(e) => setViewUrl(e.target.value)}
                  placeholder={isPublic ? "https://public.tableau.com/views/WorkbookName/ViewName" : "https://your-server/#/views/WorkbookName/ViewName"} />
              </label>
            </div>
            {checkError && <Notice tone="error">{checkError}</Notice>}
            {check && <Notice tone="ok">This is the {check.isPublic ? "Tableau Public" : `Tableau Server (${check.host}${check.site ? `, site ${check.site}` : ""})`} view <strong>{check.workbook} / {check.view}</strong>.</Notice>}
            {check?.warning && <Notice>{check.warning}</Notice>}
            {isPublic ? (
              <ul className="plain-list small">
                <li>Tableau Public needs no server and no sign-in, so it is the quickest way to try a dashboard in the portal.</li>
                <li>Anyone who has the address can open the view on Tableau Public, so only data that is meant to be public belongs there. The data classification is fixed to Public.</li>
                <li>On Tableau Public choose Share and copy the address from the Embed Code. It looks like https://public.tableau.com/views/WorkbookName/ViewName.</li>
              </ul>
            ) : (
              <ul className="plain-list small">
                <li>Each person opens the view as their own Tableau user, using the Tableau user name on their profile in Users. Tableau's own permissions and row-level security decide what they see inside.</li>
                <li>Open the view in Tableau and copy the address from the browser. It must be on this tenant's server and site.</li>
                <li>The portal's groups decide who can open the dashboard here, so it has only its default group.</li>
              </ul>
            )}
          </fieldset>

          <fieldset className="form-section">
            <legend>About the Dashboard</legend>
            <div className="cols cols-3">
              <label className="field span-2">
                <span>Dashboard name (unique)</span>
                <input required maxLength={options.limits.nameMax} value={name} onChange={(e) => setName(e.target.value)} />
              </label>
              <label className="field">
                <span>Dashboard code</span>
                <input readOnly value={code} placeholder="Type the name first" aria-label="Dashboard code, made from the name" />
                <small className="field-hint">Made from the name.</small>
              </label>
              <label className="field span-3">
                <span>Description <span className="optional">({description.length} of {options.limits.descriptionMax})</span></span>
                <textarea rows={2} maxLength={options.limits.descriptionMax} value={description} onChange={(e) => setDescription(e.target.value)} />
              </label>
              <CategoryPicker categories={options.categories} value={categoryId} onChange={setCategoryId} />
              <div className="span-3"><TagInput value={tags} onChange={setTags} max={options.limits.tagsMax} maxLength={options.limits.tagMax} /></div>
            </div>
          </fieldset>

          <fieldset className="form-section">
            <legend>Ownership and Audience</legend>
            <div className="cols cols-4">
              <label className="field">
                <span>Primary owner</span>
                <select value={primaryOwnerId} onChange={(e) => setPrimaryOwnerId(e.target.value)}>
                  {options.users.map((u) => <option key={u.id} value={u.id}>{u.displayName ?? u.email}</option>)}
                </select>
              </label>
              <label className="field">
                <span>Backup owner</span>
                <select value={backupOwnerId} onChange={(e) => setBackupOwnerId(e.target.value)}>
                  <option value="">None</option>
                  {options.users.filter((u) => String(u.id) !== primaryOwnerId).map((u) => <option key={u.id} value={u.id}>{u.displayName ?? u.email}</option>)}
                </select>
              </label>
              <label className="field">
                <span>Audience</span>
                <select value={audience} onChange={(e) => setAudience(e.target.value)}>
                  <option value="Internal">Internal</option>
                  <option value="Client">Client</option>
                  <option value="Both">Both (Internal and Client)</option>
                </select>
              </label>
              <label className="field">
                <span>Data classification</span>
                <select value={classification} disabled={isPublic} onChange={(e) => setClassification(e.target.value)}>
                  {["Public", "Internal", "Confidential", "Restricted"].map((c) => <option key={c}>{c}</option>)}
                </select>
              </label>
            </div>
          </fieldset>

          <fieldset className="form-section">
            <legend>Thumbnail <span className="optional">(optional)</span></legend>
            <ThumbnailPicker file={thumbnail} onChange={setThumbnail} current={<span className="thumb-empty">No image chosen</span>} />
          </fieldset>

          <div className="actions form-actions-sticky">
            <button className="btn btn-primary" type="submit" disabled={busy || !categoryId || !check}>{busy ? "Publishing…" : "Publish"}</button>
            <SaveDraftButton draft={draft} />
            {!categoryId && <span className="muted small">Choose a category to publish.</span>}
            {categoryId && !check && <span className="muted small">Enter a valid view address to publish.</span>}
          </div>
        </form>

        {status && (
          <Notice tone="ok">
            Published. “{status.name}” is live, and its owners are in the default group <strong>{status.defaultGroup}</strong>.{" "}
            <Link to={`/dashboards/${status.dashboardId}`}>Open it in Dashboards Master</Link>
          </Notice>
        )}
        {status?.warning && <Notice>{status.warning}</Notice>}
      </section>
    </>
  );
}
