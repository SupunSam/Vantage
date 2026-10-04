import { Fragment } from "react";
import { Icon } from "@vantage/shared";

export type FolderTile = { key: string; name: string; sub: string; onOpen: () => void };

/** A grid of folder tiles, used for categories on Home and for Personal Folders. Click a tile to go inside it. */
export function FolderGrid({ tiles }: { tiles: FolderTile[] }) {
  return (
    <ul className="folder-grid">
      {tiles.map((t) => (
        <li key={t.key}>
          <button type="button" className="folder-tile" onClick={t.onOpen}>
            <span className="folder-tile-icon" aria-hidden="true"><Icon name="folder" size={30} /></span>
            <span className="folder-tile-text">
              <strong>{t.name}</strong>
              <span>{t.sub}</span>
            </span>
            <Icon name="chevronRight" size={18} />
          </button>
        </li>
      ))}
    </ul>
  );
}

/** Where you are inside folders, with every step but the last a way back. */
export function Crumbs({ trail }: { trail: { label: string; onClick?: () => void }[] }) {
  return (
    <nav className="crumbs" aria-label="You are here">
      {trail.map((c, i) => (
        <Fragment key={i}>
          {i > 0 && <Icon name="chevronRight" size={14} />}
          {c.onClick && i < trail.length - 1
            ? <button type="button" className="link" onClick={c.onClick}>{c.label}</button>
            : <span className="crumb-here" aria-current={i === trail.length - 1 ? "page" : undefined}>{c.label}</span>}
        </Fragment>
      ))}
    </nav>
  );
}

export const plural = (n: number, one: string, many = `${one}s`) => `${n.toLocaleString()} ${n === 1 ? one : many}`;
