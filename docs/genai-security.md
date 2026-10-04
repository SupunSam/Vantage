# GenAI dashboards: security

This describes how Vantage keeps GenAI dashboards (a single HTML file, decision C32) from harming the portal, the
people who open them, or the data behind other dashboards. Read it with `docs/requirements.md` (C32) before changing
anything in `backend/src/Vantage.Infrastructure/GenAi`, `GenAiContentController`, `deploy/genai.nginx.conf` or
`GenAiFrame`.

## The short answer

- **The upload is checked, and a file that fails is blocked.** Publishing, Modify Dashboard and Restore all run the
  same check. A failing file creates nothing and changes nothing.
- **The check is a rule check, not a vulnerability or malware scan.** It confirms the file is the expected format and
  loads nothing from outside the approved CDNs. It does not read or judge the file's own JavaScript.
- **The real protection is containment when the page runs.** A GenAI page is treated as untrusted code. It runs on a
  separate origin, in a sandbox, under a strict Content-Security-Policy, so even a hostile page can't reach the
  portal, its session or its API.

The original SRS wording "every upload is scanned" is therefore replaced by C32: every upload is checked against the
template rules.

## Who can publish

Only people with **Edit** on the Publishing module (Super Admin, BPI Publisher, and any role given it) can upload a
GenAI file, for a new dashboard or a replacement. Uploads are audited (`dashboard.published`, `dashboard.replaced`,
`dashboard.restored`) with the file name, size and libraries used. Publishers are trusted staff, and the controls
below are the safety net for mistakes (for example AI-generated code nobody read) and for a compromised publisher
account.

## Layer 1: the upload check (`GenAiChecker`)

Blocked (the upload is refused with a plain-English list of what to fix):

| Check | Why |
| --- | --- |
| File is empty, not UTF-8 text, over 5 MB, or has no `<html>` element | Expected format and size |
| Template marker `<meta name="vantage-template" content="genai-1">` is missing | Must be built from the approved starter template |
| `<iframe>`, `<frame>`, `<frameset>`, `<object>`, `<embed>`, `<applet>` or `<base>` | No nested content, plug-ins or rewriting of links |
| `<meta http-equiv="refresh">` | No automatic redirect |
| Any script, stylesheet, image, media or `url()`/`@import` pointing at a host that is not on the approved, active CDN list | Libraries only from approved CDNs |
| The same, over plain `http://` | HTTPS only |
| A relative link such as `app.js` or `logo.png` | The page is served alone, so everything must be in the file |
| Dashboard type switched off in Admin Configuration, or file over the configured size limit | Configuration is data (C30) |

Warned, not blocked: a file of 2 MB or more, and use of `fetch`, `XMLHttpRequest`, `WebSocket` or `EventSource` (they
are blocked when the page runs anyway).

A restore runs the check again, so an old version that used a CDN since removed from the list can't be brought back.

### What the check does not do

Be clear about these when describing the control to anyone:

- It does **not** analyse the page's own inline JavaScript. Obfuscated or malicious script is not detected.
- The template marker is a format marker, not a signature. Anyone can copy the line into a file.
- It does not verify that an approved CDN's files are unchanged. The starter template notes "use an `integrity`
  hash" but the checker doesn't require one.
- It does not check what the page shows (misleading text, a fake sign-in form). Review of content is a publishing
  responsibility.

## Layer 2: serving the file safely

| Control | Where | What it stops |
| --- | --- | --- |
| **Separate origin** (`GenAi:BaseUrl`, `http://localhost:8082` locally, a dedicated domain in AWS) served by its own nginx listener | `deploy/genai.nginx.conf`, `GenAiContentController` | The page can't read the portals' cookies, storage, session or call `/api` as the viewer. Read-only (GET) |
| **Signed, expiring link** (default 60 minutes, `GenAi:LinkMinutes`) | `GenAiService.LinkAsync/OpenAsync` | The file is never public. A link is issued only after the normal membership check (or a Super Admin preview). A forged, tampered or expired link serves nothing |
| **Only Active dashboards are served** | `GenAiService.OpenAsync` | Retired or inactive dashboards stop being served at once |
| **Content-Security-Policy** | `GenAiService.PolicyFor` | `default-src 'none'`; scripts, styles, fonts and images only from the page itself (inline) or approved CDNs; `connect-src 'none'` (no network calls); `form-action 'none'`; `object-src 'none'`; `frame-src 'none'`; `base-uri 'none'`; `frame-ancestors` only the two portals |
| **CSP `sandbox allow-scripts`** | `GenAiService.PolicyFor` | Even if someone opens the link directly, the page has no same-origin rights, storage or cookies, and can't open pop-ups, download files or navigate the top window |
| **Sandboxed frame** in both portals | `GenAiFrame` (`sandbox="allow-scripts"`, `referrerPolicy="no-referrer"`) | The same restrictions from the portal side. `allow-same-origin` must never be added |
| `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `Cache-Control: no-store` | `GenAiContentController` | No sniffing, no leaking of the link in referrers, no caching of the page |

The CSP allows inline scripts and styles (the template needs them) but not `eval`.

## Layer 3: access and audit

- Membership of an access group is still required to open a dashboard; the link is only the delivery step.
- A member's open is counted as a view when the link is issued. A Super Admin preview is audited
  (`dashboard.previewed`) and is not a view (C27, C31).
- Every publish, replace, restore, version removal and preview is in the Audit Log.

## Known residual risks

1. **Malicious script inside an approved page** can still change what the page shows, so it could display a fake
   prompt or misleading numbers. It can't send data anywhere (no network calls, no forms) or reach the portal. It can
   navigate its own frame to another site, which the CSP can't block, so a viewer could be sent to an outside page
   from inside the frame.
2. **A compromised approved CDN** could serve altered library code. Mitigations: pin versions, use `integrity`
   hashes, keep the CDN list short.
3. **A live link is a bearer URL** until it expires. Someone who is given a live link can load that page, but only
   for the link's lifetime and only the file, never the portal.
4. **A compromised publisher account** can publish a page. The sandbox limits the damage, and the audit log records
   who did it.

## Options to strengthen it (not built; each needs a decision, record it as C33 or later)

- Refuse risky JavaScript patterns at upload (`eval`, `new Function`, `document.cookie`, `location =`, `window.open`,
  `window.top`), accepting some false positives.
- Require an `integrity` (SRI) hash on every CDN script and stylesheet.
- Reject inline event-handler attributes and `javascript:` URLs.
- Run a malware or static-analysis scan service on upload (ClamAV or a commercial scanner) and record the result in
  `DashboardVersion.ScanStatus`, which the data model already has.
- Require a second person (a Super Admin) to approve a GenAI file before it goes live.
- Shorten `GenAi:LinkMinutes`.
- In AWS: serve from a dedicated domain with its own certificate, behind CloudFront, with S3 private and the origin
  reachable only through the signed link.

## Testing

`GenAiCheckerTests` (no database) covers each rule above, and `GenAiTests` (SQL Server) covers publish, replace,
restore, signed links, forged and expired links, and retired dashboards. When you change a rule, change its test.
