import { spawnSync } from "node:child_process";
import { mkdirSync, readdirSync, renameSync, existsSync } from "node:fs";
import { resolve, join } from "node:path";

const [version, output = "artifacts/installer"] = process.argv.slice(2);
if (!/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/.test(version ?? "")) throw new Error("Expected a release version such as 0.4.0");
if (process.platform !== "win32" || process.arch !== "x64") throw new Error("Packaging requires x64 Windows and x64 Node.js");
const destination = resolve(output);
const appDir = join(destination, "app");
const releases = join(destination, "releases");
if (existsSync(appDir) || existsSync(releases)) throw new Error("Choose a fresh output directory to avoid stale package files");
mkdirSync(destination, { recursive: true });
function run(command, args) {
  const result = spawnSync(command, args, { stdio: "inherit", windowsHide: true });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`${command} exited with ${result.status}`);
}
run("dotnet", ["tool", "restore"]);
run("dotnet", ["publish", "src/Shuo/Shuo.csproj", "-c", "Release", "-r", "win-x64", "-o", appDir,
  "-p:InstallerBundle=true", `-p:Version=${version}`, `-p:NodeRuntimePath=${process.execPath}`]);
for (const file of ["shuo.exe", "Assets/AppIcon.ico", "node.exe", "worker/dictation-daemon.mjs", "node_modules", "shuo.pri", "MainWindow.xbf"]) {
  if (!existsSync(join(appDir, file))) throw new Error(`Missing published dependency: ${file}`);
}
run("dotnet", ["tool", "run", "vpk", "--", "pack", "--packId", "ShuoDesktop", "--packVersion", version,
  "--packDir", appDir, "--mainExe", "shuo.exe", "--packTitle", "说", "--packAuthors", "thomjiji",
  "--icon", "src/Shuo/Assets/AppIcon.ico", "--shortcuts", "StartMenuRoot", "--channel", "win",
  "--outputDir", releases, "--delta", "None"]);
const setup = readdirSync(releases).find(name => name.endsWith("-Setup.exe"));
if (!setup) throw new Error("Installer was not generated");
renameSync(join(releases, setup), join(releases, `Shuo-${version}-win-x64-Setup.exe`));
console.log(`Installer and update feed: ${releases}`);
