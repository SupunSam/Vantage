/** Brand colours as the page uses them: the colours themselves, plus text colours that stay readable on and next to them. */

function channels(hex: string): [number, number, number] | null {
  const m = /^#?([0-9a-f]{6})$/i.exec(hex.trim());
  if (!m) return null;
  const n = parseInt(m[1], 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}

function luminance([r, g, b]: [number, number, number]) {
  const f = (v: number) => { const s = v / 255; return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4); };
  return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b);
}

const contrast = (a: number, b: number) => (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
const WHITE = 1;
const DARK_TEXT = luminance([24, 34, 45]);

/** White or dark text, whichever reads better on this colour. */
function onColour(hex: string): string {
  const c = channels(hex);
  if (!c) return "#fff";
  const l = luminance(c);
  return contrast(l, WHITE) >= contrast(l, DARK_TEXT) ? "#fff" : "#18222d";
}

/** How much of the colour to keep (the rest is black) for it to be readable as text on white: 100 when it already is. */
function textMix(hex: string): number {
  const c = channels(hex);
  if (!c) return 100;
  for (let p = 100; p >= 30; p -= 5) {
    const scaled = c.map((v) => Math.round(v * (p / 100))) as [number, number, number];
    if (contrast(luminance(scaled), WHITE) >= 4.5) return p;
  }
  return 30;
}

/** Sets the brand variables on the page; use this wherever the colours change so text on them is always legible. */
export function applyBrand(primary?: string, accent?: string) {
  const root = document.documentElement.style;
  if (primary) {
    root.setProperty("--brand", primary);
    root.setProperty("--on-brand", onColour(primary));
    root.setProperty("--brand-text-mix", `${textMix(primary)}%`);
  }
  if (accent) {
    root.setProperty("--accent", accent);
    root.setProperty("--on-accent", onColour(accent));
  }
}
