import { randomUUID } from "node:crypto";
import { QwenStream } from "./qwen.mjs";

export class QwenRealtimeStream extends QwenStream {
  constructor(config, onPartial, options) {
    super(config, onPartial, options);
    this.itemIndexes = new Map();
  }

  sendEvent(type, fields = {}) {
    this.send(JSON.stringify({ event_id: randomUUID(), type, ...fields }));
  }

  sendCommand(action) {
    if (action === "run-task") {
      this.sendEvent("session.update", { session: {
        modalities: ["text"], input_audio_format: "pcm", sample_rate: 16000,
        turn_detection: { type: "server_vad", threshold: 0.2, silence_duration_ms: 800 },
      } });
    } else if (action === "finish-task") this.sendEvent("session.finish");
    else throw new Error("未知千问实时识别指令。");
  }

  sendAudio(audio) {
    this.sendEvent("input_audio_buffer.append", { audio: audio.toString("base64") });
  }

  itemIndex(id) {
    if (typeof id !== "string" || !id) throw new Error("千问返回的语句编号无效。");
    if (!this.itemIndexes.has(id)) this.itemIndexes.set(id, this.itemIndexes.size);
    return this.itemIndexes.get(id);
  }

  receive(event) {
    if (this.settled) return;
    switch (event.type) {
      case "session.updated":
        if (!this.ready) {
          this.ready = true;
          clearTimeout(this.timer);
          this.resolveReady();
        }
        break;
      case "input_audio_buffer.speech_started":
        this.itemIndex(event.item_id);
        break;
      case "conversation.item.created": {
        const index = this.itemIndex(event.item?.id);
        if (!this.items.has(index)) this.items.set(index, { text: "", complete: false });
        break;
      }
      case "conversation.item.input_audio_transcription.text":
      case "conversation.item.input_audio_transcription.completed": {
        const index = this.itemIndex(event.item_id);
        if (this.items.get(index)?.complete) break;
        const complete = event.type.endsWith(".completed");
        const text = complete ? event.transcript : event.text;
        if (typeof text !== "string" || (!complete && event.stash != null && typeof event.stash !== "string"))
          throw new Error("千问返回的识别结果格式无效。");
        this.items.set(index, { text: complete ? text : text + (event.stash || ""), complete });
        this.onPartial(this.transcript());
        break;
      }
      case "session.finished":
        super.receive({ header: { task_id: this.taskId, event: "task-finished" } });
        break;
      case "error":
      case "conversation.item.input_audio_transcription.failed":
        super.receive({ header: { task_id: this.taskId, event: "task-failed",
          error_message: event.error?.message, error_code: event.error?.code } });
        break;
    }
  }
}
