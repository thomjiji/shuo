import assert from "node:assert/strict";
import test from "node:test";
import { once } from "node:events";
import { WebSocketServer } from "ws";
import { SelfHostedStream, selfHostedConnection } from "../worker/selfhosted.mjs";
import { DictationDaemon } from "../worker/dictation-daemon.mjs";

async function withServer(handler, run) {
  const server = new WebSocketServer({ host: "127.0.0.1", port: 0 });
  await once(server, "listening");
  server.on("connection", socket => {
    socket.on("message", (data, binary) => handler(socket, binary ? data : JSON.parse(data.toString())));
  });
  try { await run(`http://127.0.0.1:${server.address().port}`); }
  finally {
    for (const client of server.clients) client.terminate();
    await new Promise(resolve => server.close(resolve));
  }
}
const send = (socket, event) => socket.send(JSON.stringify(event));
const ready = socket => send(socket, { type: "ready", protocol: 1, model: "Qwen3-ASR-1.7B-8bit" });

function captureDaemon(url) {
  let recorder;
  class Recorder {
    sampleRate = 16000;
    constructor() { recorder = this; }
    start() { this.isRecording = true; }
    read() { return new Promise(resolve => { this.deliver = resolve; }); }
    stop() { this.isRecording = false; this.deliver(new Int16Array()); }
    release() { this.released = true; }
  }
  const daemon = new DictationDaemon({
    chineseOutput: "simplified", transcriptionLanguage: "zh", microphone: { type: "system-default" },
  }, { PvRecorder: Recorder, OpenCC: { Converter: () => text => text } });
  daemon.configureBackend({ provider: "selfhosted", config: { url } });
  return { daemon, recorder: () => recorder };
}

for (const stopBeforeReady of [false, true]) {
  test(`daemon preserves the audio prefix with delayed ready (stop before ready: ${stopBeforeReady})`, { timeout: 3000 }, async () => {
    const samples = [];
    let acknowledgeStart;
    const started = new Promise(resolve => { acknowledgeStart = resolve; });
    await withServer((socket, event) => {
      if (event.type === "start") acknowledgeStart(socket);
      else if (Buffer.isBuffer(event)) samples.push(event);
      else if (event.type === "finish") send(socket, { type: "final", text: "完整开头" });
    }, async url => {
      const { daemon, recorder } = captureDaemon(url);
      try {
        // startRecording must return with the microphone running even while
        // the server deliberately withholds its ready acknowledgement.
        await daemon.startRecording();
        assert.equal(daemon.state, "recording");
        assert.equal(recorder().isRecording, true);
        const prefix = Int16Array.from({ length: 3501 }, (_, i) => i - 1700);
        recorder().deliver(prefix);
        await new Promise(setImmediate);
        const socket = await started;
        assert.equal(samples.length, 0);
        let stopping;
        if (stopBeforeReady) {
          stopping = daemon.toggle();
          assert.equal(recorder().isRecording, false);
          await new Promise(setImmediate);
          assert.equal(recorder().released, true);
          assert.equal(daemon.state, "transcribing");
        }
        ready(socket);
        await daemon.cloudConnecting;
        const tail = Int16Array.of(111, -222, 333);
        if (!stopBeforeReady) {
          recorder().deliver(tail);
          await new Promise(setImmediate);
          stopping = daemon.toggle();
        }
        await stopping;
        const pcm = Buffer.concat(samples);
        assert.deepEqual(Array.from({ length: pcm.length / 2 }, (_, i) => pcm.readInt16LE(i * 2)),
          [...prefix, ...(stopBeforeReady ? [] : tail)]);
        assert.equal(daemon.state, "idle");
        assert.equal(recorder().released, true);
        assert.equal(daemon.cloudFrames.length, 0);
      } finally { await daemon.shutdown(); }
    });
  });
}

test("daemon releases capture and buffered audio when the connection fails, then can restart", { timeout: 3000 }, async () => {
  let rejectStart;
  const started = new Promise(resolve => { rejectStart = resolve; });
  await withServer((socket, event) => {
    if (event.type === "start") rejectStart(socket);
  }, async url => {
    const { daemon, recorder } = captureDaemon(url);
    try {
      await daemon.startRecording();
      recorder().deliver(Int16Array.of(123));
      const socket = await started;
      send(socket, { type: "error", message: "服务忙碌" });
      await assert.rejects(daemon.cloudConnecting, /服务忙碌/);
      await new Promise(setImmediate);
      assert.equal(daemon.state, "idle");
      assert.equal(recorder().released, true);
      assert.equal(daemon.cloudFrames.length, 0);
      await daemon.startRecording();
      assert.equal(recorder().isRecording, true);
      assert.equal(daemon.cloudFrames.length, 0);
    } finally { await daemon.shutdown(); }
    await new Promise(setImmediate);
    assert.equal(daemon.state, "stopping");
  });
});

test("self-hosted streams PCM before stop, replaces drafts, and flushes the audio tail", async () => {
  const samples = [], partials = [];
  let sawPartial;
  const partialReceived = new Promise(resolve => { sawPartial = resolve; });
  await withServer((socket, event) => {
    if (Buffer.isBuffer(event)) {
      samples.push(event);
      if (samples.length === 1) {
        assert.equal(event.length, 6400);
        send(socket, { type: "partial", text: "千文" });
        send(socket, { type: "partial", text: "千问" });
      }
    } else if (event.type === "start") {
      assert.deepEqual(event, { type: "start", protocol: 1, sample_rate: 16000, format: "pcm_s16le", language: "zh" });
      ready(socket);
    } else if (event.type === "finish") send(socket, { type: "final", text: "千问 ASR。", language: "zh" });
  }, async url => {
    const stream = new SelfHostedStream({ url, language: "zh" }, text => { partials.push(text); if (text === "千问") sawPartial(); });
    try {
      await stream.connect();
      const audio = Int16Array.from({ length: 3501 }, (_, i) => i % 2 ? -231 : 145);
      stream.feed(audio);
      await partialReceived;
      assert.equal(stream.settled, false);
      const final = await stream.finish();
      assert.deepEqual(final, { text: "千问 ASR。", language: "zh", model: "Qwen3-ASR-1.7B-8bit" });
      assert.deepEqual(partials, ["千文", "千问"]);
      const pcm = Buffer.concat(samples);
      assert.equal(pcm.length, audio.length * 2);
      assert.deepEqual(Array.from({ length: audio.length }, (_, i) => pcm.readInt16LE(i * 2)), [...audio]);
    } finally { stream.close(); }
  });
});

test("self-hosted silent recordings return an empty final", async () => {
  await withServer((socket, event) => {
    if (event.type === "start") ready(socket);
    if (event.type === "finish") send(socket, { type: "final", text: "" });
  }, async url => {
    const stream = new SelfHostedStream({ url });
    try { await stream.connect(); stream.feed(new Int16Array(512)); assert.equal((await stream.finish()).text, ""); }
    finally { stream.close(); }
  });
});

test("self-hosted errors, missing final, malformed messages and early final never succeed", async () => {
  for (const mode of ["busy", "missing-ready", "version", "disconnect", "timeout", "malformed", "early-final"]) {
    await withServer((socket, event) => {
      if (event.type === "start") {
        if (mode === "missing-ready") return;
        if (mode === "busy") { send(socket, { type: "error", message: "服务忙碌" }); return; }
        if (mode === "version") { send(socket, { type: "ready", protocol: 2, model: "bad" }); return; }
        ready(socket);
        if (mode === "early-final") send(socket, { type: "final", text: "unconfirmed" });
      }
      if (event.type === "finish") {
        if (mode === "disconnect") socket.close();
        if (mode === "malformed") socket.send("not json");
      }
    }, async url => {
      const stream = new SelfHostedStream({ url }, () => {}, { timeoutMs: 100, finishTimeoutMs: 100 });
      try {
        if (["busy", "missing-ready", "version"].includes(mode)) await assert.rejects(stream.connect());
        else if (mode === "early-final") { await stream.connect().catch(() => {}); await assert.rejects(stream.result, /提前/); }
        else { await stream.connect(); await assert.rejects(stream.finish()); }
        assert.equal(stream.settled, true);
      } finally { stream.close(); }
    });
  }
});

test("self-hosted cancellation terminates the unfinished session", async () => {
  await withServer((socket, event) => { if (event.type === "start") ready(socket); }, async url => {
    const stream = new SelfHostedStream({ url });
    await stream.connect();
    stream.close();
    await assert.rejects(stream.result, /取消/);
    assert.throws(() => stream.feed(new Int16Array(1)), /取消/);
  });
});

test("self-hosted validates URLs without exposing embedded credentials", () => {
  for (const url of ["", "file:///tmp/model", "http://user:secret@host", "http://host?key=secret", "http://host/other"])
    assert.throws(() => selfHostedConnection({ url }), error => !error.message.includes("secret"));
  assert.equal(selfHostedConnection({ url: "https://mac:18765" }).url, "wss://mac:18765/v1/asr");
  assert.equal(selfHostedConnection({ url: "ws://127.0.0.1:8000/v1/asr" }).url, "ws://127.0.0.1:8000/v1/asr");
});

test("daemon selects the self-hosted stream and forwards the configured language", () => {
  const daemon = new DictationDaemon({ chineseOutput: "simplified", transcriptionLanguage: "zh" }, { OpenCC: { Converter: () => text => text } });
  daemon.configureBackend({ provider: "selfhosted", config: { url: "http://mac:18765", model: "Qwen3-ASR-0.6B-8bit" } });
  const stream = daemon.createCloudStream();
  assert.ok(stream instanceof SelfHostedStream);
  assert.equal(stream.config.language, "zh");
  assert.equal(stream.config.model, "Qwen3-ASR-0.6B-8bit");
  stream.close();
  assert.throws(() => daemon.configureBackend({ provider: "selfhosted", config: {} }), /地址/);
  daemon.configureBackend({ provider: "local" });
  assert.equal(daemon.provider, "local");
});


test("self-hosted forwards model selection and rejects an ignored selection", async () => {
  for (const mismatch of [false, true]) {
    const model = "Qwen3-ASR-0.6B-8bit";
    await withServer((socket, event) => {
      if (event.type === "start") {
        assert.equal(event.model, model);
        send(socket, {type: "ready", protocol: 1, model: mismatch ? "Qwen3-ASR-1.7B-8bit" : model});
      }
      if (event.type === "finish") send(socket, {type: "final", text: ""});
    }, async url => {
      const stream = new SelfHostedStream({url, model});
      try {
        if (mismatch) await assert.rejects(stream.connect(), /模型不同/);
        else { await stream.connect(); assert.equal((await stream.finish()).model, model); }
      } finally {stream.close();}
    });
  }
});
