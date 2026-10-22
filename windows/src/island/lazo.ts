// The Lazo pill's side of the island: connects to the engine, and when somebody offers a file
// while the island is closed, brings the card up (as a permission request would).

import { IS_TAURI } from "../core/bridge";
import { LAZO_ID, startLazo } from "../core/lazo";
import { Sound } from "../core/sound";
import { State } from "../core/state";

export interface LazoIslandHost {
  /** Opens the island on its usual view. */
  alert(view: "overview"): void;
}

export function registerLazoHandlers(island: LazoIslandHost) {
  if (!IS_TAURI || !State.settings.activeIntegrations.includes(LAZO_ID)) return;
  startLazo({
    changed: () => State.notify(),
    offered: () => {
      State.setFocus(LAZO_ID);
      Sound.play("question");
      island.alert("overview");
    },
  });
}
