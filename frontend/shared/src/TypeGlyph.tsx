/**
 * Placeholder thumbnail until a real 640x360 thumbnail is uploaded: a small abstract drawing per dashboard type,
 * varied by a seed so neighbouring cards differ.
 */
export function TypeGlyph({ type, seed }: { type: string; seed: number }) {
  const r = (n: number) => {
    const x = Math.sin(seed * 9301 + n * 49297) * 233280;
    return x - Math.floor(x);
  };

  if (type === "Tableau") {
    // Scatter of marks
    return (
      <svg viewBox="0 0 160 90" className="glyph" aria-hidden="true">
        {Array.from({ length: 18 }, (_, i) => (
          <circle key={i} cx={14 + r(i) * 132} cy={12 + r(i + 40) * 64} r={2 + r(i + 80) * 5} className={i % 4 === 0 ? "glyph-accent" : "glyph-mark"} />
        ))}
      </svg>
    );
  }

  if (type === "GenAi") {
    // Lines of generated text beside a small chart
    return (
      <svg viewBox="0 0 160 90" className="glyph" aria-hidden="true">
        {Array.from({ length: 5 }, (_, i) => (
          <rect key={i} x={12} y={14 + i * 13} width={50 + r(i) * 40} height={5} rx={2.5} className="glyph-mark" />
        ))}
        <polyline
          fill="none"
          strokeWidth={2.5}
          className="glyph-line"
          points={Array.from({ length: 6 }, (_, i) => `${112 + i * 8},${70 - r(i + 9) * 40}`).join(" ")}
        />
      </svg>
    );
  }

  // Power BI: bars with one highlighted
  const hi = Math.floor(r(99) * 7);
  return (
    <svg viewBox="0 0 160 90" className="glyph" aria-hidden="true">
      {Array.from({ length: 7 }, (_, i) => {
        const h = 16 + r(i) * 56;
        return <rect key={i} x={16 + i * 19} y={80 - h} width={12} height={h} rx={2} className={i === hi ? "glyph-accent" : "glyph-mark"} />;
      })}
    </svg>
  );
}
