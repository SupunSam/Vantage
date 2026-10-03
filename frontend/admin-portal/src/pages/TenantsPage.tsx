import { useState, type FormEvent } from "react";
import { api, can, Icon, useSession } from "@vantage/shared";
import { errorText, MenuButton, Modal, Notice, Pill, useApi, when } from "../ui";

type Check = { name: string; result: "Pass" | "Warn" | "Fail"; detail: string };
type Workspace = { id: number; name: string; workspaceId: string; isActive: boolean; servicePrincipalAccess: string | null; onDedicatedCapacity: boolean | null };
type Tenant = {
  id: number; name: string; platform: "PowerBi" | "Tableau"; isActive: boolean;
  azureTenantId: string | null; clientId: string | null;
  serverUrl: string | null; siteContentUrl: string | null; connectedAppClientId: string | null; connectedAppSecretId: string | null; verifyUserName: string | null; tableauApiVersion: string;
  secretUpdatedAtUtc: string | null; lastVerifiedAtUtc: string | null; lastVerifyPassed: boolean | null; checks: Check[];
  workspaces: Workspace[]; dashboards: number;
};

export function TenantsPage() {
  const { me } = useSession();
  const canEdit = can(me, "tenants", "Edit");
  const { data, error, reload } = useApi<Tenant[]>("/api/admin/tenants");
  const [editing, setEditing] = useState<Tenant | "PowerBi" | "Tableau" | null>(null);
  const [verifying, setVerifying] = useState<number | null>(null);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);

  async function verify(t: Tenant) {
    setVerifying(t.id);
    setMessage(null);
    try {
      const r = await api<{ passed: boolean }>(`/api/admin/tenants/${t.id}/verify`, { method: "POST" });
      setMessage({ ok: r.passed, text: r.passed ? `${t.name} is verified. You can publish into its workspaces.` : `${t.name} failed verification. The checks below say what to fix.` });
      reload();
    } catch (e) {
      setMessage({ ok: false, text: errorText(e) });
    } finally {
      setVerifying(null);
    }
  }

  if (error) return <Notice tone="error">{error}</Notice>;
  if (!data) return <p className="muted">Loading…</p>;

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Tenants</h1>
          <p>Each Power BI tenant is one service principal with the workspaces it publishes into. Verify a tenant before publishing; any change to it needs verifying again.</p>
        </div>
        {canEdit && (
          <MenuButton primary label={<><Icon name="plus" size={18} /> Add tenant</>} items={[
            { label: "Power BI", hint: "A service principal and its workspaces", onSelect: () => setEditing("PowerBi") },
            { label: "Tableau Server", hint: "A server site and its Connected App", onSelect: () => setEditing("Tableau") },
          ]} />
        )}
      </div>
      {message && <Notice tone={message.ok ? "ok" : "error"}>{message.text}</Notice>}

      {data.length === 0 && (
        <div className="empty">
          <h2>No Tenants Yet</h2>
          <p>Add your Power BI tenant: its Azure tenant ID, the service principal's client ID and secret, and the workspace dashboards will be published into.</p>
        </div>
      )}

      {data.map((t) => (
        <section key={t.id} className="panel tenant">
          <div className="panel-row">
            <div>
              <h2>{t.name} {!t.isActive && <Pill tone="neutral">Inactive</Pill>}</h2>
              <p className="muted small">
                {t.platform === "PowerBi" ? `Power BI, tenant ${t.azureTenantId}, client ${t.clientId}` : `Tableau, ${t.serverUrl}${t.siteContentUrl ? `, site ${t.siteContentUrl}` : ""}`}
                {`. Secret saved ${when(t.secretUpdatedAtUtc)}. ${t.dashboards} dashboard${t.dashboards === 1 ? "" : "s"}.`}
              </p>
            </div>
            <div className="actions">
              {t.lastVerifyPassed === true && <Pill tone="ok">Verified {when(t.lastVerifiedAtUtc)}</Pill>}
              {t.lastVerifyPassed === false && <Pill tone="bad">Failed {when(t.lastVerifiedAtUtc)}</Pill>}
              {t.lastVerifyPassed == null && <Pill tone="warn">Not verified</Pill>}
              {canEdit && <button className="btn" type="button" onClick={() => setEditing(t)}>Edit</button>}
              {canEdit && <button className="btn btn-primary" type="button" disabled={verifying === t.id} onClick={() => void verify(t)}>{verifying === t.id ? "Verifying…" : "Verify Connection"}</button>}
            </div>
          </div>

          {t.platform === "PowerBi" && (
            <table className="grid">
              <thead><tr><th>Workspace</th><th>Workspace ID</th><th>Service principal access</th><th>Capacity</th></tr></thead>
              <tbody>
                {t.workspaces.map((w) => (
                  <tr key={w.id} className={w.isActive ? "" : "muted"}>
                    <td>{w.name}{!w.isActive && " (inactive)"}</td>
                    <td className="small mono-ish">{w.workspaceId}</td>
                    <td>{w.servicePrincipalAccess ?? <span className="muted">Verify to check</span>}</td>
                    <td>{w.onDedicatedCapacity == null ? <span className="muted">Verify to check</span> : w.onDedicatedCapacity ? "Dedicated (F64)" : <span style={{ color: "var(--danger)" }}>Shared</span>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}

          {t.checks.length > 0 && <VerifyChecks checks={t.checks} />}
        </section>
      ))}

      {editing && (
        <TenantEditor
          tenant={typeof editing === "string" ? null : editing}
          platform={typeof editing === "string" ? editing : editing.platform}
          onClose={() => setEditing(null)}
          onSaved={(text) => { setEditing(null); setMessage({ ok: true, text }); reload(); }}
        />
      )}
    </>
  );
}

/** The last verification's checks, folded into one summary line; opens by itself when something failed. */
function VerifyChecks({ checks }: { checks: Check[] }) {
  const fails = checks.filter((c) => c.result === "Fail").length;
  const warns = checks.filter((c) => c.result === "Warn").length;
  const passes = checks.length - fails - warns;
  const [open, setOpen] = useState(fails > 0);
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

function TenantEditor({ tenant, platform, onClose, onSaved }: { tenant: Tenant | null; platform: "PowerBi" | "Tableau"; onClose: () => void; onSaved: (text: string) => void }) {
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
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF({ ...f, [k]: e.target.value });

  function pasteWorkspaceUrl(i: number, value: string) {
    // Accept a full Power BI workspace URL and pull out the GUID (…/groups/<id>/…).
    const m = value.match(/groups\/([0-9a-f-]{36})/i);
    const next = [...workspaces];
    next[i] = { ...next[i], workspaceId: m ? m[1] : value.trim() };
    setWorkspaces(next);
  }

  async function save(e: FormEvent) {
    e.preventDefault();
    setSaving(true);
    setError(null);
    try {
      const body = JSON.stringify({ ...f, platform, secret: f.secret || null, workspaces: platform === "PowerBi" ? workspaces : null });
      if (tenant) await api(`/api/admin/tenants/${tenant.id}`, { method: "PUT", body });
      else await api("/api/admin/tenants", { method: "POST", body });
      onSaved(`${tenant ? "Saved" : "Added"} ${f.name}. Now verify the connection.`);
    } catch (err) {
      setError(errorText(err));
    } finally {
      setSaving(false);
    }
  }

  return (
    <Modal title={tenant ? `Edit ${tenant.name}` : platform === "PowerBi" ? "Add Power BI Tenant" : "Add Tableau Server"} onClose={onClose} wide>
      <form onSubmit={save} className="stack" autoComplete="off">
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
          <button className="btn btn-primary" type="submit" disabled={saving}>{saving ? "Saving…" : tenant ? "Save Tenant" : "Add Tenant"}</button>
          <button className="btn" type="button" onClick={onClose}>Cancel</button>
        </div>
      </form>
    </Modal>
  );
}
