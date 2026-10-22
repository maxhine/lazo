// Builds lazo/bin/Lazo.exe, the classic Lazo program whose tools (point clouds, Xpace, Descanso Visual)
// the island opens as they are (src-tauri/src/lazo_tools.rs). Only Windows has it; elsewhere this does nothing.
import { spawnSync } from "node:child_process";
import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

if (process.platform !== "win32") process.exit(0);
const script = join(dirname(fileURLToPath(import.meta.url)), "../../lazo/scripts/build.ps1");
if (!existsSync(script)) process.exit(0);
const r = spawnSync("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Release"], { stdio: "inherit" });
process.exit(r.status ?? 1);
