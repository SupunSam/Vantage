import { ToastProvider } from "./Toast";
import { applyBrand } from "./brand";
import { QueryProvider, clearCachedData } from "./query";
import { ConfirmProvider } from "./Feedback";
import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from "react";
import { api, getDevUser, setDevUser, type Branding, type Me } from "./api";

/** Behaviour settings from Admin Configuration: idle timeout and the default rows per page. */
export type UiSettings = { idleTimeoutMinutes: number; gridPageSize: number; toastSeconds: number; biTypes: BiTypeState[] };

/** A BI type as the portals see it: on or off, and whether its existing dashboards are hidden while it is off (C56). */
export type BiTypeState = { type: string; displayName: string; enabled: boolean; hideExisting: boolean };

type Session = {
  me: Me | null;
  loading: boolean;
  branding: Branding;
  settings: UiSettings;
  /** Set when the person was signed out for being idle; the sign-in page shows it. */
  idleNotice: string | null;
  signIn: (email: string) => Promise<void>;
  /** Ends this portal's session only and returns to the base address, which is the sign-in page. The other portal is not affected. */
  signOut: () => void;
};

const IDLE_NOTICE_KEY = "vantage.idleNotice";

/** The "signed out for being idle" message, kept across the reload that returns to the sign-in page. Cleared on the next sign-in. */
function readIdleNotice(): string | null {
  try {
    return sessionStorage.getItem(IDLE_NOTICE_KEY);
  } catch {
    return null;
  }
}

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

/** Which BI types are on. Until the settings load, every type counts as on so nothing flickers away. */
export function useBiTypes() {
  const { biTypes } = useSession().settings;
  return {
    biTypes,
    isEnabled: (type: string) => biTypes.find((t) => t.type === type)?.enabled ?? true,
  };
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
  const [settings, setSettings] = useState<UiSettings>({ idleTimeoutMinutes: 30, gridPageSize: 25, toastSeconds: 6, biTypes: [] });
  const [idleNotice, setIdleNotice] = useState<string | null>(readIdleNotice);
  const lastActive = useRef(Date.now());

  useEffect(() => {
    api<Branding>("/api/branding")
      .then((b) => {
        setBranding(b);
        applyBrand(b.primaryColor, b.accentColor);
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
    clearCachedData();   // nothing read for someone else may show for this person
    setIdleNotice(null);
    try { sessionStorage.removeItem(IDLE_NOTICE_KEY); } catch { /* nothing to clear */ }
    await load();
  }, [load]);

  // Each portal has its own session (C41). Ending it reloads at the base address, so the next sign-in starts clean on Home.
  const endSession = useCallback((notice?: string) => {
    setDevUser(null);
    try {
      if (notice) sessionStorage.setItem(IDLE_NOTICE_KEY, notice);
      else sessionStorage.removeItem(IDLE_NOTICE_KEY);   // a manual log-out never shows an old idle message
    } catch {
      /* the sign-in page then just shows no notice */
    }
    setMe(null);
    window.location.assign("/");
  }, []);
  const signOut = useCallback(() => endSession(), [endSession]);

  // Idle timeout: any click, key press, scroll or pointer move counts as activity; checked every 15 seconds.
  useEffect(() => {
    if (!me) return;
    lastActive.current = Date.now();
    const touch = () => { lastActive.current = Date.now(); };
    const events = ["pointerdown", "pointermove", "keydown", "wheel", "touchstart"] as const;
    events.forEach((e) => window.addEventListener(e, touch, { passive: true }));
    const timer = window.setInterval(() => {
      if (Date.now() - lastActive.current >= settings.idleTimeoutMinutes * 60_000) {
        endSession(`You were signed out after ${settings.idleTimeoutMinutes} minutes without activity.`);
      }
    }, 15_000);
    return () => { events.forEach((e) => window.removeEventListener(e, touch)); window.clearInterval(timer); };
  }, [me, settings.idleTimeoutMinutes, endSession]);

  return <SessionContext.Provider value={{ me, loading, branding, settings, idleNotice, signIn, signOut }}><QueryProvider><ToastProvider seconds={settings.toastSeconds}><ConfirmProvider>{children}</ConfirmProvider></ToastProvider></QueryProvider></SessionContext.Provider>;
}
