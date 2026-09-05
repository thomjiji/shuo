import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { createInterface } from "node:readline";
import { fileURLToPath } from "node:url";

import { findLocalModels, saveModelSelection } from "./models.mjs";
import { DoubaoStream, doubaoHeaders } from "./doubao.mjs";

const SAMPLE_RATE = 16_000;
const FRAME_LENGTH = 512;
const STARTUP_GUARD_MS = 250;
const CHINESE_OUTPUTS = new Set(["simplified", "traditional-taiwan", "traditional-hong-kong"]);

function isRecord(value) {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function errorMessage(error) {
  return error instanceof Error ? error.message : String(error);
}

export function getSettingsPath(environment = process.env, localAppData = environment.LOCALAPPDATA, fileExists = existsSync) {
  const configured = environment.SHUO_SETTINGS?.trim() || environment.WINDOWS_DICTATION_SETTINGS?.trim();
  if (configured) return resolve(configured);

  const directory = localAppData?.trim() || join(homedir(), "AppData", "Local");
  const settingsPath = join(directory, "Shuo", "settings.json");
  const previousPath = join(directory, "WindowsDictation", "settings.json");
  return !fileExists(settingsPath) && fileExists(previousPath) ? previousPath : settingsPath;
}

export function getLegacySettingsPath(environment = process.env, home = homedir()) {
  const agentDir = environment.PI_CODING_AGENT_DIR?.trim() || join(home, ".pi", "agent");
  return join(agentDir, "pi-transcribe.json");
}

export function parseSettings(value, source = "settings.json") {
  if (!isRecord(value) || value.version !== 1 || value.backend?.type !== "transcribe-cpp") {
    throw new Error(`Invalid settings in ${source}`);
  }
  if (!isRecord(value.model) || typeof value.model.id !== "string" || typeof value.model.path !== "string" || (!value.model.path.trim() && value.model.id.trim())) {
    throw new Error(`Missing model settings in ${source}`);
  }
  if (typeof value.transcriptionLanguage !== "string" || !value.transcriptionLanguage.trim()) {
    throw new Error(`Missing transcription language in ${source}`);
  }
  if (!isRecord(value.microphone)) {
    throw new Error(`Missing microphone settings in ${source}`);
  }

  let microphone;
  if (value.microphone.type === "system-default") {
    microphone = { type: "system-default" };
  } else if (
    value.microphone.type === "device" &&
    typeof value.microphone.name === "string" &&
    value.microphone.name &&
    Number.isInteger(value.microphone.occurrence) &&
    value.microphone.occurrence >= 0
  ) {
    microphone = {
      type: "device",
      name: value.microphone.name,
      occurrence: value.microphone.occurrence,
    };
  } else {
    throw new Error(`Invalid microphone settings in ${source}`);
  }

  return {
    model: { id: value.model.id, path: value.model.path },
    transcriptionLanguage: value.transcriptionLanguage,
    chineseOutput: CHINESE_OUTPUTS.has(value.chineseOutput) ? value.chineseOutput : "simplified",
    microphone,
    autocorrectPath: typeof value.autocorrectPath === "string" && value.autocorrectPath.trim()
      ? value.autocorrectPath
      : undefined,
  };
}

function serializableSettings(settings) {
  return {
    version: 1,
    backend: { type: "transcribe-cpp" },
    model: settings.model,
    transcriptionLanguage: settings.transcriptionLanguage,
    chineseOutput: settings.chineseOutput,
    microphone: settings.microphone,
    ...(settings.autocorrectPath ? { autocorrectPath: settings.autocorrectPath } : {}),
  };
}

function legacyAutocorrectPath(environment, home) {
  const configured = environment.PI_TRANSCRIBE_AUTOCORRECT_PATH?.trim();
  if (configured && existsSync(configured)) return configured;

  const agentDir = environment.PI_CODING_AGENT_DIR?.trim() || join(home, ".pi", "agent");
  const candidate = join(agentDir, "bin", "autocorrect.exe");
  return existsSync(candidate) ? candidate : undefined;
}

export function importLegacySettings({
  settingsPath,
  legacyPath,
  environment = process.env,
  home = homedir(),
} = {}) {
  const target = settingsPath || getSettingsPath(environment);
  if (existsSync(target)) {
    return parseSettings(JSON.parse(readFileSync(target, "utf8")), target);
  }

  const legacy = legacyPath || getLegacySettingsPath(environment, home);
  const settings = existsSync(legacy)
    ? parseSettings(JSON.parse(readFileSync(legacy, "utf8")), legacy)
    : {
        model: { id: "", path: "" },
        transcriptionLanguage: "auto",
        chineseOutput: "simplified",
        microphone: { type: "system-default" },
      };

  settings.autocorrectPath = legacyAutocorrectPath(environment, home);
  mkdirSync(dirname(target), { recursive: true });
  writeFileSync(target, `${JSON.stringify(serializableSettings(settings), null, 2)}\n`);
  return settings;
}

export function audioLevel(frame) {
  if (!frame.length) return 0;
  let sum = 0;
  for (const sample of frame) sum += (sample / 32768) ** 2;
  const rms = Math.sqrt(sum / frame.length);
  return rms > 0 ? Math.max(0, Math.min(1, (20 * Math.log10(rms) + 55) / 40)) : 0;
}

export function convertFrames(frames) {
  const length = frames.reduce((total, frame) => total + frame.length, 0);
  const pcm = new Float32Array(length);
  let offset = 0;
  for (const frame of frames) {
    for (let index = 0; index < frame.length; index += 1) {
      pcm[offset + index] = frame[index] / 32_768;
    }
    offset += frame.length;
  }
  return pcm;
}

function readSettings() {
  const path = getSettingsPath();
  const settings = importLegacySettings({ settingsPath: path });
  return settings;
}

function emit(type, values = {}) {
  process.stdout.write(`${JSON.stringify({ type, ...values })}\n`);
}

function isChineseLanguage(language) {
  const base = language.toLowerCase().split("-", 1)[0];
  return base === "zh" || base === "yue";
}

function microphoneIndex(PvRecorder, microphone) {
  if (microphone.type === "system-default") return -1;

  let occurrence = 0;
  for (const [index, name] of PvRecorder.getAvailableDevices().entries()) {
    if (name !== microphone.name) continue;
    if (occurrence === microphone.occurrence) return index;
    occurrence += 1;
  }
  throw new Error(`Selected microphone is unavailable: ${microphone.name}`);
}

function createFormatter(OpenCC, chineseOutput) {
  const converter = OpenCC.Converter({
    simplified: { from: "t", to: "cn" },
    "traditional-taiwan": { from: "cn", to: "tw" },
    "traditional-hong-kong": { from: "cn", to: "hk" },
  }[chineseOutput]);

  return (text, detectedLanguage, configuredLanguage) => {
    const trimmed = text.trim();
    return isChineseLanguage(detectedLanguage || configuredLanguage || "")
      ? converter(trimmed)
      : trimmed;
  };
}

async function loadRuntime() {
  const [recorder, transcribe, opencc] = await Promise.all([
    import("@picovoice/pvrecorder-node"),
    import("transcribe-cpp"),
    import("opencc-js"),
  ]);
  return {
    PvRecorder: recorder.PvRecorder,
    TranscribeModel: transcribe.TranscribeModel,
    OpenCC: opencc.default,
  };
}

export class DictationDaemon {
  constructor(settings, runtime, settingsPath = getSettingsPath()) {
    this.settingsPath = settingsPath;
    this.settings = settings;
    this.runtime = runtime;
    this.format = createFormatter(runtime.OpenCC, settings.chineseOutput);
    this.state = "idle";
    this.model = undefined;
    this.modelLoading = undefined;
    this.recorder = undefined;
    this.readLoop = undefined;
    this.frames = [];
    this.captureError = undefined;
    this.stopping = false;
    this.provider = "local";
    this.cloudConfig = {};
    this.cloud = undefined;
  }

  configureBackend(command) {
    if (this.state !== "idle") throw new Error("请等待当前听写结束。");
    if (!["local", "doubao"].includes(command.provider)) throw new Error("未知转录服务。");
    if (command.provider === "doubao") doubaoHeaders(command.config || {});
    this.provider = command.provider;
    this.cloudConfig = command.config || {};
  }

  async testCloud() {
    if (this.state !== "idle") throw new Error("请等待当前听写结束。");
    this.state = "testing";
    const stream = new DoubaoStream(this.cloudConfig);
    try {
      await stream.connect();
      stream.feed(new Int16Array(3200));
      await stream.finish();
    } finally {
      stream.close();
      this.state = "idle";
    }
  }

  async abortRecording(error) {
    if (this.state !== "recording") return;
    this.state = "transcribing";
    await this.finishCapture().catch(() => {});
    this.cloud?.close();
    this.cloud = undefined;
    this.state = "idle";
    emit("error", { message: errorMessage(error) });
  }

  async toggle() {
    if (this.state === "idle") {
      try {
        await this.startRecording();
        // ponytail: fixed 250ms guard; replace with a recorder-ready signal if immediate stop matters.
        await new Promise((resolve) => setTimeout(resolve, STARTUP_GUARD_MS));
      } catch (error) {
        this.cloud?.close();
        this.cloud = undefined;
        this.state = "idle";
        emit("error", { message: errorMessage(error) });
      }
      return;
    }
    if (this.state !== "recording") {
      emit("busy");
      return;
    }

    this.state = "transcribing";
    emit("transcribing");
    try {
      const pcm = await this.finishCapture();
      if (this.cloud) {
        const result = await this.cloud.finish();
        const text = this.format(result.text, result.language, this.settings.transcriptionLanguage);
        emit(text ? "transcript" : "empty", text ? { text } : {});
        return;
      }
      if (pcm.length === 0) {
        emit("empty");
        return;
      }
      const model = await this.loadModel();
      const language = this.settings.transcriptionLanguage === "auto"
        ? undefined
        : this.settings.transcriptionLanguage;
      if (language && !model.capabilities.languages.includes(language)) {
        throw new Error(`Configured language ${language} is not supported by this model`);
      }
      const result = await model.transcribe(pcm, {
        timestamps: "none",
        ...(language ? { language } : {}),
      });
      const text = this.format(result.text, result.language, language);
      emit(text ? "transcript" : "empty", text ? { text } : {});
    } catch (error) {
      emit("error", { message: errorMessage(error) });
    } finally {
      this.cloud?.close();
      this.cloud = undefined;
      this.state = "idle";
    }
  }

  async selectModel(path) {
    if (this.state !== "idle") throw new Error("请等待当前听写完成后再切换模型。");
    this.state = "switching";
    try {
      const candidates = await findLocalModels(this.settings.model.path);
      const selected = candidates.find((model) => model.path === path);
      if (!selected) throw new Error("模型不在当前目录的可用列表中，请刷新后重试。");
      if (selected.path === this.settings.model.path) return;
      await this.modelLoading?.catch(() => undefined);
      // Release the previous model first so a switch does not require twice the RAM/VRAM.
      this.model?.dispose();
      this.model = undefined;
      this.modelLoading = undefined;
      const next = await this.runtime.TranscribeModel.load(selected.path);
      try {
        saveModelSelection(this.settingsPath, selected);
      } catch (error) {
        next.dispose();
        throw error;
      }
      this.model = next;
      this.settings.model = { id: selected.id, path: selected.path };
    } finally {
      // On failure the saved selection is unchanged and is lazily reloaded next time.
      this.state = "idle";
    }
  }

  async shutdown() {
    this.state = "stopping";
    this.cloud?.close();
    if (this.recorder) await this.finishCapture().catch(() => undefined);
    await this.modelLoading?.catch(() => undefined);
    this.model?.dispose();
    this.model = undefined;
  }

  async loadModel() {
    if (!this.settings.model.path) throw new Error("请先在转录服务中配置豆包云端，或在设置文件中指定本地模型。");
    if (this.model) return this.model;
    if (!this.modelLoading) {
      this.modelLoading = this.runtime.TranscribeModel.load(this.settings.model.path).then((model) => {
        this.model = model;
        return model;
      });
    }
    try {
      return await this.modelLoading;
    } catch (error) {
      this.modelLoading = undefined;
      throw error;
    }
  }

  async startRecording() {
    if (this.provider === "doubao") {
      this.state = "connecting";
      emit("connecting");
      this.cloud = new DoubaoStream(this.cloudConfig, (text) => emit("partial", { text }));
      try { await this.cloud.connect(); }
      catch (error) { this.cloud.close(); this.cloud = undefined; throw error; }
      this.cloud.result.catch((error) => { void this.abortRecording(error); });
    }
    const recorder = new this.runtime.PvRecorder(
      FRAME_LENGTH,
      microphoneIndex(this.runtime.PvRecorder, this.settings.microphone),
    );
    if (recorder.sampleRate !== SAMPLE_RATE) {
      recorder.release();
      throw new Error(`Recorder returned ${recorder.sampleRate} Hz; expected ${SAMPLE_RATE} Hz`);
    }

    this.frames = [];
    this.captureError = undefined;
    this.stopping = false;
    try {
      recorder.start();
      this.recorder = recorder;
      this.readLoop = this.readFrames(recorder);
      this.state = "recording";
      if (this.provider === "local") void this.loadModel().catch(() => undefined);
      emit("recording");
    } catch (error) {
      recorder.release();
      throw error;
    }
  }

  async readFrames(recorder) {
    let levelSamples = 0;
    try {
      while (!this.stopping && recorder.isRecording) {
        const frame = await recorder.read();
        if (!this.stopping) {
          levelSamples += frame.length;
          if (levelSamples >= 1024) {
            emit("audio-level", { level: audioLevel(frame) });
            levelSamples = 0;
          }
          if (this.cloud) this.cloud.feed(frame);
          else this.frames.push(frame);
        }
      }
    } catch (error) {
      if (!this.stopping) {
        this.captureError = error;
        void this.abortRecording(error);
      }
    }
  }

  async finishCapture() {
    const recorder = this.recorder;
    if (!recorder) throw new Error("Microphone capture is not active");

    this.stopping = true;
    let stopError;
    try {
      if (recorder.isRecording) recorder.stop();
    } catch (error) {
      stopError = error;
    }
    try {
      await this.readLoop;
    } finally {
      recorder.release();
      if (this.recorder === recorder) this.recorder = undefined;
      this.readLoop = undefined;
    }
    if (stopError) throw stopError;
    if (this.captureError) throw this.captureError;

    const pcm = convertFrames(this.frames);
    this.frames = [];
    return pcm;
  }
}

async function main() {
  if (process.platform !== "win32") throw new Error("Windows dictation is only supported on Windows");

  const daemon = new DictationDaemon(readSettings(), await loadRuntime());
  const input = createInterface({ input: process.stdin });
  let queue = Promise.resolve();
  let toggleQueued = false;
  let modelChangeQueued = false;
  let shuttingDown = false;

  const shutdown = async () => {
    if (shuttingDown) return;
    shuttingDown = true;
    await daemon.shutdown();
    emit("stopped");
    input.close();
  };
  const enqueue = (task) => {
    queue = queue.then(task, task).catch((error) => emit("error", { message: errorMessage(error) }));
  };

  const sendModels = async () => {
    try {
      emit("models", {
        models: daemon.settings.model.path ? await findLocalModels(daemon.settings.model.path) : [],
        modelPath: daemon.settings.model.path,
      });
    } catch (error) {
      emit("model-list-error", { message: errorMessage(error) });
    }
  };

  input.on("line", (line) => {
    if (shuttingDown) return;
    let command;
    try {
      command = line.trim().startsWith("{") ? JSON.parse(line) : { type: line.trim() };
      if (!isRecord(command)) throw new Error("Invalid command");
    } catch (error) {
      emit("error", { message: errorMessage(error) });
      return;
    }
    switch (command.type) {
      case "toggle":
        if (toggleQueued || modelChangeQueued || daemon.state === "transcribing") {
          emit("busy");
          break;
        }
        toggleQueued = true;
        enqueue(async () => {
          try {
            await daemon.toggle();
          } finally {
            toggleQueued = false;
          }
        });
        break;
      case "configure-backend":
        enqueue(async () => {
          try {
            daemon.configureBackend(command);
            emit("backend-configured");
          } catch (error) { emit("backend-error", { message: errorMessage(error) }); }
        });
        break;
      case "test-cloud":
        enqueue(async () => {
          try {
            await daemon.testCloud();
            emit("cloud-tested");
          } catch (error) { emit("cloud-test-error", { message: errorMessage(error) }); }
        });
        break;
      case "models":
        enqueue(sendModels);
        break;
      case "select-model":
        if (toggleQueued || modelChangeQueued || daemon.state !== "idle") {
          emit("model-error", { message: "请等待当前操作完成后再切换模型。", modelPath: daemon.settings.model.path });
          break;
        }
        modelChangeQueued = true;
        enqueue(async () => {
          try {
            await daemon.selectModel(command.path);
            emit("model-changed", { model: daemon.settings.model.id, modelPath: daemon.settings.model.path });
          } catch (error) {
            emit("model-error", { message: errorMessage(error), modelPath: daemon.settings.model.path });
          } finally {
            modelChangeQueued = false;
          }
        });
        break;
      case "shutdown":
        enqueue(shutdown);
        break;
      default:
        emit("error", { message: "Unknown command" });
    }
  });
  input.on("close", () => {
    if (!shuttingDown) enqueue(shutdown);
  });
  process.once("SIGINT", () => enqueue(shutdown));
  process.once("SIGTERM", () => enqueue(shutdown));
  process.stdout.once("error", () => process.exit(0));

  emit("ready", {
    model: daemon.settings.model.id,
    autocorrectPath: daemon.settings.autocorrectPath,
    modelPath: daemon.settings.model.path,
  });
  enqueue(sendModels);
}

const invokedDirectly = process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (invokedDirectly) {
  main().catch((error) => {
    emit("error", { message: errorMessage(error) });
    process.exitCode = 1;
  });
}
