const DEV_USER_KEY = "vantage.devUser";

export function getDevUser(): string | null {
  try {
    return localStorage.getItem(DEV_USER_KEY);
  } catch {
    return null;
  }
}

export function setDevUser(email: string | null) {
  try {
    if (email) localStorage.setItem(DEV_USER_KEY, email);
    else localStorage.removeItem(DEV_USER_KEY);
  } catch {
    /* storage unavailable: sign-in lasts for this page only */
  }
}

export class ApiError extends Error {
  constructor(public status: number, public body: Record<string, unknown> | null) {
    super((body?.message as string) ?? `Request failed (${status})`);
  }
  get code(): string | undefined {
    return this.body?.code as string | undefined;
  }
}

/** Calls the API through the Vite proxy. In the local build the dev user's email stands in for a sign-in token. */
export async function api<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers);
  const devUser = getDevUser();
  if (devUser) headers.set("X-Dev-User", devUser);
  if (init.body && !(init.body instanceof FormData) && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");

  const res = await fetch(path, { ...init, headers });
  const text = await res.text();
  const body = text ? safeJson(text) : null;
  if (!res.ok) throw new ApiError(res.status, body);
  return body as T;
}

/** Fetches a binary resource (an image) with the same sign-in header, as an object URL the caller must revoke. */
export async function apiObjectUrl(path: string): Promise<string> {
  const headers = new Headers();
  const devUser = getDevUser();
  if (devUser) headers.set("X-Dev-User", devUser);
  const res = await fetch(path, { headers });
  if (!res.ok) throw new ApiError(res.status, null);
  return URL.createObjectURL(await res.blob());
}

function safeJson(text: string): Record<string, unknown> | null {
  try {
    return JSON.parse(text);
  } catch {
    return { message: text };
  }
}

export type Me = {
  id: number;
  email: string;
  displayName: string;
  userType: "Internal" | "External";
  roles: string[];
  isSuperAdmin: boolean;
  permissions: Record<string, "None" | "View" | "Edit">;
};

export type DevUser = {
  email: string;
  displayName: string;
  userType: string;
  status: string;
  title: string | null;
  roles: string[];
};

export type Branding = {
  portalName?: string;
  logoUrl?: string;
  primaryColor?: string;
  accentColor?: string;
  footerText?: string;
};
