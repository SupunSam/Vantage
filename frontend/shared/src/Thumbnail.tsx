import { useEffect, useState } from "react";
import { apiObjectUrl } from "./api";
import { TypeGlyph } from "./TypeGlyph";

/**
 * A dashboard's uploaded thumbnail (16:9), or the type drawing when there is none. Images are fetched with the
 * sign-in header, so they go through the API rather than a plain <img src>. `version` changes when the image does.
 */
export function Thumbnail({ dashboardId, version, type, alt = "" }: { dashboardId: number; version: string | null; type: string; alt?: string }) {
  const [url, setUrl] = useState<string | null>(null);

  useEffect(() => {
    if (!version) {
      setUrl(null);
      return;
    }
    let cancelled = false;
    let made: string | null = null;
    apiObjectUrl(`/api/dashboards/${dashboardId}/thumbnail?v=${encodeURIComponent(version)}`)
      .then((u) => {
        made = u;
        if (cancelled) URL.revokeObjectURL(u);
        else setUrl(u);
      })
      .catch(() => !cancelled && setUrl(null));
    return () => {
      cancelled = true;
      if (made) URL.revokeObjectURL(made);
    };
  }, [dashboardId, version]);

  return url ? <img className="thumb-img" src={url} alt={alt} /> : <TypeGlyph type={type} seed={dashboardId} />;
}
