import { useBiTypes } from "./session";
/** BI types as they appear in lists: a small icon and a name (never colour alone). The colours match the charts on the Admin Home. */
export const TYPE_META: Record<string, { label: string; color: string }> = {
  PowerBi: { label: "Power BI", color: "#2a78d6" },
  Tableau: { label: "Tableau", color: "#eb6834" },
  GenAi: { label: "GenAI", color: "#1baf7a" },
};

export function TypeIcon({ type, size = 18, showLabel = true }: { type: string; size?: number; showLabel?: boolean }) {
  const meta = TYPE_META[type] ?? { label: type, color: "#5a6877" };
  return (
    <span className="type-icon" title={meta.label}>
      <svg width={size} height={size} viewBox="0 0 24 24" aria-hidden="true" style={{ color: meta.color }} className="type-icon-svg">
        {type === "PowerBi" && (
          // Rising bars
          <g fill="currentColor"><rect x="3.5" y="13" width="4.5" height="7.5" rx="1.2" /><rect x="9.75" y="8.5" width="4.5" height="12" rx="1.2" /><rect x="16" y="3.5" width="4.5" height="17" rx="1.2" /></g>
        )}
        {type === "Tableau" && (
          // A cross of small plus signs
          <g stroke="currentColor" strokeWidth="2" strokeLinecap="round" fill="none">
            <path d="M12 8.5v7M8.5 12h7" /><path d="M12 2.5v3M10.5 4h3" /><path d="M12 18.5v3M10.5 20h3" /><path d="M2.5 12h3M4 10.5v3" /><path d="M18.5 12h3M20 10.5v3" />
          </g>
        )}
        {type === "GenAi" && (
          // A sparkle
          <path fill="currentColor" d="M12 2.5l2.4 6.9 6.9 2.6-6.9 2.6L12 21.5l-2.4-6.9L2.7 12l6.9-2.6z" />
        )}
        {!TYPE_META[type] && <circle cx="12" cy="12" r="6" fill="currentColor" />}
      </svg>
      {showLabel && <span>{meta.label}</span>}
    </span>
  );
}

/** Shown beside a dashboard whose BI type is switched off in Admin Configuration (C56). Existing dashboards keep working. */
export function BiInactiveBadge({ type }: { type: string }) {
  const { isEnabled } = useBiTypes();
  if (isEnabled(type)) return null;
  return <span className="bi-inactive" title="This BI type is switched off. No new dashboards or versions can be published, but this one keeps working.">BI Inactive</span>;
}
