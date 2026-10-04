import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from "react";
import { api, getDevUser, setDevUser, type Branding, type Me } from "./api";

/** Behaviour settings from Admin Configuration: idle timeout and the default rows per page. */
export type UiSettings = { idleTimeoutMinutes: number; gridPageSize: number };

type Session = {
  me: Me | null;
  loading: boolean;
  branding: Branding;
  settings: UiSettings;
  /** Set when the person was signed out for being idle; the sign-in page shows it. */
  idleNotice: string | null;
  signIn: (email: string) => Promise<void>;
  signOut: () => void;
};

const SessionContext = createContext<Session | null>(null);

export function useSession(): Session {
  const ctx = useContext(SessionContext);
  if (!ctx) throw new Error("useSession must be used inside <SessionProvider>");
  return ctx;
}

export function can(me: Me | null, module: string, level: "View" | "Edit" = "View") {
  const l = me?.permissions[module];
  return l === "Edit" || (level === "View" && l === "View");
}

/** Rows per page to start a grid or list on: the Admin Configuration default, or `fallback` until it has loaded. */
export function useGridPageSize(): number {
  return useSession().settings.gridPageSize;
}

/** Loads branding (public) and the signed-in user; applies brand colours as CSS variables, and signs people out when idle. */
export function SessionProvider({ children }: { children: ReactNode }) {
  const [me, setMe] = useState<Me | null>(null);
  const [loading, setLoading] = useState(true);
  const [branding, setBranding] = useState<Branding>({});
  const [settings, setSettings] = useState<UiSettings>({ idleTimeoutMinutes: 30, gridPageSize: 25 });
  const [idleNotice, setIdleNotice] = useState<string | null>(null);
  const lastActive = useRef(Date.now());

  useEffect(() => {
    api<Branding>("/api/branding")
      .then((b) => {
        setBranding(b);
        const root = document.documentElement.style;
        if (b.primaryColor) root.setProperty("--brand", b.primaryColor);
        if (b.accentColor) root.setProperty("--accent", b.accentColor);
        if (b.portalName) document.title = b.portalName;
      })
      .catch(() => {});
  }, []);

  const load = useCallback(async () => {
    if (!getDevUser()) {
      setMe(null);
      setLoading(false);
      return;
    }
    setLoading(true);
    try {
      setMe(await api<Me>("/api/me"));
      api<UiSettings>("/api/me/settings").then(setSettings).catch(() => {});
    } catch {
      setDevUser(null);
      setMe(null);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const signIn = useCallback(async (email: string) => {
    setDevUser(email);
    setIdleNotice(null);
    await load();
  }, [load]);

  const signOut = useCallback(() => {
    setDevUser(null);
    setMe(null);
  }, []);

  // Idle timeout: any click, key press, scroll or pointer move counts as activity; checked every 15 seconds.
  useEffect(() => {
    if (!me) return;
    lastActive.current = Date.now();
    const touch = () => { lastActive.current = Date.now(); };
    const events = ["pointerdown", "pointermove", "keydown", "wheel", "touchstart"] as const;
    events.forEach((e) => window.addEventListener(e, touch, { passive: true }));
    const timer = window.setInterval(() => {
      if (Date.now() - lastActive.current >= settings.idleTimeoutMinutes * 60_000) {
        setIdleNotice(`You were signed out after ${settings.idleTimeoutMinutes} minutes without activity.`);
        signOut();
      }
    }, 15_000);
    return () => { events.forEach((e) => window.removeEventListener(e, touch)); window.clearInterval(timer); };
  }, [me, settings.idleTimeoutMinutes, signOut]);

  return <SessionContext.Provider value={{ me, loading, branding, settings, idleNotice, signIn, signOut }}>{children}</SessionContext.Provider>;
}
