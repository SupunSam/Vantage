import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type CSSProperties, type ReactNode } from "react";
import { Icon } from "./Icon";

// ---------------------------------------------------------------- Loading

/** A grey block that shimmers while content loads. Sizes are CSS values. */
export function Skeleton({ w = "100%", h = 14, r, className = "", style }: { w?: number | string; h?: number | string; r?: number | string; className?: string; style?: CSSProperties }) {
  return <span className={`sk ${className}`} aria-hidden="true" style={{ width: w, height: h, borderRadius: r, ...style }} />;
}

export type SkeletonKind = "page" | "cards" | "table" | "tiles" | "detail";

/**
 * What a page looks like while it loads: the same shape as the real thing, so nothing jumps when the data arrives.
 * Pass the kind that matches the page (cards, a table, stat tiles, a detail page or just a form).
 */
export function PageSkeleton({ kind = "page", label = "Loading", head = true }: { kind?: SkeletonKind; label?: string; head?: boolean }) {
  return (
    <div className="sk-page" role="status" aria-live="polite" aria-busy="true">
      <span className="visually-hidden">{label}…</span>
      {head && (
        <div className="sk-head">
          <Skeleton w={220} h={28} />
          <Skeleton w="min(520px, 70%)" h={14} />
        </div>
      )}
      {kind === "page" && (
        <>
          <Skeleton h={180} r={8} />
          <Skeleton h={120} r={8} />
        </>
      )}
      {kind === "tiles" && (
        <>
          <div className="sk-tiles">{[0, 1, 2, 3].map((i) => <Skeleton key={i} h={82} r={8} />)}</div>
          <Skeleton h={220} r={8} />
        </>
      )}
      {kind === "table" && (
        <>
          <div className="sk-filters"><Skeleton w={320} h={40} r={6} /><Skeleton w={140} h={40} r={6} /><Skeleton w={140} h={40} r={6} /></div>
          <div className="sk-table">
            {[0, 1, 2, 3, 4, 5, 6].map((i) => (
              <div className="sk-row" key={i}><Skeleton w={96} h={54} r={6} /><div className="sk-col"><Skeleton w="45%" h={14} /><Skeleton w="25%" h={11} /></div><Skeleton w={80} h={14} /><Skeleton w={70} h={22} r={11} /></div>
            ))}
          </div>
        </>
      )}
      {kind === "cards" && (
        <>
          <div className="sk-filters"><Skeleton w={320} h={44} r={8} /><Skeleton w={200} h={44} r={8} /></div>
          <div className="sk-grid">
            {[0, 1, 2, 3, 4, 5].map((i) => (
              <div className="sk-card" key={i}><Skeleton h={150} r={0} /><div className="sk-card-body"><Skeleton w="40%" h={11} /><Skeleton w="75%" h={16} /><Skeleton w="90%" h={12} /></div></div>
            ))}
          </div>
        </>
      )}
      {kind === "detail" && (
        <>
          <div className="sk-hero"><Skeleton w={224} h={126} r={8} /><div className="sk-col"><Skeleton w="50%" h={26} /><Skeleton w="20%" h={14} /><Skeleton w="35%" h={22} r={11} /></div></div>
          <Skeleton w="60%" h={36} r={6} />
          <Skeleton h={240} r={8} />
        </>
      )}
    </div>
  );
}

/** The frame a dashboard loads into, at the same 16:9 shape as the real one. */
export function FrameSkeleton() {
  return <div className="sk sk-frame" role="status" aria-label="Loading the dashboard" />;
}

// ---------------------------------------------------------------- Empty and error

/** "Nothing here yet": an icon, a short line, and one thing to do about it. */
export function EmptyState({ icon = "dashboards", title, children, action }: { icon?: string; title: string; children?: ReactNode; action?: ReactNode }) {
  return (
    <div className="empty empty-state">
      <span className="empty-icon" aria-hidden="true"><Icon name={icon} size={26} /></span>
      <h2>{title}</h2>
      {children && <p>{children}</p>}
      {action && <div className="empty-action">{action}</div>}
    </div>
  );
}

/** "Something went wrong": what happened, and a way to try again. */
export function ErrorState({ title = "That Didn't Load", children, onRetry }: { title?: string; children?: ReactNode; onRetry?: () => void }) {
  return (
    <div className="empty empty-state empty-error" role="alert">
      <span className="empty-icon" aria-hidden="true"><Icon name="close" size={26} /></span>
      <h2>{title}</h2>
      {children && <p>{children}</p>}
      {onRetry && <div className="empty-action"><button type="button" className="btn btn-primary" onClick={onRetry}>Try Again</button></div>}
    </div>
  );
}

// ---------------------------------------------------------------- Confirm

export type ConfirmOptions = {
  title?: string;
  message: ReactNode;
  confirmLabel?: string;
  cancelLabel?: string;
  /** A red confirm button, for deleting or removing things. */
  danger?: boolean;
};

type Confirmer = (options: ConfirmOptions) => Promise<boolean>;
const ConfirmContext = createContext<Confirmer>(() => Promise.resolve(false));

/** Our own "Are you sure?" dialog, in place of the browser's. `const confirm = useConfirm(); if (await confirm({ ... })) ...` */
export function useConfirm() {
  return useContext(ConfirmContext);
}

export function ConfirmProvider({ children }: { children: ReactNode }) {
  const [open, setOpen] = useState<(ConfirmOptions & { resolve: (ok: boolean) => void }) | null>(null);
  const confirm = useCallback<Confirmer>((options) => new Promise<boolean>((resolve) => setOpen({ ...options, resolve })), []);
  const answer = useCallback((ok: boolean) => { setOpen((cur) => { cur?.resolve(ok); return null; }); }, []);
  const value = useMemo(() => confirm, [confirm]);
  return (
    <ConfirmContext.Provider value={value}>
      {children}
      {open && <ConfirmDialog options={open} onAnswer={answer} />}
    </ConfirmContext.Provider>
  );
}

function ConfirmDialog({ options, onAnswer }: { options: ConfirmOptions; onAnswer: (ok: boolean) => void }) {
  const ref = useRef<HTMLDialogElement>(null);
  const confirmButton = useRef<HTMLButtonElement>(null);
  const opener = useRef<Element | null>(document.activeElement);
  useEffect(() => {
    const d = ref.current;
    if (d && !d.open) d.showModal();
    (options.danger ? ref.current?.querySelector<HTMLButtonElement>("[data-cancel]") : confirmButton.current)?.focus();
    const back = opener.current;
    return () => { d?.close(); (back as HTMLElement | null)?.focus?.(); };
  }, [options.danger]);
  return (
    <dialog ref={ref} className="confirm" aria-labelledby="confirm-title" onCancel={(e) => { e.preventDefault(); onAnswer(false); }}>
      <div className="confirm-body">
        <span className={`confirm-icon ${options.danger ? "confirm-icon-danger" : ""}`} aria-hidden="true"><Icon name={options.danger ? "trash" : "shield"} size={22} /></span>
        <div>
          <h2 id="confirm-title">{options.title ?? (options.danger ? "Are You Sure?" : "Please Confirm")}</h2>
          <p>{options.message}</p>
        </div>
      </div>
      <div className="confirm-actions">
        <button type="button" className="btn" data-cancel onClick={() => onAnswer(false)}>{options.cancelLabel ?? "Cancel"}</button>
        <button ref={confirmButton} type="button" className={`btn ${options.danger ? "btn-danger" : "btn-primary"}`} onClick={() => onAnswer(true)}>{options.confirmLabel ?? (options.danger ? "Delete" : "Confirm")}</button>
      </div>
    </dialog>
  );
}
