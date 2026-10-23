// The enter / over / leave of a file dragged over the island, made steady. The page's dragenter and dragleave fire for every
// element crossed, and again whenever the island rebuilds what is under the pointer (the tiles of the people, a view that
// opens): taken as they come they flap enter / leave dozens of times a second, and a drop could land on "not dragging".
// Here a leave only counts when nothing was entered or crossed for a moment, and an "over" implies the enter.

export type DragPhase = "enter" | "over" | "leave";

export class DragFlow {
  private inside = false;
  private timer: unknown = null;

  private emit: (phase: DragPhase, x: number, y: number) => void;
  private wait: number;
  private setTimer: (fn: () => void, ms: number) => unknown;
  private clearTimer: (id: unknown) => void;

  /** The timer functions can be swapped, so tests can run it without waiting. */
  constructor(
    emit: (phase: DragPhase, x: number, y: number) => void,
    wait = 90,
    setTimer: (fn: () => void, ms: number) => unknown = (fn, ms) => setTimeout(fn, ms),
    clearTimer: (id: unknown) => void = (id) => clearTimeout(id as ReturnType<typeof setTimeout>),
  ) {
    this.emit = emit;
    this.wait = wait;
    this.setTimer = setTimer;
    this.clearTimer = clearTimer;
  }

  private stay() {
    if (this.timer != null) this.clearTimer(this.timer);
    this.timer = null;
  }

  /** The pointer came in (or crossed into another element). */
  enter(x: number, y: number) {
    this.stay();
    if (!this.inside) {
      this.inside = true;
      this.emit("enter", x, y);
    }
  }

  /** The pointer moved. */
  over(x: number, y: number) {
    this.enter(x, y);
    this.emit("over", x, y);
  }

  /** The pointer left an element: it left the island only if nothing else is entered soon. */
  leave() {
    if (!this.inside) return;
    this.stay();
    this.timer = this.setTimer(() => {
      this.timer = null;
      this.inside = false;
      this.emit("leave", -1, -1);
    }, this.wait);
  }

  /** The files were dropped: the drag is over. */
  drop() {
    this.stay();
    this.inside = false;
  }
}
