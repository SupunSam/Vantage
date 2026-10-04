import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { NavLink, useLocation, useNavigate } from "react-router-dom";
import { api } from "./api";
import { Icon } from "./Icon";
import { useSession } from "./session";

export type NavItem = { to: string; label: string; icon: string; end?: boolean; soon?: boolean };
export type NavSection = { title?: string; items: NavItem[] };

const COLLAPSE_KEY = "vantage.navCollapsed";

function readCollapsed() {
  try {
    return localStorage.getItem(COLLAPSE_KEY) === "1";
  } catch {
    return false;
  }
}

/**
 * The frame both portals share: a sidebar that collapses to icons (hamburger in the top bar), and a top bar with
 * notifications and the user menu. On narrow screens the sidebar slides over the page instead.
 */
export function AppShell({ portalLabel, sections, otherPortal, children }: {
  portalLabel: string;
  sections: NavSection[];
  otherPortal?: { label: string; href: string };
  children: ReactNode;
}) {
  const { branding } = useSession();
  const [collapsed, setCollapsed] = useState(readCollapsed);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const location = useLocation();

  useEffect(() => setDrawerOpen(false), [location.pathname]);

  function toggle() {
    if (window.matchMedia("(max-width: 900px)").matches) {
      setDrawerOpen((o) => !o);
      return;
    }
    setCollapsed((c) => {
      try {
        localStorage.setItem(COLLAPSE_KEY, c ? "0" : "1");
      } catch {
        /* the choice lasts for this page only */
      }
      return !c;
    });
  }

  return (
    <div className={`app ${collapsed ? "app-collapsed" : ""} ${drawerOpen ? "app-drawer" : ""}`}>
      <aside className="side" aria-label="Main menu">
        <NavLink to="/" className="side-brand" title={branding.portalName ?? "Vantage"}>
          <img src={branding.logoUrl ?? "/brand/logo.svg"} alt="" />
          <span className="side-label">
            <strong>{branding.portalName ?? "Vantage"}</strong>
            <small>{portalLabel}</small>
          </span>
        </NavLink>
        <nav className="side-nav">
          {sections.map((s, i) =>
            s.items.length === 0 ? null : (
              <div key={s.title ?? i} className="side-section">
                {s.title && <p className="side-title side-label">{s.title}</p>}
                {s.items.map((item) =>
                  item.soon ? (
                    <span key={item.label} className="side-link side-soon" title={`${item.label}: coming in a later step`}>
                      <Icon name={item.icon} />
                      <span className="side-label">{item.label}</span>
                    </span>
                  ) : (
                    <NavLink key={item.to} to={item.to} end={item.end} className="side-link" title={collapsed ? item.label : undefined}>
                      <Icon name={item.icon} />
                      <span className="side-label">{item.label}</span>
                    </NavLink>
                  ),
                )}
              </div>
            ),
          )}
        </nav>
      </aside>
      <button type="button" className="side-scrim" aria-label="Close menu" onClick={() => setDrawerOpen(false)} tabIndex={-1} />

      <div className="main">
        <header className="topbar">
          <button type="button" className="icon-btn" onClick={toggle} aria-label={collapsed ? "Expand menu" : "Collapse menu"}>
            <Icon name="menu" />
          </button>
          <div className="topbar-fill" />
          <NotificationBell />
          <UserMenu otherPortal={otherPortal} />
          <LogOutButton />
        </header>
        <main className="content">{children}</main>
      </div>
    </div>
  );
}

/** Closes a popover when the user clicks elsewhere or presses Escape. */
function usePopover() {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => !ref.current?.contains(e.target as Node) && setOpen(false);
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && setOpen(false);
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);
  return { open, setOpen, ref };
}

type NotificationList = { unread: number; items: { id: number; type: string; title: string; body: string | null; link: string | null; createdAtUtc: string; read: boolean }[] };

function NotificationBell() {
  const { open, setOpen, ref } = usePopover();
  const [data, setData] = useState<NotificationList | null>(null);
  const navigate = useNavigate();

  const load = useCallback(() => {
    api<NotificationList>("/api/notifications").then(setData).catch(() => {});
  }, []);

  useEffect(() => {
    load();
    const t = window.setInterval(load, 60_000);
    return () => window.clearInterval(t);
  }, [load]);

  useEffect(() => {
    if (open) load();
  }, [open, load]);

  async function openItem(n: NotificationList["items"][number]) {
    if (!n.read) await api(`/api/notifications/${n.id}/read`, { method: "POST" }).catch(() => {});
    setOpen(false);
    load();
    if (n.link) navigate(n.link);
  }

  async function readAll() {
    await api("/api/notifications/read-all", { method: "POST" }).catch(() => {});
    load();
  }

  const unread = data?.unread ?? 0;
  return (
    <div className="pop" ref={ref}>
      <button type="button" className="icon-btn" aria-label={unread ? `Notifications, ${unread} unread` : "Notifications"} aria-expanded={open} onClick={() => setOpen(!open)}>
        <Icon name="bell" />
        {unread > 0 && <span className="badge">{unread > 99 ? "99+" : unread}</span>}
      </button>
      {open && (
        <div className="pop-panel pop-notes" role="dialog" aria-label="Notifications">
          <div className="pop-head">
            <strong>Notifications</strong>
            {unread > 0 && <button type="button" className="link" onClick={() => void readAll()}>Mark All as Read</button>}
          </div>
          {!data || data.items.length === 0 ? (
            <p className="pop-empty">Nothing yet. You'll hear here about publishing, access requests and reviews.</p>
          ) : (
            <ul className="notes">
              {data.items.map((n) => (
                <li key={n.id}>
                  <button type="button" className={`note ${n.read ? "" : "note-unread"}`} onClick={() => void openItem(n)}>
                    <span className="note-title">{n.title}</span>
                    {n.body && <span className="note-body">{n.body}</span>}
                    <span className="note-when">{ago(n.createdAtUtc)}</span>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </div>
  );
}

function UserMenu({ otherPortal }: { otherPortal?: { label: string; href: string } }) {
  const { me, signOut } = useSession();
  const { open, setOpen, ref } = usePopover();
  if (!me) return null;
  const initials = me.displayName.split(/\s+/).filter(Boolean).slice(0, 2).map((p) => p[0]!.toUpperCase()).join("") || "?";
  const roles = me.roles.filter((r) => r !== "Dashboard User");

  return (
    <div className="pop" ref={ref}>
      <button type="button" className="user-btn" aria-expanded={open} onClick={() => setOpen(!open)}>
        <span className="avatar" aria-hidden="true">{initials}</span>
        <span className="user-name">{me.displayName}</span>
        <Icon name="chevronDown" size={16} />
      </button>
      {open && (
        <div className="pop-panel pop-user" role="menu">
          <div className="pop-user-head">
            <span className="avatar avatar-lg" aria-hidden="true">{initials}</span>
            <span>
              <strong>{me.displayName}</strong>
              <span className="muted small">{me.email}</span>
            </span>
          </div>
          <div className="pop-roles">
            {(roles.length ? roles : ["Dashboard User"]).map((r) => <span key={r} className="role-chip">{r}</span>)}
          </div>
          {otherPortal && (
            <a className="pop-item" role="menuitem" href={otherPortal.href}>
              <Icon name="external" size={18} /> {otherPortal.label}
            </a>
          )}
          <button type="button" className="pop-item" role="menuitem" onClick={signOut}>
            <Icon name="logout" size={18} /> Log Out
          </button>
        </div>
      )}
    </div>
  );
}

/** Always in view, so signing out never needs a search through menus. */
function LogOutButton() {
  const { signOut } = useSession();
  return (
    <button type="button" className="icon-btn" onClick={signOut} aria-label="Log Out" title="Log Out">
      <Icon name="logout" />
    </button>
  );
}

function ago(iso: string) {
  const t = new Date(iso.endsWith("Z") || iso.includes("+") ? iso : iso + "Z").getTime();
  const mins = Math.round((Date.now() - t) / 60_000);
  if (mins < 1) return "Just now";
  if (mins < 60) return `${mins} min ago`;
  const hours = Math.round(mins / 60);
  if (hours < 24) return `${hours} h ago`;
  const days = Math.round(hours / 24);
  return days < 7 ? `${days} d ago` : new Date(t).toLocaleDateString(undefined, { day: "numeric", month: "short" });
}
