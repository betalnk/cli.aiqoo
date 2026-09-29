import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { webcrypto } from 'node:crypto';

const deviceId = '0123456789abcdef0123456789abcdef';
const clientId = '11111111111111111111111111111111';
const threadId = '12345678-1234-1234-1234-123456789abc';
const key = await webcrypto.subtle.importKey('raw', new Uint8Array(32).fill(7), 'AES-GCM', false, ['encrypt', 'decrypt']);
const pair = { deviceId, clientId, key };
const encoder = new TextEncoder();
const decoder = new TextDecoder();
Object.defineProperty(globalThis, 'crypto', { configurable: true, value: webcrypto });
globalThis.location = { protocol: 'https:', host: 'cli.example.test' };

function aad(command, direction, frame, seq, stage = '') {
  return encoder.encode(`CLI Voice command v1\0${deviceId}\0${clientId}\0${command.requestId}\0${command.op}\0${command.threadId || ''}\0${direction}\0${frame}\0${seq}\0${stage}`);
}
async function decrypt(command, frame, seq, direction) {
  return new Uint8Array(await webcrypto.subtle.decrypt({ name: 'AES-GCM', iv: Buffer.from(frame.nonce, 'base64url'),
    additionalData: aad(command, direction, frame.type === 'command_start' ? 'start' : 'chunk', seq), tagLength: 128 },
  key, Buffer.from(frame.ciphertext, 'base64url')));
}
async function ack(command, ackSeq, stage, payload) {
  const nonce = webcrypto.getRandomValues(new Uint8Array(12));
  const ciphertext = await webcrypto.subtle.encrypt({ name: 'AES-GCM', iv: nonce,
    additionalData: aad(command, 'device', 'ack', ackSeq, stage), tagLength: 128 }, key,
  encoder.encode(JSON.stringify({ v: 1, stage, ...payload })));
  return { type: 'command_ack', requestId: command.requestId, deviceId, clientId,
    op: command.op, threadId: command.threadId, ackSeq, stage,
    nonce: Buffer.from(nonce).toString('base64url'), ciphertext: Buffer.from(ciphertext).toString('base64url') };
}
const instances = [];
let behavior = 'send';
class FakeWebSocket {
  static OPEN = 1;
  constructor(url) {
    assert.equal(url, 'wss://cli.example.test/api/v1/ws/history');
    this.readyState = 0;
    this.bufferedAmount = 0;
    this.frames = [];
    this.mode = behavior;
    instances.push(this);
    setTimeout(() => { this.readyState = 1; this.onopen?.(); }, 0);
  }
  send(raw) {
    const frame = JSON.parse(raw);
    this.frames.push(frame);
    if (frame.type === 'command_start') {
      this.command = frame;
      if (this.mode === 'drop') return setTimeout(() => this.close(), 0);
      setTimeout(async () => {
        this.receive({ type: 'command_started', requestId: frame.requestId });
        this.receive(await ack(frame, 1, 'ready', {}));
      }, 0);
    } else if (frame.type === 'command_chunk' && this.mode === 'reject' && frame.seq === 1) {
      setTimeout(async () => this.receive(await ack(this.command, 2, 'rejected',
        { reason: 'input_failed', maybeEntered: false })), 0);
    } else if (frame.type === 'command_end') {
      setTimeout(async () => {
        if (this.mode === 'send') {
          this.receive(await ack(this.command, 2, 'submitted', {}));
          this.receive(await ack(this.command, 3, 'confirmed', {}));
        } else if (this.mode === 'transcribe') {
          this.receive(await ack(this.command, 2, 'transcribed', { text: 'Алло, проверка' }));
        }
      }, 0);
    }
  }
  receive(frame) { this.onmessage?.({ data: JSON.stringify(frame) }); }
  close() { if (this.readyState === 3) return; this.readyState = 3; this.onclose?.(); }
}
globalThis.WebSocket = FakeWebSocket;

const source = readFileSync(new URL('../command-client.js', import.meta.url), 'utf8');
const { runCommand } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

const vectorKey = await webcrypto.subtle.importKey('raw', Uint8Array.from({ length: 32 }, (_, index) => index),
  'AES-GCM', false, ['decrypt']);
const vectorAad = encoder.encode(['CLI Voice command v1',
  '11111111111111111111111111111111', '22222222222222222222222222222222',
  '33333333333333333333333333333333', 'send_text',
  '123e4567-e89b-42d3-a456-426614174000', 'browser', 'start', '0', ''].join('\0'));
const vectorCipher = 'PCCgOf_U7jniMbWxk5odA-eJ81GID31QGhON93gIZPtlMpTenvMh_UCRSdql4hABjHRUvz7ljrsLohw0LNHD2MEI903n4RZRPniIGoD7b4R6870HBUFpWtHf6qMMkdXOo403jddj8ivj6R9tDYTbENKjNfWAbW_Yqw';
const vectorPlain = await webcrypto.subtle.decrypt({ name: 'AES-GCM',
  iv: Buffer.from('AAECAwQFBgcICQoL', 'base64url'), additionalData: vectorAad, tagLength: 128 },
vectorKey, Buffer.from(vectorCipher, 'base64url'));
assert.equal(decoder.decode(vectorPlain),
  '{"v":1,"op":"send_text","threadId":"123e4567-e89b-42d3-a456-426614174000","totalBytes":2,"totalChunks":1}');

behavior = 'send';
const stages = [];
const textBytes = encoder.encode('Проверка');
const sendResult = await runCommand({ pair, op: 'send_text', threadId, bytes: textBytes,
  onStage: item => stages.push(item.stage) });
assert.equal(sendResult.stage, 'confirmed');
assert.ok(stages.includes('accepted'));
assert.ok(stages.includes('submitted'));
const sendSocket = instances.at(-1);
assert.deepEqual(sendSocket.frames.map(frame => frame.type), ['command_start', 'command_chunk', 'command_end']);
const sendStart = sendSocket.frames[0];
assert.equal(sendStart.threadId, threadId);
assert.deepEqual(JSON.parse(decoder.decode(await decrypt(sendStart, sendStart, 0, 'browser'))),
  { v: 1, op: 'send_text', threadId, totalBytes: textBytes.length, totalChunks: 1 });
assert.equal(decoder.decode(await decrypt(sendStart, sendSocket.frames[1], 1, 'browser')), 'Проверка');

behavior = 'transcribe';
const audio = new Uint8Array(9000);
const audioResult = await runCommand({ pair, op: 'transcribe_audio', threadId: null,
  sampleRate: 16000, bytes: audio });
assert.deepEqual({ stage: audioResult.stage, text: audioResult.text },
  { stage: 'transcribed', text: 'Алло, проверка' });
const audioFrames = instances.at(-1).frames;
assert.deepEqual(audioFrames.map(frame => frame.type),
  ['command_start', 'command_chunk', 'command_chunk', 'command_end']);
assert.deepEqual(JSON.parse(decoder.decode(await decrypt(audioFrames[0], audioFrames[0], 0, 'browser'))),
  { v: 1, op: 'transcribe_audio', threadId: null, totalBytes: 9000, totalChunks: 2,
    format: 'pcm_s16le', sampleRate: 16000, channels: 1 });
assert.equal((await decrypt(audioFrames[0], audioFrames[1], 1, 'browser')).length, 8192);
assert.equal((await decrypt(audioFrames[0], audioFrames[2], 2, 'browser')).length, 808);

behavior = 'reject';
const rejected = await runCommand({ pair, op: 'transcribe_audio', threadId: null,
  sampleRate: 16000, bytes: audio });
assert.deepEqual({ stage: rejected.stage, reason: rejected.reason },
  { stage: 'rejected', reason: 'input_failed' });
assert.deepEqual(instances.at(-1).frames.map(frame => frame.type), ['command_start', 'command_chunk']);

behavior = 'drop';
const count = instances.length;
const unknown = await runCommand({ pair, op: 'send_text', threadId, bytes: textBytes });
assert.equal(unknown.stage, 'unknown');
assert.equal(instances.length, count + 1);
console.log('COMMAND_SMOKE_OK');
