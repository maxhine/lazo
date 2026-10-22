// Builds engine/bin/Lazo.Engine.exe (the engine without windows, docs/engine-protocol.md) for the
// Windows bundle. Other systems have their own engine, so there it does nothing.
import { spawnSync } from "node:child_process";
import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

if (process.platform !== "win32") process.exit(0);
const script = join(dirname(fileURLToPath(import.meta.url)), "../../engine/build.ps1");
if (!existsSync(script)) process.exit(0);
const r = spawnSync("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Release"], { stdio: "inherit" });
process.exit(r.status ?? 1);
