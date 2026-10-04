import { useEffect, useRef } from "react";

type Props = {
  /** The view's address as the Embedding API wants it (the portal sends it ready). */
  src: string;
  /** A Connected App token for Tableau Server. Leave it out for Tableau Public. */
  token?: string | null;
  /** The Embedding API script: Tableau Public's, or the Tableau Server's own. */
  scriptUrl: string;
  /** Called when the library can't load or Tableau refuses to open the view (for example an unknown Tableau user). */
  onError?: (message: string) => void;
};

/**
 * Shows a Tableau view with the Embedding API v3: loads the script once, then renders a `<tableau-viz>`.
 * The address and token come from the portal's API, never from the page, so a viewer can't point it somewhere else.
 */
export function TableauViz({ src, token, scriptUrl, onError }: Props) {
  const host = useRef<HTMLDivElement>(null);
  const report = useRef(onError);
  report.current = onError;

  useEffect(() => {
    const el = host.current;
    if (!el) return;
    let gone = false;

    const render = () => {
      if (gone) return;
      el.innerHTML = "";
      const viz = document.createElement("tableau-viz");
      viz.setAttribute("src", src);
      if (token) viz.setAttribute("token", token);
      viz.setAttribute("toolbar", "bottom");
      viz.setAttribute("hide-tabs", "false");
      viz.style.width = "100%";
      viz.style.height = "100%";
      viz.addEventListener("vizloaderror", (e) => {
        const detail = (e as CustomEvent<{ message?: string }>).detail;
        report.current?.(detail?.message ?? "Tableau couldn't open the view.");
      });
      el.appendChild(viz);
    };

    if (customElements.get("tableau-viz")) render();
    else {
      let script = document.querySelector<HTMLScriptElement>(`script[data-tableau-api="${scriptUrl}"]`);
      if (!script) {
        script = document.createElement("script");
        script.type = "module";
        script.src = scriptUrl;
        script.dataset.tableauApi = scriptUrl;
        document.head.appendChild(script);
      }
      script.addEventListener("load", render);
      script.addEventListener("error", () => report.current?.(`Couldn't load the Tableau embedding library from ${new URL(scriptUrl).host}.`));
    }

    return () => {
      gone = true;
      el.innerHTML = "";
    };
  }, [src, token, scriptUrl]);

  return <div ref={host} className="tableau-host" style={{ width: "100%", height: "100%" }} />;
}
