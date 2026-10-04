import { useEffect, useRef, useState, type FormEvent } from "react";
import { Link, useParams, useSearchParams } from "react-router-dom";
import * as pbi from "powerbi-client";
import { api, ApiError, apiObjectUrl, GenAiFrame, Icon, TableauViz, Thumbnail } from "@vantage/shared";
import { errorText, Modal, Notice, StatusPill, useApi, when } from "../ui";
import { CategoryPicker, TagInput, ThumbnailPicker } from "../fields";
import type { CategoryNode } from "./CategoriesPage";

type Group = { id: number; name: string; rlsValue: string | null; isDefault: boolean; status: string; members: { email: string; displayName: string | null; source: string }[] };
type Detail = {
  dashboard: {
    id: number; code: string; name: string; description: string | null; type: string; status: string; rlsEnabled: boolean; lastError: string | null;
    categoryId: number | null; primaryOwnerId: number | null; backupOwnerId: number | null;
    tenant: string | null; workspace: string | null; owner: string | null; backupOwner: string | null;
    tags: string[]; audience: string; dataClassification: string; sharePointFolder: string | null; sharePointSubFolder: string | null;
    tenantId: number | null; tableauViewUrl: string | null;
    powerBiReportId: string | null; powerBiDatasetId: string | null; publishedAtUtc: string | null; createdAtUtc: string; updatedAtUtc: string | null;
    groups: Group[];
    versions: Version[];
    replaceVersionNumber: number | null;
  };
  categoryPath: string | null;
  thumbnail: string | null;
  canPreview: boolean;
  canEdit: boolean;
  canEditGroups: boolean;
  canModify: boolean;
  isSuperAdmin: boolean;
  versionsKept: number;
  noRlsMessage: string;
};
type Version = { versionNumber: number; fileName: string; sizeBytes: number; isCurrent: boolean; uploadedAtUtc: string; note: string | null; uploadedBy: string | null };
type ReplaceStatus = { dashboardId: number; state: "Idle" | "Replacing" | "Succeeded" | "Failed"; versionNumber: number | null; message: string | null };
type TableauTenant = { id: number; name: string; serverUrl: string | null; siteContentUrl: string | null };
type FormOptions = { tableauTenants?: TableauTenant[]; users: { id: number; email: string; displayName: string | null }[]; categories: CategoryNode[]; limits: { nameMax: number; descriptionMax: number; tagsMax: number; tagMax: number } };
type EmbedInfo = { type: string; name?: string; embedUrl: string | null; token: string | null; expiresAt: string | null; reportId: string | null; tableauScriptUrl?: string | null };

const powerbiService = new pbi.service.Service(pbi.factories.hpmFactory, pbi.factories.wpmpFactory, pbi.factories.routerFactory);
const tabs = [
  { key: "details", label: "Details" },
  { key: "groups", label: "Access Groups" },
  { key: "preview", label: "Preview" },
  { key: "versions", label: "File Versions" },
] as const;
type Tab = (typeof tabs)[number]["key"];
const typeLabel: Record<string, string> = { PowerBi: "Power BI", Tableau: "Tableau", GenAi: "GenAI" };

/** One dashboard in Dashboards Master: details (editable), thumbnail, groups with their RLS values, preview and file versions. */
export function DashboardDetailPage() {
  const { id } = useParams();
  const [search, setSearch] = useSearchParams();
  const tab = (tabs.some((t) => t.key === search.get("tab")) ? search.get("tab") : "details") as Tab;
  const { data, error, reload } = useApi<Detail>(`/api/admin/dashboards/${id}`);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);
  const [modifying, setModifying] = useState(false);
  useEffect(() => setMessage(null), [id]);

  // While a new .pbix imports over the live report, check every few seconds until Power BI finishes.
  const replacing = data?.dashboard.replaceVersionNumber ?? null;
  useEffect(() => {
    if (replacing == null) return;
    let stop = false;
    let timer: number | undefined;
    const tick = async () => {
      try {
        const r = await api<ReplaceStatus>(`/api/admin/dashboards/${id}/modify-status`);
        if (stop) return;
        if (r.state === "Replacing") { timer = window.setTimeout(() => void tick(), 3000); return; }
        if (r.state === "Succeeded") setMessage({ ok: true, text: `Updated. ${r.message ?? ""} Details, access groups and RLS values are unchanged.` });
        reload();
      } catch {
        if (!stop) timer = window.setTimeout(() => void tick(), 5000);
      }
    };
    timer = window.setTimeout(() => void tick(), 2000);
    return () => { stop = true; window.clearTimeout(timer); };
  }, [replacing, id, reload]);

  if (error) return <Notice tone="error">{error}</Notice>;
  if (!data) return <p className="muted">Loading…</p>;
  const d = data.dashboard;
  const say = (ok: boolean, text: string) => setMessage({ ok, text });
  const needsRls = d.rlsEnabled && d.groups.some((g) => g.status === "Active" && !g.rlsValue);

  return (
    <>
      <div className="detail-hero">
        <div className="detail-thumb"><Thumbnail dashboardId={d.id} version={data.thumbnail} type={d.type} /></div>
        <div className="detail-title grow">
          <p className="crumb"><Link to="/dashboards">Dashboards Master</Link></p>
          <h1>{d.name}</h1>
          <p className="muted">{data.categoryPath ?? "No category yet"}</p>
          <div className="detail-badges">
            <StatusPill status={d.status} />
            <span className="pill pill-neutral">{typeLabel[d.type] ?? d.type}</span>
            <span className={`pill ${d.rlsEnabled ? "pill-brand" : "pill-neutral"}`}>{d.rlsEnabled ? "RLS on" : "No RLS"}</span>
            {needsRls && <span className="pill pill-warn">A group needs an RLS value</span>}
          </div>
        </div>
        {(d.type === "PowerBi" || (d.type === "GenAi" && data.isSuperAdmin)) && data.canModify && ["Active", "Inactive"].includes(d.status) && (
          <button className="btn btn-primary" type="button" disabled={replacing != null} onClick={() => setModifying(true)}>
            <Icon name="upload" size={18} /> Modify Dashboard
          </button>
        )}
      </div>

      {replacing != null && (
        <Notice>Importing version {replacing} into Power BI. The current version stays live until it finishes; this page updates by itself.</Notice>
      )}
      {modifying && (
        <ModifyDialog dashboard={d} onClose={() => setModifying(false)}
          onDone={(r) => {
            setModifying(false);
            // A GenAI file goes live at once; a Power BI file is imported in the background and the page keeps checking.
            setMessage({ ok: true, text: r.state === "Succeeded" ? `${r.message ?? "Updated."} Details, access groups and owners are unchanged.` : `Version ${r.versionNumber ?? ""} uploaded. Power BI is importing it now.` });
            reload();
          }} />
      )}

      <div className="tabs" role="tablist">
        {tabs.map((t) => (
          <button key={t.key} type="button" role="tab" aria-selected={tab === t.key} className={`tab ${tab === t.key ? "tab-on" : ""}`}
            onClick={() => { setMessage(null); setSearch(t.key === "details" ? {} : { tab: t.key }, { replace: true }); }}>
            {t.label}{t.key === "groups" && <span className="tab-count">{d.groups.length}</span>}
          </button>
        ))}
      </div>

      {message && <Notice tone={message.ok ? "ok" : "error"}>{message.text}</Notice>}
      {d.lastError && <Notice tone="error">{d.lastError}</Notice>}

      {tab === "details" && <DetailsTab data={data} onSaved={(t) => { say(true, t); reload(); }} onError={(t) => say(false, t)} />}
      {tab === "groups" && <GroupsTab data={data} onChanged={(t) => { say(true, t); reload(); }} onError={(t) => say(false, t)} />}
      {tab === "preview" && <PreviewTab data={data} />}
      {tab === "versions" && (
        <VersionsTab data={data}
          onRestored={(r) => { setMessage({ ok: true, text: r.state === "Succeeded" ? (r.message ?? "Restored.") : `Restoring: version ${r.versionNumber} is being imported into Power BI.` }); reload(); }}
          onError={(t) => say(false, t)} />
      )}
    </>
  );
}

// ---------------------------------------------------------------- Modify Dashboard and file versions

function ModifyDialog({ dashboard, onClose, onDone }: { dashboard: Detail["dashboard"]; onClose: () => void; onDone: (r: ReplaceStatus) => void }) {
  const genAi = dashboard.type === "GenAi";
  const [file, setFile] = useState<File | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function upload(e: FormEvent) {
    e.preventDefault();
    if (!file) return;
    setBusy(true);
    setError(null);
    const body = new FormData();
    body.append("file", file);
    try {
      const r = await api<ReplaceStatus>(`/api/admin/dashboards/${dashboard.id}/modify`, { method: "POST", body });
      onDone(r);
    } catch (err) {
      setError(errorText(err));
      setBusy(false);
    }
  }

  return (
    <Modal title={`Modify ${dashboard.name}`} onClose={onClose}>
      <form className="stack" onSubmit={upload}>
        {genAi ? (
          <>
            <p>Upload a new .html file to replace this dashboard's page. It must still be built from the approved template.</p>
            <ul className="plain-list small">
              <li>The file is checked first. If it fails, nothing changes and the checks tell you what to fix.</li>
              <li>A file that passes goes live straight away. Name, category, owners, thumbnail, access groups and pins stay as they are.</li>
              <li>The last three files are kept, so you can go back to an earlier one under File Versions.</li>
            </ul>
          </>
        ) : (
          <>
            <p>Upload a new .pbix to replace this report in its Power BI workspace ({dashboard.workspace ?? "–"}).</p>
            <ul className="plain-list small">
              <li>Everything in the portal stays as it is: name, category, owners, thumbnail, access groups, RLS values and pins.</li>
              <li>The current version stays live until Power BI finishes importing the new one. If the import fails, nothing changes.</li>
              <li>The new file's RLS roles are read again afterwards. Keep the role names the groups use, or update the groups' RLS values.</li>
              <li>The last three files are kept, so you can go back to an earlier one under File Versions.</li>
            </ul>
          </>
        )}
        <label className="field">
          <span>{genAi ? "New .html file" : "New .pbix file"}</span>
          <input type="file" accept={genAi ? ".html,.htm" : ".pbix"} required onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
        </label>
        {error && <Notice tone="error">{error}</Notice>}
        <div className="actions">
          <button className="btn btn-primary" type="submit" disabled={busy || !file}>{busy ? (genAi ? "Checking…" : "Uploading…") : "Upload and Replace"}</button>
          <button className="btn btn-quiet" type="button" onClick={onClose}>Cancel</button>
        </div>
      </form>
    </Modal>
  );
}

function VersionsTab({ data, onRestored, onError }: { data: Detail; onRestored: (r: ReplaceStatus) => void; onError: (t: string) => void }) {
  const d = data.dashboard;
  const [downloading, setDownloading] = useState<number | null>(null);
  const busy = d.replaceVersionNumber != null;

  async function download(v: Version) {
    setDownloading(v.versionNumber);
    try {
      const url = await apiObjectUrl(`/api/admin/dashboards/${d.id}/versions/${v.versionNumber}/download`);
      const a = document.createElement("a");
      a.href = url;
      a.download = v.fileName;
      document.body.appendChild(a);
      a.click();
      a.remove();
      window.setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (e) {
      onError(errorText(e));
    } finally {
      setDownloading(null);
    }
  }

  async function restore(v: Version) {
    const what = d.type === "GenAi" ? "That file goes live again" : "Power BI re-imports that file over the live report";
    if (!window.confirm(`Restore version ${v.versionNumber} (${v.fileName})? ${what}. It becomes the newest version; the current file stays available as a version.`)) return;
    try {
      const r = await api<ReplaceStatus>(`/api/admin/dashboards/${d.id}/versions/${v.versionNumber}/restore`, { method: "POST" });
      onRestored(r);
    } catch (e) {
      onError(errorText(e));
    }
  }

  return (
    <section className="panel">
      <div className="panel-row">
        <div>
          <h2>File Versions</h2>
          <p className="muted small">The last {data.versionsKept} uploaded files are kept. {data.isSuperAdmin ? "Download any of them, or restore one to make it live again." : "Super Admins can download or restore them."}</p>
        </div>
      </div>
      <table className="grid">
        <thead><tr><th>Version</th><th>File</th><th className="num">Size</th><th>Uploaded</th><th>State</th>{data.isSuperAdmin && <th />}</tr></thead>
        <tbody>
          {d.versions.map((v) => {
            const pending = v.versionNumber === d.replaceVersionNumber;
            return (
              <tr key={v.versionNumber}>
                <td>v{v.versionNumber}</td>
                <td>{v.fileName}{v.note && <div className="muted small">{v.note}</div>}</td>
                <td className="num">{size(v.sizeBytes)}</td>
                <td className="small"><span className="muted">{when(v.uploadedAtUtc)}</span>{v.uploadedBy && <div>{v.uploadedBy}</div>}</td>
                <td>{pending ? <span className="pill pill-warn">Importing</span> : v.isCurrent ? <span className="pill pill-ok">Live</span> : <span className="muted small">Previous</span>}</td>
                {data.isSuperAdmin && (
                  <td className="cell-actions">
                    <span className="member-actions">
                      <button className="btn btn-sm" type="button" disabled={downloading === v.versionNumber} onClick={() => void download(v)}>
                        <Icon name="arrowDown" size={16} /> {downloading === v.versionNumber ? "Downloading…" : "Download"}
                      </button>
                      {!v.isCurrent && !pending && (d.type === "PowerBi" || d.type === "GenAi") && (
                        <button className="btn btn-sm" type="button" disabled={busy} onClick={() => void restore(v)}><Icon name="refresh" size={16} /> Restore</button>
                      )}
                    </span>
                  </td>
                )}
              </tr>
            );
          })}
        </tbody>
      </table>
      {d.versions.length === 0 && <p className="pop-empty">No files uploaded.</p>}
    </section>
  );
}

// ---------------------------------------------------------------- Details

function DetailsTab({ data, onSaved, onError }: { data: Detail; onSaved: (t: string) => void; onError: (t: string) => void }) {
  const d = data.dashboard;
  const [editing, setEditing] = useState(false);
  const [checking, setChecking] = useState(false);

  async function checkRls() {
    setChecking(true);
    try {
      const r = await api<{ rlsEnabled: boolean; changed: boolean }>(`/api/admin/dashboards/${d.id}/check-rls`, { method: "POST" });
      onSaved(r.changed
        ? `Power BI says this report ${r.rlsEnabled ? "now has" : "no longer has"} RLS roles, so RLS is now ${r.rlsEnabled ? "on" : "off"}.`
        : `Checked with Power BI: RLS is ${r.rlsEnabled ? "on" : "off"}, as recorded.`);
    } catch (e) {
      onError(errorText(e));
    } finally {
      setChecking(false);
    }
  }

  if (editing) return <DetailsForm data={data} onCancel={() => setEditing(false)} onSaved={(t) => { setEditing(false); onSaved(t); }} onError={onError} />;

  return (
    <div className="detail-grid">
      <section className="panel">
        <div className="panel-row">
          <h2>Details</h2>
          {data.canEdit && d.status !== "Retired" && <button className="btn" type="button" onClick={() => setEditing(true)}><Icon name="edit" size={18} /> Edit Details</button>}
        </div>
        <dl className="facts facts-wide">
          <dt>Name</dt><dd>{d.name}</dd>
          <dt>Code and ID</dt><dd>{d.code}, #{d.id} <span className="muted small">(the code can't change: the default group's name is built from it)</span></dd>
          <dt>Description</dt><dd>{d.description ?? <span className="muted">None</span>}</dd>
          <dt>Category</dt><dd>{data.categoryPath ?? <span className="warn-text">No category yet</span>}</dd>
          <dt>Owners</dt><dd>{d.owner ?? "–"}{d.backupOwner ? <span className="muted">, backup {d.backupOwner}</span> : <span className="muted">, no backup owner</span>}</dd>
          <dt>Tags</dt><dd>{d.tags.length ? <span className="tag-list">{d.tags.map((t) => <span key={t} className="tag">{t}</span>)}</span> : <span className="muted">None</span>}</dd>
          <dt>Audience</dt><dd>{d.audience}</dd>
          <dt>Data classification</dt><dd>{d.dataClassification}</dd>
          <dt>SharePoint folder</dt>
          <dd>
            {d.sharePointFolder ? <SpLink value={d.sharePointFolder} /> : <span className="muted">None</span>}
            {d.sharePointSubFolder && <> / <SpLink value={d.sharePointSubFolder} /></>}
            <div className="muted small">Where the dashboard's documentation and source files are kept.</div>
          </dd>
          <dt>Last changed</dt><dd className="muted">{when(d.updatedAtUtc ?? d.createdAtUtc)}</dd>
        </dl>
      </section>

      <div className="stack">
        <ThumbnailPanel data={data} onSaved={onSaved} onError={onError} />
        {d.type === "GenAi" && (
          <section className="panel">
            <div className="panel-row"><h2>GenAI Page</h2></div>
            <dl className="facts">
              <dt>Live file</dt><dd>{(() => { const v = d.versions.find((x) => x.isCurrent); return v ? `${v.fileName} (v${v.versionNumber}, ${size(v.sizeBytes)})` : "–"; })()}</dd>
              <dt>How it runs</dt><dd>On its own web address, in a sandbox, with scripts only from the approved CDNs and no network calls. It can't reach the portal.</dd>
              <dt>Published</dt><dd>{when(d.publishedAtUtc)}</dd>
            </dl>
          </section>
        )}
        {d.type === "Tableau" && <TableauPanel data={data} onSaved={onSaved} onError={onError} />}
        {d.type === "PowerBi" && (
          <section className="panel">
            <div className="panel-row">
              <h2>Power BI</h2>
              {data.canEdit && d.powerBiReportId && (
                <button className="btn" type="button" disabled={checking} onClick={() => void checkRls()}>
                  <Icon name="refresh" size={18} /> {checking ? "Checking…" : "Check RLS with Power BI"}
                </button>
              )}
            </div>
            <dl className="facts">
              <dt>Workspace</dt><dd>{d.tenant ?? "–"}{d.workspace ? `, ${d.workspace}` : ""}</dd>
              <dt>Row-level security</dt><dd>{d.rlsEnabled ? "On: each group's RLS value is sent as the role" : "Off: RLS values are ignored"}</dd>
              <dt>Report ID</dt><dd className="small mono-ish">{d.powerBiReportId ?? "–"}</dd>
              <dt>Published</dt><dd>{when(d.publishedAtUtc)}</dd>
            </dl>
          </section>
        )}
      </div>
    </div>
  );
}

function ThumbnailPanel({ data, onSaved, onError }: { data: Detail; onSaved: (t: string) => void; onError: (t: string) => void }) {
  const d = data.dashboard;
  const [file, setFile] = useState<File | null>(null);
  const [busy, setBusy] = useState(false);

  async function upload() {
    if (!file) return;
    setBusy(true);
    const body = new FormData();
    body.append("file", file);
    try {
      await api(`/api/admin/dashboards/${d.id}/thumbnail`, { method: "POST", body });
      setFile(null);
      onSaved("Thumbnail updated. Users see it on their home page.");
    } catch (e) {
      onError(errorText(e));
    } finally {
      setBusy(false);
    }
  }

  async function remove() {
    if (!window.confirm("Remove the thumbnail? The type drawing is shown instead.")) return;
    try {
      await api(`/api/admin/dashboards/${d.id}/thumbnail`, { method: "DELETE" });
      onSaved("Thumbnail removed.");
    } catch (e) {
      onError(errorText(e));
    }
  }

  return (
    <section className="panel">
      <div className="panel-row">
        <h2>Thumbnail</h2>
        {data.canEdit && data.thumbnail && !file && <button className="btn btn-quiet" type="button" onClick={() => void remove()}>Remove</button>}
      </div>
      {data.canEdit ? (
        <>
          <ThumbnailPicker stacked file={file} onChange={setFile} current={<Thumbnail dashboardId={d.id} version={data.thumbnail} type={d.type} />} />
          {file && <div className="actions" style={{ marginTop: 12 }}><button className="btn btn-primary" type="button" disabled={busy} onClick={() => void upload()}>{busy ? "Uploading…" : "Save Thumbnail"}</button></div>}
        </>
      ) : (
        <div className="thumb-frame"><Thumbnail dashboardId={d.id} version={data.thumbnail} type={d.type} /></div>
      )}
    </section>
  );
}

function DetailsForm({ data, onCancel, onSaved, onError }: { data: Detail; onCancel: () => void; onSaved: (t: string) => void; onError: (t: string) => void }) {
  const d = data.dashboard;
  const { data: options, error } = useApi<FormOptions>("/api/admin/dashboards/form-options");
  const [name, setName] = useState(d.name);
  const [description, setDescription] = useState(d.description ?? "");
  const [categoryId, setCategoryId] = useState<number | null>(d.categoryId);
  const [primaryOwnerId, setPrimaryOwnerId] = useState(String(d.primaryOwnerId ?? ""));
  const [backupOwnerId, setBackupOwnerId] = useState(String(d.backupOwnerId ?? ""));
  const [tags, setTags] = useState(d.tags);
  const [audience, setAudience] = useState(d.audience);
  const [classification, setClassification] = useState(d.dataClassification);
  const [folder, setFolder] = useState(d.sharePointFolder ?? "");
  const [subFolder, setSubFolder] = useState(d.sharePointSubFolder ?? "");
  const [busy, setBusy] = useState(false);

  if (error) return <Notice tone="error">{error}</Notice>;
  if (!options) return <p className="muted">Loading…</p>;

  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    try {
      await api(`/api/admin/dashboards/${d.id}`, {
        method: "PUT",
        body: JSON.stringify({
          name, description: description || null, categoryId, primaryOwnerId: Number(primaryOwnerId), backupOwnerId: backupOwnerId ? Number(backupOwnerId) : null,
          tags, audience, dataClassification: classification, sharePointFolder: folder || null, sharePointSubFolder: subFolder || null,
        }),
      });
      onSaved("Details saved.");
    } catch (err) {
      onError(errorText(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="panel">
      <form className="form-sections" onSubmit={save}>
        <fieldset className="form-section">
          <legend>About the Dashboard</legend>
          <div className="cols cols-3">
            <label className="field span-3"><span>Dashboard name (unique)</span><input required maxLength={options.limits.nameMax} value={name} onChange={(e) => setName(e.target.value)} /></label>
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
              <select required value={primaryOwnerId} onChange={(e) => setPrimaryOwnerId(e.target.value)}>
                <option value="">Choose…</option>
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
              <select value={audience} onChange={(e) => setAudience(e.target.value)}><option>Internal</option><option>Client</option></select>
            </label>
            <label className="field">
              <span>Data classification</span>
              <select value={classification} onChange={(e) => setClassification(e.target.value)}>
                {["Public", "Internal", "Confidential", "Restricted"].map((c) => <option key={c}>{c}</option>)}
              </select>
            </label>
          </div>
          <p className="muted small">A new owner is added to the default group automatically and gets the Dashboard Owner role. A previous owner keeps their group membership until someone removes it.</p>
        </fieldset>
        <fieldset className="form-section">
          <legend>SharePoint Documentation</legend>
          <p className="muted small">Where this dashboard's documentation and source files are kept, for the Dashboard Directory. Paste the folder link or path.</p>
          <div className="cols cols-3">
            <label className="field"><span>Folder <span className="optional">(optional)</span></span><input maxLength={400} value={folder} onChange={(e) => setFolder(e.target.value)} /></label>
            <label className="field"><span>Sub-folder <span className="optional">(optional)</span></span><input maxLength={400} value={subFolder} onChange={(e) => setSubFolder(e.target.value)} /></label>
          </div>
        </fieldset>
        <div className="actions">
          <button className="btn btn-primary" type="submit" disabled={busy || !categoryId}>{busy ? "Saving…" : "Save Details"}</button>
          <button className="btn btn-quiet" type="button" onClick={onCancel}>Cancel</button>
        </div>
      </form>
    </section>
  );
}

// ---------------------------------------------------------------- Groups and RLS

function GroupsTab({ data, onChanged, onError }: { data: Detail; onChanged: (t: string) => void; onError: (t: string) => void }) {
  const d = data.dashboard;
  const [adding, setAdding] = useState(false);

  return (
    <section className="panel">
      <div className="panel-row">
        <div>
          <h2>Access Groups</h2>
          <p className="muted small">
            Being in a group is the only way to use a dashboard in the User Portal. Super Admins administer rather than consume, so they check dashboards from the Preview tab without joining any group. Each person is in one group per dashboard.
            {d.rlsEnabled
              ? " This report uses row-level security: each group's RLS value is the Power BI role its members see, exactly as named in the .pbix (several roles with commas)."
              : " This report has no row-level security, so everyone sees the same data and the dashboard needs only its default group. RLS values are kept but not used."}
          </p>
        </div>
        {data.canEditGroups && !adding && (
          <button className="btn" type="button" onClick={() => (d.rlsEnabled ? setAdding(true) : onError(data.noRlsMessage))}>
            <Icon name="plus" size={18} /> Add Group
          </button>
        )}
      </div>

      {adding && <AddGroupForm dashboardId={d.id} onCancel={() => setAdding(false)} onAdded={(t) => { setAdding(false); onChanged(t); }} />}

      <table className="grid">
        <thead><tr><th>Group</th><th>RLS value</th><th>Members</th></tr></thead>
        <tbody>
          {d.groups.map((g) => (
            <tr key={g.id}>
              <td>
                <Link to={`/access-groups/${g.id}`} className="group-name">{g.name}</Link>
                {g.isDefault && <div className="muted small">Default group, holds the owners</div>}
                {g.status !== "Active" && <StatusPill status={g.status} />}
              </td>
              <td>
                <RlsCell dashboardId={d.id} group={g} rlsEnabled={d.rlsEnabled} canEdit={data.canEditGroups}
                  onSaved={onChanged} />
              </td>
              <td className="small">
                {g.members.length === 0 ? <span className="muted">No members</span> : (
                  <span className="member-list">
                    {g.members.slice(0, 6).map((m) => <span key={m.email} className="member" title={m.email}>{m.displayName ?? m.email}</span>)}
                    {g.members.length > 6 && <span className="muted">and {g.members.length - 6} more</span>}
                  </span>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <p className="muted small" style={{ marginTop: 12 }}>Open a group to add, move or remove its members.</p>
    </section>
  );
}

function RlsCell({ dashboardId, group, rlsEnabled, canEdit, onSaved }: { dashboardId: number; group: Group; rlsEnabled: boolean; canEdit: boolean; onSaved: (t: string) => void }) {
  const [editing, setEditing] = useState(false);
  const [text, setText] = useState(group.rlsValue ?? "");
  const [error, setError] = useState<string | null>(null);

  if (!editing) {
    return (
      <span className="rls-cell">
        {group.rlsValue
          ? <code className={rlsEnabled ? "" : "rls-ignored"} title={rlsEnabled ? undefined : "Not used: this report has no RLS"}>{group.rlsValue}</code>
          : rlsEnabled ? <span className="warn-text">None: members can't open the dashboard</span> : <span className="muted">None</span>}
        {!rlsEnabled && group.rlsValue && <span className="muted small">not used</span>}
        {canEdit && <button type="button" className="link" onClick={() => { setText(group.rlsValue ?? ""); setError(null); setEditing(true); }}>Change</button>}
      </span>
    );
  }

  async function save(e: FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      const r = await api<{ name: string; rlsValue: string | null }>(`/api/admin/dashboards/${dashboardId}/groups/${group.id}`, { method: "PUT", body: JSON.stringify({ rlsValue: text }) });
      setEditing(false);
      onSaved(r.rlsValue ? `${r.name} now uses RLS value ${r.rlsValue}.${rlsEnabled ? "" : " It isn't used while the report has no RLS."}` : `${r.name} has no RLS value now.`);
    } catch (err) {
      setError(errorText(err));
    }
  }
  return (
    <form className="rls-edit" onSubmit={save}>
      <input autoFocus value={text} onChange={(e) => setText(e.target.value)} placeholder="Role name from the report" aria-label="RLS value" />
      <button className="btn" type="submit">Save</button>
      <button className="btn btn-quiet" type="button" onClick={() => setEditing(false)}>Cancel</button>
      {error && <span className="small warn-text">{error}</span>}
    </form>
  );
}

function AddGroupForm({ dashboardId, onCancel, onAdded }: { dashboardId: number; onCancel: () => void; onAdded: (t: string) => void }) {
  const [name, setName] = useState("");
  const [rls, setRls] = useState("");
  const [error, setError] = useState<string | null>(null);
  async function add(e: FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      await api(`/api/admin/dashboards/${dashboardId}/groups`, { method: "POST", body: JSON.stringify({ name, rlsValue: rls || null }) });
      onAdded(`Added group ${name}.`);
    } catch (err) {
      setError(errorText(err));
    }
  }
  return (
    <form className="form-grid inset" onSubmit={add}>
      <label className="field"><span>Group name (unique, up to 40)</span><input autoFocus required maxLength={40} pattern="[A-Za-z0-9_\-]+" title="Letters, digits, hyphens and underscores" value={name} onChange={(e) => setName(e.target.value)} /></label>
      <label className="field"><span>RLS value (role name in the .pbix)</span><input required value={rls} onChange={(e) => setRls(e.target.value)} placeholder="e.g. Region_North" /></label>
      <div className="actions"><button className="btn btn-primary" type="submit">Add Group</button><button className="btn btn-quiet" type="button" onClick={onCancel}>Cancel</button></div>
      {error && <div className="span-2"><Notice tone="error">{error}</Notice></div>}
    </form>
  );
}

// ---------------------------------------------------------------- Preview

function PreviewTab({ data }: { data: Detail }) {
  const d = data.dashboard;
  // Groups a preview can go through: with RLS on, only those that have an RLS value (the token needs one).
  const usable = d.groups.filter((g) => g.status !== "Retired" && (!d.rlsEnabled || g.rlsValue));
  const [groupId, setGroupId] = useState<number | null>(usable.find((g) => g.isDefault)?.id ?? usable[0]?.id ?? null);
  const [show, setShow] = useState(false);
  const group = usable.find((g) => g.id === groupId);

  if (d.status !== "Active") return <div className="empty"><h2>Preview Is Available Once the Dashboard Is Active</h2><p>It's {d.status.toLowerCase()} right now.</p></div>;
  if (!data.canPreview) return <div className="empty"><h2>Previews Are for Super Admins</h2><p>Open the dashboards you have access to in the User Portal.</p></div>;
  if (usable.length === 0) return <div className="empty"><h2>No Group to Preview Through</h2><p>This report uses row-level security and none of its groups has an RLS value yet. Set one on the Access Groups tab.</p></div>;

  return (
    <section className="panel">
      <div className="panel-row">
        <div>
          <h2>Preview</h2>
          <p className="muted small">
            Check that the dashboard works, without being in any access group: it opens as a member of the group you choose would see it.
            {d.rlsEnabled ? " The group's RLS role decides the data; roles that filter on the signed-in email use yours." : ""}
            {" "}A preview isn't counted as usage, and each one is recorded in the audit log.
          </p>
        </div>
      </div>
      <div className="filters">
        <label className="field">
          <span>Preview as a member of</span>
          <select value={groupId ?? ""} onChange={(e) => { setGroupId(Number(e.target.value)); setShow(false); }}>
            {usable.map((g) => <option key={g.id} value={g.id}>{g.name}{d.rlsEnabled && g.rlsValue ? ` (RLS ${g.rlsValue})` : ""}{g.status !== "Active" ? ` – ${g.status.toLowerCase()}` : ""}</option>)}
          </select>
        </label>
        {!show && <button className="btn btn-primary" type="button" onClick={() => setShow(true)}>Open Preview</button>}
      </div>
      {show && group && <Preview key={group.id} dashboardId={d.id} groupId={group.id} />}
    </section>
  );
}

function Preview({ dashboardId, groupId }: { dashboardId: number; groupId: number }) {
  const host = useRef<HTMLDivElement>(null);
  const [error, setError] = useState<string | null>(null);
  const [state, setState] = useState("Getting an embed token…");
  const [genAi, setGenAi] = useState<EmbedInfo | null>(null);
  const [tableau, setTableau] = useState<EmbedInfo | null>(null);

  useEffect(() => {
    let cancelled = false;
    const el = host.current!;
    api<EmbedInfo>(`/api/admin/dashboards/${dashboardId}/preview-embed?groupId=${groupId}`)
      .then((info) => {
        if (cancelled) return;
        if (info.type === "genai" && info.embedUrl) {
          setGenAi(info);
          setState(`Loaded. The link is valid until ${when(info.expiresAt)}.`);
          return;
        }
        if (info.type === "tableau" && info.embedUrl && info.tableauScriptUrl) {
          setTableau(info);
          setState(info.token ? `Loading the view. Token valid until ${when(info.expiresAt)}.` : "Loading the Tableau Public view…");
          return;
        }
        if (!info.token || !info.embedUrl) return;
        setState("Loading the report…");
        const report = powerbiService.embed(el, {
          type: "report", id: info.reportId ?? undefined, embedUrl: info.embedUrl, accessToken: info.token,
          tokenType: pbi.models.TokenType.Embed, settings: { panes: { filters: { visible: false } } },
        }) as pbi.Report;
        report.on("loaded", () => setState(`Loaded. Token valid until ${when(info.expiresAt)}.`));
        report.on("error", (ev) => {
          const detail = ev.detail as { message?: string; detailedMessage?: string } | undefined;
          setError(detail?.detailedMessage ?? detail?.message ?? "Power BI could not load the report.");
        });
      })
      .catch((e) => setError(e instanceof ApiError ? (e.body?.message as string) ?? e.message : String(e)));
    return () => {
      cancelled = true;
      powerbiService.reset(el);
    };
  }, [dashboardId, groupId]);

  return (
    <>
      {error ? <Notice tone="error">{error}</Notice> : <p className="muted small">{state}</p>}
      {genAi?.embedUrl ? <div className="embed-host"><GenAiFrame url={genAi.embedUrl} title="Preview" /></div>
        : tableau?.embedUrl && tableau.tableauScriptUrl
          ? <div className="embed-host"><TableauViz src={tableau.embedUrl} token={tableau.token} scriptUrl={tableau.tableauScriptUrl}
              onError={(m) => setError(`Tableau couldn't open the view: ${m} On Tableau Server this usually means your own Tableau user name (on your profile in Users) is missing or wrong.`)} /></div>
          : <div ref={host} className="embed-host" />}
    </>
  );
}

/** The Tableau view behind a Tableau dashboard: Tableau Public or a Tableau Server tenant, with the view's address. Changing it keeps the groups and access as they are. */
function TableauPanel({ data, onSaved, onError }: { data: Detail; onSaved: (t: string) => void; onError: (t: string) => void }) {
  const d = data.dashboard;
  const [editing, setEditing] = useState(false);
  const [tenantId, setTenantId] = useState(d.tenantId ? String(d.tenantId) : "");
  const [url, setUrl] = useState(d.tableauViewUrl ?? "");
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const options = useApi<FormOptions>(editing ? "/api/admin/dashboards/form-options" : null);
  const tenants = options.data?.tableauTenants ?? [];

  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setProblem(null);
    try {
      await api(`/api/admin/dashboards/${d.id}/tableau-view`, { method: "PUT", body: JSON.stringify({ tenantId: tenantId ? Number(tenantId) : null, viewUrl: url }) });
      setEditing(false);
      onSaved("Saved the Tableau view.");
    } catch (err) {
      setProblem(errorText(err));
      onError(errorText(err));
    } finally { setBusy(false); }
  }

  return (
    <section className="panel">
      <div className="panel-row">
        <h2>Tableau View</h2>
        {data.canEdit && d.status !== "Retired" && !editing && <button className="btn" type="button" onClick={() => setEditing(true)}><Icon name="edit" size={18} /> Edit View</button>}
      </div>
      {!editing ? (
        <dl className="facts">
          <dt>Runs on</dt><dd>{d.tenant ? <>Tableau Server: {d.tenant}</> : "Tableau Public"}</dd>
          <dt>View</dt><dd>{d.tableauViewUrl ? <a href={d.tableauViewUrl} target="_blank" rel="noreferrer">{d.tableauViewUrl}</a> : <span className="warn-text">No view address yet</span>}</dd>
          <dt>Who sees what</dt>
          <dd className="small">
            {d.tenant
              ? "Each person opens it as their own Tableau user (the Tableau user name on their profile), so Tableau's own permissions and row-level security apply. The portal's groups decide who can open it here."
              : "Tableau Public views are open to anyone who has the address. The portal's groups only decide who is shown it here, so only public data belongs on it."}
          </dd>
        </dl>
      ) : (
        <form className="stack" onSubmit={save}>
          <label className="field">
            <span>Runs on</span>
            <select value={tenantId} onChange={(e) => setTenantId(e.target.value)}>
              <option value="">Tableau Public</option>
              {tenants.map((t) => <option key={t.id} value={t.id}>Tableau Server: {t.name}{t.siteContentUrl ? ` (site ${t.siteContentUrl})` : ""}</option>)}
            </select>
          </label>
          <label className="field">
            <span>View address</span>
            <input required value={url} onChange={(e) => setUrl(e.target.value)} placeholder="https://public.tableau.com/views/WorkbookName/ViewName" />
          </label>
          {problem && <Notice tone="error">{problem}</Notice>}
          <div className="actions">
            <button className="btn btn-primary" type="submit" disabled={busy || !options.data}>{busy ? "Saving…" : "Save View"}</button>
            <button className="btn btn-quiet" type="button" onClick={() => { setEditing(false); setProblem(null); }}>Cancel</button>
          </div>
        </form>
      )}
    </section>
  );
}

/** Shows a SharePoint folder as a link when it's a web address, otherwise as text. */
function SpLink({ value }: { value: string }) {
  return /^https?:\/\//i.test(value) ? <a href={value} target="_blank" rel="noreferrer">{value}</a> : <span>{value}</span>;
}

function size(bytes: number) {
  return bytes >= 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`;
}
