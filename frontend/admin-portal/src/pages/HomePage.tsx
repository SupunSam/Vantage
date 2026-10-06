import { Link } from "react-router-dom";
import { Icon, TrendChart, useSession, PageSkeleton, ErrorState, EmptyState } from "@vantage/shared";
import { useApi, when } from "@vantage/shared";

type Home = {
  dashboards: { live: number; inactive: number; notLive: number; liveByType: { key: string; count: number }[] } | null;
  users: { activeInternal: number; activeExternal: number; inactive: number; setupPending: number; accessGroups: number } | null;
  usage: {
    days: number; views: number; uniqueUsers: number; inScope: number; viewed: number; unused: number;
    daily: { day: string; views: number; users: number }[];
    top: { id: number; name: string; type: string; views: number; users: number }[];
  } | null;
  requests: { accessRequests: number; groupAdditions: number } | null;
  attention: { key: string; text: string; count: number; link: string }[];
  recent: { occurredAtUtc: string; actor: string | null; description: string }[] | null;
};

const TYPES: Record<string, { label: string; className: string }> = {
  PowerBi: { label: "Power BI", className: "hv-s1" },
  Tableau: { label: "Tableau", className: "hv-s2" },
  GenAi: { label: "GenAI", className: "hv-s3" },
};
const typeLabel = (t: string) => TYPES[t]?.label ?? t;
const n = (v: number) => v.toLocaleString();

/** Home: how the portal looks today. Each part appears only if the person's role may see it. */
export function HomePage() {
  const { me } = useSession();
  const { data, error } = useApi<Home>("/api/admin/home");
  if (error) return <ErrorState>{error}</ErrorState>;
  if (!data) return <PageSkeleton kind="tiles" />;

  const firstName = me?.displayName.split(" ")[0];
  const waiting = data.requests ? data.requests.accessRequests + data.requests.groupAdditions : 0;
  const hasAnything = data.dashboards || data.users || data.usage || data.requests || data.recent;

  return (
    <>
      <div className="page-head">
        <div>
          <h1>Home</h1>
          <p>{firstName ? `Welcome back, ${firstName}. ` : ""}Here is how the portal looks today.</p>
        </div>
      </div>

      {!hasAnything && <EmptyState icon="home" title="Welcome">Use the menu to open the parts of the Admin Portal your role allows.</EmptyState>}

      <div className="an-tiles">
        {data.dashboards && <HomeTile icon="dashboards" to="/dashboards" label="Live Dashboards" value={n(data.dashboards.live)} hint={data.dashboards.inactive ? `${n(data.dashboards.inactive)} inactive` : "all live"} />}
        {data.users && <HomeTile icon="users" to="/users" label="Active Users" value={n(data.users.activeInternal + data.users.activeExternal)} hint={`${n(data.users.activeInternal)} internal, ${n(data.users.activeExternal)} external`} />}
        {data.usage && <HomeTile icon="chart" to="/analytics" label="Views, Last 30 Days" value={n(data.usage.views)} hint={`${n(data.usage.uniqueUsers)} ${data.usage.uniqueUsers === 1 ? "person" : "people"}`} />}
        {data.requests && <HomeTile icon="inbox" to="/requests" label="Waiting for a Decision" value={n(waiting)} hint={waiting === 0 ? "nothing waiting" : `${n(data.requests.accessRequests)} access, ${n(data.requests.groupAdditions)} group additions`} />}
      </div>

      {data.usage && (
        <section className="panel">
          <TrendChart points={data.usage.daily} title={`Dashboard views per day, last ${data.usage.days} days`} />
        </section>
      )}

      <div className="an-cols hv-cols">
        {data.dashboards && <TypeMix rows={data.dashboards.liveByType} />}
        {data.usage && <TopDashboards rows={data.usage.top} days={data.usage.days} />}
        {data.users && <PeopleMix users={data.users} />}
      </div>

      <div className="an-cols hv-cols">
        <Attention items={data.attention} />
        {data.recent && <Recent rows={data.recent} />}
      </div>
    </>
  );
}

function HomeTile({ to, label, value, hint, icon }: { to: string; label: string; value: string; hint: string; icon: string }) {
  return (
    <Link to={to} className="an-tile hv-tile">
      <span className="tile-icon" aria-hidden="true"><Icon name={icon} size={20} /></span>
      <span className="an-tile-label">{label}</span>
      <strong className="an-tile-value">{value}</strong>
      <span className="an-tile-hint">{hint}</span>
    </Link>
  );
}

/** Part of a whole: one stacked bar of the live dashboards by type. Fixed colour per type, always in the same order. */
function TypeMix({ rows }: { rows: { key: string; count: number }[] }) {
  const total = rows.reduce((a, r) => a + r.count, 0);
  const summary = rows.map((r) => `${typeLabel(r.key)} ${r.count}`).join(", ");
  return (
    <section className="panel hv-panel">
      <h2>Live Dashboards by Type</h2>
      {total === 0 ? <p className="muted">No dashboards are live yet.</p> : (
        <>
          <div className="hv-stack" role="img" aria-label={`Live dashboards by type: ${summary}`}>
            {rows.filter((r) => r.count > 0).map((r) => (
              <span key={r.key} className={`hv-seg ${TYPES[r.key]?.className ?? ""}`} style={{ flexGrow: r.count }} title={`${typeLabel(r.key)}: ${r.count} (${Math.round((r.count / total) * 100)}%)`} />
            ))}
          </div>
          <ul className="hv-legend">
            {rows.map((r) => (
              <li key={r.key}>
                <span className={`hv-dot ${TYPES[r.key]?.className ?? ""}`} aria-hidden="true" />
                <span className="hv-legend-name">{typeLabel(r.key)}</span>
                <strong>{n(r.count)}</strong>
                <span className="muted small">{total ? `${Math.round((r.count / total) * 100)}%` : ""}</span>
              </li>
            ))}
          </ul>
        </>
      )}
    </section>
  );
}

/** Magnitude: one hue, longest bar first, with the number printed beside each. */
function BarList({ rows, empty }: { rows: { key: string; label: string; sub?: string; value: number; to?: string }[]; empty: string }) {
  const max = Math.max(1, ...rows.map((r) => r.value));
  if (rows.length === 0) return <p className="muted">{empty}</p>;
  return (
    <ul className="hv-bars">
      {rows.map((r) => (
        <li key={r.key} title={`${r.label}: ${n(r.value)}`}>
          <span className="hv-bar-name">{r.to ? <Link to={r.to}>{r.label}</Link> : r.label}{r.sub && <span className="muted small"> {r.sub}</span>}</span>
          <span className="hv-track"><span className="hv-fill" style={{ width: `${Math.max(2, (r.value / max) * 100)}%` }} /></span>
          <strong className="hv-value">{n(r.value)}</strong>
        </li>
      ))}
    </ul>
  );
}

function TopDashboards({ rows, days }: { rows: { id: number; name: string; type: string; views: number }[]; days: number }) {
  return (
    <section className="panel hv-panel">
      <h2>Most Opened, Last {days} Days</h2>
      <BarList empty="Nobody has opened a dashboard in this period yet." rows={rows.map((r) => ({ key: String(r.id), label: r.name, sub: typeLabel(r.type), value: r.views, to: `/dashboards/${r.id}` }))} />
    </section>
  );
}

function PeopleMix({ users }: { users: NonNullable<Home["users"]> }) {
  const rows = [
    { key: "i", label: "Internal, active", value: users.activeInternal },
    { key: "e", label: "External, active", value: users.activeExternal },
    { key: "s", label: "Setup pending", value: users.setupPending },
    { key: "x", label: "Inactive", value: users.inactive },
  ];
  return (
    <section className="panel hv-panel">
      <h2>People</h2>
      <BarList empty="No users yet." rows={rows} />
      <p className="muted small hv-foot">{n(users.accessGroups)} active access {users.accessGroups === 1 ? "group" : "groups"}.</p>
    </section>
  );
}

function Attention({ items }: { items: Home["attention"] }) {
  return (
    <section className="panel hv-panel">
      <h2>Needs Attention</h2>
      {items.length === 0 ? <p className="muted">Nothing needs attention right now.</p> : (
        <ul className="hv-list">
          {items.map((a) => <li key={a.key}><Link to={a.link}>{a.text}</Link></li>)}
        </ul>
      )}
    </section>
  );
}

function Recent({ rows }: { rows: NonNullable<Home["recent"]> }) {
  return (
    <section className="panel hv-panel">
      <div className="panel-row"><h2>Recent Activity</h2><Link to="/audit" className="small">Open the Audit Log</Link></div>
      {rows.length === 0 ? <p className="muted">Nothing has been logged yet.</p> : (
        <ul className="hv-list hv-activity">
          {rows.map((r, i) => (
            <li key={i}>
              <span>{r.description}</span>
              <span className="muted small">{r.actor ?? "System"}, {when(r.occurredAtUtc)}</span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
