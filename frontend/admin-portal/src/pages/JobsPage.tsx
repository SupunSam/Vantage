import { useEffect, useState } from "react";
import { api, can, Icon, useGridPageSize, useSession, useFlash, PageSkeleton } from "@vantage/shared";
import { Link } from "react-router-dom";
import { errorText, Notice, Pill, useApi, when } from "@vantage/shared";

type Run = { id: number; job: string; title: string; startedAtUtc: string; finishedAtUtc: string | null; status: string; trigger: string; triggeredBy: string | null; summary: string | null };
type Job = { name: string; title: string; description: string; schedule: string | null; isPaused: boolean; isRunning: boolean; nextRunAtUtc: string | null; lastRun: Run | null };
type RunPage = { items: Run[]; total: number };

const weekdays = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
const two = (n: string) => n.padStart(2, "0");

/** A cron schedule in plain words for the common shapes; anything else is shown as written. */
export function describeSchedule(cron: string | null): string {
  if (!cron) return "Switched off";
  const [min, hour, dom, month, dow] = cron.split(" ");
  const num = (v: string) => /^\d+$/.test(v);
  if (!num(min) || !num(hour) || month !== "*") return `Cron ${cron} (UTC)`;
  const at = `${two(hour)}:${two(min)} UTC`;
  if (dom === "*" && dow === "*") return `Every day at ${at}`;
  if (num(dom) && dow === "*") return `Day ${dom} of every month at ${at}`;
  if (dom === "*" && num(dow)) return `Every ${weekdays[Number(dow) % 7]} at ${at}`;
  return `Cron ${cron} (UTC)`;
}

function runTone(status: string): "ok" | "bad" | "warn" {
  return status === "Succeeded" ? "ok" : status === "Failed" ? "bad" : "warn";
}

function who(r: Run) {
  return r.trigger === "Manual" ? `Run Now by ${r.triggeredBy ?? "an admin"}` : "Schedule";
}

/** Scheduled Jobs: when each background job runs, what the last run did, Run Now, pause and resume, and the run history. */
export function JobsPage() {
  const { me } = useSession();
  const canEdit = can(me, "scheduled-jobs", "Edit");
  const jobs = useApi<Job[]>("/api/admin/jobs");
  const [filter, setFilter] = useState("");
  const [page, setPage] = useState(1);
  const size = useGridPageSize();
  const runs = useApi<RunPage>(`/api/admin/jobs/runs?${new URLSearchParams({ ...(filter ? { job: filter } : {}), page: String(page), pageSize: String(size) })}`);
  const [busy, setBusy] = useState<string | null>(null);
  const setMessage = useFlash();

  const anyRunning = jobs.data?.some((j) => j.isRunning) ?? false;
  const { reload: reloadJobs } = jobs;
  const { reload: reloadRuns } = runs;
  useEffect(() => {
    if (!anyRunning) return;
    const t = setInterval(() => { reloadJobs(); reloadRuns(); }, 5000);
    return () => clearInterval(t);
  }, [anyRunning, reloadJobs, reloadRuns]);

  function refresh() { reloadJobs(); reloadRuns(); }

  async function runNow(j: Job) {
    setBusy(j.name);
    setMessage(null);
    try {
      const r = await api<Run>(`/api/admin/jobs/${j.name}/run`, { method: "POST" });
      setMessage({ ok: r.status === "Succeeded", text: `${j.title}: ${r.summary ?? r.status}` });
    } catch (e) { setMessage({ ok: false, text: errorText(e) }); } finally { setBusy(null); refresh(); }
  }

  async function setPaused(j: Job, paused: boolean) {
    setBusy(j.name);
    setMessage(null);
    try {
      await api(`/api/admin/jobs/${j.name}/paused`, { method: "PUT", body: JSON.stringify({ paused }) });
      setMessage({ ok: true, text: paused ? `${j.title} is paused. It won't run on its schedule until you resume it.` : `${j.title} is running on its schedule again.` });
    } catch (e) { setMessage({ ok: false, text: errorText(e) }); } finally { setBusy(null); refresh(); }
  }

  if (jobs.error) return <Notice tone="error">{jobs.error}</Notice>;
  const pages = runs.data ? Math.max(1, Math.ceil(runs.data.total / size)) : 1;

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Scheduled Jobs</h1>
          <p>
            The background jobs the portal runs for itself. Times are UTC. Every run is recorded here and in the Audit Log. Schedules and thresholds are set in{" "}
            <Link to="/config">Configuration</Link>.
          </p>
        </div>
        <button className="btn" type="button" onClick={refresh}><Icon name="refresh" size={18} /> Refresh</button>
      </div>

      {!jobs.data ? <PageSkeleton kind="table" head={false} /> : (
        <div className="stack">
          {jobs.data.map((j) => (
            <section key={j.name} className="panel job-card">
              <div className="job-main">
                <h2>{j.title} {j.isRunning ? <Pill tone="warn">Running</Pill> : j.isPaused ? <Pill tone="neutral">Paused</Pill> : j.schedule ? <Pill tone="ok">Scheduled</Pill> : <Pill tone="neutral">Off</Pill>}</h2>
                <p className="muted small">{j.description}</p>
                <dl className="job-facts">
                  <div><dt>Schedule</dt><dd>{describeSchedule(j.schedule)}</dd></div>
                  <div><dt>Next run</dt><dd>{j.isPaused ? <span className="muted">Paused</span> : j.nextRunAtUtc ? when(j.nextRunAtUtc) : <span className="muted">Not scheduled</span>}</dd></div>
                  <div>
                    <dt>Last run</dt>
                    <dd>
                      {j.lastRun
                        ? <><Pill tone={runTone(j.lastRun.status)}>{j.lastRun.status}</Pill> {when(j.lastRun.startedAtUtc)} <span className="muted">({who(j.lastRun)})</span></>
                        : <span className="muted">Has not run yet</span>}
                    </dd>
                  </div>
                </dl>
                {j.lastRun?.summary && <p className="small job-summary">{j.lastRun.summary}</p>}
              </div>
              {canEdit && (
                <div className="job-actions">
                  <button className="btn btn-primary" type="button" disabled={busy !== null || j.isRunning} onClick={() => void runNow(j)}>{busy === j.name ? "Working…" : "Run Now"}</button>
                  <button className="btn" type="button" disabled={busy !== null || (!j.schedule && !j.isPaused)} onClick={() => void setPaused(j, !j.isPaused)}>{j.isPaused ? "Resume" : "Pause"}</button>
                </div>
              )}
            </section>
          ))}
        </div>
      )}

      <h2 className="sub">Run History</h2>
      <div className="filters">
        <label className="field">
          <span>Job</span>
          <select value={filter} onChange={(e) => { setFilter(e.target.value); setPage(1); }}>
            <option value="">All jobs</option>
            {jobs.data?.map((j) => <option key={j.name} value={j.name}>{j.title}</option>)}
          </select>
        </label>
        <span className="muted small filters-count">{runs.data ? `${runs.data.total.toLocaleString()} ${runs.data.total === 1 ? "run" : "runs"}` : ""}</span>
      </div>
      {!runs.data ? <p className="muted">Loading…</p> : runs.data.items.length === 0 ? <p className="muted">Nothing has run yet.</p> : (
        <div className="table-wrap" aria-busy={runs.loading}>
          <table className="grid grid-rows">
            <thead><tr><th>Started</th><th>Job</th><th>Started By</th><th>Result</th><th>What It Did</th></tr></thead>
            <tbody>
              {runs.data.items.map((r) => (
                <tr key={r.id}>
                  <td>{when(r.startedAtUtc)}</td>
                  <td>{r.title}</td>
                  <td>{who(r)}</td>
                  <td><Pill tone={runTone(r.status)}>{r.status}</Pill></td>
                  <td className="small">{r.summary ?? <span className="muted">–</span>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {runs.data && runs.data.total > size && (
        <div className="pager">
          <span className="muted small">Page {page} of {pages}</span>
          <button className="btn" type="button" disabled={page <= 1} onClick={() => setPage(page - 1)}>Previous</button>
          <button className="btn" type="button" disabled={page >= pages} onClick={() => setPage(page + 1)}>Next</button>
        </div>
      )}
    </>
  );
}
