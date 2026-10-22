// The page's end of Lazo's tools program: one channel for all tools (src-tauri/src/lazo_tools.rs), and the model of
// each tool that is drawn inside the island.

import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { LazoTools } from "../lazo/tools";
import { XpaceModel } from "./xpace";
import { State } from "./state";

export const Tools = new LazoTools({
  send: (text) => invoke<void>("lazo_tools_send", { message: text }),
  subscribe: (handler) => listen<string>("lazo-tools", (e) => handler(e.payload)),
});

export const Xpace = new XpaceModel((command) => Tools.post(command), () => State.notify());
Tools.on((m) => Xpace.handle(m));
