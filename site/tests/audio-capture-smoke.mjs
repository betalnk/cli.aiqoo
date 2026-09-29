import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../audio-capture.js', import.meta.url), 'utf8');
const { pcm16FromFloatChunks, wavFromPcm, MAX_PCM_BYTES } = await import(
  `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
const pcm = pcm16FromFloatChunks([new Float32Array([-1, -0.5, 0, 0.5, 1])], 5);
assert.deepEqual(Array.from(pcm), [0, 128, 0, 192, 0, 0, 0, 64, 255, 127]);
const wav = wavFromPcm(pcm, 16000);
assert.equal(wav.type, 'audio/wav');
const bytes = new Uint8Array(await wav.arrayBuffer());
const view = new DataView(bytes.buffer);
const ascii = (start, end) => String.fromCharCode(...bytes.subarray(start, end));
assert.equal(ascii(0, 4), 'RIFF');
assert.equal(ascii(8, 12), 'WAVE');
assert.equal(ascii(12, 16), 'fmt ');
assert.equal(ascii(36, 40), 'data');
assert.equal(view.getUint32(4, true), bytes.length - 8);
assert.equal(view.getUint16(20, true), 1);
assert.equal(view.getUint16(22, true), 1);
assert.equal(view.getUint32(24, true), 16000);
assert.equal(view.getUint32(28, true), 32000);
assert.equal(view.getUint16(34, true), 16);
assert.equal(view.getUint32(40, true), pcm.length);
assert.deepEqual(Array.from(bytes.subarray(44)), Array.from(pcm));
assert.throws(() => pcm16FromFloatChunks([new Float32Array([0, 1])], 1));
assert.throws(() => wavFromPcm(new Uint8Array(MAX_PCM_BYTES + 2), 16000));
assert.throws(() => wavFromPcm(pcm, 96000));
console.log('AUDIO_CAPTURE_SMOKE_OK');
