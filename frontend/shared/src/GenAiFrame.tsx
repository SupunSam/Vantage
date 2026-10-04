/**
 * Shows a GenAI dashboard. The page comes from the separate GenAI origin (a signed link), and the frame is sandboxed
 * with scripts allowed but never "allow-same-origin", so the page can't read the portal's session or call its API.
 * Don't add other sandbox permissions here without a Super Admin decision recorded in the requirements.
 */
export function GenAiFrame({ url, title }: { url: string; title: string }) {
  return <iframe className="genai-frame" src={url} title={title} sandbox="allow-scripts" referrerPolicy="no-referrer" />;
}
