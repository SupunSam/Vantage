import { useMemo, useRef, useState, type KeyboardEvent } from "react";
import { GridFrame, usePaged } from "./Pager";

export type TrendPoint = { day: string; views: number; users: number };

type Bar = { label: string; short: string; views: number; users: number };

const W = 800;
const H = 230;
const PAD = { left: 44, right: 8, top: 10, bottom: 26 };
const MAX_BARS = 120;

const fmt = (day: string, opts: Intl.DateTimeFormatOptions) => new Date(`${day}T00:00:00`).toLocaleDateString(undefined, opts);

/** More than 120 days would make bars thinner than a pixel, so long ranges are summed by week. */
function toBars(points: TrendPoint[]): { bars: Bar[]; unit: string } {
  if (points.length <= MAX_BARS) {
    return { unit: "day", bars: points.map((p) => ({ label: fmt(p.day, { weekday: "short", day: "numeric", month: "short", year: "numeric" }), short: fmt(p.day, { day: "numeric", month: "short" }), views: p.views, users: p.users })) };
  }
  const bars: Bar[] = [];
  for (let i = 0; i < points.length; i += 7) {
    const week = points.slice(i, i + 7);
    // People are counted per day, so a week's "people" is the busiest day's count: an honest lower bound, not a sum.
    bars.push({
      label: `Week of ${fmt(week[0].day, { day: "numeric", month: "short", year: "numeric" })}`, short: fmt(week[0].day, { day: "numeric", month: "short" }),
      views: week.reduce((n, p) => n + p.views, 0), users: Math.max(...week.map((p) => p.users)),
    });
  }
  return { bars, unit: "week" };
}

/** A round number at or above the largest value, so the axis ends on a clean tick. */
function niceMax(v: number) {
  if (v <= 4) return 4;
  const pow = 10 ** Math.floor(Math.log10(v));
  const f = v / pow;
  return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10) * pow;
}

/**
 * Views over time as thin columns. One series, so no legend: the title says what it is. Hover or focus a column for its
 * exact numbers (arrow keys move between columns), or switch to the table, which holds every value.
 */
export function TrendChart({ points, title }: { points: TrendPoint[]; title: string }) {
  const { bars, unit } = useMemo(() => toBars(points), [points]);
  const [active, setActive] = useState<number | null>(null);
  const [table, setTable] = useState(false);
  const wrap = useRef<HTMLDivElement>(null);
  const tablePage = usePaged(bars, 15);

  const total = bars.reduce((n, b) => n + b.views, 0);
  const max = niceMax(Math.max(0, ...bars.map((b) => b.views)));
  const innerW = W - PAD.left - PAD.right;
  const innerH = H - PAD.top - PAD.bottom;
  const slot = innerW / Math.max(1, bars.length);
  const barW = Math.max(1, Math.min(24, slot - Math.min(2, slot * 0.3)));
  const y = (v: number) => PAD.top + innerH - (v / max) * innerH;
  const ticks = [0, max / 2, max];
  const labelEvery = Math.max(1, Math.ceil(bars.length / 7));

  function onKey(e: KeyboardEvent) {
    if (e.key === "ArrowRight") { e.preventDefault(); setActive((a) => Math.min(bars.length - 1, (a ?? -1) + 1)); }
    else if (e.key === "ArrowLeft") { e.preventDefault(); setActive((a) => Math.max(0, (a ?? bars.length) - 1)); }
    else if (e.key === "Escape") setActive(null);
  }

  const a = active !== null ? bars[active] : null;
  const tipX = a !== null && active !== null ? ((PAD.left + slot * active + slot / 2) / W) * 100 : 0;

  return (
    <figure className="trend">
      <figcaption className="trend-head">
        <span>{title}</span>
        <button type="button" className="link" onClick={() => setTable(!table)}>{table ? "View as Chart" : "View as Table"}</button>
      </figcaption>

      {total === 0 && !table ? <p className="trend-empty muted">No views in this period.</p> : null}

      {!table ? (
        <div className="trend-plot" ref={wrap} tabIndex={0} onKeyDown={onKey} onBlur={() => setActive(null)} onPointerLeave={() => setActive(null)}
          aria-label={`${title}: ${total.toLocaleString()} views. Use the left and right arrow keys to read each ${unit}.`}>
          <svg key={`${bars.length}:${total}`} viewBox={`0 0 ${W} ${H}`} role="img" aria-hidden="true">
            {ticks.map((t) => (
              <g key={t}>
                <line x1={PAD.left} x2={W - PAD.right} y1={y(t)} y2={y(t)} className="trend-grid" />
                <text x={PAD.left - 8} y={y(t) + 4} textAnchor="end" className="trend-axis">{Math.round(t).toLocaleString()}</text>
              </g>
            ))}
            {bars.map((b, i) => {
              const cx = PAD.left + slot * i + slot / 2;
              const h = (b.views / max) * innerH;
              const r = Math.min(4, barW / 2, h);
              const x = cx - barW / 2;
              const top = y(b.views);
              // Rounded at the data end (the top), square at the baseline.
              const d = b.views > 0 ? `M${x},${top + h} V${top + r} Q${x},${top} ${x + r},${top} H${x + barW - r} Q${x + barW},${top} ${x + barW},${top + r} V${top + h} Z` : "";
              return (
                <g key={i}>
                  {d && <path d={d} className={`trend-bar ${active === i ? "trend-bar-on" : ""}`} style={{ "--i": i } as React.CSSProperties} />}
                  {i % labelEvery === 0 && <text x={cx} y={H - 8} textAnchor="middle" className="trend-axis">{b.short}</text>}
                  <rect x={PAD.left + slot * i} y={PAD.top} width={slot} height={innerH} className="trend-hit" onPointerEnter={() => setActive(i)} onPointerMove={() => setActive(i)} />
                </g>
              );
            })}
          </svg>
          {a && (
            <div className="trend-tip" style={{ left: `${Math.min(86, Math.max(14, tipX))}%` }} role="status">
              <strong>{a.views.toLocaleString()} {a.views === 1 ? "view" : "views"}</strong>
              <span>{a.users.toLocaleString()} {unit === "week" ? "most people in a day" : a.users === 1 ? "person" : "people"}</span>
              <span className="trend-tip-date">{a.label}</span>
            </div>
          )}
        </div>
      ) : (
        <>
          <GridFrame pager={tablePage.pager} sizes={[15, 30, 60]}>
            <div className="requests-table-wrap">
              <table className="requests-table">
                <thead><tr><th>{unit === "week" ? "Week" : "Day"}</th><th className="num">Views</th><th className="num">{unit === "week" ? "Most people in a day" : "People"}</th></tr></thead>
                <tbody>{tablePage.rows.map((b) => <tr key={b.label}><td>{b.label}</td><td className="num">{b.views.toLocaleString()}</td><td className="num">{b.users.toLocaleString()}</td></tr>)}</tbody>
              </table>
            </div>
          </GridFrame>
        </>
      )}
    </figure>
  );
}
