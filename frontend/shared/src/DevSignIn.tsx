import { useEffect, useMemo, useState } from "react";
import { api, type DevUser } from "./api";
import { useSession } from "./session";

/**
 * Local build only: stands in for ADFS (internal) and Cognito (external) sign-in.
 * Pick any seeded user to see the portal through their roles and group memberships.
 */
export function DevSignIn({ portalLabel }: { portalLabel: string }) {
  const { branding, signIn } = useSession();
  const [users, setUsers] = useState<DevUser[]>([]);
  const [filter, setFilter] = useState("");
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api<DevUser[]>("/api/dev/users")
      .then(setUsers)
      .catch(() => setError("The API is not reachable on http://localhost:5080. Start it with dotnet run in backend/src/Vantage.Api."));
  }, []);

  const groups = useMemo(() => {
    const q = filter.trim().toLowerCase();
    const shown = users.filter((u) => !q || `${u.displayName} ${u.email} ${u.roles.join(" ")}`.toLowerCase().includes(q));
    return [
      { label: "Internal Users (ADFS)", users: shown.filter((u) => u.userType === "Internal") },
      { label: "External Users (Cognito)", users: shown.filter((u) => u.userType === "External") },
    ];
  }, [users, filter]);

  return (
    <div className="signin">
      <header className="signin-head">
        <img src={branding.logoUrl ?? "/brand/logo.svg"} alt="" className="signin-logo" />
        <div>
          <h1>{branding.portalName ?? "Vantage"}</h1>
          <p>{portalLabel}: choose who to sign in as. This picker replaces ADFS and Cognito in the local build.</p>
        </div>
      </header>

      {error && <p className="notice notice-error">{error}</p>}

      <label className="field">
        <span>Find a user</span>
        <input value={filter} onChange={(e) => setFilter(e.target.value)} placeholder="Name, email or role" autoFocus />
      </label>

      {groups.map((g) =>
        g.users.length === 0 ? null : (
          <section key={g.label} className="signin-group">
            <h2>{g.label}</h2>
            <ul>
              {g.users.map((u) => (
                <li key={u.email}>
                  <button type="button" className="signin-user" onClick={() => void signIn(u.email)}>
                    <span className="signin-name">{u.displayName}</span>
                    <span className="signin-meta">{u.title ?? u.email}</span>
                    <span className="signin-roles">
                      {u.roles.map((r) => (
                        <span key={r} className={`role-chip ${r === "Super Admin" ? "role-chip-strong" : ""}`}>{r}</span>
                      ))}
                      {u.status === "PendingSetup" && <span className="role-chip">Setup pending</span>}
                    </span>
                  </button>
                </li>
              ))}
            </ul>
          </section>
        ),
      )}
    </div>
  );
}
