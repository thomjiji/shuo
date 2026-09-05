import { readFileSync, writeFileSync, renameSync, rmSync } from "node:fs";
import { readdir, stat, realpath } from "node:fs/promises";
import { randomUUID } from "node:crypto";
import { basename, dirname, extname, join, resolve } from "node:path";

export function getModelDirectory(modelPath) {
  const folder = dirname(resolve(modelPath));
  // pi-transcribe uses <cache>/models--owner--repo/snapshots/<revision>/<file>.
  const snapshots = dirname(folder);
  const repository = dirname(snapshots);
  if (basename(snapshots) === "snapshots" && basename(repository).startsWith("models--")) {
    return { path: dirname(repository), huggingFace: true };
  }
  return { path: folder, huggingFace: false };
}

async function entries(path) {
  try {
    return await readdir(path, { withFileTypes: true });
  } catch (error) {
    if (error.code === "ENOENT" || error.code === "ENOTDIR") return [];
    throw error;
  }
}

export async function findLocalModels(currentPath) {
  const directory = getModelDirectory(currentPath);
  const folders = [];
  if (directory.huggingFace) {
    for (const repository of await entries(directory.path)) {
      if (!repository.isDirectory() || !repository.name.startsWith("models--")) continue;
      const snapshots = join(directory.path, repository.name, "snapshots");
      for (const revision of await entries(snapshots)) {
        if (revision.isDirectory()) folders.push(join(snapshots, revision.name));
      }
    }
  } else {
    folders.push(directory.path);
  }

  const candidates = [];
  for (const folder of folders) {
    for (const file of await entries(folder)) {
      if (extname(file.name).toLowerCase() !== ".gguf" || (!file.isFile() && !file.isSymbolicLink())) continue;
      const path = join(folder, file.name);
      try {
        const info = await stat(path);
        if (!info.isFile() || info.size === 0) continue;
        candidates.push({
          id: basename(file.name, extname(file.name)).replace(/-(?:Q\d[A-Z0-9_]*|IQ\d[A-Z0-9_]*|BF16|F16|F32)$/i, ""),
          name: basename(file.name, extname(file.name)),
          path,
          target: await realpath(path),
          modified: info.mtimeMs,
        });
      } catch (error) {
        // Downloads/cache cleanup can add or remove a snapshot while listing it.
        if (error.code !== "ENOENT") throw error;
      }
    }
  }
  const normalize = (path) => process.platform === "win32" ? resolve(path).toLowerCase() : resolve(path);
  const active = normalize(currentPath);
  candidates.sort((a, b) => Number(normalize(b.path) === active) - Number(normalize(a.path) === active) || b.modified - a.modified);
  const seen = new Set();
  return candidates.filter((model) => {
    const key = normalize(model.target);
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  }).map(({ id, name, path }) => ({ id, name, path })).sort((a, b) => a.name.localeCompare(b.name) || a.path.localeCompare(b.path));
}

export function saveModelSelection(settingsPath, model) {
  const value = JSON.parse(readFileSync(settingsPath, "utf8"));
  if (!value || typeof value !== "object" || Array.isArray(value)) throw new Error("Invalid settings object");
  value.model = { id: model.id, path: model.path };
  const temporary = settingsPath + "." + randomUUID() + ".tmp";
  try {
    writeFileSync(temporary, JSON.stringify(value, null, 2) + "\n", { flag: "wx" });
    renameSync(temporary, settingsPath);
  } finally {
    rmSync(temporary, { force: true });
  }
}
