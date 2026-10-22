// Dev harness: dibuja el personaje en cada estado en un navegador normal. No forma parte de la app.
import { BotEngine, BOT_STATES, type BotStateName } from "../src/nudo/engine";

const row = document.getElementById("row")!;
const SIZE = 180;
const names = Object.keys(BOT_STATES) as BotStateName[];
const dpr = window.devicePixelRatio || 1;

const bots: { e: BotEngine; ctx: CanvasRenderingContext2D }[] = [];
for (const name of names) {
  const fig = document.createElement("figure");
  const c = document.createElement("canvas");
  c.width = SIZE * dpr; c.height = SIZE * dpr; c.style.width = `${SIZE}px`; c.style.height = `${SIZE}px`;
  const cap = document.createElement("figcaption"); cap.textContent = name;
  fig.append(c, cap); row.append(fig);
  const e = new BotEngine();
  e.setState(name, true);
  bots.push({ e, ctx: c.getContext("2d")! });
}
let last = performance.now();
function frame(t: number) {
  const dt = Math.min(0.05, (t - last) / 1000); last = t;
  for (const { e, ctx } of bots) {
    e.update(dt);
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, SIZE, SIZE);
    e.draw(ctx, SIZE, SIZE);
  }
  requestAnimationFrame(frame);
}
requestAnimationFrame(frame);
