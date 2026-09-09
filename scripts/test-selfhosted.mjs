import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { performance } from "node:perf_hooks";
import { setTimeout as delay } from "node:timers/promises";
import { SelfHostedStream } from "../worker/selfhosted.mjs";

const [url, audioPath, model] = process.argv.slice(2);
if (!url || !audioPath) throw new Error("Usage: node scripts/test-selfhosted.mjs URL chinese-pcm16-mono-16000.pcm");
const sample = readFileSync(audioPath);
assert.equal(sample.length % 2, 0);
const cases = [
  ["silence", Buffer.alloc(64000)],
  ["short-stop", sample.subarray(0, 38400)],
  ["chinese", sample],
  ["pauses", Buffer.concat([sample, Buffer.alloc(32000), sample, Buffer.alloc(32000)])],
  ["continuous", Buffer.concat(Array.from({ length: 5 }, () => Buffer.concat([sample, Buffer.alloc(9600)])))],
];
for (const [name, audio] of cases) {
  let started, first, partials = 0;
  const stream = new SelfHostedStream({ url, language: "zh", model }, text => {
    if (text && first === undefined) first = (performance.now() - started) / 1000;
    partials++;
  });
  try {
    await stream.connect();
    started = performance.now();
    for (let offset = 0; offset < audio.length; offset += 6400) {
      const end = Math.min(offset + 6400, audio.length);
      await delay(Math.max(0, started + end / 32 - performance.now()));
      const packet = audio.subarray(offset, end);
      const samples = new Int16Array(packet.length / 2);
      for (let i = 0; i < samples.length; i++) samples[i] = packet.readInt16LE(i * 2);
      stream.feed(samples);
    }
    const stopped = performance.now();
    const result = await stream.finish();
    if (name === "silence") { assert.equal(result.text, ""); assert.equal(partials, 0); }
    else assert.ok(result.text);
    console.log(JSON.stringify({ name, audio_seconds: audio.length / 32000, first_text_seconds: first,
      finish_wait_seconds: (performance.now() - stopped) / 1000, partials, ...result }));
  } finally { stream.close(); }
}
