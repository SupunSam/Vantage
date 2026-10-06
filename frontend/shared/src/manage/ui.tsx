import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { Icon } from "../Icon";
import { ApiError } from "../api";
import { usePanelPosition } from "../Tip";

export { useApi } from "../query";

export function errorText(e: unknown) {
  return e instanceof ApiError ? e.message : e instanceof Error ? e.message : String(e);
}

export function Modal({ title, onClose, children, wide }: { title: string; onClose: () => void; children: ReactNode; wide?: boolean }) {
  const ref = useRef<HTMLDialogElement>(null);
  const [closing, setClosing] = useState(false);
  useEffect(() => {
    const d = ref.current;
    if (d && !d.open) d.showModal();
    return () => d?.close();
  }, []);
  // The window eases out before it is taken away; with reduced motion it goes at once.
  const close = useCallback(() => {
    if (closing) return;
    if (window.matchMedia("(prefers-reduced-motion: reduce)").matches) { onClose(); return; }
    setClosing(true);
    window.setTimeout(onClose, 150);
  }, [closing, onClose]);
  return (
    <dialog ref={ref} className={`modal ${wide ? "modal-wide" : ""} ${closing ? "modal-closing" : ""}`} onCancel={(e) => { e.preventDefault(); close(); }}>
      <div className="modal-head">
        <h2>{title}</h2>
        <button type="button" className="modal-x" onClick={close} aria-label="Close" title="Close"><Icon name="close" size={20} /></button>
      </div>
      <div className="modal-body">{children}</div>
    </dialog>
  );
}

export function Pill({ tone, children }: { tone: "ok" | "warn" | "bad" | "neutral"; children: ReactNode }) {
  return <span className={`pill pill-${tone}`}>{children}</span>;
}

export function statusTone(status: string): "ok" | "warn" | "bad" | "neutral" {
  if (["Active", "Pass", "Passed"].includes(status)) return "ok";
  if (["Publishing", "PendingSetup", "Draft", "Warn"].includes(status)) return "warn";
  if (["Failed", "Inactive", "Fail", "Retired"].includes(status)) return "bad";
  return "neutral";
}

const statusLabel: Record<string, string> = { PendingSetup: "Setup pending" };
export function StatusPill({ status }: { status: string }) {
  return <Pill tone={statusTone(status)}>{statusLabel[status] ?? status}</Pill>;
}

export function when(iso: string | null | undefined) {
  if (!iso) return "–";
  const d = new Date(iso.endsWith("Z") || iso.includes("+") ? iso : iso + "Z");
  return d.toLocaleString(undefined, { day: "numeric", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit" });
}

export function Notice({ tone, children }: { tone?: "ok" | "error"; children: ReactNode }) {
  return <p className={`notice ${tone === "ok" ? "notice-ok" : tone === "error" ? "notice-error" : ""}`}>{children}</p>;
}

/** A button that opens a small menu of actions, closed by clicking elsewhere or Escape. */
export function MenuButton({ label, items, primary, align = "right" }: {
  label: ReactNode;
  items: { label: ReactNode; hint?: string; onSelect: () => void; disabled?: boolean }[];
  primary?: boolean;
  align?: "left" | "right";
}) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const button = useRef<HTMLButtonElement>(null);
  const panel = useRef<HTMLDivElement>(null);
  const pos = usePanelPosition(open, button, panel, align);
  useEffect(() => {
    if (!open) return;
    const down = (e: MouseEvent) => !ref.current?.contains(e.target as Node) && setOpen(false);
    const key = (e: KeyboardEvent) => e.key === "Escape" && setOpen(false);
    document.addEventListener("mousedown", down);
    document.addEventListener("keydown", key);
    return () => {
      document.removeEventListener("mousedown", down);
      document.removeEventListener("keydown", key);
    };
  }, [open]);
  return (
    <div className="menu" ref={ref}>
      <button ref={button} type="button" className={`btn ${primary ? "btn-primary" : ""}`} aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen(!open)}>
        {label}
        <Icon name="chevronDown" size={16} />
      </button>
      {open && (
        <div ref={panel} className={`menu-panel menu-${align}`} role="menu" style={pos}>
          {items.map((it, i) => (
            <button key={i} type="button" role="menuitem" className="menu-item" disabled={it.disabled} onClick={() => { setOpen(false); it.onSelect(); }}>
              <span>{it.label}</span>
              {it.hint && <small>{it.hint}</small>}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- Sorting

export type SortState = { key: string; desc: boolean };

/**
 * Click-to-sort for a list held in the browser. `accessors` says what each column sorts on; text sorts without regard to case,
 * and empty values always go last.
 */
export function useSort<T>(rows: readonly T[], accessors: Record<string, (row: T) => string | number | null | undefined>, initial?: SortState) {
  const [sort, setSort] = useState<SortState | null>(initial ?? null);
  const sorted = useMemo(() => {
    if (!sort) return [...rows];
    const get = accessors[sort.key];
    if (!get) return [...rows];
    const dir = sort.desc ? -1 : 1;
    return [...rows].sort((a, b) => {
      const x = get(a), y = get(b);
      const xe = x === null || x === undefined || x === "", ye = y === null || y === undefined || y === "";
      if (xe || ye) return xe === ye ? 0 : xe ? 1 : -1;
      return (typeof x === "number" && typeof y === "number" ? x - y : String(x).localeCompare(String(y), undefined, { sensitivity: "base", numeric: true })) * dir;
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rows, sort]);
  const toggle = useCallback((key: string) => setSort((s) => (s?.key === key ? { key, desc: !s.desc } : { key, desc: false })), []);
  return { sorted, sort, toggle };
}

/** A column heading that sorts the table when clicked. */
export function SortHeader({ label, k, sort, onSort, className }: { label: ReactNode; k: string; sort: SortState | null; onSort: (key: string) => void; className?: string }) {
  const on = sort?.key === k;
  return (
    <th className={`th-has-sort ${className ?? ""}`} aria-sort={on ? (sort!.desc ? "descending" : "ascending") : "none"}>
      <button type="button" className="th-sort" onClick={() => onSort(k)}>
        {label}
        <Icon name={on ? (sort!.desc ? "arrowDown" : "arrowUp") : "arrowUp"} size={14} className={on ? "th-arrow th-arrow-on" : "th-arrow"} />
      </button>
    </th>
  );
}

// ---------------------------------------------------------------- Row menu

/** The "⋯" at the end of a table row, with that row's actions. */
export function RowMenu({ label, items }: { label: string; items: { label: ReactNode; onSelect: () => void; disabled?: boolean; danger?: boolean }[] }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const button = useRef<HTMLButtonElement>(null);
  const panel = useRef<HTMLDivElement>(null);
  const pos = usePanelPosition(open, button, panel, "right");
  useEffect(() => {
    if (!open) return;
    const down = (e: MouseEvent) => !ref.current?.contains(e.target as Node) && setOpen(false);
    const key = (e: KeyboardEvent) => e.key === "Escape" && setOpen(false);
    document.addEventListener("mousedown", down);
    document.addEventListener("keydown", key);
    return () => { document.removeEventListener("mousedown", down); document.removeEventListener("keydown", key); };
  }, [open]);
  return (
    <div className="menu row-menu" ref={ref} onClick={(e) => e.stopPropagation()}>
      <button ref={button} type="button" className="icon-btn icon-btn-sm" aria-haspopup="menu" aria-expanded={open} aria-label={label} title={label} onClick={() => setOpen(!open)}>
        <Icon name="more" size={20} />
      </button>
      {open && (
        <div ref={panel} className="menu-panel menu-right" role="menu" style={pos}>
          {items.map((it, i) => (
            <button key={i} type="button" role="menuitem" className={`menu-item ${it.danger ? "menu-item-danger" : ""}`} disabled={it.disabled} onClick={() => { setOpen(false); it.onSelect(); }}>
              <span>{it.label}</span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- Drawer

/** A panel that slides in from the right for looking at one thing without leaving the page. Escape and the cross close it. */
export function Drawer({ title, subtitle, onClose, children }: { title: string; subtitle?: ReactNode; onClose: () => void; children: ReactNode }) {
  const ref = useRef<HTMLDialogElement>(null);
  const [closing, setClosing] = useState(false);
  useEffect(() => {
    const d = ref.current;
    if (d && !d.open) d.showModal();
    return () => d?.close();
  }, []);
  const close = useCallback(() => {
    if (closing) return;
    if (window.matchMedia("(prefers-reduced-motion: reduce)").matches) { onClose(); return; }
    setClosing(true);
    window.setTimeout(onClose, 200);
  }, [closing, onClose]);
  return (
    <dialog ref={ref} className={`drawer ${closing ? "drawer-closing" : ""}`} onCancel={(e) => { e.preventDefault(); close(); }}
      onClick={(e) => { if (e.target === ref.current) close(); }}>
      <div className="drawer-head">
        <div>
          <h2>{title}</h2>
          {subtitle && <p className="muted small">{subtitle}</p>}
        </div>
        <button type="button" className="modal-x" onClick={close} aria-label="Close" title="Close"><Icon name="close" size={20} /></button>
      </div>
      <div className="drawer-body">{children}</div>
    </dialog>
  );
}

// ---------------------------------------------------------------- Avatar

const AVATAR_HUES = [208, 262, 150, 24, 340, 186, 42, 290];

/** A round badge with a person's initials, in a colour that stays the same for that person. */
export function Avatar({ name, email, size = 32 }: { name?: string | null; email?: string; size?: number }) {
  const text = (name ?? "").trim() || (email ?? "?");
  const parts = text.split(/[\s@._-]+/).filter(Boolean);
  const initials = ((parts[0]?.[0] ?? "?") + (parts.length > 1 ? parts[parts.length - 1][0] : "")).toUpperCase();
  let h = 0;
  for (const c of (email ?? text)) h = (h * 31 + c.charCodeAt(0)) >>> 0;
  const hue = AVATAR_HUES[h % AVATAR_HUES.length];
  return (
    <span className="avatar-chip" aria-hidden="true" style={{ width: size, height: size, fontSize: Math.round(size * 0.38), background: `hsl(${hue} 55% 92%)`, color: `hsl(${hue} 50% 30%)` }}>{initials}</span>
  );
}

// ---------------------------------------------------------------- File picker

/** A drop zone in place of the browser's "Choose File" button. Drag a file onto it or click it; the chosen name and size show inside. */
export function FilePicker({ file, onChange, accept, required, hint, label = "Choose a file" }: {
  file: File | null; onChange: (file: File | null) => void; accept?: string; required?: boolean; hint?: ReactNode; label?: string;
}) {
  const [over, setOver] = useState(false);
  const input = useRef<HTMLInputElement>(null);
  const size = file ? (file.size >= 1048576 ? `${(file.size / 1048576).toFixed(1)} MB` : `${Math.max(1, Math.round(file.size / 1024))} KB`) : "";
  return (
    <div className={`file-picker ${over ? "file-picker-over" : ""} ${file ? "file-picker-has" : ""}`}
      onDragEnter={() => setOver(true)} onDragLeave={() => setOver(false)} onDrop={() => setOver(false)}>
      <input ref={input} type="file" accept={accept} required={required} aria-label={label}
        onChange={(e) => onChange(e.target.files?.[0] ?? null)} />
      <span className="file-picker-icon" aria-hidden="true"><Icon name={file ? "check" : "upload"} size={22} /></span>
      <span className="file-picker-text">
        {file ? <><strong>{file.name}</strong><small>{size}</small></> : <><strong>{label}</strong><small>or drag it here{hint ? <> · {hint}</> : null}</small></>}
      </span>
      {file && (
        <button type="button" className="file-picker-clear" aria-label="Remove the chosen file" title="Remove"
          onClick={(e) => { e.preventDefault(); if (input.current) input.current.value = ""; onChange(null); }}><Icon name="close" size={16} /></button>
      )}
    </div>
  );
}
