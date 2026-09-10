import assert from "node:assert/strict";
import test from "node:test";
import { once } from "node:events";
import { WebSocketServer } from "ws";
import { QwenStream, qwenConnection, QWEN_MODEL } from "../worker/qwen.mjs";
import { DictationDaemon } from "../worker/dictation-daemon.mjs";
import { DoubaoStream } from "../worker/doubao.mjs";

async function withServer(handler, run) {
  const server = new WebSocketServer({ host: "127.0.0.1", port: 0 });
  await once(server, "listening");
  server.on("connection", (socket, request) => {
    socket.on("message", (data, binary) => {
      const event = binary ? data : JSON.parse(data.toString());
      if (!binary) {
        assert.equal(event.header.streaming, "duplex");
        if (event.header.action === "run-task") socket.taskId = event.header.task_id;
        assert.equal(event.header.task_id, socket.taskId);
      }
      handler(socket, event, request);
    });
  });
  try { await run("ws://127.0.0.1:" + server.address().port); }
  finally {
    for (const client of server.clients) client.terminate();
    await new Promise(resolve => server.close(resolve));
  }
}
const send = (socket, event, payload = {}, header = {}) => socket.send(JSON.stringify({
  header: { event, task_id: socket.taskId, ...header }, payload,
}));
const sentence = (socket, id, text, complete = false) => send(socket, "result-generated", {
  output: { sentence: { sentence_id: id, text, sentence_end: complete } },
});

test("Fun-ASR streams binary PCM before stop and replaces draft with corrected final text", async () => {
  const samples = [], partials = [];
  let chunkCount = 0, sawPartial;
  const partialReceived = new Promise(resolve => { sawPartial = resolve; });
  await withServer((socket, event, request) => {
    assert.equal(request.headers.authorization, "Bearer test-only");
    if (Buffer.isBuffer(event)) {
      chunkCount++;
      assert.equal(event.length, chunkCount === 1 ? 6400 : 600);
      for (let i = 0; i < event.length; i += 2) samples.push(event.readInt16LE(i));
      if (chunkCount === 1) {
        sentence(socket, 1, "千文");
        sentence(socket, 1, "千问ASR");
      }
    } else if (event.header.action === "run-task") {
      assert.equal(event.payload.model, "fun-asr-realtime");
      assert.equal(QWEN_MODEL, "fun-asr-realtime");
      assert.deepEqual(event.payload.input, {});
      assert.equal(event.payload.task_group, "audio");
      assert.equal(event.payload.task, "asr");
      assert.equal(event.payload.function, "recognition");
      assert.equal(event.payload.parameters.sample_rate, 16000);
      assert.equal(event.payload.parameters.format, "pcm");
      assert.equal(event.payload.parameters.punctuation_prediction_enabled, true);
      assert.equal(event.payload.parameters.language_hints, undefined);
      send(socket, "task-started");
    } else if (event.header.action === "finish-task") {
      assert.deepEqual(event.payload, { input: {} });
      sentence(socket, 1, "千问 ASR。", true);
      send(socket, "task-finished");
    } else assert.fail("Unexpected client event");
  }, async url => {
    const stream = new QwenStream({ apiKey: "test-only" }, text => {
      partials.push(text);
      if (text === "千问ASR") sawPartial();
    }, { url, timeoutMs: 1000 });
    try {
      assert.throws(() => stream.feed(new Int16Array(1)), /接收/);
      await stream.connect();
      const audio = Int16Array.from({ length: 3500 }, (_, i) => i % 2 ? -456 : 123);
      stream.feed(audio);
      await partialReceived;
      assert.equal(stream.settled, false);
      assert.equal((await stream.finish()).text, "千问 ASR。");
      assert.deepEqual(partials, ["千文", "千问ASR", "千问 ASR。"]);
      assert.deepEqual(samples, [...audio]);
    } finally { stream.close(); }
  });
});

test("Fun-ASR orders sentence IDs and ignores late drafts and heartbeat packets", async () => {
  await withServer((socket, event) => {
    if (event.header?.action === "run-task") send(socket, "task-started");
    if (event.header?.action === "finish-task") {
      sentence(socket, 2, "Second sentence.", true);
      sentence(socket, 1, "First sentence.", true);
      sentence(socket, 1, "obsolete");
      send(socket, "result-generated", { output: { sentence: { heartbeat: true, sentence_id: 0 } } });
      send(socket, "task-finished");
    }
  }, async url => {
    const stream = new QwenStream({ apiKey: "test-only" }, () => {}, { url });
    try {
      await stream.connect();
      assert.equal((await stream.finish()).text, "First sentence. Second sentence.");
    } finally { stream.close(); }
  });
});

test("Fun-ASR handles silent recordings", async () => {
  await withServer((socket, event) => {
    if (event.header?.action === "run-task") send(socket, "task-started");
    if (event.header?.action === "finish-task") send(socket, "task-finished");
  }, async url => {
    const stream = new QwenStream({ apiKey: "test-only" }, () => {}, { url });
    try {
      await stream.connect();
      stream.feed(new Int16Array(3200));
      assert.equal((await stream.finish()).text, "");
    } finally { stream.close(); }
  });
});

test("Fun-ASR rejects interrupted, incomplete, malformed and timed-out tasks", async () => {
  for (const mode of ["disconnect", "timeout", "incomplete", "malformed", "missing-ready", "wrong-task"]) {
    await withServer((socket, event) => {
      if (event.header?.action === "run-task" && mode !== "missing-ready") send(socket, "task-started");
      if (event.header?.action === "finish-task") {
        if (mode === "malformed") { socket.send("{invalid"); return; }
        if (mode === "wrong-task") { send(socket, "task-finished", {}, { task_id: "wrong" }); return; }
        sentence(socket, 1, "unconfirmed");
        if (mode === "disconnect") socket.close();
        if (mode === "incomplete") send(socket, "task-finished");
      }
    }, async url => {
      const stream = new QwenStream({ apiKey: "test-only" }, () => {}, { url, timeoutMs: 100 });
      try {
        if (mode === "missing-ready") await assert.rejects(stream.connect(), /就绪超时/);
        else {
          await stream.connect();
          await assert.rejects(stream.finish(), /断开|超时|最终结果|无效/);
        }
      } finally { stream.close(); }
    });
  }
});

test("Qwen rejects HTTP authentication errors and redacts keys in service errors", async () => {
  const server = new WebSocketServer({ host: "127.0.0.1", port: 0, verifyClient: () => false });
  await once(server, "listening");
  const stream = new QwenStream({ apiKey: "secret-test-value" }, () => {}, {
    url: "ws://127.0.0.1:" + server.address().port, timeoutMs: 1000,
  });
  try {
    await assert.rejects(stream.connect(), error => /HTTP 401/.test(error.message) && !error.message.includes("secret-test-value"));
  } finally {
    stream.close();
    await new Promise(resolve => server.close(resolve));
  }
  await withServer((socket) => send(socket, "task-failed", {}, { error_message: "invalid secret-test-value" }), async url => {
    const stream = new QwenStream({ apiKey: "secret-test-value" }, () => {}, { url });
    try {
      await assert.rejects(stream.connect(), error => !error.message.includes("secret-test-value") && /已隐藏/.test(error.message));
    } finally { stream.close(); }
  });
});

test("provider switching selects the matching stream and refuses invalid or busy changes", async () => {
  const daemon = new DictationDaemon({ chineseOutput: "simplified" }, { OpenCC: { Converter: () => text => text } });
  daemon.configureBackend({ provider: "qwen", config: { apiKey: "test-only", region: "cn-beijing" } });
  const qwen = daemon.createCloudStream();
  assert.ok(qwen instanceof QwenStream);
  qwen.close();
  assert.throws(() => daemon.configureBackend({ provider: "qwen", config: {} }), /API Key/);
  assert.equal(daemon.provider, "qwen");
  assert.deepEqual(daemon.cloudConfig, {});
  await assert.rejects(daemon.createCloudStream().connect(), /API Key/);
  daemon.state = "recording";
  assert.throws(() => daemon.configureBackend({ provider: "local" }), /听写结束/);
  daemon.state = "idle";
  daemon.configureBackend({ provider: "doubao", config: { apiKey: "doubao-test" } });
  const doubao = daemon.createCloudStream();
  assert.ok(doubao instanceof DoubaoStream);
  doubao.close();
  daemon.configureBackend({ provider: "local" });
  assert.throws(() => daemon.createCloudStream(), /云端/);
  await assert.rejects(daemon.testCloud(), /云端/);
  assert.equal(daemon.state, "idle");
  assert.throws(() => qwenConnection({ apiKey: "test", region: "wrong" }), /地域/);
  assert.match(qwenConnection({ apiKey: "test", region: "ap-southeast-1" }).url, /dashscope-intl/);
});


test("Qwen3 realtime sends base64 PCM and waits for corrected final tail", async () => {
  const { QwenRealtimeStream } = await import("../worker/qwen-realtime.mjs");
  const server = new WebSocketServer({ host: "127.0.0.1", port: 0 });
  await once(server, "listening");
  const received = [], partials = [];
  server.on("connection", socket => {
    socket.send(JSON.stringify({ type: "session.created" }));
    socket.on("message", data => {
      const event = JSON.parse(data);
      assert.ok(event.event_id);
      if (event.type === "session.update") {
        assert.equal(event.session.sample_rate, 16000);
        socket.send(JSON.stringify({ type: "session.updated" }));
      } else if (event.type === "input_audio_buffer.append") {
        received.push(Buffer.from(event.audio, "base64"));
        socket.send(JSON.stringify({ type: "conversation.item.input_audio_transcription.text",
          item_id: "a", text: "你好", stash: "千文" }));
      } else if (event.type === "session.finish") {
        socket.send(JSON.stringify({ type: "conversation.item.input_audio_transcription.completed",
          item_id: "a", transcript: "你好千问。" }));
        socket.send(JSON.stringify({ type: "session.finished" }));
      } else assert.fail(event.type);
    });
  });
  const stream = new QwenRealtimeStream({ apiKey: "test", model: "qwen3-asr-flash-realtime" },
    text => partials.push(text), { url: "ws://127.0.0.1:" + server.address().port, timeoutMs: 1000 });
  try {
    await stream.connect();
    const frames = Int16Array.from({ length: 3500 }, (_, i) => i - 1700);
    stream.feed(frames);
    const result = await stream.finish();
    assert.equal(result.text, "你好千问。");
    assert.equal(result.model, "qwen3-asr-flash-realtime");
    assert.deepEqual(Buffer.concat(received), Buffer.from(frames.buffer));
    assert.equal(partials.at(-1), result.text);
    assert.ok(partials.includes("你好千文"));
  } finally {
    stream.close();
    for (const client of server.clients) client.terminate();
    await new Promise(resolve => server.close(resolve));
  }
});

test("Qwen3 rejects incomplete results and protects final text and credentials", async () => {
  const { QwenRealtimeStream } = await import("../worker/qwen-realtime.mjs");
  const stream = new QwenRealtimeStream({ apiKey: "secret", model: "qwen3-asr-flash-realtime" });
  stream.receive({ type: "conversation.item.input_audio_transcription.completed", item_id: "a", transcript: "完成" });
  stream.receive({ type: "conversation.item.input_audio_transcription.text", item_id: "a", text: "过时", stash: "" });
  assert.equal(stream.transcript(), "完成");
  stream.receive({ type: "conversation.item.input_audio_transcription.text", item_id: "b", text: "", stash: "草稿" });
  stream.ending = true;
  assert.throws(() => stream.receive({ type: "session.finished" }), /最终结果/);
  assert.throws(() => stream.receive({ type: "conversation.item.input_audio_transcription.failed",
    error: { message: "denied secret" } }), error => !error.message.includes("secret"));
  stream.close();
  assert.match(qwenConnection({ apiKey: "test", model: "qwen3-asr-flash-realtime" }).url,
    /realtime\?model=qwen3-asr-flash-realtime$/);
  assert.throws(() => qwenConnection({ apiKey: "test", model: "invented-asr" }), /模型/);
  const daemon = new DictationDaemon({ chineseOutput: "simplified" }, { OpenCC: { Converter: () => text => text } });
  daemon.configureBackend({ provider: "qwen", config: { apiKey: "test", model: "qwen3-asr-flash-realtime" } });
  const selected = daemon.createCloudStream();
  assert.ok(selected instanceof QwenRealtimeStream);
  selected.close();
});
