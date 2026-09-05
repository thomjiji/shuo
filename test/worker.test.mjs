import assert from "node:assert/strict";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import {
  convertFrames,
  getSettingsPath,
  importLegacySettings,
  parseSettings,
} from "../worker/dictation-daemon.mjs";

const settings = {
  version: 1,
  backend: { type: "transcribe-cpp" },
  transcriptionLanguage: "auto",
  chineseOutput: "simplified",
  microphone: { type: "system-default" },
  model: { id: "Qwen3-ASR-0.6B", path: "C:\\models\\qwen.gguf" },
};

test("Shuo settings path prefers explicit overrides without default fallback", () => {
  const directory = join(tmpdir(), "shuo-path-tests");
  const preferred = join(directory, "preferred.json");
  const compatible = join(directory, "compatible.json");
  const noLookup = () => { throw new Error("Explicit paths must not probe defaults"); };
  assert.equal(getSettingsPath({ SHUO_SETTINGS: preferred, WINDOWS_DICTATION_SETTINGS: compatible }, directory, noLookup), preferred);
  assert.equal(getSettingsPath({ WINDOWS_DICTATION_SETTINGS: compatible }, directory, noLookup), compatible);
  assert.equal(getSettingsPath({ SHUO_SETTINGS: "  ", WINDOWS_DICTATION_SETTINGS: compatible }, directory, noLookup), compatible);
});

test("Shuo defaults to its new folder and reuses an existing WindowsDictation file", () => {
  const directory = join(tmpdir(), "shuo-path-tests");
  const current = join(directory, "Shuo", "settings.json");
  const previous = join(directory, "WindowsDictation", "settings.json");
  for (const [files, expected] of [
    [[], current],
    [[current], current],
    [[previous], previous],
    [[current, previous], current],
  ]) {
    assert.equal(getSettingsPath({ LOCALAPPDATA: directory }, undefined, (path) => files.includes(path)), expected);
  }
});

test("Shuo reads the previous settings in place without losing app preferences", () => {
  const directory = mkdtempSync(join(tmpdir(), "shuo-settings-"));
  const previousPath = join(directory, "WindowsDictation", "settings.json");
  const stored = JSON.stringify({
    ...settings,
    hotkey: { modifiers: 3, virtualKey: 0xDC },
    removeFillerWords: true,
    trimTrailingPeriod: true,
    customPreference: "keep",
  });
  mkdirSync(join(directory, "WindowsDictation"));
  writeFileSync(previousPath, stored);
  try {
    const imported = importLegacySettings({ environment: { LOCALAPPDATA: directory }, home: directory });
    assert.equal(imported.model.path, settings.model.path);
    assert.equal(readFileSync(previousPath, "utf8"), stored);
    assert.equal(existsSync(join(directory, "Shuo")), false);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test("worker ignores app-only hotkey preferences", () => {
  for (const hotkey of [{ modifiers: 3, virtualKey: 0xDC }, null]) {
    const parsed = parseSettings({ ...settings, hotkey });
    assert.equal("hotkey" in parsed, false);
  }
});

test("Shuo imports pi-transcribe settings only once", () => {
  const directory = mkdtempSync(join(tmpdir(), "shuo-"));
  const legacyPath = join(directory, "pi-transcribe.json");
  const settingsPath = join(directory, "owned", "settings.json");
  const autocorrectPath = join(directory, "autocorrect.exe");
  writeFileSync(legacyPath, JSON.stringify(settings));
  writeFileSync(autocorrectPath, "placeholder");

  try {
    const imported = importLegacySettings({
      settingsPath,
      legacyPath,
      environment: { PI_TRANSCRIBE_AUTOCORRECT_PATH: autocorrectPath },
      home: directory,
    });

    assert.equal(imported.model.path, settings.model.path);
    assert.equal(imported.autocorrectPath, autocorrectPath);
    assert.equal(existsSync(settingsPath), true);
    assert.equal(JSON.parse(readFileSync(settingsPath, "utf8")).autocorrectPath, autocorrectPath);

    writeFileSync(legacyPath, "{}");
    assert.equal(importLegacySettings({ settingsPath, legacyPath }).model.id, "Qwen3-ASR-0.6B");
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test("worker validates settings and converts PCM frames", () => {
  assert.throws(() => parseSettings({ ...settings, model: { id: "missing path" } }), /Missing model settings/);
  assert.deepEqual([...convertFrames([Int16Array.of(-32_768, 0), Int16Array.of(16_384)])], [-1, 0, 0.5]);
});
