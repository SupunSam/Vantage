import { useMemo, useState, type FormEvent } from "react";
import { api, can, Icon, Pager, usePaged, useBiTypes, useGridPageSize, useSession, useFlash, PageSkeleton, ErrorState, EmptyState } from "@vantage/shared";
import { Drawer, errorText, MenuButton, Modal, Notice, Pill, RowMenu, SortHeader, useApi, useSort, when } from "@vantage/shared";

type Check = { name: string; result: "Pass" | "Warn" | "Fail"; detail: string };
type Workspace = { id: number; name: string; workspaceId: string; isActive: boolean; servicePrincipalAccess: string | null; onDedicatedCapacity: boolean | null };
type Tenant = {
  id: number; name: string; platform: "PowerBi" | "Tableau"; isActive: boolean;
  azureTenantId: string | null; clientId: string | null;
  serverUrl: string | null; siteContentUrl: string | null; connectedAppClientId: string | null; connectedAppSecretId: string | null; verifyUserName: string | null; tableauApiVersion: string;
  secretUpdatedAtUtc: string | null; lastVerifiedAtUtc: string | null; lastVerifyPassed: boolean | null; checks: Check[];
  workspaces: Workspace[]; dashboards: number;
};

const platformLabel = (p: Tenant["platform"]) => (p === "PowerBi" ? "Power BI" : "Tableau Server");
const short = (v: string | null) => (v && v.length > 13 ? `${v.slice(0, 8)}…${v.slice(-4)}` : v ?? "–");

function VerifyPill({ t }: { t: Tenant }) {
  if (t.lastVerifyPassed === true) return <Pill tone="ok">Verified</Pill>;
  if (t.lastVerifyPassed === false) return <Pill tone="bad">Failed</Pill>;
  return <Pill tone="warn">Not verified</Pill>;
}

export function TenantsPage() {
  const { me } = useSession();
  const canEdit = can(me, "tenants", "Edit");
  const { isEnabled } = useBiTypes();
  const { data, error, reload } = useApi<Tenant[]>("/api/admin/tenants");
  const [editing, setEditing] = useState<Tenant | "PowerBi" | "Tableau" | null>(null);
  const [openId, setOpenId] = useState<number | null>(null);
  const [verifying, setVerifying] = useState<number | null>(null);
  const [q, setQ] = useState("");
  const [platform, setPlatform] = useState("");
  const setMessage = useFlash();

  const rows = useMemo(() => {
    const needle = q.trim().toLowerCase();
    return (data ?? []).filter((t) => (!platform || t.platform === platform)
      && (!needle || [t.name, t.azureTenantId, t.clientId, t.serverUrl, ...t.workspaces.map((w) => w.name)].some((v) => v?.toLowerCase().includes(needle))));
  }, [data, q, platform]);
  const { sorted, sort, toggle } = useSort(rows, {
    name: (t) => t.name, platform: (t) => t.platform, workspaces: (t) => (t.platform === "PowerBi" ? t.workspaces.length : -1), dashboards: (t) => t.dashboards,
    verified: (t) => (t.lastVerifyPassed === true ? 2 : t.lastVerifyPassed === false ? 0 : 1), last: (t) => t.lastVerifiedAtUtc,
  });
  const { rows: pageRows, pager } = usePaged(sorted, useGridPageSize(), `${q}|${platform}|${sort?.key}|${sort?.desc}`);

  async function verify(t: Tenant) {
    setVerifying(t.id);
    try {
      const r = await api<{ passed: boolean }>(`/api/admin/tenants/${t.id}/verify`, { method: "POST" });
      setMessage({ ok: r.passed, text: r.passed ? `${t.name} is verified. You can publish into its workspaces.` : `${t.name} failed verification. Open the tenant to see which checks to fix.` });
      reload();
    } catch (e) {
      setMessage({ ok: false, text: errorText(e) });
    } finally {
      setVerifying(null);
    }
  }

  if (error) return <ErrorState onRetry={reload}>{error}</ErrorState>;
  if (!data) return <PageSkeleton kind="table" />;

  const opened = data.find((t) => t.id === openId) ?? null;
  // A BI type switched off in Configuration can't get new tenants (C56).
  const addItems = [
    ...(isEnabled("PowerBi") ? [{ label: "Power BI", hint: "A service principal and its workspaces", onSelect: () => setEditing("PowerBi" as const) }] : []),
    ...(isEnabled("Tableau") ? [{ label: "Tableau Server", hint: "A server site and its Connected App", onSelect: () => setEditing("Tableau" as const) }] : []),
  ];

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Tenants</h1>
          <p>Each tenant is one connection a BI platform publishes through: a Power BI service principal with its workspaces, or a Tableau Server site. Verify a tenant before publishing; any change to it needs verifying again.</p>
        </div>
        {canEdit && addItems.length > 0 && <MenuButton primary label={<><Icon name="plus" size={18} /> Add tenant</>} items={addItems} />}
      </div>

      {data.length === 0 ? (
        <EmptyState icon="server" title="No Tenants Yet">Add your Power BI tenant: its Azure tenant ID, the service principal's client ID and secret, and the workspace dashboards will be published into.</EmptyState>
      ) : (
        <>
          <div className="filters">
            <label className="field field-search">
              <span>Search</span>
              <span className="input-icon"><Icon name="search" size={18} /><input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Name, tenant ID, client ID, server or workspace" /></span>
            </label>
            <label className="field">
              <span>Platform</span>
              <select value={platform} onChange={(e) => setPlatform(e.target.value)}>
                <option value="">All</option>
                <option value="PowerBi">Power BI</option>
                <option value="Tableau">Tableau Server</option>
              </select>
            </label>
            <span className="muted small filters-count">{rows.length} of {data.length}</span>
          </div>

          <div className="table-wrap">
            <table className="grid grid-rows">
              <thead>
                <tr><SortHeader label="Tenant" k="name" sort={sort} onSort={toggle} /><SortHeader label="Platform" k="platform" sort={sort} onSort={toggle} /><th>Connection</th><SortHeader label="Workspaces" k="workspaces" sort={sort} onSort={toggle} className="num" /><SortHeader label="Dashboards" k="dashboards" sort={sort} onSort={toggle} className="num" /><SortHeader label="Verification" k="verified" sort={sort} onSort={toggle} /><SortHeader label="Last Verified" k="last" sort={sort} onSort={toggle} />{canEdit && <th><span className="visually-hidden">Actions</span></th>}</tr>
              </thead>
              <tbody>
                {pageRows.map((t) => (
                  <tr key={t.id} onClick={() => setOpenId(t.id)}>
                    <td>
                      <button type="button" className="link" onClick={(e) => { e.stopPropagation(); setOpenId(t.id); }}>{t.name}</button>
                      {!t.isActive && <> <Pill tone="neutral">Inactive</Pill></>}
                    </td>
                    <td className="small">{platformLabel(t.platform)}</td>
                    <td className="small mono-ish" title={t.platform === "PowerBi" ? `Tenant ${t.azureTenantId}, client ${t.clientId}` : t.serverUrl ?? undefined}>
                      {t.platform === "PowerBi" ? short(t.azureTenantId) : (t.serverUrl ?? "–").replace(/^https?:\/\//, "")}
                    </td>
                    <td className="num">{t.platform === "PowerBi" ? t.workspaces.length : "–"}</td>
                    <td className="num">{t.dashboards}</td>
                    <td><VerifyPill t={t} /></td>
                    <td className="small muted">{when(t.lastVerifiedAtUtc)}</td>
                    {canEdit && (
                      <td className="cell-actions">
                        <RowMenu label={`Actions for ${t.name}`} items={[
                          { label: "Open Details", onSelect: () => setOpenId(t.id) },
                          { label: verifying === t.id ? "Verifying…" : "Verify Connection", onSelect: () => void verify(t), disabled: verifying === t.id },
                          { label: "Edit", onSelect: () => setEditing(t) },
                        ]} />
                      </td>
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
            {rows.length === 0 && <p className="pop-empty">No tenants match these filters.</p>}
          </div>
          <Pager {...pager} />
        </>
      )}

      {opened && (
        <Drawer title={opened.name} subtitle={platformLabel(opened.platform)} onClose={() => setOpenId(null)}>
          <div className="stack">
            <p className="muted small">
              {opened.platform === "PowerBi" ? `Power BI. Azure tenant ${opened.azureTenantId}, client ${opened.clientId}.` : `Tableau Server ${opened.serverUrl}${opened.siteContentUrl ? `, site ${opened.siteContentUrl}` : ""}.`}
              {` Secret saved ${when(opened.secretUpdatedAtUtc)}. ${opened.dashboards} dashboard${opened.dashboards === 1 ? "" : "s"}.`}
            </p>
            <div className="actions">
              <VerifyPill t={opened} />
              {opened.lastVerifiedAtUtc && <span className="muted small">{when(opened.lastVerifiedAtUtc)}</span>}
              {canEdit && <button className="btn" type="button" onClick={() => { setOpenId(null); setEditing(opened); }}>Edit</button>}
              {canEdit && <button className="btn btn-primary" type="button" disabled={verifying === opened.id} onClick={() => void verify(opened)}>{verifying === opened.id ? "Verifying…" : "Verify Connection"}</button>}
            </div>

            {opened.platform === "PowerBi" && (
              <div className="table-wrap">
                <table className="grid">
                  <thead><tr><th>Workspace</th><th>Workspace ID</th><th>Service principal access</th><th>Capacity</th></tr></thead>
                  <tbody>
                    {opened.workspaces.map((w) => (
                      <tr key={w.id} className={w.isActive ? "" : "muted"}>
                        <td>{w.name}{!w.isActive && " (inactive)"}</td>
                        <td className="small mono-ish">{w.workspaceId}</td>
                        <td>{w.servicePrincipalAccess ?? <span className="muted">Verify to check</span>}</td>
                        <td>{w.onDedicatedCapacity == null ? <span className="muted">Verify to check</span> : w.onDedicatedCapacity ? "Dedicated (F64)" : <span style={{ color: "var(--danger)" }}>Shared</span>}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}

            {opened.checks.length > 0 && <VerifyChecks checks={opened.checks} />}
          </div>
        </Drawer>
      )}

      {editing && (
        <TenantEditor
          tenant={typeof editing === "string" ? null : editing}
          platform={typeof editing === "string" ? editing : editing.platform}
          onClose={() => setEditing(null)}
          onSaved={(text) => { setEditing(null); setMessage({ ok: true, text }); reload(); }}
          onChanged={reload}
        />
      )}
    </>
  );
}

/** The last verification's checks, folded into one summary line; opens by itself when something failed. */
function VerifyChecks({ checks, startOpen }: { checks: Check[]; startOpen?: boolean }) {
  const fails = checks.filter((c) => c.result === "Fail").length;
  const warns = checks.filter((c) => c.result === "Warn").length;
  const passes = checks.length - fails - warns;
  const [open, setOpen] = useState(startOpen === true || fails > 0);
  const tone = fails ? "fail" : warns ? "warn" : "pass";
  return (
    <div className={`verify verify-${tone}`}>
      <button type="button" className="verify-head" aria-expanded={open} onClick={() => setOpen(!open)}>
        <span className="check-mark" aria-hidden="true">{fails ? "✕" : warns ? "!" : "✓"}</span>
        <span className="verify-summary">
          <strong>Verification Checks</strong>
          <span className="muted small">
            {passes} passed{warns ? `, ${warns} warning${warns === 1 ? "" : "s"}` : ""}{fails ? `, ${fails} failed` : ""}
          </span>
        </span>
        <span className="verify-toggle small">{open ? "Hide Details" : "Show Details"}<Icon name={open ? "chevronDown" : "chevronRight"} size={16} /></span>
      </button>
      {open && (
        <ol className="checks-list">
          {checks.map((c, i) => (
            <li key={i} className={`check-${c.result.toLowerCase()}`}>
              <span className="check-mark" aria-label={c.result}>{c.result === "Pass" ? "✓" : c.result === "Warn" ? "!" : "✕"}</span>
              <div><strong>{c.name}</strong><p>{c.detail}</p></div>
            </li>
          ))}
        </ol>
      )}
    </div>
  );
}

type WsRow = { id?: number; name: string; workspaceId: string; isActive: boolean };

function TenantEditor({ tenant, platform, onClose, onSaved, onChanged }: { tenant: Tenant | null; platform: "PowerBi" | "Tableau"; onClose: () => void; onSaved: (text: string) => void; onChanged: () => void }) {
  const [f, setF] = useState({
    name: tenant?.name ?? (platform === "PowerBi" ? "RRD Power BI" : "RRD Tableau Server"),
    isActive: tenant?.isActive ?? true,
    azureTenantId: tenant?.azureTenantId ?? "",
    clientId: tenant?.clientId ?? "",
    serverUrl: tenant?.serverUrl ?? "",
    siteContentUrl: tenant?.siteContentUrl ?? "",
    connectedAppClientId: tenant?.connectedAppClientId ?? "",
    connectedAppSecretId: tenant?.connectedAppSecretId ?? "",
    verifyUserName: tenant?.verifyUserName ?? "",
    tableauApiVersion: tenant?.tableauApiVersion ?? "3.21",
    secret: "",
  });
  const [workspaces, setWorkspaces] = useState<WsRow[]>(
    tenant?.workspaces.map((w) => ({ id: w.id, name: w.name, workspaceId: w.workspaceId, isActive: w.isActive })) ?? [{ name: "", workspaceId: "", isActive: true }],
  );
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [savedId, setSavedId] = useState<number | null>(tenant?.id ?? null);
  const [verifying, setVerifying] = useState(false);
  const [report, setReport] = useState<{ passed: boolean; checks: Check[] } | null>(null);
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF({ ...f, [k]: e.target.value });

  function pasteWorkspaceUrl(i: number, value: string) {
    // Accept a full Power BI workspace URL and pull out the GUID (…/groups/<id>/…).
    const m = value.match(/groups\/([0-9a-f-]{36})/i);
    const next = [...workspaces];
    next[i] = { ...next[i], workspaceId: m ? m[1] : value.trim() };
    setWorkspaces(next);
  }

  /** Saves the form as it is. Returns the tenant's id. */
  async function persist(): Promise<number> {
    const body = JSON.stringify({ ...f, platform, secret: f.secret || null, workspaces: platform === "PowerBi" ? workspaces : null });
    if (savedId !== null) {
      await api(`/api/admin/tenants/${savedId}`, { method: "PUT", body });
      return savedId;
    }
    const created = await api<{ id: number }>("/api/admin/tenants", { method: "POST", body });
    setSavedId(created.id);
    return created.id;
  }

  async function save(e: FormEvent) {
    e.preventDefault();
    setSaving(true);
    setError(null);
    try {
      await persist();
      onSaved(`${savedId !== null ? "Saved" : "Added"} ${f.name}. Now verify the connection.`);
    } catch (err) {
      setError(errorText(err));
    } finally {
      setSaving(false);
    }
  }

  /** Saves, then runs every check, and keeps the window open so each stage's result can be read. */
  async function saveAndVerify() {
    if (!(document.getElementById("tenant-form") as HTMLFormElement | null)?.reportValidity()) return;
    setVerifying(true);
    setError(null);
    setReport(null);
    try {
      const id = await persist();
      setReport(await api<{ passed: boolean; checks: Check[] }>(`/api/admin/tenants/${id}/verify`, { method: "POST" }));
      setF((x) => ({ ...x, secret: "" }));
      onChanged();
    } catch (err) {
      setError(errorText(err));
    } finally {
      setVerifying(false);
    }
  }

  return (
    <Modal title={tenant ? `Edit ${tenant.name}` : platform === "PowerBi" ? "Add Power BI Tenant" : "Add Tableau Server"} onClose={onClose} wide>
      <form id="tenant-form" onSubmit={save} className="stack" autoComplete="off">
        <div className="form-grid">
          <label className="field"><span>Name</span><input required maxLength={100} value={f.name} onChange={set("name")} /></label>
          <label className="check"><input type="checkbox" checked={f.isActive} onChange={(e) => setF({ ...f, isActive: e.target.checked })} /> Active</label>
        </div>

        {platform === "PowerBi" ? (
          <>
            <div className="form-grid">
              <label className="field"><span>Azure tenant ID</span><input required value={f.azureTenantId} onChange={set("azureTenantId")} placeholder="8eb1788b-…" /></label>
              <label className="field"><span>Client (application) ID</span><input required value={f.clientId} onChange={set("clientId")} placeholder="05cbb1f9-…" /></label>
              <label className="field span-2">
                <span>Client secret {tenant ? "(leave blank to keep the saved one)" : ""}</span>
                <input type="password" required={!tenant} value={f.secret} onChange={set("secret")} autoComplete="new-password" />
              </label>
            </div>
            <fieldset className="plain">
              <h3 className="sub">Workspaces</h3>
              <p className="muted small">Paste the workspace ID or the workspace's address from your browser (app.powerbi.com/groups/…). The service principal must be Admin, Member or Contributor of each one.</p>
              {workspaces.map((w, i) => (
                <div key={w.id ?? `new-${i}`} className="ws-row">
                  <label className="field"><span>Name</span><input required value={w.name} onChange={(e) => { const n = [...workspaces]; n[i] = { ...w, name: e.target.value }; setWorkspaces(n); }} /></label>
                  <label className="field grow"><span>Workspace ID</span><input required value={w.workspaceId} onChange={(e) => pasteWorkspaceUrl(i, e.target.value)} /></label>
                  <label className="check"><input type="checkbox" checked={w.isActive} onChange={(e) => { const n = [...workspaces]; n[i] = { ...w, isActive: e.target.checked }; setWorkspaces(n); }} /> Active</label>
                  {!w.id && workspaces.length > 1 && <button type="button" className="link link-danger" onClick={() => setWorkspaces(workspaces.filter((_, j) => j !== i))}>Remove</button>}
                </div>
              ))}
              <button type="button" className="btn" onClick={() => setWorkspaces([...workspaces, { name: "", workspaceId: "", isActive: true }])}>Add Workspace</button>
            </fieldset>
          </>
        ) : (
          <div className="form-grid">
            <label className="field span-2"><span>Tableau Server URL</span><input required value={f.serverUrl} onChange={set("serverUrl")} placeholder="https://tableau.company.com" /></label>
            <label className="field"><span>Site (content URL, blank for Default)</span><input value={f.siteContentUrl} onChange={set("siteContentUrl")} /></label>
            <label className="field"><span>REST API version</span><input value={f.tableauApiVersion} onChange={set("tableauApiVersion")} /></label>
            <label className="field"><span>Connected App client ID</span><input required value={f.connectedAppClientId} onChange={set("connectedAppClientId")} /></label>
            <label className="field"><span>Secret ID</span><input required value={f.connectedAppSecretId} onChange={set("connectedAppSecretId")} /></label>
            <label className="field"><span>Secret value {tenant ? "(leave blank to keep)" : ""}</span><input type="password" required={!tenant} value={f.secret} onChange={set("secret")} autoComplete="new-password" /></label>
            <label className="field"><span>Test user name (for Verify)</span><input value={f.verifyUserName} onChange={set("verifyUserName")} placeholder="a licensed Tableau user" /></label>
          </div>
        )}

        <p className="muted small">Secrets are stored encrypted and never shown again.</p>
        {error && <Notice tone="error">{error}</Notice>}
        <div className="actions">
          <button className="btn btn-primary" type="submit" disabled={saving || verifying}>{saving ? "Saving…" : savedId !== null ? "Save Tenant" : "Add Tenant"}</button>
          <button className="btn" type="button" disabled={saving || verifying} onClick={() => void saveAndVerify()}>{verifying ? "Verifying…" : "Save and Verify"}</button>
          <button className="btn" type="button" onClick={onClose}>{report ? "Close" : "Cancel"}</button>
        </div>
        {verifying && <p className="muted small">Saving, then checking each stage. This can take a little while…</p>}
        {report && (
          <div className="stack">
            <Notice tone={report.passed ? "ok" : "error"}>{report.passed ? "Every required check passed. You can publish through this tenant." : "Some checks failed. Each stage below says what to fix; then run Save and Verify again."}</Notice>
            <VerifyChecks checks={report.checks} startOpen />
          </div>
        )}
      </form>
    </Modal>
  );
}
