import WebSocket from "ws";

const CHUNK_BYTES = 6400;

export function selfHostedConnection(config) {
  let url;
  try { url = new URL(config.url?.trim()); }
  catch { throw new Error("请填写自托管服务地址，例如 http://macbook:18765。"); }
  if (!["http:", "https:", "ws:", "wss:"].includes(url.protocol) || url.username || url.password || url.search || url.hash)
    throw new Error("自托管地址只支持 HTTP 或 WebSocket，不能包含凭据、查询参数或片段。");
  if (url.pathname !== "/" && url.pathname !== "/v1/asr")
    throw new Error("请填写服务根地址，或以 /v1/asr 结尾的地址。");
  url.protocol = ["https:", "wss:"].includes(url.protocol) ? "wss:" : "ws:";
  url.pathname = "/v1/asr";
  return { url: url.href };
}

export class SelfHostedStream {
  constructor(config, onPartial = () => {}, { timeoutMs = 15000, finishTimeoutMs = 60000 } = {}) {
    this.config = config;
    this.onPartial = onPartial;
    this.timeoutMs = timeoutMs;
    this.finishTimeoutMs = finishTimeoutMs;
    this.pending = Buffer.alloc(0);
    this.ready = false;
    this.ending = false;
    this.settled = false;
    this.result = new Promise((resolve, reject) => { this.resolve = resolve; this.reject = reject; });
    this.result.catch(() => {});
  }

  async connect() {
    const { url } = selfHostedConnection(this.config);
    const socket = this.socket = new WebSocket(url, {
      handshakeTimeout: this.timeoutMs, maxPayload: 1024 * 1024, followRedirects: false,
    });
    const ready = new Promise(resolve => { this.resolveReady = resolve; });
    this.timer = setTimeout(() => this.fail(new Error("等待自托管服务就绪超时，请确认 Mac 服务已启动且端口可访问。")), this.timeoutMs);
    socket.on("open", () => {
      try {
        this.send(JSON.stringify({ type: "start", protocol: 1, sample_rate: 16000,
          format: "pcm_s16le", language: this.config.language || "auto", model: this.config.model || undefined }));
      } catch (error) { this.fail(error); }
    });
    socket.on("message", (data, binary) => {
      try {
        if (binary) throw new Error("自托管服务返回了无效数据。");
        this.receive(JSON.parse(data.toString("utf8")));
      } catch (error) { this.fail(error instanceof SyntaxError ? new Error("自托管服务返回了无效 JSON。") : error); }
    });
    socket.on("pong", () => { this.alive = true; });
    socket.on("error", () => this.fail(new Error("自托管服务连接失败，请检查服务地址、Tailscale 和端口访问权限。")));
    socket.on("unexpected-response", (_request, response) => {
      response.resume();
      this.fail(new Error(`自托管服务拒绝连接（HTTP ${response.statusCode}），请检查服务地址。`));
    });
    socket.on("close", () => { if (!this.settled) this.fail(new Error("自托管连接已断开，未收到最终结果，请重试。")); });
    await Promise.race([ready, this.result]);
    if (this.error) throw this.error;
  }

  receive(event) {
    if (this.settled) return;
    if (!event || typeof event !== "object") throw new Error("自托管服务返回了无效事件。");
    if (event.type === "error") throw new Error(typeof event.message === "string" ? event.message.slice(0, 300) : "自托管识别失败。");
    if (event.type === "ready") {
      if (this.ready || event.protocol !== 1 || typeof event.model !== "string" || !event.model.trim())
        throw new Error("自托管服务协议不兼容。");
      if (this.config.model && event.model !== this.config.model)
        throw new Error("Mac 返回的模型与所选模型不同，请更新 Mac 服务或重新选择模型。");
      this.model = event.model;
      this.ready = true;
      clearTimeout(this.timer);
      this.alive = true;
      this.heartbeat = setInterval(() => {
        if (!this.alive) { this.fail(new Error("自托管连接无响应，请检查 Mac 是否休眠或断网。")); return; }
        this.alive = false;
        this.socket.ping();
      }, this.timeoutMs);
      this.resolveReady();
      return;
    }
    if (!this.ready || !["partial", "final"].includes(event.type) || typeof event.text !== "string")
      throw new Error("自托管服务返回的识别结果无效。");
    if (event.type === "partial") { this.onPartial(event.text); return; }
    if (!this.ending) throw new Error("自托管服务提前结束了识别，请重新开始听写。");
    this.settled = true;
    clearTimeout(this.timer);
    clearInterval(this.heartbeat);
    this.resolve({ text: event.text, language: typeof event.language === "string" ? event.language : undefined, model: this.model });
    this.socket.close();
  }

  send(data) {
    if (this.error) throw this.error;
    if (this.socket?.readyState !== WebSocket.OPEN) throw new Error("自托管连接尚未就绪。");
    if (this.socket.bufferedAmount > 320000) throw new Error("音频上传积压超过 10 秒，请检查连接。");
    this.socket.send(data, error => { if (error) this.fail(new Error("无法向自托管服务发送音频。")); });
  }

  feed(frame) {
    if (this.error) throw this.error;
    if (!this.ready || this.ending) throw new Error("自托管服务当前无法接收音频。");
    const bytes = Buffer.alloc(frame.length * 2);
    for (let i = 0; i < frame.length; i++) bytes.writeInt16LE(frame[i], i * 2);
    this.pending = Buffer.concat([this.pending, bytes]);
    while (this.pending.length >= CHUNK_BYTES) {
      this.send(this.pending.subarray(0, CHUNK_BYTES));
      this.pending = this.pending.subarray(CHUNK_BYTES);
    }
  }

  async finish() {
    if (this.error) throw this.error;
    if (this.ending) return this.result;
    this.ending = true;
    this.timer = setTimeout(() => this.fail(new Error("等待自托管最终结果超时，请重试。")), this.finishTimeoutMs);
    try {
      if (this.pending.length) this.send(this.pending);
      this.pending = Buffer.alloc(0);
      this.send(JSON.stringify({ type: "finish" }));
      return await this.result;
    } catch (error) { this.fail(error); throw error; }
  }

  fail(error) {
    if (this.settled) return;
    this.settled = true;
    this.error = error;
    clearTimeout(this.timer);
    clearInterval(this.heartbeat);
    this.reject(error);
    this.socket?.terminate();
  }

  close() { this.fail(new Error("自托管识别已取消。")); }
}
