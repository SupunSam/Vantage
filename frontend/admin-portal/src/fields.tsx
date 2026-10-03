import { useEffect, useMemo, useState, type KeyboardEvent } from "react";
import { Icon } from "@vantage/shared";
import type { CategoryNode } from "./pages/CategoriesPage";

/**
 * Primary (required), Secondary and Tertiary category pickers. The value is the deepest category chosen,
 * which is where the dashboard sits.
 */
export function CategoryPicker({ categories, value, onChange }: { categories: CategoryNode[]; value: number | null; onChange: (id: number | null) => void }) {
  const byId = useMemo(() => new Map(categories.map((c) => [c.id, c])), [categories]);
  const chain: (number | null)[] = [null, null, null];
  let cur = value != null ? byId.get(value) : undefined;
  while (cur) {
    chain[cur.level - 1] = cur.id;
    cur = cur.parentId != null ? byId.get(cur.parentId) : undefined;
  }
  const [l1, l2, l3] = chain;
  const childrenOf = (parent: number | null) => categories.filter((c) => c.parentId === parent);
  const level2 = l1 != null ? childrenOf(l1) : [];
  const level3 = l2 != null ? childrenOf(l2) : [];

  return (
    <>
      <label className="field">
        <span>Primary category</span>
        <select required value={l1 ?? ""} onChange={(e) => onChange(e.target.value ? Number(e.target.value) : null)}>
          <option value="">Choose…</option>
          {childrenOf(null).map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
        </select>
      </label>
      <label className="field">
        <span>Secondary category <span className="optional">(optional)</span></span>
        <select value={l2 ?? ""} disabled={level2.length === 0} onChange={(e) => onChange(e.target.value ? Number(e.target.value) : l1)}>
          <option value="">{level2.length === 0 ? "None available" : "None"}</option>
          {level2.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
        </select>
      </label>
      <label className="field">
        <span>Tertiary category <span className="optional">(optional)</span></span>
        <select value={l3 ?? ""} disabled={level3.length === 0} onChange={(e) => onChange(e.target.value ? Number(e.target.value) : l2)}>
          <option value="">{level3.length === 0 ? "None available" : "None"}</option>
          {level3.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
        </select>
      </label>
    </>
  );
}

/** Tags as removable chips; Enter, comma or space adds one. Letters and digits only, up to 10 characters, 8 tags. */
export function TagInput({ value, onChange, max = 8, maxLength = 10 }: { value: string[]; onChange: (tags: string[]) => void; max?: number; maxLength?: number }) {
  const [text, setText] = useState("");
  const [hint, setHint] = useState<string | null>(null);

  function add(raw: string) {
    const tag = raw.trim();
    if (!tag) return;
    if (!/^[A-Za-z0-9]+$/.test(tag)) return setHint("Tags are letters and digits only.");
    if (tag.length > maxLength) return setHint(`Tags are up to ${maxLength} characters.`);
    if (value.some((t) => t.toLowerCase() === tag.toLowerCase())) return setText("");
    if (value.length >= max) return setHint(`Up to ${max} tags.`);
    onChange([...value, tag]);
    setText("");
    setHint(null);
  }

  function onKey(e: KeyboardEvent<HTMLInputElement>) {
    if (e.key === "Enter" || e.key === "," || e.key === " ") {
      e.preventDefault();
      add(text);
    } else if (e.key === "Backspace" && !text && value.length) {
      onChange(value.slice(0, -1));
    }
  }

  return (
    <div className="field">
      <span>Tags <span className="optional">({value.length} of {max})</span></span>
      <div className="tags-input">
        {value.map((t) => (
          <span key={t} className="tag">
            {t}
            <button type="button" aria-label={`Remove tag ${t}`} onClick={() => onChange(value.filter((x) => x !== t))}><Icon name="close" size={14} /></button>
          </span>
        ))}
        <input value={text} onChange={(e) => setText(e.target.value)} onKeyDown={onKey} onBlur={() => add(text)}
          placeholder={value.length >= max ? "" : "Type a tag, press Enter"} disabled={value.length >= max} aria-label="Add a tag" />
      </div>
      {hint && <small className="field-hint field-hint-bad">{hint}</small>}
    </div>
  );
}

/** Reads an image's pixel size in the browser so problems show before upload; the API checks again. */
export function checkThumbnail(file: File): Promise<string | null> {
  if (!/^image\/(png|jpeg)$/.test(file.type)) return Promise.resolve("Choose a PNG or JPG image.");
  if (file.size > 1024 * 1024) return Promise.resolve("The image is larger than 1 MB.");
  return new Promise((resolve) => {
    const url = URL.createObjectURL(file);
    const img = new Image();
    img.onload = () => {
      URL.revokeObjectURL(url);
      const { naturalWidth: w, naturalHeight: h } = img;
      if (w < 640 || h < 360) resolve(`The image is ${w} x ${h} px; it needs to be at least 640 x 360.`);
      else if (Math.abs(w / h - 16 / 9) > 0.03) resolve(`The image is ${w} x ${h} px; it needs a 16:9 shape, such as 640 x 360 or 1280 x 720.`);
      else resolve(null);
    };
    img.onerror = () => {
      URL.revokeObjectURL(url);
      resolve("That file couldn't be read as an image.");
    };
    img.src = url;
  });
}

/** Picks a thumbnail, checks it and shows a 16:9 preview. */
export function ThumbnailPicker({ file, onChange, current, stacked }: { file: File | null; onChange: (file: File | null) => void; current?: React.ReactNode; stacked?: boolean }) {
  const [preview, setPreview] = useState<string | null>(null);
  const [problem, setProblem] = useState<string | null>(null);

  useEffect(() => {
    if (!file) {
      setPreview(null);
      return;
    }
    const url = URL.createObjectURL(file);
    setPreview(url);
    return () => URL.revokeObjectURL(url);
  }, [file]);

  async function pick(f: File | null) {
    setProblem(null);
    if (!f) return onChange(null);
    const issue = await checkThumbnail(f);
    if (issue) {
      setProblem(issue);
      onChange(null);
    } else onChange(f);
  }

  return (
    <div className={`thumb-picker ${stacked ? "thumb-picker-stacked" : ""}`}>
      <div className="thumb-frame">{preview ? <img className="thumb-img" src={preview} alt="New thumbnail preview" /> : current}</div>
      <div className="thumb-picker-side">
        <label className="btn">
          <Icon name="image" size={18} /> {file ? "Choose Another Image" : "Choose an Image"}
          <input type="file" accept="image/png,image/jpeg" className="visually-hidden" onChange={(e) => void pick(e.target.files?.[0] ?? null)} />
        </label>
        {file && <button type="button" className="btn btn-quiet" onClick={() => void pick(null)}>Don't Use This Image</button>}
        <p className="muted small">PNG or JPG, 16:9 (640 x 360 or larger, such as 1280 x 720), up to 1 MB. Without one, a drawing for the dashboard type is shown.</p>
        {problem && <p className="field-hint field-hint-bad">{problem}</p>}
      </div>
    </div>
  );
}
