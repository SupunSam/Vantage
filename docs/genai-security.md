# GenAI dashboards: security

This describes how Vantage keeps GenAI dashboards (a single HTML file, decisions C32 and C33) from harming corporate
systems. Read it with `docs/requirements.md` before changing anything in `backend/src/Vantage.Infrastructure/GenAi`,
`GenAiContentController`, `deploy/genai.nginx.conf` or `GenAiFrame`.

## The short answer

- **Only Super Admins publish a GenAI file or change the GenAI settings and approved CDNs.** The author hands the `.html` file to a Super Admin, who uploads it. The
  same applies to Modify Dashboard and Restore. Other roles, including those with the Publishing permission, are
  refused by the API (and don't see the GenAI tab).
- **Every upload is vetted before it is stored, and a file that fails is blocked.** The same vetting runs on publish,
  Modify and Restore. A failing file creates nothing and changes nothing.
- **The vetting is layered.** A rule check (format, approved CDNs, exact versions, integrity hashes, banned code
  patterns), then a malware scan when a scanner is configured, then containment when the page runs (separate origin,
  sandbox, strict policy). No single layer is relied on alone.
- **Be precise when describing it.** The rule check is a strong filter for the accidental and the obvious. It is not a
  proof that code is harmless: a determined author can obfuscate JavaScript past any pattern list. What holds in that
  case is the containment layer and the Super Admin's own review.

## Layer 1: the file check (`GenAiChecker`)

A file is refused, with a list of what to fix, if any of these hold.

| Area | Refused when |
| --- | --- |
| Format | Empty, not UTF-8, over 5 MB (warning from 2 MB), no `<html>` element, or no template marker `<meta name="vantage-template" content="genai-1">` (a marker inside a comment doesn't count) |
| Structure | `<iframe>`, `<frame>`, `<object>`, `<embed>`, `<applet>`, `<base>`, `<meta http-equiv="refresh">`, `<link>` other than a stylesheet, `<script type>` other than JavaScript, module or JSON data (so no import maps or speculation rules), a link to another site, CSS `@import` |
| Libraries | A script or stylesheet from a host not on the approved, active CDN list; over plain HTTP; a relative file (`app.js`, `logo.png`); a version that isn't exact (`@latest`, `@4`, a range, `/combine/`); no `integrity="sha256/384/512-…"` hash of the right length; no `crossorigin="anonymous"` |
| Images and fonts | Loaded from anywhere but approved CDNs over HTTPS (no integrity hash needed) |
| Code | `eval()`, `new Function()`, `setTimeout`/`setInterval` with a text argument, `document.write()`, `document.cookie`, `window.open()`, `window.top`/`parent`/`opener`, changing `location`, `javascript:` URLs, Workers, service workers, `importScripts`, WebAssembly, dynamic `import()`, `postMessage()`, `sendBeacon()`, `fetch`, `XMLHttpRequest`, `WebSocket`, `EventSource` |

Warned, not blocked: a file of 2 MB or more, a long encoded text blob that isn't a `data:` URL (it can hide code), and
`atob()`.

Notes for authors: the patterns match the whole file, including comments, so don't write those words in comments. An
inline copy of a big library (for example a pasted minified chart library) will usually trip a code rule; load it from
an approved CDN instead. The starter template passes all of these and shows the exact script tag to copy.

A restore runs the whole vetting again, so an old version that used a CDN since removed from the list can't come back.

### What the file check does not do

- It does not understand JavaScript. Patterns can be evaded by building names at run time
  (`window["ev"+"al"]`, `this.constructor.constructor(...)`). The runtime policy below is what stops those.
- The template marker is a format marker, not a signature. Anyone can copy the line.
- The integrity hash proves the CDN file is the one the author referenced. It does not prove that file is benign.
  Exact versions plus a short CDN list plus a Super Admin who recognises the libraries is the control.
- It does not judge what the page displays.

## Settings (Admin Configuration, GenAI Config tab)

The GenAI values are data, not code, so they change without a redeploy. They are validated before saving, audited with the
old and new value, applied at once, and **only Super Admins can change them or the approved CDN list** (the API refuses
anyone else), because they decide what code may run in front of staff.

| Setting | Meaning | Blank / default |
| --- | --- | --- |
| GenAI web address | The separate site GenAI pages load from (`https://host[:port]`, no path or login details) | Blank uses the environment's `GenAi:BaseUrl` |
| Portals allowed to show GenAI dashboards | Origins in the policy's `frame-ancestors`, space separated, up to 10, no wildcards | Blank uses the environment's `GenAi:FrameAncestors` |
| Link lifetime (minutes) | 1 to 1440 | 60 |
| Warn when a file is this big (MB) | 1 to 5 (the hard maximum is on the Dashboard Types tab, never above 5) | 2 |
| Malware scanner host / port | ClamAV (clamd). A host name or address only | Blank host uses the environment's `GenAi:ScanHost`, else no scan. Port 3310 |

Deliberately **not** settings: the file-check rules and the policy's directives. They are in code, covered by tests, and
change only through a reviewed code change and a recorded decision. A setting can't switch the security checks off. A
blank scanner host in the portal falls back to the environment's, so the portal can't turn off a scan the environment requires.

## Layer 2: malware scan (`IFileScanner`, ClamAV)

When a scanner host is set (GenAI Config, or `GenAi:ScanHost` in the environment), every upload and every restore is streamed to ClamAV (clamd). A flagged file is
refused with the signature name; the clean result is stored on the file version (`ScanStatus`, `ScanReport`) and shown
in File Versions. If a scanner is configured but can't answer, the upload is refused (fail closed), so nothing is
accepted unscanned by accident. When no scanner host is set anywhere (the local default) no scan runs, the version's status is
`NotRequired`, and the vetting is the file check plus containment. Production should always set it. ClamAV finds known
malware signatures; it will not find a novel malicious script, so it adds to the layers around it and does not replace them.

## Layer 3: containment when the page runs

| Control | Where | What it stops |
| --- | --- | --- |
| **Separate origin** (GenAI web address: `http://localhost:8082` locally, a dedicated domain in AWS) with its own nginx listener | `deploy/genai.nginx.conf`, `GenAiContentController` | The page can't read the portals' cookies, storage or session, or call `/api` as the viewer. GET only |
| **Signed, expiring link** (default 60 minutes, the Link lifetime setting) | `GenAiService.LinkAsync/OpenAsync` | The file is never public. Issued only after the normal membership check (or a Super Admin preview). A forged, tampered or expired link serves nothing |
| **Only Active dashboards are served** | `GenAiService.OpenAsync` | A retired dashboard stops being served at once |
| **Content-Security-Policy** | `GenAiService.PolicyFor` | `default-src 'none'`; scripts, styles, fonts and images only from the page itself (inline) or approved CDNs; **no `unsafe-eval`**, so `eval`/`new Function` fail even if obfuscated; `connect-src 'none'`; `form-action 'none'`; `object-src 'none'`; `frame-src 'none'`; `base-uri 'none'`; `frame-ancestors` only the two portals |
| **CSP `sandbox allow-scripts`** | `GenAiService.PolicyFor` | Even if someone opens the link directly: no same-origin rights, storage or cookies, no pop-ups, downloads, forms or top-window navigation |
| **Sandboxed frame** in both portals | `GenAiFrame` (`sandbox="allow-scripts"`, `referrerPolicy="no-referrer"`) | The same restrictions from the portal side. `allow-same-origin` must never be added |
| `nosniff`, `Referrer-Policy: no-referrer`, `Cache-Control: no-store` | `GenAiContentController` | No sniffing, no leaking the link in referrers, no caching |

## Layer 4: access and audit

- Publishing, replacing and restoring are Super Admin only, enforced in the API.
- Membership of an access group is still required to open a dashboard; the signed link is only the delivery step.
- Publish, replace, restore, version removal and preview are in the Audit Log with the file name, size, libraries and
  scan result.

## Known residual risks

1. **Obfuscated script in an approved page** can still change what the page shows. It can't use the portal, the
   viewer's session or `eval`, and it can't make network calls or submit forms. It can navigate its own frame to
   another site (a browser can't block that), and browsers keep a few weak covert channels such as DNS prefetch. Both
   are limited by what the page itself contains. The Super Admin's review is the control for this.
2. **A compromised approved CDN** is limited by exact versions and integrity hashes: the browser refuses a changed
   file. A bad library the author chose deliberately is a review matter.
3. **A live link is a bearer URL** until it expires. Whoever holds one can load that page, for its lifetime, and only
   that file.
4. **A compromised Super Admin account** can publish a page. The sandbox limits what it can do, and the audit log
   records who published it.
5. **Without a configured scanner** there is no malware scan. Set the scanner host in every shared environment.

## Options not built

- Verify on upload that each integrity hash matches the file the CDN actually serves (needs outbound access to the CDNs).
- Run a real JavaScript parser (an AST check) in place of patterns, which closes the obfuscation gap for code that is
  visible in the file.
- Require a second Super Admin to approve a file before it goes live.
- Shorter links (the Link lifetime setting).
- In AWS: a dedicated registrable domain (not a subdomain of the portal's), CloudFront in front, the policy also set at
  the edge, S3 private, a WAF, and the GenAI origin included in the planned penetration test.

## Testing

`GenAiCheckerTests` (no database) covers every rule above, including that the starter template passes and that ordinary
chart code isn't mistaken for risky code. `FileScannerTests` runs the ClamAV client against a fake scanner. `GenAiTests`
(SQL Server) covers publish, replace, restore, scan refusal, signed, forged and expired links, and retired dashboards.
`ControllerWiringTests` fails if a controller needs a service that isn't registered. When you change a rule, change its test.
