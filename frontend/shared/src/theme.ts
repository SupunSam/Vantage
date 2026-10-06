import { useCallback, useSyncExternalStore } from "react";

export type ThemePreference = "system" | "light" | "dark";
const KEY = "vantage.theme";
const listeners = new Set<() => void>();

function read(): ThemePreference {
  try {
    const v = localStorage.getItem(KEY);
    return v === "light" || v === "dark" ? v : "system";
  } catch {
    return "system";
  }
}

function resolve(pref: ThemePreference): "light" | "dark" {
  return pref === "system" ? (window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light") : pref;
}

function apply() {
  document.documentElement.dataset.theme = resolve(read());
  listeners.forEach((l) => l());
}

// With "System", follow the computer when it switches between light and dark.
window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => { if (read() === "system") apply(); });

export function setThemePreference(pref: ThemePreference) {
  try {
    if (pref === "system") localStorage.removeItem(KEY); else localStorage.setItem(KEY, pref);
  } catch { /* the choice lasts for this visit only */ }
  apply();
}

/** The person's theme choice (Light, Dark or System) and what is showing now. */
export function useTheme() {
  const pref = useSyncExternalStore((cb) => { listeners.add(cb); return () => listeners.delete(cb); }, read, () => "system" as ThemePreference);
  const set = useCallback((p: ThemePreference) => setThemePreference(p), []);
  return { preference: pref, resolved: resolve(pref), set };
}
