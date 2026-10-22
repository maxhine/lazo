// The character is a bow (lazo): shared by the main body, the greeting and the outfits.

/**
 * The body is a bow (lazo): two plump wings pinched at the waist. Unit coordinates (x, y in −1…1,
 * scaled by rx/ry), as a closed polyline that starts at the top of the waist and runs clockwise.
 * Right half as cubic Béziers, the left half is its mirror image.
 */
export const BOW_OUTLINE: ReadonlyArray<{ x: number; y: number }> = (() => {
  const seg: ReadonlyArray<readonly [number, number][]> = [
    [[0, -0.6], [0.3, -0.62], [0.52, -0.98], [0.78, -0.98]],
    [[0.78, -0.98], [0.96, -0.98], [1, -0.76], [1, -0.5]],
    [[1, -0.5], [1, -0.17], [1, 0.17], [1, 0.5]],
    [[1, 0.5], [1, 0.76], [0.96, 0.98], [0.78, 0.98]],
    [[0.78, 0.98], [0.52, 0.98], [0.3, 0.62], [0, 0.6]],
  ];
  const steps = 14;
  const right: { x: number; y: number }[] = [];
  seg.forEach((c, si) => {
    for (let i = si === 0 ? 0 : 1; i <= steps; i++) {
      const t = i / steps, u = 1 - t;
      const b = [u * u * u, 3 * u * u * t, 3 * u * t * t, t * t * t];
      right.push({
        x: b[0] * c[0][0] + b[1] * c[1][0] + b[2] * c[2][0] + b[3] * c[3][0],
        y: b[0] * c[0][1] + b[1] * c[1][1] + b[2] * c[2][1] + b[3] * c[3][1],
      });
    }
  });
  // right ends at the bottom of the waist; the mirror comes back up without repeating either centre point
  const left = right.slice(1, -1).reverse().map((v) => ({ x: -v.x, y: v.y }));
  return [...right, ...left];
})();

/** The bow outline as a closed Path2D for a body of half-width `rx` and half-height `ry`. */
export function bowPath(rx: number, ry: number): Path2D {
  const p = new Path2D();
  BOW_OUTLINE.forEach((v, i) => (i === 0 ? p.moveTo(v.x * rx, v.y * ry) : p.lineTo(v.x * rx, v.y * ry)));
  p.closePath();
  return p;
}
