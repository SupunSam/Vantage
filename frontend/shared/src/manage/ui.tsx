import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { Icon } from "../Icon";
import { api, ApiError } from "../api";

/** Loads data from the API; `reload` fetches again. */
export function useApi<T>(path: string | null) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [tick, setTick] = useState(0);

  useEffect(() => {
    if (!path) return;
    let cancelled = false;
    setLoading(true);
    api<T>(path)
      .then((d) => {
        if (!cancelled) {
          setData(d);
          setError(null);
        }
      })
      .catch((e) => !cancelled && setError(e instanceof ApiError ? e.message : String(e)))
      .finally(() => !cancelled && setLoading(false));
    return () => {
      cancelled = true;
    };
  }, [path, tick]);

  const reload = useCallback(() => setTick((n) => n + 1), []);
  return { data, error, loading, reload };
}

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
      <button type="button" className={`btn ${primary ? "btn-primary" : ""}`} aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen(!open)}>
        {label}
        <Icon name="chevronDown" size={16} />
      </button>
      {open && (
        <div className={`menu-panel menu-${align}`} role="menu">
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
