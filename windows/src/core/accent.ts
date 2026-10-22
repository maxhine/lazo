// A colour of its own for each person, drawn from the same palette as the pills' colours (core/pill-colors.ts), so a face
// keeps its colour in the people list, the chat strip and the drop targets. The white of the palette is left out: it is the
// island's own ink, not a colour that tells one person from another.

import { PILL_PALETTE } from "./pill-colors";

const COLORS = PILL_PALETTE.filter((c) => c !== "#F5F6F8");

/** The same id always gets the same colour. */
export function accentFor(id: string): string {
  let hash = 2166136261;
  for (let i = 0; i < id.length; i++) {
    hash ^= id.charCodeAt(i);
    hash = Math.imul(hash, 16777619);
  }
  return COLORS[(hash >>> 0) % COLORS.length];
}
