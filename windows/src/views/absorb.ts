// A file dropped on a person is absorbed by them: a small card with its type flies from where it was let go to their tile,
// shrinking as it goes, and the tile takes it in with a pulse. (The big picture that follows the pointer while dragging is
// Explorer's own and cannot be changed from here; this takes over the moment it is dropped.)

/** What a file looks like on the little card: its type in capitals ("LAS", "PDF"), or a dot-less "FILE". */
export function fileBadge(name: string): string {
  const file = name.split(/[\\/]/).pop() ?? name;
  const dot = file.lastIndexOf(".");
  const ext = dot > 0 ? file.slice(dot + 1) : "";
  return /^[A-Za-z0-9]{1,5}$/.test(ext) ? ext.toUpperCase() : "FILE";
}

const FLIGHT_MS = 460;

/**
 * Flies a card from `from` (page coordinates) into `tile`. Resolves when it has been absorbed; the tile is pulsing by then.
 * Without the Web Animations API (or with reduced motion) it resolves at once, so a drop is never slowed down for nothing.
 */
export function absorb(from: { x: number; y: number }, tile: HTMLElement, fileName: string, accent: string): Promise<void> {
  const reduced = typeof matchMedia === "function" && matchMedia("(prefers-reduced-motion: reduce)").matches;
  if (reduced || typeof tile.animate !== "function") return Promise.resolve();
  const rect = tile.getBoundingClientRect();
  const to = { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 };
  const size = 56;

  const ghost = document.createElement("div");
  ghost.className = "absorb-ghost";
  ghost.textContent = fileBadge(fileName);
  ghost.style.setProperty("--acc", accent);
  ghost.style.width = ghost.style.height = `${size}px`;
  ghost.style.left = `${from.x - size / 2}px`;
  ghost.style.top = `${from.y - size / 2}px`;
  document.body.append(ghost);

  const dx = to.x - from.x;
  const dy = to.y - from.y;
  const flight = ghost.animate(
    [
      { transform: "translate(0, 0) scale(1) rotate(0deg)", opacity: 1, offset: 0 },
      { transform: `translate(${dx * 0.35}px, ${dy * 0.35 - 14}px) scale(0.8) rotate(-6deg)`, opacity: 1, offset: 0.35 },
      { transform: `translate(${dx}px, ${dy}px) scale(0.06) rotate(18deg)`, opacity: 0.15, offset: 1 },
    ],
    { duration: FLIGHT_MS, easing: "cubic-bezier(0.55, 0, 0.85, 0.45)", fill: "forwards" },
  );
  return flight.finished.then(
    () => {
      ghost.remove();
      tile.classList.remove("absorbed");
      void tile.offsetWidth; // so the pulse starts over
      tile.classList.add("absorbed");
      window.setTimeout(() => tile.classList.remove("absorbed"), 600);
    },
    () => ghost.remove(),
  );
}
