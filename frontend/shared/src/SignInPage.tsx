import { useEffect, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { api, type DevUser } from "./api";
import { Icon } from "./Icon";
import { useSession } from "./session";
import { LoginArt } from "./LoginArt";

type Kind = "Internal" | "External";

const KINDS: { kind: Kind; title: string; text: string; icon: string }[] = [
  { kind: "Internal", title: "Login as Internal User", text: "For employees with a company (@rrd.com) account. You sign in with your company account.", icon: "shield" },
  { kind: "External", title: "Login as External User", text: "For clients and partners. You sign in with your email address and your authenticator app.", icon: "users" },
];

/**
 * The page both portals show before sign-in. Two choices: internal users (ADFS with Duo in AWS) and external users (Cognito with an
 * authenticator app). In the local build each choice opens a picker of the matching seeded users, which stands in for that sign-in.
 * Whoever signs in lands on Home.
 */
export function SignInPage({ portalLabel }: { portalLabel: string }) {
  const { branding, signIn, idleNotice } = useSession();
  const navigate = useNavigate();
  const [kind, setKind] = useState<Kind | null>(null);
  const [users, setUsers] = useState<DevUser[]>([]);
  const [filter, setFilter] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!kind || users.length > 0) return;
    api<DevUser[]>("/api/dev/users")
      .then(setUsers)
      .catch(() => setError("The API is not reachable. Check that the Docker stack is running."));
  }, [kind, users.length]);

  const shown = useMemo(() => {
    const q = filter.trim().toLowerCase();
    return users.filter((u) => u.userType === kind && (!q || `${u.displayName} ${u.email} ${u.roles.join(" ")}`.toLowerCase().includes(q)));
  }, [users, kind, filter]);

  async function signInAs(email: string) {
    setBusy(true);
    try {
      await signIn(email);
      navigate("/", { replace: true });   // always start on Home, whatever page the address bar showed
    } finally {
      setBusy(false);
    }
  }

  const portalName = branding.portalName ?? "Vantage";
  const admin = /admin/i.test(portalLabel);
  const chosen = KINDS.find((k) => k.kind === kind);

  return (
    <div className="login">
      <aside className="login-brand">
        <div className="login-brand-inner">
          <div className="login-mark">
            <img src={branding.logoUrl ?? "/brand/logo.svg"} alt="" className="login-logo" />
            <span className="login-mark-text">
              <strong>{portalName}</strong>
              <span className="login-chip">{portalLabel}</span>
            </span>
          </div>

          <div className="login-hero">
            <h1>{admin ? "Run every dashboard from one place." : "Every dashboard you need, one place."}</h1>
            <p className="login-blurb">{admin
              ? "Publish Power BI, Tableau and GenAI dashboards, decide who sees each one, and see how they are used."
              : "Open the dashboards you have access to, keep your favourites close, and ask owners for the rest."}</p>
          </div>

          <LoginArt />

          <ul className="login-points">
            <li><span aria-hidden="true"><Icon name="dashboards" size={18} /></span>Power BI, Tableau and GenAI dashboards, side by side</li>
            <li><span aria-hidden="true"><Icon name="shield" size={18} /></span>Access that follows your groups, nothing more</li>
            <li><span aria-hidden="true"><Icon name="lock" size={18} /></span>Company sign-in with a second step to prove it is you</li>
          </ul>
        </div>
        <p className="login-foot">© {new Date().getFullYear()} {portalName}</p>
      </aside>

      <main className="login-main">
        <div className="login-card">
          {idleNotice && <p className="notice">{idleNotice}</p>}
          {error && <p className="notice notice-error">{error}</p>}

          {!chosen ? (
            <>
              <h2>Sign In</h2>
              <p className="muted">Choose how you sign in to the {portalLabel}.</p>
              <div className="login-options">
                {KINDS.map((k) => (
                  <button key={k.kind} type="button" className="login-option" onClick={() => { setKind(k.kind); setFilter(""); setError(null); }}>
                    <span className="login-option-icon" aria-hidden="true"><Icon name={k.icon} size={26} /></span>
                    <span className="login-option-text">
                      <strong>{k.title}</strong>
                      <span>{k.text}</span>
                    </span>
                    <Icon name="chevronRight" size={20} />
                  </button>
                ))}
              </div>
            </>
          ) : (
            <>
              <button type="button" className="link login-back" onClick={() => { setKind(null); setError(null); }}>Change Sign-In Type</button>
              <h2>{chosen.title}</h2>
              <p className="notice">Local build: choose a user below. In production this step is the company {kind === "Internal" ? "ADFS (with Duo)" : "Cognito (with an authenticator app)"} sign-in.</p>
              <label className="field">
                <span>Find a user</span>
                <input value={filter} onChange={(e) => setFilter(e.target.value)} placeholder="Name, email or role" autoFocus />
              </label>
              {users.length > 0 && shown.length === 0 && <p className="muted">No {kind?.toLowerCase()} users match.</p>}
              <ul className="login-users">
                {shown.map((u) => (
                  <li key={u.email}>
                    <button type="button" className="signin-user" disabled={busy} onClick={() => void signInAs(u.email)}>
                      <span className="signin-name">{u.displayName}</span>
                      <span className="signin-meta">{u.title ?? u.email}</span>
                      <span className="signin-roles">
                        {u.roles.map((r) => <span key={r} className={`role-chip ${r === "Super Admin" ? "role-chip-strong" : ""}`}>{r}</span>)}
                        {u.status === "PendingSetup" && <span className="role-chip">Setup pending</span>}
                      </span>
                    </button>
                  </li>
                ))}
              </ul>
            </>
          )}
        </div>
      </main>
    </div>
  );
}
