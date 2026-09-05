import WebSocket from "ws";
import { randomUUID } from "node:crypto";
import { gzipSync, gunzipSync } from "node:zlib";

export const DOUBAO_URL = "wss://openspeech.bytedance.com/api/v3/sauc/bigmodel_async";
const CHUNK_BYTES = 6400; // 200 ms of 16 kHz, signed 16-bit mono PCM.

export function encodePacket(type, payload, final = false) {
  const body = gzipSync(payload);
  const header = Buffer.from([0x11, (type << 4) | (final ? 2 : 0), type === 1 ? 0x11 : 0x01, 0]);
  const size = Buffer.alloc(4);
  size.writeUInt32BE(body.length);
  return Buffer.concat([header, size, body]);
}

export function decodePacket(data) {
  const buffer = Buffer.from(data);
  if (buffer.length < 8 || buffer[0] >> 4 !== 1) throw new Error("豆包返回了无效数据包。");
  const type = buffer[1] >> 4;
  const flags = buffer[1] & 15;
  let offset = (buffer[0] & 15) * 4;
  if (offset < 4 || offset + 4 > buffer.length) throw new Error("豆包数据包头不完整。");
  let sequence;
  let code;
  if (type === 15) {
    code = buffer.readUInt32BE(offset);
    offset += 4;
  } else if (flags & 1) {
    sequence = buffer.readInt32BE(offset);
    offset += 4;
  }
  if (offset + 4 > buffer.length) throw new Error("豆包数据包缺少长度。");
  const length = buffer.readUInt32BE(offset);
  offset += 4;
  if (offset + length !== buffer.length) throw new Error("豆包数据包长度不匹配。");
  let payload = buffer.subarray(offset);
  const compression = buffer[2] & 15;
  if (compression === 1) payload = gunzipSync(payload, { maxOutputLength: 4 * 1024 * 1024 });
  else if (compression !== 0) throw new Error("豆包返回了未知压缩格式。");
  const json = payload.length ? JSON.parse(payload.toString("utf8")) : {};
  if (type === 15) throw new Error(`豆包识别错误（${code}）：${json.message || json.error || "请求失败"}`);
  if (type !== 9) throw new Error("豆包返回了未知消息类型。");
  return { payload: json, final: Boolean(flags & 2) || sequence < 0 };
}

export function doubaoHeaders(config) {
  const headers = {
    "X-Api-Resource-Id": config.resourceId || "volc.seedasr.sauc.duration",
    "X-Api-Connect-Id": randomUUID(),
    "X-Api-Request-Id": randomUUID(),
    "X-Api-Sequence": "-1",
  };
  if (config.apiKey?.trim()) headers["X-Api-Key"] = config.apiKey.trim();
  else if (config.appId?.trim() && config.accessToken?.trim()) {
    headers["X-Api-App-Key"] = config.appId.trim();
    headers["X-Api-Access-Key"] = config.accessToken.trim();
  } else throw new Error("请先在 shuo 中填写豆包语音 API Key，或旧版 App ID 和 Access Token。");
  return headers;
}

export class DoubaoStream {
  constructor(config, onPartial = () => {}, { url = DOUBAO_URL, timeoutMs = 15000 } = {}) {
    this.config = config;
    this.onPartial = onPartial;
    this.url = url;
    this.timeoutMs = timeoutMs;
    this.pending = Buffer.alloc(0);
    this.text = "";
    this.settled = false;
    this.ending = false;
    this.result = new Promise((resolve, reject) => {
      this.resolve = resolve;
      this.reject = reject;
    });
    // The result can fail while the microphone is still running.
    this.result.catch(() => {});
  }

  async connect() {
    const headers = doubaoHeaders(this.config);
    const socket = this.socket = new WebSocket(this.url, {
      headers, handshakeTimeout: this.timeoutMs, maxPayload: 4 * 1024 * 1024,
      followRedirects: false,
    });
    socket.on("message", (data) => {
      try {
        const response = decodePacket(data);
        if (response.payload.result?.text !== undefined) {
          this.text = response.payload.result.text;
          this.onPartial(this.text);
        }
        if (response.final) {
          if (!this.ending) throw new Error("豆包提前结束了识别，请重新开始听写。");
          this.settled = true;
          clearTimeout(this.timer);
          this.resolve({ text: this.text, language: "zh" });
          socket.close();
        }
      } catch (error) { this.fail(error); }
    });
    socket.on("error", () => this.fail(new Error("豆包连接失败，请检查网络和调用凭据。")));
    socket.on("unexpected-response", (_request, response) => {
      response.resume();
      this.fail(new Error(`豆包连接被拒绝（HTTP ${response.statusCode}），请检查 API Key 和流式识别服务权限。`));
    });
    socket.on("close", () => {
      if (!this.settled) this.fail(new Error("豆包连接已断开，未收到完整结果，请重试。"));
    });
    await Promise.race([
      new Promise((resolve) => socket.once("open", resolve)),
      this.result,
    ]);
    if (this.error) throw this.error;
    this.send(encodePacket(1, Buffer.from(JSON.stringify({
      user: { uid: "shuo" },
      audio: { format: "pcm", codec: "raw", rate: 16000, bits: 16, channel: 1 },
      request: { model_name: "bigmodel", enable_itn: true, enable_punc: true, result_type: "full" },
    }))));
  }

  send(packet) {
    if (this.error) throw this.error;
    if (this.socket?.readyState !== WebSocket.OPEN) throw new Error("豆包连接尚未就绪。");
    if (this.socket.bufferedAmount > 320000) throw new Error("网络上传积压超过 10 秒，请检查网络后重试。");
    this.socket.send(packet, (error) => { if (error) this.fail(new Error("无法向豆包发送音频。")); });
  }

  feed(frame) {
    if (this.error) throw this.error;
    const audio = Buffer.alloc(frame.length * 2);
    for (let index = 0; index < frame.length; index++) audio.writeInt16LE(frame[index], index * 2);
    this.pending = Buffer.concat([this.pending, audio]);
    while (this.pending.length >= CHUNK_BYTES) {
      this.send(encodePacket(2, this.pending.subarray(0, CHUNK_BYTES)));
      this.pending = this.pending.subarray(CHUNK_BYTES);
    }
  }

  async finish() {
    if (this.error) throw this.error;
    this.ending = true;
    this.timer = setTimeout(() => this.fail(new Error("等待豆包最终结果超时，请重试。")), this.timeoutMs);
    try {
      this.send(encodePacket(2, this.pending, true));
      this.pending = Buffer.alloc(0);
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

  close() { this.fail(new Error("豆包识别已取消。")); }
}
