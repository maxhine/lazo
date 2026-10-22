// Builds engine/bin/Lazo.Engine.exe: the classic Lazo logic (Xpace, point clouds…) without windows, which the island
// draws its tools over (src-tauri/src/lazo_tools.rs, docs/engine-protocol.md). Only Windows has it; elsewhere this does nothing.
import { spawnSync } from "node:child_process";
import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

if (process.platform !== "win32") process.exit(0);
const script = join(dirname(fileURLToPath(import.meta.url)), "../../engine/build.ps1");
if (!existsSync(script)) process.exit(0);
const r = spawnSync("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Release"], { stdio: "inherit" });
process.exit(r.status ?? 1);
