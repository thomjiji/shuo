import assert from "node:assert/strict";
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
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

test("Windows Dictation owns its settings path", () => {
  assert.equal(
    getSettingsPath({ LOCALAPPDATA: "C:\\local" }),
    "C:\\local\\WindowsDictation\\settings.json",
  );
  assert.equal(
    getSettingsPath({ WINDOWS_DICTATION_SETTINGS: "C:\\custom\\settings.json" }),
    "C:\\custom\\settings.json",
  );
});

test("Windows Dictation imports pi-transcribe settings only once", () => {
  const directory = mkdtempSync(join(tmpdir(), "windows-dictation-"));
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
