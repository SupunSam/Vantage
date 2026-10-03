import { useEffect, useRef, useState, type FormEvent } from "react";
import { Link } from "react-router-dom";
import { api, can, useSession } from "@vantage/shared";
import { errorText, Notice, useApi } from "../ui";
import { CategoryPicker, TagInput, ThumbnailPicker } from "../fields";
import type { CategoryNode } from "./CategoriesPage";

type Options = {
  tenants: { id: number; name: string; verified: boolean; lastVerifiedAtUtc: string | null; workspaces: { id: number; name: string; workspaceId: string; servicePrincipalAccess: string | null; onDedicatedCapacity: boolean | null }[] }[];
  categories: CategoryNode[];
  users: { id: number; email: string; displayName: string | null }[];
  limits: { nameMax: number; codeMax: number; descriptionMax: number; pbixMaxMb: number; tagsMax: number; tagMax: number };
};
type PublishStatus = { dashboardId: number; name: string; status: string; error: string | null; reportId: string | null; defaultGroup: string | null; warning: string | null };

/**
 * Publish a Power BI dashboard: upload the .pbix, the service principal imports it into the chosen workspace,
 * and the dashboard goes live with its default group (the owners). Only verified tenants can be chosen.
 */
export function PublishPage() {
  const { me } = useSession();
  const { data: options, error } = useApi<Options>("/api/publishing/options");

  const verifiedTenants = options?.tenants.filter((t) => t.verified) ?? [];
  const [tenantId, setTenantId] = useState(0);
  const [workspaceId, setWorkspaceId] = useState(0);
  const [file, setFile] = useState<File | null>(null);
  const [name, setName] = useState("");
  const [code, setCode] = useState("");
  const [description, setDescription] = useState("");
  const [categoryId, setCategoryId] = useState<number | null>(null);
  const [tags, setTags] = useState<string[]>([]);
  const [audience, setAudience] = useState("Internal");
  const [classification, setClassification] = useState("Internal");
  const [thumbnail, setThumbnail] = useState<File | null>(null);
  const [primaryOwnerId, setPrimaryOwnerId] = useState("");
  const [backupOwnerId, setBackupOwnerId] = useState("");
  const [rls, setRls] = useState(false);
  const [rlsValue, setRlsValue] = useState("");
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState<PublishStatus | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const poll = useRef<number | undefined>(undefined);

  useEffect(() => () => window.clearTimeout(poll.current), []);
  useEffect(() => {
    if (!options || tenantId) return;
    const t = options.tenants.find((x) => x.verified);
    if (t) { setTenantId(t.id); setWorkspaceId(t.workspaces[0]?.id ?? 0); }
    if (me) setPrimaryOwnerId(String(me.id));
  }, [options, tenantId, me]);

  if (error) return <Notice tone="error">{error}</Notice>;
  if (!options || !me) return <p className="muted">Loading…</p>;

  if (options.categories.length === 0) {
    return (
      <>
        <div className="page-head"><div><h1>Publish a Power BI Dashboard</h1></div></div>
        <div className="empty">
          <h2>Add a Category First</h2>
          <p>Every dashboard is filed under at least a primary category, so publishing needs one to exist.</p>
          {can(me, "categories", "Edit") ? <Link className="btn btn-primary" to="/categories">Go to Categories</Link> : <p className="small">Ask a Super Admin to add categories.</p>}
        </div>
      </>
    );
  }

  if (verifiedTenants.length === 0) {
    return (
      <>
        <div className="page-head"><div><h1>Publish a Power BI Dashboard</h1></div></div>
        <div className="empty">
          <h2>No Verified Tenant Yet</h2>
          <p>Publishing needs a Power BI tenant whose connection and permissions have been verified.</p>
          <Link className="btn btn-primary" to="/tenants">Go to Tenants</Link>
        </div>
      </>
    );
  }

  const tenant = verifiedTenants.find((t) => t.id === tenantId);

  async function watch(id: number) {
    try {
      const s = await api<PublishStatus>(`/api/publishing/${id}/status`);
      setStatus(s);
      if (s.status === "Publishing") poll.current = window.setTimeout(() => void watch(id), 3000);
      else setBusy(false);
    } catch (e) {
      setBusy(false);
      setMessage(errorText(e));
    }
  }

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (!file) return;
    setBusy(true);
    setMessage(null);
    setStatus(null);
    const body = new FormData();
    body.append("file", file);
    body.append("name", name);
    body.append("code", code);
    body.append("description", description);
    body.append("workspaceId", String(workspaceId));
    body.append("primaryOwnerId", primaryOwnerId);
    if (backupOwnerId) body.append("backupOwnerId", backupOwnerId);
    if (categoryId) body.append("categoryId", String(categoryId));
    body.append("tags", tags.join(","));
    body.append("audience", audience);
    body.append("dataClassification", classification);
    if (thumbnail) body.append("thumbnail", thumbnail);
    body.append("rlsEnabled", String(rls));
    if (rlsValue.trim()) body.append("defaultGroupRlsValue", rlsValue);
    try {
      const s = await api<PublishStatus>("/api/publishing/powerbi", { method: "POST", body });
      setStatus(s);
      if (s.status === "Publishing") await watch(s.dashboardId);
      else setBusy(false);
    } catch (err) {
      setBusy(false);
      setMessage(errorText(err));
    }
  }

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Publish a Power BI Dashboard</h1>
          <p>Upload the .pbix. The service principal imports it into the workspace you choose, and it goes live for the people in its groups. The owners are put in its default group automatically.</p>
        </div>
      </div>

      <section className="panel">
        <form className="form-sections" onSubmit={submit}>
          <fieldset className="form-section">
            <legend>Report File</legend>
            <div className="cols cols-3">
              <label className="field">
                <span>.pbix file (up to {options.limits.pbixMaxMb} MB)</span>
                <input type="file" accept=".pbix" required onChange={(e) => { const f = e.target.files?.[0] ?? null; setFile(f); if (f && !name) setName(f.name.replace(/\.pbix$/i, "")); }} />
              </label>
              <label className="field">
                <span>Tenant</span>
                <select value={tenantId} onChange={(e) => { const t = Number(e.target.value); setTenantId(t); setWorkspaceId(verifiedTenants.find((x) => x.id === t)?.workspaces[0]?.id ?? 0); }}>
                  {verifiedTenants.map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
                </select>
              </label>
              <label className="field">
                <span>Workspace</span>
                <select value={workspaceId} onChange={(e) => setWorkspaceId(Number(e.target.value))}>
                  {tenant?.workspaces.map((w) => <option key={w.id} value={w.id}>{w.name}{w.servicePrincipalAccess ? ` (${w.servicePrincipalAccess})` : ""}</option>)}
                </select>
              </label>
            </div>
          </fieldset>

          <fieldset className="form-section">
            <legend>About the Dashboard</legend>
            <div className="cols cols-3">
              <label className="field span-2">
                <span>Dashboard name (unique)</span>
                <input required maxLength={options.limits.nameMax} value={name} onChange={(e) => setName(e.target.value)} />
              </label>
              <label className="field">
                <span>Dashboard code (up to {options.limits.codeMax} characters)</span>
                <input required maxLength={options.limits.codeMax} pattern="[A-Za-z0-9_\-]+" title="Letters, digits, hyphens and underscores" value={code} onChange={(e) => setCode(e.target.value.toUpperCase())} />
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
                </select>
              </label>
              <label className="field">
                <span>Data classification</span>
                <select value={classification} onChange={(e) => setClassification(e.target.value)}>
                  {["Public", "Internal", "Confidential", "Restricted"].map((c) => <option key={c}>{c}</option>)}
                </select>
              </label>
            </div>
          </fieldset>

          <fieldset className="form-section">
            <legend>Thumbnail <span className="optional">(optional)</span></legend>
            <ThumbnailPicker file={thumbnail} onChange={setThumbnail} current={<span className="thumb-empty">No image chosen</span>} />
          </fieldset>

          <fieldset className="form-section">
            <legend>Row-Level Security</legend>
            <label className="check">
              <input type="checkbox" checked={rls} onChange={(e) => setRls(e.target.checked)} />
              The report uses row-level security (RLS)
            </label>
            <label className="field rls-field">
              <span>Default group's RLS value {rls ? "(required)" : <span className="optional">(not used unless the report has RLS)</span>}</span>
              <input required={rls} value={rlsValue} onChange={(e) => setRlsValue(e.target.value)} placeholder="Role name as defined in the .pbix, e.g. Region_All" />
              <small className="field-hint">
                {rls
                  ? "The default group holds the owners, so this is usually the role that sees everything. List several roles with commas."
                  : "Without RLS everyone sees the same data, so the dashboard keeps just its default group and this value is ignored. If the report turns out to have RLS roles, the portal switches RLS on after the upload and uses this value."}
              </small>
            </label>
          </fieldset>

          <div className="actions">
            <button className="btn btn-primary" type="submit" disabled={busy || !file || !categoryId}>{busy ? "Publishing…" : "Publish"}</button>
            {!categoryId && <span className="muted small">Choose a category to publish.</span>}
          </div>
        </form>

        {message && <Notice tone="error">{message}</Notice>}
        {status?.status === "Publishing" && <Notice>Importing “{status.name}” into Power BI. This usually takes under a minute…</Notice>}
        {status?.status === "Active" && (
          <Notice tone="ok">
            Published. “{status.name}” is live, and its owners are in the default group <strong>{status.defaultGroup}</strong>.{" "}
            <Link to={`/dashboards/${status.dashboardId}`}>Open it in Dashboards Master</Link>
          </Notice>
        )}
        {status?.status === "Active" && status.warning && <Notice tone="error">{status.warning} <Link to={`/dashboards/${status.dashboardId}`}>Set it on the dashboard page</Link>.</Notice>}
        {status?.status === "Failed" && <Notice tone="error">Publishing failed: {status.error} Fix the file and publish again with the same name; it will reuse this dashboard.</Notice>}
      </section>
    </>
  );
}
