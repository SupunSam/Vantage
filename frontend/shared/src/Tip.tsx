import { useCallback, useEffect, useId, useLayoutEffect, useRef, useState, type CSSProperties, type ReactNode, type RefObject } from "react";
import { Icon } from "./Icon";

/**
 * Where to put a panel that opens from an element: fixed to the window (so a table that scrolls can't cut it off),
 * under the element when there is room and above it when there isn't, and kept inside the window sideways.
 */
export function usePanelPosition(open: boolean, anchor: RefObject<HTMLElement | null>, panel: RefObject<HTMLElement | null>, align: "left" | "right" | "center" = "left"): CSSProperties {
  const [style, setStyle] = useState<CSSProperties>({ position: "fixed", top: 0, left: 0, visibility: "hidden" });
  const place = useCallback(() => {
    const a = anchor.current, p = panel.current;
    if (!a || !p) return;
    const r = a.getBoundingClientRect();
    const w = p.offsetWidth, h = p.offsetHeight;
    const gap = 6, pad = 8;
    let left = align === "right" ? r.right - w : align === "center" ? r.left + r.width / 2 - w / 2 : r.left;
    left = Math.max(pad, Math.min(left, window.innerWidth - w - pad));
    const below = r.bottom + gap;
    const top = below + h > window.innerHeight - pad && r.top - gap - h > pad ? r.top - gap - h : below;
    setStyle({ position: "fixed", top, left, right: "auto", bottom: "auto", visibility: "visible" });
  }, [anchor, panel, align]);
  useLayoutEffect(() => { if (open) place(); }, [open, place]);
  useEffect(() => {
    if (!open) return;
    window.addEventListener("scroll", place, true);
    window.addEventListener("resize", place);
    return () => { window.removeEventListener("scroll", place, true); window.removeEventListener("resize", place); };
  }, [open, place]);
  return style;
}

function TipPanel({ id, anchor, children, open }: { id: string; anchor: RefObject<HTMLElement | null>; children: ReactNode; open: boolean }) {
  const panel = useRef<HTMLDivElement>(null);
  const style = usePanelPosition(open, anchor, panel, "center");
  if (!open) return null;
  return <div ref={panel} id={id} role="tooltip" className="tip-panel" style={style}>{children}</div>;
}

/**
 * A small (i) that explains something on hover, keyboard focus or tap. Use it for the longer explanations
 * that would otherwise crowd the page.
 */
export function InfoTip({ children, label = "More information" }: { children: ReactNode; label?: string }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLButtonElement>(null);
  const id = useId();
  useEffect(() => {
    if (!open) return;
    const key = (e: KeyboardEvent) => e.key === "Escape" && setOpen(false);
    const down = (e: MouseEvent) => !ref.current?.contains(e.target as Node) && setOpen(false);
    document.addEventListener("keydown", key);
    document.addEventListener("mousedown", down);
    return () => { document.removeEventListener("keydown", key); document.removeEventListener("mousedown", down); };
  }, [open]);
  return (
    <span className="info-tip-wrap">
      <button ref={ref} type="button" className="info-tip" aria-label={label} aria-describedby={open ? id : undefined} aria-expanded={open}
        onMouseEnter={() => setOpen(true)} onMouseLeave={() => setOpen(false)} onFocus={() => setOpen(true)} onBlur={() => setOpen(false)}
        onClick={(e) => { e.stopPropagation(); setOpen((o) => !o); }}>
        <Icon name="info" size={16} />
      </button>
      <TipPanel id={id} anchor={ref} open={open}>{children}</TipPanel>
    </span>
  );
}

/** Wraps any text or element so the full wording shows on hover or focus, for things the page only has room to abbreviate. */
export function Tip({ text, children }: { text: ReactNode; children: ReactNode }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLSpanElement>(null);
  const id = useId();
  return (
    <span ref={ref} className="tip-anchor" aria-describedby={open ? id : undefined}
      onMouseEnter={() => setOpen(true)} onMouseLeave={() => setOpen(false)} onFocus={() => setOpen(true)} onBlur={() => setOpen(false)}>
      {children}
      <TipPanel id={id} anchor={ref} open={open}>{text}</TipPanel>
    </span>
  );
}
