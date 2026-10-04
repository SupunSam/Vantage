import { useCallback, useEffect, useRef, useState } from "react";
import { Link, useParams } from "react-router-dom";
import * as pbi from "powerbi-client";
import { api, ApiError, GenAiFrame, TableauViz } from "@vantage/shared";

type EmbedInfo = {
  type: "powerbi" | "tableau" | "genai";
  dashboardId: number;
  name: string;
  embedUrl: string | null;
  token: string | null;
  expiresAt: string | null;
  reportId: string | null;
  tableauScriptUrl: string | null;
};

type Failure = { kind: "no-access"; name?: string; pending?: boolean; ownerless?: boolean } | { kind: "error"; code?: string; message: string; correlationId?: string; name?: string };

const powerbiService = new pbi.service.Service(pbi.factories.hpmFactory, pbi.factories.wpmpFactory, pbi.factories.routerFactory);
const LOAD_TIMEOUT_MS = 30_000;
const REFRESH_BEFORE_MS = 5 * 60_000;

const friendly: Record<string, string> = {
  "not-linked": "This dashboard isn't linked to a report yet. Its owner or a Super Admin needs to finish publishing it.",
  "not-configured": "The BI platform for this dashboard isn't fully set up in the tenant master.",
  "secret-missing": "The portal can't sign in to the BI platform because its secret isn't set on this machine.",
  "rls-missing": "This dashboard uses row-level security, but your group has no RLS value. Ask a Super Admin to set it.",
  "tableau-user-missing": "Your profile has no Tableau user name. Ask a Super Admin to add it.",
  "tableau-load": "Tableau couldn't open this view for you. On Tableau Server this usually means your Tableau user name in your profile is wrong, or you have no Tableau account. Ask a Super Admin to check it.",
  "platform-error": "The BI platform returned an error.",
  timeout: "The dashboard took more than 30 seconds to load.",
};

export function ViewerPage() {
  const { id } = useParams();
  const [info, setInfo] = useState<EmbedInfo | null>(null);
  const [failure, setFailure] = useState<Failure | null>(null);
  const [attempt, setAttempt] = useState(0);
  const hostRef = useRef<HTMLDivElement>(null);

  const fetchEmbed = useCallback(() => api<EmbedInfo>(`/api/dashboards/${id}/embed`), [id]);

  useEffect(() => {
    let cancelled = false;
    setInfo(null);
    setFailure(null);
    fetchEmbed()
      .then((i) => !cancelled && setInfo(i))
      .catch((e: unknown) => {
        if (cancelled) return;
        if (e instanceof ApiError && e.status === 403) setFailure({ kind: "no-access", name: e.body?.name as string | undefined, pending: e.body?.requestPending === true, ownerless: e.body?.ownerless === true });
        else if (e instanceof ApiError)
          setFailure({ kind: "error", code: e.code, message: e.message, correlationId: e.body?.correlationId as string | undefined, name: e.body?.name as string | undefined });
        else setFailure({ kind: "error", message: String(e) });
      });
    return () => {
      cancelled = true;
    };
  }, [fetchEmbed, attempt]);

  // Power BI: embed, time out after 30 s, and swap in a fresh token before the current one expires.
  useEffect(() => {
    if (info?.type !== "powerbi" || !hostRef.current || !info.token || !info.embedUrl) return;
    const host = hostRef.current;
    const report = powerbiService.embed(host, {
      type: "report",
      id: info.reportId ?? undefined,
      embedUrl: info.embedUrl,
      accessToken: info.token,
      tokenType: pbi.models.TokenType.Embed,
      settings: { panes: { filters: { visible: false }, pageNavigation: { visible: true } } },
    }) as pbi.Report;

    let loaded = false;
    const timeout = window.setTimeout(() => {
      if (!loaded) setFailure({ kind: "error", code: "timeout", message: "Timed out" });
    }, LOAD_TIMEOUT_MS);
    report.on("loaded", () => {
      loaded = true;
      window.clearTimeout(timeout);
    });
    report.on("error", (event) => {
      const detail = event.detail as { message?: string; detailedMessage?: string } | undefined;
      setFailure({ kind: "error", code: "platform-error", message: detail?.detailedMessage ?? detail?.message ?? "Power BI could not load the report." });
    });

    let refreshTimer: number | undefined;
    const scheduleRefresh = (expiresAt: string | null) => {
      if (!expiresAt) return;
      const wait = Math.max(30_000, new Date(expiresAt).getTime() - Date.now() - REFRESH_BEFORE_MS);
      refreshTimer = window.setTimeout(async () => {
        try {
          const next = await fetchEmbed();
          if (next.token) await report.setAccessToken(next.token);
          scheduleRefresh(next.expiresAt);
        } catch {
          setFailure({ kind: "error", code: "platform-error", message: "Your session with Power BI expired and could not be renewed." });
        }
      }, wait);
    };
    scheduleRefresh(info.expiresAt);

    return () => {
      window.clearTimeout(timeout);
      window.clearTimeout(refreshTimer);
      powerbiService.reset(host);
    };
  }, [info, fetchEmbed]);

  return (
    <div className="viewer">
      <div className="viewer-bar">
        <Link to="/" className="btn">My Dashboards</Link>
        <h1>{info?.name ?? (failure && "name" in failure ? failure.name : null) ?? "Dashboard"}</h1>
      </div>

      {failure?.kind === "no-access" && (
        <div className="state">
          <h2>You Don't Have Access to This Dashboard</h2>
          <RequestAccess dashboardId={Number(id)} name={failure.name} pending={failure.pending} ownerless={failure.ownerless} />
        </div>
      )}

      {failure?.kind === "error" && (
        <div className="state">
          <h2>This Dashboard Didn't Load</h2>
          <p>{(failure.code && friendly[failure.code]) ?? failure.message}</p>
          {failure.code && friendly[failure.code] && failure.message !== friendly[failure.code] && <p className="muted small">{failure.message}</p>}
          {failure.correlationId && <p className="muted small">Reference: {failure.correlationId}</p>}
          <button className="btn btn-primary" type="button" onClick={() => setAttempt((n) => n + 1)}>Retry</button>
        </div>
      )}

      {!failure && !info && <p className="muted">Loading…</p>}

      {!failure && info?.type === "genai" && info.embedUrl && (
        <div className="embed-host"><GenAiFrame url={info.embedUrl} title={info.name} /></div>
      )}

      {!failure && info?.type === "tableau" && info.embedUrl && info.tableauScriptUrl && (
        <div className="embed-host">
          <TableauViz src={info.embedUrl} token={info.token} scriptUrl={info.tableauScriptUrl}
            onError={(message) => setFailure({ kind: "error", code: "tableau-load", message, name: info.name })} />
        </div>
      )}

      {!failure && info?.type === "powerbi" && <div ref={hostRef} className="embed-host" />}
    </div>
  );
}

/** The "no access" page: ask the owner for access, with an optional reason. */
function RequestAccess({ dashboardId, name, pending, ownerless }: { dashboardId: number; name?: string; pending?: boolean; ownerless?: boolean }) {
  const [reason, setReason] = useState("");
  const [state, setState] = useState<"idle" | "sending" | "sent">(pending ? "sent" : "idle");
  const [error, setError] = useState<string | null>(null);

  if (ownerless) return <p className="muted">This dashboard has no owner right now. Please request access again in a few days.</p>;
  if (state === "sent") {
    return (
      <>
        <p className="notice notice-ok">Your request is with the owner of {name ?? "this dashboard"}. You'll get a notification and an email when they decide.</p>
        <Link to="/catalogue" className="btn">Go to the Catalogue</Link>
      </>
    );
  }

  async function send() {
    setState("sending");
    setError(null);
    try {
      await api("/api/access-requests", { method: "POST", body: JSON.stringify({ dashboardId, comment: reason || null }) });
      setState("sent");
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e));
      setState("idle");
    }
  }

  return (
    <>
      <p className="muted">Access is given by the dashboard's owner. Ask for it here; they'll decide what you can see.</p>
      <label className="field" style={{ width: "100%" }}>
        <span>Why do you need it? (optional, helps the owner decide)</span>
        <textarea rows={3} maxLength={1000} value={reason} onChange={(e) => setReason(e.target.value)} />
      </label>
      {error && <p className="notice notice-error">{error}</p>}
      <button className="btn btn-primary" type="button" disabled={state === "sending"} onClick={() => void send()}>{state === "sending" ? "Sending…" : "Request Access"}</button>
    </>
  );
}
