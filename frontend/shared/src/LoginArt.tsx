/**
 * The picture on the left of the sign-in pages: a small dashboard drawn from shapes, with columns that grow, a line that draws
 * itself and a ring that fills, floating gently. No text in it, so it needs no translation and works in any brand colour.
 */
export function LoginArt() {
  const bars = [38, 62, 48, 84, 58, 96, 72, 110, 88, 124];
  return (
    <svg className="login-art" viewBox="0 0 560 400" role="img" aria-label="An illustration of a dashboard with charts" focusable="false">
      <defs>
        <linearGradient id="la-area" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#fff" stopOpacity="0.35" />
          <stop offset="1" stopColor="#fff" stopOpacity="0" />
        </linearGradient>
        <clipPath id="la-clip"><rect x="40" y="70" width="400" height="260" rx="18" /></clipPath>
      </defs>

      {/* the main window */}
      <g className="la-float la-float-a">
        <rect x="40" y="40" width="440" height="290" rx="18" className="la-glass" />
        <circle cx="64" cy="64" r="5" className="la-dot" /><circle cx="82" cy="64" r="5" className="la-dot" /><circle cx="100" cy="64" r="5" className="la-dot" />
        <rect x="320" y="58" width="130" height="12" rx="6" className="la-line" />
        <line x1="40" y1="86" x2="480" y2="86" className="la-rule" />

        {/* three figures */}
        {[0, 1, 2].map((i) => (
          <g key={i} className="la-rise" style={{ animationDelay: `${0.5 + i * 0.12}s` }}>
            <rect x={64 + i * 140} y="104" width="124" height="58" rx="10" className="la-tile" />
            <rect x={78 + i * 140} y="118" width="46" height="8" rx="4" className="la-line" />
            <rect x={78 + i * 140} y="134" width={[70, 54, 62][i]} height="14" rx="5" className={i === 1 ? "la-accent-fill" : "la-bold"} />
          </g>
        ))}

        {/* columns */}
        <g>
          {bars.map((h, i) => (
            <rect key={i} x={68 + i * 22} y={310 - h} width="13" height={h} rx="4" className={`la-bar ${i === 7 ? "la-bar-hi" : ""}`} style={{ animationDelay: `${0.7 + i * 0.07}s` }} />
          ))}
        </g>

        {/* line */}
        <g clipPath="url(#la-clip)">
          <path d="M300 300 C 330 270, 345 285, 372 248 S 420 236, 440 196 L440 330 L300 330 Z" fill="url(#la-area)" className="la-fade" style={{ animationDelay: "1.4s" }} />
          <path d="M300 300 C 330 270, 345 285, 372 248 S 420 236, 440 196" className="la-path" pathLength={1} />
          <circle cx="440" cy="196" r="5" className="la-accent-fill la-fade" style={{ animationDelay: "2.1s" }} />
        </g>
      </g>

      {/* ring card */}
      <g className="la-float la-float-b">
        <rect x="372" y="216" width="150" height="120" rx="16" className="la-glass la-glass-strong" />
        <circle cx="430" cy="276" r="26" className="la-ring-track" />
        <circle cx="430" cy="276" r="26" className="la-ring" pathLength={100} />
        <rect x="466" y="262" width="40" height="8" rx="4" className="la-line" />
        <rect x="466" y="278" width="28" height="8" rx="4" className="la-bold" />
        <rect x="392" y="312" width="110" height="8" rx="4" className="la-line" />
      </g>

      {/* a small badge */}
      <g className="la-float la-float-c">
        <rect x="14" y="258" width="168" height="54" rx="14" className="la-glass la-glass-strong" />
        <circle cx="42" cy="285" r="14" className="la-accent-fill" />
        <path d="M35 285 l5 5 l9 -10" className="la-tick" />
        <rect x="66" y="273" width="88" height="8" rx="4" className="la-bold" />
        <rect x="66" y="290" width="62" height="7" rx="3.5" className="la-line" />
      </g>
    </svg>
  );
}
