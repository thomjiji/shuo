import WebSocket from "ws";
import { randomUUID } from "node:crypto";

export const QWEN_MODEL = "fun-asr-realtime";
const ENDPOINTS = {
  "cn-beijing": "wss://dashscope.aliyuncs.com/api-ws/v1/inference",
  "ap-southeast-1": "wss://dashscope-intl.aliyuncs.com/api-ws/v1/inference",
};
const CHUNK_BYTES = 6400;

export function qwenConnection(config) {
  const apiKey = config.apiKey?.trim();
  if (!apiKey) throw new Error("请先填写百炼 API Key。");
  const endpoint = ENDPOINTS[config.region || "cn-beijing"];
  if (!endpoint) throw new Error("请选择百炼服务地域：北京或新加坡。");
  return {
    url: endpoint,
    headers: { Authorization: "Bearer " + apiKey },
  };
}

export class QwenStream {
  constructor(config, onPartial = () => {}, { url, timeoutMs = 15000 } = {}) {
    this.config = config;
    this.onPartial = onPartial;
    this.url = url;
    this.timeoutMs = timeoutMs;
    this.pending = Buffer.alloc(0);
    this.items = new Map();
    this.taskId = randomUUID();
    this.ending = false;
    this.settled = false;
    this.ready = false;
    this.result = new Promise((resolve, reject) => { this.resolve = resolve; this.reject = reject; });
    this.result.catch(() => {});
  }

  async connect() {
    const connection = qwenConnection(this.config);
    const socket = this.socket = new WebSocket(this.url || connection.url, {
      headers: connection.headers, handshakeTimeout: this.timeoutMs,
      maxPayload: 4 * 1024 * 1024, followRedirects: false,
    });
    const ready = new Promise((resolve) => { this.resolveReady = resolve; });
    this.timer = setTimeout(() => this.fail(new Error("等待百炼会话就绪超时，请重试。")), this.timeoutMs);
    socket.on("open", () => {
      try {
        this.sendCommand("run-task", {
          task_group: "audio", task: "asr", function: "recognition", model: QWEN_MODEL,
          // Fun-ASR includes filler filtering and ITN; neither has a documented toggle.
          parameters: { punctuation_prediction_enabled: true, format: "pcm", sample_rate: 16000, max_sentence_silence: 800, heartbeat: true },
          input: {},
        });
      } catch (error) { this.fail(error); }
    });
    socket.on("message", (data) => {
      try { this.receive(JSON.parse(data.toString("utf8"))); }
      catch (error) {
        this.fail(error instanceof SyntaxError ? new Error("百炼返回了无效数据。") : error);
      }
    });
    socket.on("error", () => this.fail(new Error("百炼连接失败，请检查网络和调用凭据。")));
    socket.on("unexpected-response", (_request, response) => {
      response.resume();
      this.fail(new Error(`百炼连接被拒绝（HTTP ${response.statusCode}），请检查 API Key、地域和模型权限。`));
    });
    socket.on("close", () => {
      if (!this.settled) this.fail(new Error("百炼连接已断开，未收到完整结果，请重试。"));
    });
    await Promise.race([ready, this.result]);
    if (this.error) throw this.error;
  }

  receive(event) {
    if (this.settled) return;
    if (!event.header || event.header.task_id !== this.taskId)
      throw new Error("百炼返回的任务编号无效。");
    switch (event.header.event) {
      case "task-started":
        if (this.ready) break;
        this.ready = true;
        clearTimeout(this.timer);
        this.resolveReady();
        break;
      case "result-generated": {
        const sentence = event.payload?.output?.sentence;
        if (sentence?.heartbeat) break;
        if (!sentence || !Number.isInteger(sentence.sentence_id) || sentence.sentence_id < 0 ||
            typeof sentence.text !== "string" || typeof sentence.sentence_end !== "boolean")
          throw new Error("百炼返回的识别结果格式无效。");
        if (this.items.get(sentence.sentence_id)?.complete) break;
        this.items.set(sentence.sentence_id, { text: sentence.text, complete: sentence.sentence_end });
        this.onPartial(this.transcript());
        break;
      }
      case "task-finished":
        if (!this.ending) throw new Error("百炼提前结束了识别，请重新开始听写。");
        if ([...this.items.values()].some(item => !item.complete))
          throw new Error("百炼未返回所有句子的最终结果，请重试。");
        this.settled = true;
        clearTimeout(this.timer);
        this.resolve({ text: this.transcript() });
        this.socket.close();
        break;
      case "task-failed": {
        const detail = String(event.header.error_message || event.header.error_code || "请求失败");
        const key = this.config.apiKey?.trim();
        throw new Error("百炼识别失败：" + (key ? detail.replaceAll(key, "[已隐藏]") : detail).slice(0, 300));
      }
    }
  }

  transcript() {
    return [...this.items.entries()].sort(([a], [b]) => a - b).map(([, item]) => item.text)
      .filter(Boolean).reduce((text, next) =>
        text + (/[A-Za-z0-9.!?]$/.test(text) && /^[A-Za-z0-9]/.test(next) ? " " : "") + next, "");
  }

  sendCommand(action, payload) {
    this.send(JSON.stringify({ header: { action, task_id: this.taskId, streaming: "duplex" }, payload }));
  }

  send(data) {
    if (this.error) throw this.error;
    if (this.socket?.readyState !== WebSocket.OPEN) throw new Error("百炼连接尚未就绪。");
    if (this.socket.bufferedAmount > 320000) throw new Error("网络上传积压超过 10 秒，请检查网络后重试。");
    this.socket.send(data, (error) => {
      if (error) this.fail(new Error("无法向百炼发送音频。"));
    });
  }

  feed(frame) {
    if (this.error) throw this.error;
    if (!this.ready || this.ending) throw new Error("百炼当前无法接收音频。");
    const audio = Buffer.alloc(frame.length * 2);
    for (let i = 0; i < frame.length; i++) audio.writeInt16LE(frame[i], i * 2);
    this.pending = Buffer.concat([this.pending, audio]);
    while (this.pending.length >= CHUNK_BYTES) {
      this.send(this.pending.subarray(0, CHUNK_BYTES));
      this.pending = this.pending.subarray(CHUNK_BYTES);
    }
  }

  async finish() {
    if (this.error) throw this.error;
    if (this.ending) return this.result;
    this.ending = true;
    this.timer = setTimeout(() => this.fail(new Error("等待百炼最终结果超时，请重试。")), this.timeoutMs);
    try {
      if (this.pending.length) this.send(this.pending);
      this.pending = Buffer.alloc(0);
      // VAD finalizes sentences during recording; finish also flushes the last unfinished sentence.
      this.sendCommand("finish-task", { input: {} });
      return await this.result;
    } catch (error) { this.fail(error); throw error; }
  }

  fail(error) {
    if (this.settled) return;
    this.settled = true;
    this.error = error;
    clearTimeout(this.timer);
    this.reject(error);
    this.socket?.terminate();
  }

  close() { this.fail(new Error("百炼识别已取消。")); }
}
