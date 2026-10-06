import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { Icon } from "./Icon";

export type ToastTone = "ok" | "error" | "info";
type ToastItem = { id: number; tone: ToastTone; text: string };
type Toasts = { show: (tone: ToastTone, text: string) => void };

const ToastContext = createContext<Toasts>({ show: () => {} });

const MAX_VISIBLE = 5;

/** Pop-up messages in the bottom right. Every toast stays for `seconds` (Admin Configuration), and waits while the pointer is over it. */
export function ToastProvider({ seconds, children }: { seconds: number; children: ReactNode }) {
  const [items, setItems] = useState<ToastItem[]>([]);
  const next = useRef(1);
  const show = useCallback((tone: ToastTone, text: string) => {
    setItems((all) => [...all, { id: next.current++, tone, text }].slice(-MAX_VISIBLE));
  }, []);
  const dismiss = useCallback((id: number) => setItems((all) => all.filter((t) => t.id !== id)), []);
  const value = useMemo(() => ({ show }), [show]);
  return (
    <ToastContext.Provider value={value}>
      {children}
      <div className="toasts" aria-live="polite">
        {items.map((t) => <ToastCard key={t.id} item={t} seconds={seconds} onDismiss={dismiss} />)}
      </div>
    </ToastContext.Provider>
  );
}

function ToastCard({ item, seconds, onDismiss }: { item: ToastItem; seconds: number; onDismiss: (id: number) => void }) {
  const [paused, setPaused] = useState(false);
  useEffect(() => {
    if (paused) return;
    const timer = window.setTimeout(() => onDismiss(item.id), seconds * 1000);
    return () => window.clearTimeout(timer);
  }, [paused, seconds, item.id, onDismiss]);
  return (
    <div className={`toast toast-${item.tone}`} role={item.tone === "error" ? "alert" : "status"}
      onMouseEnter={() => setPaused(true)} onMouseLeave={() => setPaused(false)}>
      <span className="toast-text">{item.text}</span>
      <button type="button" className="toast-x" aria-label="Dismiss message" title="Dismiss" onClick={() => onDismiss(item.id)}><Icon name="close" size={16} /></button>
    </div>
  );
}

export function useToast() {
  const { show } = useContext(ToastContext);
  return useMemo(() => ({
    ok: (text: string) => show("ok", text),
    error: (text: string) => show("error", text),
    info: (text: string) => show("info", text),
  }), [show]);
}

/**
 * A drop-in for a page's "message" state: call it with `{ ok, text }`, or a plain string (shown as `tone` , success by default),
 * and it pops a toast. `null` does nothing.
 */
export function useFlash(tone: "ok" | "error" = "ok") {
  const { show } = useContext(ToastContext);
  return useCallback((m: string | { ok: boolean; text: string } | null) => {
    if (!m) return;
    if (typeof m === "string") show(tone, m);
    else show(m.ok ? "ok" : "error", m.text);
  }, [show, tone]);
}
