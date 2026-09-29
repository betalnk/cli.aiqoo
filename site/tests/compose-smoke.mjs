import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const nodes = new Map();
class Element {
  constructor() {
    this.hidden = false;
    this.disabled = false;
    this.value = '';
    this.textContent = '';
    this.attributes = new Map();
    this.listeners = new Map();
    this.classList = { toggle() {}, add() {} };
  }
  addEventListener(type, listener) { this.listeners.set(type, listener); }
  hasAttribute(name) { return this.attributes.has(name); }
  removeAttribute(name) { this.attributes.delete(name); }
  pause() {}
  load() {}
}
globalThis.document = { visibilityState: 'visible',
  getElementById(id) { if (!nodes.has(id)) nodes.set(id, new Element()); return nodes.get(id); },
  addEventListener() {} };
globalThis.window = { addEventListener() {} };
globalThis.__testMicrophoneAvailable = () => true;
globalThis.__testStartCapture = async () => ({
  stop: async () => ({ pcm: new Uint8Array([0, 0]), sampleRate: 16000,
    wav: new Blob([new Uint8Array(46)], { type: 'audio/wav' }) }),
  cancel: async () => {}
});
const calls = [];
let nextResult = { stage: 'confirmed' };
globalThis.__testRunCommand = async input => {
  calls.push(input);
  input.onStage({ stage: 'accepted' });
  return nextResult;
};
const source = readFileSync(new URL('../compose.js', import.meta.url), 'utf8')
  .replace("import { microphoneAvailable, startMicrophoneCapture, MAX_RECORDING_SECONDS } from './audio-capture.js';",
    'const microphoneAvailable = globalThis.__testMicrophoneAvailable; const startMicrophoneCapture = globalThis.__testStartCapture; const MAX_RECORDING_SECONDS = 45;')
  .replace("import { runCommand } from './command-client.js';",
    'const runCommand = globalThis.__testRunCommand;');
const { setComposeRecipient } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
const form = nodes.get('compose');
const input = nodes.get('message-draft');
const send = nodes.get('send-message');
const status = nodes.get('send-status');
assert.equal(form.hidden, true);
assert.equal(send.disabled, true);
const pair = { deviceId: 'a'.repeat(32), deviceName: 'ПК' };
const first = { pair, threadId: '12345678-1234-1234-1234-123456789abc', title: 'Первая' };
const second = { pair, threadId: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee', title: 'Вторая' };
const edit = value => { input.value = value; input.listeners.get('input')(); };
const submit = () => form.listeners.get('submit')({ preventDefault() {} });
setComposeRecipient(first);
assert.equal(form.hidden, false);
edit('текст для первой');
setComposeRecipient(second);
edit('текст для второй');
setComposeRecipient(first);
assert.equal(input.value, 'текст для первой');
edit('строка\nкоманда');
submit();
assert.equal(calls.length, 0);
assert.match(status.textContent, /одну строку/);
edit('Текст с эмодзи 🙂');
submit();
assert.equal(calls.length, 0);
edit('echo & calc');
submit();
assert.equal(calls.length, 0);
edit('Проверка');
submit();
await new Promise(resolve => setTimeout(resolve, 0));
assert.equal(calls.length, 1);
assert.equal(calls[0].threadId, first.threadId);
assert.equal(new TextDecoder().decode(calls[0].bytes), 'Проверка');
assert.equal(input.value, '');
assert.match(status.textContent, /Подтверждено/);
nextResult = { stage: 'unknown', reason: 'timeout' };
edit('Повторять нельзя');
submit();
await new Promise(resolve => setTimeout(resolve, 0));
assert.equal(calls.length, 2);
assert.equal(input.value, 'Повторять нельзя');
await new Promise(resolve => setTimeout(resolve, 20));
assert.equal(calls.length, 2);
nextResult = { stage: 'rejected', reason: 'input_failed' };
submit();
await new Promise(resolve => setTimeout(resolve, 0));
assert.equal(calls.length, 3);
assert.equal(input.value, 'Повторять нельзя');
assert.match(status.textContent, /проверьте текст во вкладке ПК/);
setComposeRecipient(null);
assert.equal(form.hidden, true);
assert.equal(send.disabled, true);
setComposeRecipient(first);
assert.equal(input.value, 'Повторять нельзя');
setComposeRecipient(second);
assert.equal(input.value, 'текст для второй');
const record = nodes.get('record-voice');
record.listeners.get('click')();
await new Promise(resolve => setTimeout(resolve, 0));
assert.match(record.textContent, /Остановить/);
record.listeners.get('click')();
await new Promise(resolve => setTimeout(resolve, 0));
assert.equal(nodes.get('voice-preview-wrap').hidden, false);
nextResult = { stage: 'transcribed', text: 'Алло, проверка' };
nodes.get('transcribe-voice').listeners.get('click')();
await new Promise(resolve => setTimeout(resolve, 0));
assert.equal(calls.length, 4);
assert.equal(calls[3].op, 'transcribe_audio');
assert.equal(calls[3].threadId, null);
assert.equal(input.value, 'текст для второй Алло, проверка');
console.log('COMPOSE_SMOKE_OK');
