// Squarified treemap (Bruls, Huizing, van Wijk): the rectangles of what is biggest, as close to squares as can be.
// Xpace's map uses it to draw a folder's contents.

export interface Rect {
  x: number;
  y: number;
  w: number;
  h: number;
}

/**
 * Lays `items` out inside `box`, each with an area proportional to its `size`, the biggest first.
 * Items without size are left out. The rectangles tile the box exactly.
 */
export function squarify<T extends { size: number }>(items: readonly T[], box: Rect): { item: T; rect: Rect }[] {
  const list = items.filter((i) => i.size > 0).sort((a, b) => b.size - a.size);
  const total = list.reduce((sum, i) => sum + i.size, 0);
  const out: { item: T; rect: Rect }[] = [];
  if (list.length === 0 || box.w <= 0 || box.h <= 0 || total <= 0) return out;

  const scale = (box.w * box.h) / total;
  let { x, y, w, h } = box;
  /** The worst aspect ratio of a row laid along a side of length `side`. */
  const worst = (row: number[], side: number) => {
    const sum = row.reduce((a, b) => a + b, 0);
    return Math.max((side * side * Math.max(...row)) / (sum * sum), (sum * sum) / (side * side * Math.min(...row)));
  };

  let i = 0;
  while (i < list.length) {
    const side = Math.min(w, h);
    const row: number[] = [];
    let j = i;
    while (j < list.length) {
      const area = list[j].size * scale;
      if (row.length > 0 && worst([...row, area], side) > worst(row, side)) break;
      row.push(area);
      j++;
    }
    const sum = row.reduce((a, b) => a + b, 0);
    if (w >= h) {
      // A column on the left, as tall as what is left.
      const colW = sum / h;
      let cy = y;
      row.forEach((area, k) => {
        const rh = area / colW;
        out.push({ item: list[i + k], rect: { x, y: cy, w: colW, h: rh } });
        cy += rh;
      });
      x += colW;
      w -= colW;
    } else {
      // A row on top, as wide as what is left.
      const rowH = sum / w;
      let cx = x;
      row.forEach((area, k) => {
        const rw = area / rowH;
        out.push({ item: list[i + k], rect: { x: cx, y, w: rw, h: rowH } });
        cx += rw;
      });
      y += rowH;
      h -= rowH;
    }
    i = j;
  }
  return out;
}
