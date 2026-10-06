import { useEffect, useRef, useState } from "react";

/**
 * A whole number that counts up to its value when it appears (and again when it changes). Text that isn't a plain number,
 * such as "3 of 12" or "98%", is shown as it is. With reduced motion the number just appears.
 */
export function CountUp({ text, ms = 900 }: { text: string; ms?: number }) {
  const plain = /^\d[\d,]*$/.test(text);
  const target = plain ? Number(text.replace(/,/g, "")) : 0;
  const grouped = text.includes(",");
  const [shown, setShown] = useState(plain ? 0 : target);
  const from = useRef(0);

  useEffect(() => {
    if (!plain) return;
    if (window.matchMedia("(prefers-reduced-motion: reduce)").matches || target === 0) { setShown(target); from.current = target; return; }
    const start = performance.now();
    const begin = from.current;
    let frame = 0;
    const tick = (now: number) => {
      const t = Math.min(1, (now - start) / ms);
      const eased = 1 - Math.pow(1 - t, 3);
      setShown(Math.round(begin + (target - begin) * eased));
      if (t < 1) frame = requestAnimationFrame(tick); else from.current = target;
    };
    frame = requestAnimationFrame(tick);
    // A background tab doesn't run animation frames; the final number still has to arrive.
    const settle = window.setTimeout(() => { setShown(target); from.current = target; }, ms + 150);
    return () => { cancelAnimationFrame(frame); window.clearTimeout(settle); };
  }, [plain, target, ms]);

  if (!plain) return <>{text}</>;
  return <span aria-label={text}>{grouped ? shown.toLocaleString() : String(shown)}</span>;
}
