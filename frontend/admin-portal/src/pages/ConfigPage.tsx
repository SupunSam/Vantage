import { useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { api, Icon, useSession, useFlash, PageSkeleton, useConfirm, ErrorState } from "@vantage/shared";
import { errorText, Notice, Pill, useApi, when } from "@vantage/shared";

type Setting = {
  key: string; label: string; group: string; kind: "text" | "int" | "bool" | "choice" | "color" | "cron" | "domain" | "host" | "url" | "origins" | "folder"; description: string; value: string; defaultValue: string;
  min: number | null; max: number | null; options: string[] | null; inUse: boolean; note: string | null; updatedAtUtc: string | null; updatedBy: string | null;
};
type Cdn = { id: number; host: string; notes: string | null; isActive: boolean };
type ServiceType = { type: string; displayName: string; isEnabled: boolean; hideWhenInactive: boolean; requiresFile: boolean; allowedExtensions: string | null; maxFileSizeMb: number | null };
type Config = { canEdit: boolean; canEditGenAi: boolean; settings: Setting[]; cdns: Cdn[]; types: ServiceType[]; logoMaxBytes: number };

const tabs = [
  { key: "branding", label: "Branding" },
  { key: "settings", label: "Settings" },
  { key: "genai", label: "GenAI Config" },
  { key: "types", label: "BI Types" },
] as const;
type Tab = (typeof tabs)[number]["key"];

/** Admin Configuration: how the portals look and behave. Every change is checked first and written to the audit log. */
export function ConfigPage() {
  const { data, error, reload } = useApi<Config>("/api/admin/config");
  const [search, setSearch] = useSearchParams();
  const wanted = search.get("tab") === "cdns" ? "genai" : search.get("tab"); // the GenAI Config tab used to be "Approved CDNs"
  const tab: Tab = (tabs.find((t) => t.key === wanted)?.key ?? "branding") as Tab;
  const setMessage = useFlash();

  if (error) return <ErrorState onRetry={reload}>{error}</ErrorState>;
  if (!data) return <PageSkeleton kind="page" />;
  const say = (ok: boolean, text: string) => setMessage({ ok, text });
  const done = (text: string) => { say(true, text); reload(); };

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Configuration</h1>
          <p>How the portals look and behave. Each change is checked before it is saved and appears in the Audit Log with the old and new value.</p>
        </div>
      </div>
      {!data.canEdit && <Notice>You can see the configuration but not change it. You need Edit permission on Admin Configuration.</Notice>}

      <div className="tabs" role="tablist">
        {tabs.map((t) => (
          <button key={t.key} type="button" role="tab" aria-selected={tab === t.key} className={`tab ${tab === t.key ? "tab-on" : ""}`}
            onClick={() => { setMessage(null); setSearch(t.key === "branding" ? {} : { tab: t.key }, { replace: true }); }}>{t.label}</button>
        ))}
      </div>

      {tab === "branding" && <BrandingTab data={data} onDone={done} onError={(t) => say(false, t)} />}
      {tab === "settings" && <SettingsTab data={data} scope="general" onDone={done} onError={(t) => say(false, t)} />}
      {tab === "genai" && <GenAiTab data={data} onDone={done} onError={(t) => say(false, t)} />}
      {tab === "types" && <TypesTab data={data} onDone={done} onError={(t) => say(false, t)} />}
    </>
  );
}

type TabProps = { data: Config; onDone: (text: string) => void; onError: (text: string) => void };

// ---------------------------------------------------------------- Branding

function BrandingTab({ data, onDone, onError }: TabProps) {
  const { branding } = useSession();
  const get = (k: string) => data.settings.find((s) => s.key === k)!;
  const [name, setName] = useState(get("branding.portalName").value);
  const [primary, setPrimary] = useState(get("branding.primaryColor").value);
  const [accent, setAccent] = useState(get("branding.accentColor").value);
  const [footer, setFooter] = useState(get("branding.footerText").value);
  const [busy, setBusy] = useState(false);
  const file = useRef<HTMLInputElement>(null);
  const hex = /^#[0-9A-Fa-f]{6}$/;
  const valid = name.trim().length > 0 && hex.test(primary) && hex.test(accent);
  const logo = branding.logoUrl ?? "/brand/logo.svg";

  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    try {
      await api("/api/admin/config/settings", { method: "PUT", body: JSON.stringify({ values: { "branding.portalName": name, "branding.primaryColor": primary, "branding.accentColor": accent, "branding.footerText": footer } }) });
      // Apply straight away on this page; other pages and other people see it the next time they load.
      const root = document.documentElement.style;
      root.setProperty("--brand", primary);
      root.setProperty("--accent", accent);
      document.title = name;
      onDone("Branding saved. Other people see it when they next open or reload a page.");
    } catch (err) { onError(errorText(err)); } finally { setBusy(false); }
  }

  async function upload(f: File | undefined) {
    if (!f) return;
    if (f.size > data.logoMaxBytes) { onError(`The logo can be up to ${Math.round(data.logoMaxBytes / 1024)} KB.`); return; }
    const body = new FormData();
    body.append("file", f);
    try { await api("/api/admin/config/logo", { method: "POST", body }); onDone("Logo updated. Reload the page to see it in the header."); } catch (err) { onError(errorText(err)); }
    if (file.current) file.current.value = "";
  }

  async function resetLogo() {
    try { await api("/api/admin/config/logo", { method: "DELETE" }); onDone("Back to the standard logo. Reload the page to see it in the header."); } catch (err) { onError(errorText(err)); }
  }

  return (
    <div className="cfg-cols">
      <form className="panel stack" onSubmit={save}>
        <h2>Name and Colours</h2>
        <label className="field">
          <span>Portal name <span className="optional">(shown in both portals and in emails)</span></span>
          <input required maxLength={60} value={name} disabled={!data.canEdit} onChange={(e) => setName(e.target.value)} />
        </label>
        <div className="cols cols-2">
          <ColorField label="Primary colour" value={primary} onChange={setPrimary} disabled={!data.canEdit} />
          <ColorField label="Accent colour" value={accent} onChange={setAccent} disabled={!data.canEdit} />
        </div>
        <p className="muted small">Primary is used for the menu, buttons and links; accent for highlights such as "New" badges.</p>
        <label className="field">
          <span>Footer text <span className="optional">(optional, shown after the copyright in both portals)</span></span>
          <input maxLength={200} value={footer} disabled={!data.canEdit} onChange={(e) => setFooter(e.target.value)} />
        </label>
        {data.canEdit && <div className="actions"><button className="btn btn-primary" type="submit" disabled={busy || !valid}>{busy ? "Saving…" : "Save Branding"}</button></div>}
      </form>

      <div className="stack">
        <section className="panel stack">
          <h2>Logo</h2>
          <div className="cfg-logo"><img src={logo} alt="Current logo" /></div>
          <p className="muted small">PNG, JPG, WebP or SVG, up to {Math.round(data.logoMaxBytes / 1024)} KB. A square or wide mark with a transparent background works best.</p>
          {data.canEdit && (
            <div className="actions">
              <input ref={file} type="file" accept=".png,.jpg,.jpeg,.webp,.svg,image/png,image/jpeg,image/webp,image/svg+xml" hidden onChange={(e) => void upload(e.target.files?.[0])} />
              <button className="btn" type="button" onClick={() => file.current?.click()}><Icon name="upload" size={18} /> Upload New Logo</button>
              {branding.logoUrl && branding.logoUrl !== "/brand/logo.svg" && <button className="btn btn-quiet" type="button" onClick={() => void resetLogo()}>Use Standard Logo</button>}
            </div>
          )}
        </section>

        <section className="panel">
          <h2>Preview</h2>
          <div className="cfg-preview" style={{ background: primary }}>
            <img src={logo} alt="" />
            <strong>{name || "Portal name"}</strong>
            <span className="cfg-badge" style={{ background: accent }}>New</span>
          </div>
        </section>
      </div>
    </div>
  );
}

function ColorField({ label, value, onChange, disabled }: { label: string; value: string; onChange: (v: string) => void; disabled: boolean }) {
  const ok = /^#[0-9A-Fa-f]{6}$/.test(value);
  return (
    <label className="field">
      <span>{label}</span>
      <span className="cfg-color">
        <input type="color" value={ok ? value : "#000000"} disabled={disabled} onChange={(e) => onChange(e.target.value.toUpperCase())} aria-label={`${label} picker`} />
        <input value={value} maxLength={7} disabled={disabled} onChange={(e) => onChange(e.target.value)} aria-invalid={!ok} />
      </span>
      {!ok && <span className="field-hint field-hint-bad">Use a colour like #1F4E79</span>}
    </label>
  );
}

// ---------------------------------------------------------------- Settings

const isGenAi = (group: string) => group.startsWith("GenAI");

/** The settings of one tab: "general" is everything except Branding and GenAI; "genai" is the GenAI Config settings. */
function SettingsTab({ data, scope, onDone, onError }: TabProps & { scope: "general" | "genai" }) {
  const list = useMemo(() => data.settings.filter((s) => (scope === "genai" ? isGenAi(s.group) : s.group !== "Branding" && !isGenAi(s.group))), [data.settings, scope]);
  const [values, setValues] = useState<Record<string, string>>(() => Object.fromEntries(list.map((s) => [s.key, s.value])));
  const [busy, setBusy] = useState(false);
  useEffect(() => setValues(Object.fromEntries(list.map((s) => [s.key, s.value]))), [list]);

  const changed = list.filter((s) => values[s.key] !== s.value);
  const groups = [...new Set(list.map((s) => s.group))];

  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    try {
      const r = await api<{ changed: number }>("/api/admin/config/settings", { method: "PUT", body: JSON.stringify({ values: Object.fromEntries(changed.map((s) => [s.key, values[s.key]])) }) });
      onDone(r.changed === 0 ? "Nothing changed." : `Saved ${r.changed} ${r.changed === 1 ? "setting" : "settings"}.`);
    } catch (err) { onError(errorText(err)); } finally { setBusy(false); }
  }

  return (
    <form className="stack" onSubmit={save}>
      {groups.map((g) => (
        <section key={g} className="panel stack">
          <h2>{scope === "genai" ? g.replace(/^GenAI\s+/, "") : g}</h2>
          {g === "Scheduled jobs" && <p className="muted small">Times are UTC. A change applies from the next check (within a minute). See when each job runs next, or run one now, on <Link to="/jobs">Scheduled Jobs</Link>.</p>}
          {list.filter((s) => s.group === g).map((s) => (
            <div key={s.key} className="cfg-setting">
              <div className="cfg-setting-text">
                <strong>{s.label}</strong>
                <span className="muted small">{s.description}</span>
                {!s.inUse && <span className="small"><Pill tone="neutral">Saved for later</Pill> <span className="muted">{s.note}</span></span>}
                {s.updatedAtUtc && <span className="muted small">Last changed {when(s.updatedAtUtc)}{s.updatedBy ? ` by ${s.updatedBy}` : ""}</span>}
              </div>
              <SettingInput s={s} value={values[s.key] ?? ""} disabled={!data.canEdit} onChange={(v) => setValues((x) => ({ ...x, [s.key]: v }))} />
            </div>
          ))}
        </section>
      ))}
      {data.canEdit && (
        <div className="actions">
          <button className="btn btn-primary" type="submit" disabled={busy || changed.length === 0}>{busy ? "Saving…" : changed.length ? `Save ${changed.length} ${changed.length === 1 ? "Change" : "Changes"}` : "Save Changes"}</button>
          {changed.length > 0 && <button className="btn btn-quiet" type="button" onClick={() => setValues(Object.fromEntries(list.map((s) => [s.key, s.value])))}>Discard</button>}
        </div>
      )}
    </form>
  );
}

function SettingInput({ s, value, disabled, onChange }: { s: Setting; value: string; disabled: boolean; onChange: (v: string) => void }) {
  if (s.kind === "bool") {
    return (
      <label className="switch">
        <input type="checkbox" checked={value === "true"} disabled={disabled} onChange={(e) => onChange(e.target.checked ? "true" : "false")} />
        <span>{value === "true" ? "On" : "Off"}</span>
      </label>
    );
  }
  if (s.kind === "choice") {
    return <select value={value} disabled={disabled} onChange={(e) => onChange(e.target.value)} aria-label={s.label}>{s.options!.map((o) => <option key={o}>{o}</option>)}</select>;
  }
  if (s.kind === "int") {
    return <input type="number" inputMode="numeric" min={s.min ?? undefined} max={s.max ?? undefined} value={value} disabled={disabled} onChange={(e) => onChange(e.target.value)} aria-label={s.label} className="cfg-num" />;
  }
  const placeholder = { folder: "powerbi", cron: "0 2 1 * *", domain: "rrd.com", url: "https://genai.example.com", origins: "https://portal.example.com https://admin.example.com", host: "clamav" }[s.kind as string];
  return <input value={value} disabled={disabled} onChange={(e) => onChange(e.target.value)} aria-label={s.label} className="cfg-text" placeholder={placeholder} />;
}

// ---------------------------------------------------------------- GenAI Config

/** GenAI Config: how GenAI dashboards are served, checked and scanned, and which CDNs they may load libraries from. Super Admins only. */
function GenAiTab({ data, onDone, onError }: TabProps) {
  // These settings decide what code may run in front of staff, so only Super Admins can change them.
  const genAiData = { ...data, canEdit: data.canEditGenAi };
  return (
    <div className="stack">
      {data.canEdit && !data.canEditGenAi && <Notice>Only Super Admins can change the GenAI settings and the approved CDNs.</Notice>}
      <SettingsTab data={genAiData} scope="genai" onDone={onDone} onError={onError} />
      <CdnsTab data={genAiData} onDone={onDone} onError={onError} />
    </div>
  );
}

// ---------------------------------------------------------------- Approved CDNs

function CdnsTab({ data, onDone, onError }: TabProps) {
  const confirm = useConfirm();
  const [host, setHost] = useState("");
  const [notes, setNotes] = useState("");
  const [busy, setBusy] = useState(false);

  async function add(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    try { await api("/api/admin/config/cdns", { method: "POST", body: JSON.stringify({ host, notes: notes || null }) }); setHost(""); setNotes(""); onDone("Added to the approved CDNs."); } catch (err) { onError(errorText(err)); } finally { setBusy(false); }
  }
  async function toggle(c: Cdn) {
    try { await api(`/api/admin/config/cdns/${c.id}`, { method: "PUT", body: JSON.stringify({ notes: c.notes, isActive: !c.isActive }) }); onDone(`${c.host} is now ${c.isActive ? "off" : "on"}.`); } catch (err) { onError(errorText(err)); }
  }
  async function remove(c: Cdn) {
    if (!(await confirm({ title: "Remove Approved CDN", message: `Remove ${c.host} from the approved CDNs? GenAI dashboards that load files from it would no longer be allowed to.`, confirmLabel: "Remove", danger: true }))) return;
    try { await api(`/api/admin/config/cdns/${c.id}`, { method: "DELETE" }); onDone(`Removed ${c.host}.`); } catch (err) { onError(errorText(err)); }
  }

  return (
    <section className="panel stack">
      <h2>Approved CDNs</h2>
      <p className="muted small">GenAI dashboards are a single HTML file. They may load scripts and stylesheets only from the hosts listed here, at an exact version and with an integrity hash. The upload check enforces this.</p>
      {data.cdns.length === 0 ? <p className="pop-empty">No CDN is approved, so GenAI dashboards can't load any outside library.</p> : (
        <div className="requests-table-wrap">
          <table className="requests-table">
            <thead><tr><th>Host</th><th>Notes</th><th>Status</th>{data.canEdit && <th />}</tr></thead>
            <tbody>
              {data.cdns.map((c) => (
                <tr key={c.id}>
                  <td><code>{c.host}</code></td>
                  <td className="small">{c.notes ?? <span className="muted">–</span>}</td>
                  <td>{c.isActive ? <Pill tone="ok">Allowed</Pill> : <Pill tone="neutral">Off</Pill>}</td>
                  {data.canEdit && (
                    <td className="cell-actions"><span className="member-actions">
                      <button type="button" className="btn" onClick={() => void toggle(c)}>{c.isActive ? "Switch Off" : "Switch On"}</button>
                      <button type="button" className="icon-btn icon-btn-sm icon-btn-danger" aria-label={`Remove ${c.host}`} onClick={() => void remove(c)}><Icon name="trash" size={18} /></button>
                    </span></td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {data.canEdit && (
        <form className="filters" onSubmit={add}>
          <label className="field"><span>Host name</span><input required value={host} onChange={(e) => setHost(e.target.value)} placeholder="cdn.example.com" /></label>
          <label className="field field-search"><span>Notes (optional)</span><input maxLength={200} value={notes} onChange={(e) => setNotes(e.target.value)} placeholder="e.g. Pin versions" /></label>
          <button className="btn btn-primary" type="submit" disabled={busy || !host.trim()}><Icon name="plus" size={18} /> Add CDN</button>
        </form>
      )}
    </section>
  );
}

// ---------------------------------------------------------------- BI types

function TypesTab({ data, onDone, onError }: TabProps) {
  return (
    <section className="panel stack">
      <h2>BI Types</h2>
      <p className="muted small">Set a type to Inactive to stop new dashboards, tenants and file versions of that type, and to hide its tab and options in the portals. Existing dashboards keep working and show a BI Inactive badge, unless you also choose to hide them from the User Portal. At least one type must stay Active.</p>
      <div className="requests-table-wrap">
        <table className="requests-table">
          <thead><tr><th>Type</th><th>Files accepted</th><th>Largest upload (MB)</th><th>Status</th><th>Hide Existing</th>{data.canEdit && <th />}</tr></thead>
          <tbody>{data.types.map((t) => <TypeRow key={t.type} t={t} canEdit={data.canEdit} onDone={onDone} onError={onError} />)}</tbody>
        </table>
      </div>
    </section>
  );
}

function TypeRow({ t, canEdit, onDone, onError }: { t: ServiceType; canEdit: boolean; onDone: (s: string) => void; onError: (s: string) => void }) {
  const [on, setOn] = useState(t.isEnabled);
  const [hide, setHide] = useState(t.hideWhenInactive);
  const [mb, setMb] = useState(t.maxFileSizeMb?.toString() ?? "");
  useEffect(() => { setOn(t.isEnabled); setHide(t.hideWhenInactive); setMb(t.maxFileSizeMb?.toString() ?? ""); }, [t]);
  const dirty = on !== t.isEnabled || hide !== t.hideWhenInactive || (t.requiresFile && mb !== (t.maxFileSizeMb?.toString() ?? ""));

  async function save() {
    try {
      await api(`/api/admin/config/types/${t.type}`, { method: "PUT", body: JSON.stringify({ isEnabled: on, hideWhenInactive: hide, maxFileSizeMb: t.requiresFile ? Number(mb) : null }) });
      onDone(`${t.displayName} saved.`);
    } catch (err) { onError(errorText(err)); setOn(t.isEnabled); }
  }

  return (
    <tr>
      <td><strong>{t.displayName}</strong></td>
      <td className="small">{t.allowedExtensions ?? <span className="muted">A web address, no file</span>}</td>
      <td>{t.requiresFile ? <input type="number" min={1} max={2048} value={mb} disabled={!canEdit} onChange={(e) => setMb(e.target.value)} className="cfg-num" aria-label={`Largest ${t.displayName} upload in MB`} /> : <span className="muted">–</span>}</td>
      <td><label className="switch"><input type="checkbox" checked={on} disabled={!canEdit} onChange={(e) => setOn(e.target.checked)} /><span>{on ? "Active" : "Inactive"}</span></label></td>
      <td><label className="switch"><input type="checkbox" checked={hide} disabled={!canEdit || on} onChange={(e) => setHide(e.target.checked)} /><span>{hide ? "Hidden while inactive" : "Stay visible"}</span></label></td>
      {canEdit && <td className="cell-actions"><button type="button" className="btn btn-primary" disabled={!dirty} onClick={() => void save()}>Save</button></td>}
    </tr>
  );
}
