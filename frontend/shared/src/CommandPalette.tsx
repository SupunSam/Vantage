import { useEffect, useMemo, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { Icon } from "./Icon";
import { useApi } from "./query";
import { setThemePreference, useTheme } from "./theme";

export type PaletteLink = { label: string; to: string; icon: string; group: string };
export type DashboardSearch = { path: string; open: (d: { id: number }) => string };

type Command = { id: string; label: string; hint?: string; icon: string; group: string; run: () => void };

/**
 * Ctrl+K (or Cmd+K): jump to any page or dashboard by typing, change the theme, or log out, without the mouse.
 * Arrow keys move, Enter chooses, Escape closes. Dashboards are read when it opens (and cached after that).
 */
export function CommandPalette({ open, onClose, links, dashboards, otherPortal, onSignOut }: {
  open: boolean;
  onClose: () => void;
  links: PaletteLink[];
  dashboards?: DashboardSearch;
  otherPortal?: { label: string; href: string };
  onSignOut: () => void;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  const input = useRef<HTMLInputElement>(null);
  const navigate = useNavigate();
  const { preference } = useTheme();
  const [q, setQ] = useState("");
  const [active, setActive] = useState(0);
  const { data: list } = useApi<{ id: number; name: string; type?: string; code?: string; description?: string | null }[]>(open && dashboards ? dashboards.path : null);

  useEffect(() => {
    const d = ref.current;
    if (!d) return;
    if (open && !d.open) { d.showModal(); setQ(""); setActive(0); window.setTimeout(() => input.current?.focus(), 0); }
    if (!open && d.open) d.close();
  }, [open]);

  const commands = useMemo<Command[]>(() => {
    const go = (to: string) => () => { onClose(); navigate(to); };
    const out: Command[] = links.map((l) => ({ id: `page:${l.to}`, label: l.label, hint: l.group, icon: l.icon, group: "Go to", run: go(l.to) }));
    for (const d of list ?? []) {
      out.push({ id: `dash:${d.id}`, label: d.name, hint: [d.type, d.code].filter(Boolean).join(" · "), icon: "dashboards", group: "Dashboards", run: go(dashboards!.open(d)) });
    }
    const theme = (p: "system" | "light" | "dark", label: string) => ({ id: `theme:${p}`, label: `Theme: ${label}`, hint: preference === p ? "Current" : undefined, icon: "settings", group: "Actions", run: () => { setThemePreference(p); onClose(); } });
    out.push(theme("light", "Light"), theme("dark", "Dark"), theme("system", "Match My Computer"));
    if (otherPortal) out.push({ id: "other", label: otherPortal.label, icon: "external", group: "Actions", run: () => { window.location.href = otherPortal.href; } });
    out.push({ id: "signout", label: "Log Out", icon: "logout", group: "Actions", run: () => { onClose(); onSignOut(); } });
    return out;
  }, [links, list, dashboards, navigate, onClose, otherPortal, onSignOut, preference]);

  const results = useMemo(() => {
    const words = q.trim().toLowerCase().split(/\s+/).filter(Boolean);
    if (words.length === 0) return commands.filter((c) => c.group !== "Dashboards").slice(0, 12);
    const hit = commands.filter((c) => words.every((w) => `${c.label} ${c.hint ?? ""}`.toLowerCase().includes(w)));
    // Names that start with what was typed come first.
    const order = ["Go to", "Dashboards", "Actions"];
    const starts = (c: Command) => Number(c.label.toLowerCase().startsWith(words[0]!));
    return hit.sort((a, b) => order.indexOf(a.group) - order.indexOf(b.group) || starts(b) - starts(a)).slice(0, 14);
  }, [commands, q]);

  useEffect(() => setActive(0), [q]);
  useEffect(() => { document.getElementById(`cmd-${active}`)?.scrollIntoView({ block: "nearest" }); }, [active]);

  function key(e: React.KeyboardEvent) {
    if (e.key === "ArrowDown") { e.preventDefault(); setActive((a) => Math.min(a + 1, results.length - 1)); }
    else if (e.key === "ArrowUp") { e.preventDefault(); setActive((a) => Math.max(a - 1, 0)); }
    else if (e.key === "Enter") { e.preventDefault(); results[active]?.run(); }
  }

  let lastGroup = "";
  return (
    <dialog ref={ref} className="palette" aria-label="Quick search" onCancel={(e) => { e.preventDefault(); onClose(); }}
      onClick={(e) => { if (e.target === ref.current) onClose(); }}>
      <div className="palette-box" onKeyDown={key}>
        <div className="palette-input">
          <Icon name="search" size={20} />
          <input ref={input} value={q} onChange={(e) => setQ(e.target.value)} placeholder="Search pages and dashboards, or type a command"
            role="combobox" aria-expanded="true" aria-controls="cmd-list" aria-activedescendant={results.length ? `cmd-${active}` : undefined} aria-label="Search pages and dashboards" />
          <kbd>Esc</kbd>
        </div>
        <ul id="cmd-list" className="palette-list" role="listbox">
          {results.length === 0 && <li className="palette-empty">Nothing matches “{q}”.</li>}
          {results.map((c, i) => {
            const head = c.group !== lastGroup ? (lastGroup = c.group) : null;
            return (
              <li key={c.id} role="presentation">
                {head && <p className="palette-group">{head}</p>}
                <button id={`cmd-${i}`} type="button" role="option" aria-selected={i === active} className={`palette-item ${i === active ? "on" : ""}`}
                  onMouseMove={() => setActive(i)} onClick={c.run}>
                  <span className="palette-icon" aria-hidden="true"><Icon name={c.icon} size={18} /></span>
                  <span className="palette-label">{c.label}</span>
                  {c.hint && <span className="palette-hint">{c.hint}</span>}
                </button>
              </li>
            );
          })}
        </ul>
        <p className="palette-foot"><kbd>↑</kbd><kbd>↓</kbd> to move <kbd>Enter</kbd> to open <kbd>Ctrl</kbd><kbd>K</kbd> anywhere</p>
      </div>
    </dialog>
  );
}
