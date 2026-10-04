import { useEffect, useState } from "react";

export type PagerState = { page: number; pages: number; size: number; total: number; onPage: (page: number) => void; onSize: (size: number) => void };

/**
 * Pages a list that is already in the browser. Starts on page 1 again whenever `resetKey` changes (for example when a
 * search or filter changes), and stays on a valid page when the list gets shorter.
 */
export function usePaged<T>(items: readonly T[], initialSize = 10, resetKey?: unknown) {
  const [page, setPage] = useState(1);
  const [size, setSize] = useState(initialSize);
  useEffect(() => setPage(1), [resetKey]);
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
