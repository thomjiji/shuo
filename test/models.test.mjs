import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, readdirSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { getModelDirectory, findLocalModels } from "../worker/models.mjs";
import { DictationDaemon, parseSettings } from "../worker/dictation-daemon.mjs";

function fixture(t) {
  const root = mkdtempSync(join(tmpdir(), "shuo-model-tests-"));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const file = (...parts) => {
    const path = join(root, ...parts);
    mkdirSync(join(path, ".."), { recursive: true });
    writeFileSync(path, "GGUF fixture");
    return path;
  };
  return { root, file };
}

test("Hugging Face discovery finds sibling model snapshots, not blobs or other cache folders", async (t) => {
  const { root, file } = fixture(t);
  const qwen = file("hub", "models--handy-computer--Qwen3-ASR-0.6B-gguf", "snapshots", "rev-qwen", "Qwen3-ASR-0.6B-Q8_0.gguf");
  const fun = file("hub", "models--handy-computer--Fun-ASR-MLT-Nano-2512-gguf", "snapshots", "rev-fun", "Fun-ASR-MLT-Nano-2512-Q8_0.gguf");
  file("hub", "models--handy-computer--Fun-ASR-MLT-Nano-2512-gguf", "blobs", "duplicate.gguf");
  file("hub", "datasets--other", "snapshots", "rev", "unrelated.gguf");
  file("hub", "models--partial", "snapshots", "rev", "download.gguf.incomplete");
  const empty = file("hub", "models--partial", "snapshots", "rev", "empty.gguf");
  writeFileSync(empty, "");
  file("outside", "outside.gguf");
  assert.deepEqual(getModelDirectory(qwen), { path: join(root, "hub"), huggingFace: true });
  const models = await findLocalModels(qwen);
  assert.deepEqual(models.map((model) => model.path).sort(), [qwen, fun].sort());
  assert.equal(models.find((model) => model.path === fun).id, "Fun-ASR-MLT-Nano-2512");
  assert.deepEqual(await findLocalModels(fun), models);
});

test("plain model directories stay local and a refresh discovers new downloads", async (t) => {
  const { file } = fixture(t);
  const current = file("models", "Qwen-Q8_0.gguf");
  file("sibling", "outside.gguf");
  file("models", "nested", "outside.gguf");
  assert.deepEqual((await findLocalModels(current)).map((model) => model.path), [current]);
  const next = file("models", "Fun-Q4_K_M.GGUF");
  assert.equal((await findLocalModels(current)).length, 2);
  assert.equal((await findLocalModels(current)).find((model) => model.path === next).id, "Fun");
});

function daemonFixture(t, load) {
  const { root, file } = fixture(t);
  const originalPath = file("models", "Qwen-Q8_0.gguf");
  const nextPath = file("models", "Fun-Q8_0.gguf");
  const outsidePath = file("elsewhere", "Other.gguf");
  const settingsPath = join(root, "settings.json");
  const settings = {
    version: 1, backend: { type: "transcribe-cpp" }, model: { id: "Qwen", path: originalPath },
    transcriptionLanguage: "auto", chineseOutput: "simplified", microphone: { type: "system-default" },
    hotkey: { modifiers: 2, virtualKey: 220 }, removeFillerWords: true, custom: { keep: true },
  };
  writeFileSync(settingsPath, JSON.stringify(settings));
  const daemon = new DictationDaemon(parseSettings(settings), {
    OpenCC: { Converter: () => (text) => text }, TranscribeModel: { load },
  }, settingsPath);
  return { root, settings, settingsPath, daemon, originalPath, nextPath, outsidePath };
}

test("switch releases the old model before loading and preserves the latest app preferences", async (t) => {
  const events = [];
  const nextModel = { dispose: () => events.push("next disposed") };
  let release;
  const gate = new Promise((resolve) => { release = resolve; });
  const { daemon, settings, settingsPath, nextPath } = daemonFixture(t, async (path) => {
    events.push("load " + path);
    await gate;
    return nextModel;
  });
  daemon.model = { dispose: () => events.push("old disposed") };
  const switching = daemon.selectModel(nextPath);
  await new Promise((resolve) => setTimeout(resolve, 25));
  assert.equal(daemon.state, "switching");
  assert.equal(JSON.parse(readFileSync(settingsPath)).model.path, settings.model.path);
  writeFileSync(settingsPath, JSON.stringify({ ...settings, removeFillerWords: false, trimTrailingPeriod: true }));
  release();
  await switching;
  assert.deepEqual(events, ["old disposed", "load " + nextPath]);
  const saved = JSON.parse(readFileSync(settingsPath));
  assert.deepEqual(saved.model, { id: "Fun", path: nextPath });
  assert.deepEqual(saved.hotkey, settings.hotkey);
  assert.deepEqual(saved.custom, settings.custom);
  assert.equal(saved.removeFillerWords, false);
  assert.equal(saved.trimTrailingPeriod, true);
  assert.equal(daemon.state, "idle");
  assert.equal(daemon.model, nextModel);
});

test("failed loading keeps the original selection and can reload it for the next dictation", async (t) => {
  const oldModel = { dispose() {} };
  const { daemon, settingsPath, originalPath, nextPath } = daemonFixture(t, async (path) => {
    if (path === nextPath) throw new Error("unsupported model");
    return oldModel;
  });
  const before = readFileSync(settingsPath, "utf8");
  daemon.model = oldModel;
  await assert.rejects(daemon.selectModel(nextPath), /unsupported model/);
  assert.equal(readFileSync(settingsPath, "utf8"), before);
  assert.equal(daemon.settings.model.path, originalPath);
  assert.equal(daemon.state, "idle");
  assert.equal(await daemon.loadModel(), oldModel);
});

test("failed persistence disposes the replacement without changing the active selection", async (t) => {
  let disposed = false;
  const { root, daemon, settingsPath, originalPath, nextPath } = daemonFixture(t, async () => ({ dispose() { disposed = true; } }));
  writeFileSync(settingsPath, "invalid json");
  await assert.rejects(daemon.selectModel(nextPath), SyntaxError);
  assert.equal(disposed, true);
  assert.equal(daemon.settings.model.path, originalPath);
  assert.equal(daemon.state, "idle");
  assert.equal(readFileSync(settingsPath, "utf8"), "invalid json");
  assert.equal(readdirSync(root).some((name) => name.endsWith(".tmp")), false);
});

test("recording, transcribing and out-of-directory selections never load or save a replacement", async (t) => {
  const { daemon, settingsPath, nextPath, outsidePath } = daemonFixture(t, () => { throw new Error("must not load"); });
  const before = readFileSync(settingsPath, "utf8");
  for (const state of ["recording", "transcribing", "switching"]) {
    daemon.state = state;
    await assert.rejects(daemon.selectModel(nextPath), /等待/);
    assert.equal(daemon.state, state);
  }
  daemon.state = "idle";
  await assert.rejects(daemon.selectModel(outsidePath), /不在/);
  assert.equal(readFileSync(settingsPath, "utf8"), before);
  assert.equal(daemon.state, "idle");
});
