import { useEffect, useState, type FormEvent } from "react";
import { Link } from "react-router-dom";
import { api, apiObjectUrl, can, Icon, useSession, useFlash } from "@vantage/shared";
import { DraftPicker, SaveDraftButton, useDraft, useGeneratedCode } from "../drafts";
import { errorText, Notice, useApi } from "@vantage/shared";
import { CategoryPicker, TagInput, ThumbnailPicker } from "@vantage/shared";
import type { CategoryNode } from "@vantage/shared";

type Options = {
  categories: CategoryNode[];
  users: { id: number; email: string; displayName: string | null }[];
  limits: { nameMax: number; codeMax: number; descriptionMax: number; tagsMax: number; tagMax: number };
  genAi: { maxBytes: number; warnBytes: number; versionsKept: number; approvedHosts: string[] };
};
type Check = { passed: boolean; errors: string[]; warnings: string[]; libraries: string[]; sizeBytes: number };
type PublishStatus = { dashboardId: number; name: string; status: string; defaultGroup: string | null; warning: string | null };

const mb = (bytes: number) => `${(bytes / 1024 / 1024).toFixed(bytes >= 1024 * 1024 ? 1 : 2)} MB`;

/**
 * Publish a GenAI dashboard: one HTML file made from the approved starter template. The file is checked first
 * (template marker, size, libraries only from approved CDNs). A file that passes is live at once for the owners' default group.
 */
export function PublishGenAiPage() {
  const { me } = useSession();
  const { data: options, error } = useApi<Options>("/api/publishing/options");

  const [file, setFile] = useState<File | null>(null);
  const [check, setCheck] = useState<Check | null>(null);
  const [checking, setChecking] = useState(false);
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
  const draft = useDraft("GenAi", name, () => ({ name, description, categoryId, tags, audience, classification, primaryOwnerId, backupOwnerId }), (v) => {
    setName(String(v.name ?? "")); setDescription(String(v.description ?? "")); setCategoryId((v.categoryId as number | null) ?? null);
    setTags((v.tags as string[]) ?? []); setAudience(String(v.audience ?? "Internal")); setClassification(String(v.classification ?? "Internal"));
    setPrimaryOwnerId(String(v.primaryOwnerId ?? "")); setBackupOwnerId(String(v.backupOwnerId ?? ""));
  });
  useEffect(() => { if (status?.status === "Active") draft.finish(); }, [status?.status]); // eslint-disable-line react-hooks/exhaustive-deps
  const setMessage = useFlash();

  useEffect(() => {
    if (me && !primaryOwnerId) setPrimaryOwnerId(String(me.id));
  }, [me, primaryOwnerId]);

  if (error) return <Notice tone="error">{error}</Notice>;
  if (!options || !me) return <p className="muted">Loading…</p>;

  if (options.categories.length === 0) {
    return (
      <div className="empty">
        <h2>Add a Category First</h2>
        <p>Every dashboard is filed under at least a primary category, so publishing needs one to exist.</p>
        {can(me, "categories", "Edit") ? <Link className="btn btn-primary" to="/categories">Go to Categories</Link> : <p className="small">Ask a Super Admin to add categories.</p>}
      </div>
    );
  }

  async function downloadTemplate() {
    try {
      const url = await apiObjectUrl("/api/publishing/genai/template");
      const a = document.createElement("a");
      a.href = url;
      a.download = "vantage-genai-starter-template.html";
      document.body.appendChild(a);
      a.click();
      a.remove();
      window.setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (e) {
      setMessage(errorText(e));
    }
  }

  async function runCheck(f: File) {
    setChecking(true);
    setCheck(null);
    setMessage(null);
    const body = new FormData();
    body.append("file", f);
    try {
      setCheck(await api<Check>("/api/publishing/genai/check", { method: "POST", body }));
    } catch (e) {
      setMessage(errorText(e));
    } finally {
      setChecking(false);
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
    body.append("primaryOwnerId", primaryOwnerId);
    if (backupOwnerId) body.append("backupOwnerId", backupOwnerId);
    if (categoryId) body.append("categoryId", String(categoryId));
    body.append("tags", tags.join(","));
    body.append("audience", audience);
    body.append("dataClassification", classification);
    if (thumbnail) body.append("thumbnail", thumbnail);
    try {
      setStatus(await api<PublishStatus>("/api/publishing/genai", { method: "POST", body }));
    } catch (err) {
      setMessage(errorText(err));
    } finally {
      setBusy(false);
    }
  }

  const g = options.genAi;

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Publish a GenAI Dashboard</h1>
          <p>Upload the .html file the author handed over, built from the approved starter template. Vet it here first: the portal checks the file, scans it for malware where a scanner is set up, and refuses anything that fails. A file that passes goes live for the people in its groups. The owners are put in its default group automatically.</p>
        </div>
      </div>

      <section className="panel">
        <DraftPicker draft={draft} />
        <form className="form-sections" onSubmit={submit}>
          <fieldset className="form-section">
            <legend>Dashboard File</legend>
            <div className="cols cols-3">
              <label className="field span-2">
                <span>.html file (up to {mb(g.maxBytes)}; a warning appears over {mb(g.warnBytes)})</span>
                <input type="file" accept=".html,.htm" required onChange={(e) => {
                  const f = e.target.files?.[0] ?? null;
                  setFile(f);
                  setCheck(null);
                  if (f) { if (!name) setName(f.name.replace(/\.html?$/i, "")); void runCheck(f); }
                }} />
              </label>
              <div className="field">
                <span>Don't have a file yet?</span>
                <button className="btn" type="button" onClick={() => void downloadTemplate()}><Icon name="arrowDown" size={18} /> Download Starter Template</button>
              </div>
            </div>
            <ul className="plain-list small">
              <li>One self-contained page: the HTML, CSS, JavaScript and data all live in the file. It must keep the template marker from the starter template.</li>
              <li>Libraries may load only from the approved CDNs, over HTTPS: {g.approvedHosts.length > 0 ? <strong>{g.approvedHosts.join(", ")}</strong> : "none are approved yet (add them in Admin Configuration)"}. Each script and stylesheet needs an exact version in its address and an integrity hash.</li>
              <li>Code that runs text as code, changes the page's address, opens windows, reaches the parent window or makes network calls is refused. Links to other sites are refused too.</li>
              <li>The page runs in a sandbox on its own web address. It can't make network calls or reach the portal, so put its data in the file.</li>
              <li>The last {g.versionsKept} files are kept, so you can go back to an earlier one.</li>
            </ul>
            {checking && <p className="muted small">Checking the file…</p>}
            {check && (
              <>
                {check.passed
                  ? <Notice tone="ok">The file passes the file checks ({mb(check.sizeBytes)}{check.libraries.length > 0 ? `; libraries: ${check.libraries.join(", ")}` : "; no external libraries"}).</Notice>
                  : <Notice tone="error"><strong>The file can't be published yet.</strong><ul className="plain-list">{check.errors.map((x) => <li key={x}>{x}</li>)}</ul></Notice>}
                {check.warnings.length > 0 && <Notice><ul className="plain-list">{check.warnings.map((x) => <li key={x}>{x}</li>)}</ul></Notice>}
              </>
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
                <span>Dashboard code <span className="optional">(made from the name)</span></span>
                <input readOnly value={code} placeholder="Type the name first" aria-label="Dashboard code, made from the name" />
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

          <div className="actions">
            <button className="btn btn-primary" type="submit" disabled={busy || !file || !categoryId || (check != null && !check.passed)}>{busy ? "Publishing…" : "Publish"}</button>
            <SaveDraftButton draft={draft} />
            {!categoryId && <span className="muted small">Choose a category to publish.</span>}
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
