import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from "react";
import { api, getDevUser, setDevUser, type Branding, type Me } from "./api";

type Session = {
  me: Me | null;
  loading: boolean;
  branding: Branding;
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

/** Loads branding (public) and the signed-in user; applies brand colours as CSS variables. */
export function SessionProvider({ children }: { children: ReactNode }) {
  const [me, setMe] = useState<Me | null>(null);
  const [loading, setLoading] = useState(true);
  const [branding, setBranding] = useState<Branding>({});

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
    await load();
  }, [load]);

  const signOut = useCallback(() => {
    setDevUser(null);
    setMe(null);
  }, []);

  return <SessionContext.Provider value={{ me, loading, branding, signIn, signOut }}>{children}</SessionContext.Provider>;
}
