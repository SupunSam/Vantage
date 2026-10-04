import { useEffect, useState } from "react";
import { api, ApiError, DashboardDetailPanel, DashboardsPanel, OverviewPanel, RangePicker, TypePicker, typeParam, useSession, type Overview } from "@vantage/shared";

const BASE = "/api/owner/analytics";

/** Owner workspace: how the dashboards this person owns are being used. */
export function OwnerAnalyticsPage() {
  const { me } = useSession();
  const [days, setDays] = useState(30);
  const [type, setType] = useState("");   // all dashboard types by default
  const [selected, setSelected] = useState<number | null>(null);
  const [data, setData] = useState<{ owns: boolean; overview: Overview } | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setData(null);
    api<{ owns: boolean; overview: Overview }>(`${BASE}/overview?days=${days}${typeParam(type)}`).then(setData).catch((e) => setError(e instanceof ApiError ? e.message : String(e)));
  }, [days, type]);

  if (error) return <p className="notice notice-error">{error}</p>;
  if (!data) return <p className="muted">Loading…</p>;
  if (!data.owns) {
    return (
      <div className="state">
        <h2>No Dashboards to Show</h2>
        <p className="muted">{me?.displayName ?? "You"} {me ? "doesn't" : "don't"} own a dashboard right now. When you become the primary or backup owner of one, its usage appears here.</p>
      </div>
    );
  }

  return (
    <>
      <div className="home-head an-head">
        <div>
          <h1>Analytics</h1>
          <p className="muted">How the dashboards you own are used. Each time someone opens one counts as a view.</p>
        </div>
        <div className="an-filters"><TypePicker type={type} onChange={setType} /><RangePicker days={days} onChange={setDays} /></div>
      </div>
      {selected !== null && <DashboardDetailPanel base={BASE} id={selected} days={days} onClose={() => setSelected(null)} />}
      <OverviewPanel o={data.overview} scopeLabel="your active dashboards" onOpenDashboard={setSelected} />
      <DashboardsPanel base={BASE} days={days} type={type} onOpen={setSelected} />
    </>
  );
}
