import assert from "node:assert/strict";
import test from "node:test";
import { once } from "node:events";
import { gzipSync, gunzipSync } from "node:zlib";
import { WebSocketServer } from "ws";
import { decodePacket, doubaoHeaders, DoubaoStream } from "../worker/doubao.mjs";

function response(text, final = false) {
  const payload = gzipSync(Buffer.from(JSON.stringify({ result: { text } })));
  const header = Buffer.from([0x11, final ? 0x93 : 0x91, 0x11, 0]);
  const fields = Buffer.alloc(8);
  fields.writeInt32BE(final ? -2 : 1);
  fields.writeUInt32BE(payload.length, 4);
  return Buffer.concat([header, fields, payload]);
}

async function withServer(handler, run) {
  const server = new WebSocketServer({ host: "127.0.0.1", port: 0 });
  await once(server, "listening");
  server.on("connection", handler);
  try { await run("ws://127.0.0.1:" + server.address().port); }
  finally {
    for (const client of server.clients) client.terminate();
    await new Promise((resolve) => server.close(resolve));
  }
}

test("stream sends 200ms PCM while recording and waits for the final corrected transcript", async () => {
  let samples = [];
  let packetCount = 0;
  const partials = [];
  await withServer((socket, request) => {
    assert.equal(request.headers["x-api-key"], "test-only");
    assert.equal(request.headers["x-api-resource-id"], "volc.seedasr.sauc.duration");
    socket.on("message", (message) => {
      const type = message[1] >> 4;
      const payload = gunzipSync(message.subarray(8));
      if (type === 1) {
        const config = JSON.parse(payload);
        assert.equal(config.audio.rate, 16000);
        assert.equal(config.request.result_type, "full");
        return;
      }
      packetCount++;
      if (!(message[1] & 2)) assert.equal(payload.length, 6400);
      for (let i = 0; i < payload.length; i += 2) samples.push(payload.readInt16LE(i));
      socket.send(response(message[1] & 2 ? "你好，世界。" : "你好", Boolean(message[1] & 2)));
    });
  }, async (url) => {
    const stream = new DoubaoStream({ apiKey: "test-only" }, (text) => partials.push(text), { url });
    await stream.connect();
    const audio = Int16Array.from({ length: 3500 }, (_, i) => i % 2 ? -123 : 456);
    stream.feed(audio);
    await new Promise((resolve) => setTimeout(resolve, 30));
    assert.deepEqual(partials, ["你好"]);
    const result = await stream.finish();
    assert.equal(result.text, "你好，世界。");
    assert.equal(packetCount, 2);
    assert.deepEqual(samples, [...audio]);
    stream.close();
  });
});

test("disconnects and missing final responses fail instead of pasting partial text", async () => {
  for (const mode of ["disconnect", "timeout"]) {
    await withServer((socket) => {
      socket.on("message", (message) => {
        if (message[1] & 2) {
          socket.send(response("尚未确认"));
          if (mode === "disconnect") socket.close();
        }
      });
    }, async (url) => {
      const stream = new DoubaoStream({ apiKey: "test-only" }, () => {}, { url, timeoutMs: 100 });
      await stream.connect();
      stream.feed(new Int16Array(3200));
      await assert.rejects(stream.finish(), /断开|超时/);
      stream.close();
    });
  }
});

test("malformed server packets are rejected and final flags are decoded", () => {
  assert.equal(decodePacket(response("完成", true)).final, true);
  assert.throws(() => decodePacket(response("截断").subarray(0, 9)), /长度|缺少/);
  assert.throws(() => decodePacket(Buffer.from([0x11, 0x91])), /无效/);
});

test("supports new and legacy authentication without mixing credential schemes", () => {
  assert.throws(() => doubaoHeaders({}), /凭据|API Key/);
  const current = doubaoHeaders({ apiKey: " key ", appId: "old", accessToken: "token" });
  assert.equal(current["X-Api-Key"], "key");
  assert.equal(current["X-Api-App-Key"], undefined);
  const legacy = doubaoHeaders({ appId: "old", accessToken: "token" });
  assert.equal(legacy["X-Api-App-Key"], "old");
  assert.equal(legacy["X-Api-Access-Key"], "token");
});

test("authentication rejection is bounded and does not expose credentials", async () => {
  const server = new WebSocketServer({ host: "127.0.0.1", port: 0, verifyClient: () => false });
  await once(server, "listening");
  const stream = new DoubaoStream({ apiKey: "secret-test-value" }, () => {}, {
    url: "ws://127.0.0.1:" + server.address().port, timeoutMs: 1000,
  });
  try {
    await assert.rejects(stream.connect(), (error) => /HTTP 401/.test(error.message) && !error.message.includes("secret-test-value"));
  } finally {
    stream.close();
    await new Promise((resolve) => server.close(resolve));
  }
});
