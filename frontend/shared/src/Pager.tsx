import { useEffect, useState, type ReactNode } from "react";

export type PagerState = { page: number; pages: number; size: number; total: number; onPage: (page: number) => void; onSize: (size: number) => void };

/**
 * Pages a list that is already in the browser. Starts on page 1 again whenever `resetKey` changes (for example when a
 * search or filter changes), and stays on a valid page when the list gets shorter.
 */
export function usePaged<T>(items: readonly T[], initialSize = 10, resetKey?: unknown) {
  const [page, setPage] = useState(1);
  const [size, setSize] = useState(initialSize);
  useEffect(() => setPage(1), [resetKey]);
  useEffect(() => setSize(initialSize), [initialSize]); // the configured default arrives after the first render
  const pages = Math.max(1, Math.ceil(items.length / size));
  const current = Math.min(page, pages);
  const pager: PagerState = { page: current, pages, size, total: items.length, onPage: setPage, onSize: (n) => { setSize(n); setPage(1); } };
  return { rows: items.slice((current - 1) * size, current * size), pager };
}

/** "Showing 11 to 20 of 56", rows per page, and Previous / Next. Hidden while everything fits on the first page. */
export function Pager({ page, pages, size, total, onPage, onSize, sizes = [10, 25, 50, 100] }: PagerState & { sizes?: number[] }) {
  if (total <= Math.min(...sizes)) return null;
  const from = (page - 1) * size + 1;
  const to = Math.min(total, page * size);
  return (
    <div className="pager-bar" role="navigation" aria-label="Pages">
      <span className="muted small">Showing {from} to {to} of {total.toLocaleString()}</span>
      <span className="pager-controls">
        <label className="small muted">
          Rows per page{" "}
          <select value={size} onChange={(e) => onSize(Number(e.target.value))}>
            {sizes.map((s) => <option key={s} value={s}>{s}</option>)}
          </select>
        </label>
        <button type="button" className="btn" disabled={page <= 1} onClick={() => onPage(1)} aria-label="First page">First</button>
        <button type="button" className="btn" disabled={page <= 1} onClick={() => onPage(page - 1)}>Previous</button>
        <span className="small">Page {page} of {pages}</span>
        <button type="button" className="btn" disabled={page >= pages} onClick={() => onPage(page + 1)}>Next</button>
        <button type="button" className="btn" disabled={page >= pages} onClick={() => onPage(pages)} aria-label="Last page">Last</button>
      </span>
    </div>
  );
}

/** 1 … 4 5 [6] 7 8 … 12: the page numbers to show around the current page. */
function pageWindow(page: number, pages: number): (number | "…")[] {
  if (pages <= 7) return Array.from({ length: pages }, (_, i) => i + 1);
  const out: (number | "…")[] = [1];
  const from = Math.max(2, page - 1), to = Math.min(pages - 1, page + 1);
  if (from > 2) out.push("…");
  for (let i = from; i <= to; i++) out.push(i);
  if (to < pages - 1) out.push("…");
  out.push(pages);
  return out;
}

/**
 * A table's card with its paging built in: how many rows to show sits above the rows, and the page buttons sit below them,
 * so the table and its paging read as one thing. Put the table (inside its usual scrolling wrapper) in as children.
 * Nothing shows above or below when everything fits on one page.
 */
export function GridFrame({ pager, sizes = [10, 25, 50, 100], children }: { pager: PagerState; sizes?: number[]; children: ReactNode }) {
  const { page, pages, size, total, onPage, onSize } = pager;
  const choosable = total > Math.min(...sizes);
  const from = Math.min(total, (page - 1) * size + 1);
  const to = Math.min(total, page * size);
  return (
    <div className="grid-frame">
      <div className="grid-frame-top">
        <span className="muted small">{total === 0 ? "Nothing to show" : total <= size && page === 1 ? `${total.toLocaleString()} ${total === 1 ? "item" : "items"}` : `Showing ${from} to ${to} of ${total.toLocaleString()}`}</span>
        {choosable && (
          <label className="small muted grid-size">
            Rows per page
            <select value={size} onChange={(e) => onSize(Number(e.target.value))} aria-label="Rows per page">
              {sizes.map((n) => <option key={n} value={n}>{n}</option>)}
            </select>
          </label>
        )}
      </div>
      {children}
      {pages > 1 && (
        <nav className="grid-frame-bottom" aria-label="Pages">
          <button type="button" className="page-btn" disabled={page <= 1} onClick={() => onPage(page - 1)}>Previous</button>
          <span className="page-nums">
            {pageWindow(page, pages).map((n, i) => n === "…"
              ? <span key={`gap${i}`} className="page-gap" aria-hidden="true">…</span>
              : <button key={n} type="button" className={`page-num ${n === page ? "on" : ""}`} aria-current={n === page ? "page" : undefined} aria-label={`Page ${n}`} onClick={() => onPage(n)}>{n}</button>)}
          </span>
          <button type="button" className="page-btn" disabled={page >= pages} onClick={() => onPage(page + 1)}>Next</button>
        </nav>
      )}
    </div>
  );
}

/** Paging for lists the server pages: the page and size to ask for, and the PagerState to hand to GridFrame once the total is known. */
export function useServerPaging(initialSize: number) {
  const [page, setPage] = useState(1);
  const [size, setSize] = useState(initialSize);
  useEffect(() => setSize(initialSize), [initialSize]); // the configured default arrives after the first render
  const pagerFor = (total: number): PagerState => ({
    page, pages: Math.max(1, Math.ceil(total / size)), size, total, onPage: setPage, onSize: (n) => { setSize(n); setPage(1); },
  });
  return { page, setPage, size, pagerFor };
}
