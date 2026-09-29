import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { webcrypto } from 'node:crypto';

const deviceId = '0123456789abcdef0123456789abcdef';
const clientId = '11111111111111111111111111111111';
const requestId = '22222222222222222222222222222222';
const threadId = '12345678-1234-1234-1234-123456789abc';
const key = await webcrypto.subtle.importKey('raw', new Uint8Array(32).fill(7), 'AES-GCM', false, ['encrypt', 'decrypt']);
const listeners = new Map();
const nodes = new Map();

class Element {
  constructor() {
    this.children = [];
    this.hidden = false;
    this.textContent = '';
    this.dataset = {};
    this.listeners = new Map();
    this.classList = { toggle() {} };
  }
  append(...children) { this.children.push(...children); }
  replaceChildren(...children) { this.children = [...children]; }
  setAttribute(name, value) { this[name] = value; }
  addEventListener(name, listener) { this.listeners.set(name, listener); }
  focus() { document.activeElement = this; }
  click() { this.listeners.get('click')?.(); }
}

globalThis.document = {
  visibilityState: 'visible', activeElement: null,
  getElementById(id) { if (!nodes.has(id)) nodes.set(id, new Element()); return nodes.get(id); },
  createElement() { return new Element(); },
  addEventListener(name, listener) { listeners.set(name, listener); }
};
globalThis.location = { protocol: 'http:', host: 'localhost:4178' };
globalThis.indexedDB = {
  open() {
    const request = {};
    queueMicrotask(() => {
      request.result = {
        transaction() {
          return { objectStore() {
            return { getAll() {
              const result = {};
              queueMicrotask(() => {
                result.result = [{ pairId: 'a'.repeat(32), ownerId: 'user-1',
                  status: 'active', deviceId, clientId, key }];
                result.onsuccess?.();
              });
              return result;
            } };
          } };
        },
        close() {}
      };
      request.onsuccess?.();
    });
    return request;
  }
};

const cryptoForPage = {
  subtle: webcrypto.subtle,
  getRandomValues(bytes) { bytes.fill(0x22); return bytes; }
};
Object.defineProperty(globalThis, 'crypto', { configurable: true, value: cryptoForPage });

function encoded(bytes) { return Buffer.from(bytes).toString('base64url'); }

async function packet(watch, seq, nonceByte, payload) {
  const nonce = new Uint8Array(12).fill(nonceByte);
  const aad = Buffer.from(`CLI Voice history v1\0${deviceId}\0${clientId}\0${requestId}\0${watch.kind}\0${watch.threadId || ''}\0${seq}`);
  const cipher = await webcrypto.subtle.encrypt({ name: 'AES-GCM', iv: nonce,
    additionalData: aad, tagLength: 128 }, key, Buffer.from(JSON.stringify(payload)));
  return { type: 'history_packet', requestId, deviceId, clientId,
    kind: watch.kind, threadId: watch.threadId, seq, nonce: encoded(nonce),
    ciphertext: encoded(cipher) };
}

const vector = await packet({ kind: 'threads', threadId: null }, 1, 9,
  { kind: 'threads', threads: [], capturedAt: 1 });
assert.equal(vector.ciphertext,
  'XKfv_dCU41uCFqNKg4vHrPacQYdcnJKhC91knI4pLViwhJe3lyBPdD7Hhyx1Sf5GO8CZBWzdp7bkJSk-HSc');

class FakeWebSocket {
  static OPEN = 1;
  static instances = [];
  constructor() {
    this.readyState = 0;
    FakeWebSocket.instances.push(this);
    queueMicrotask(() => { this.readyState = 1; this.onopen?.(); });
  }
  send(raw) {
    this.watch = JSON.parse(raw);
    assert.equal(this.watch.type, 'watch_history');
    assert.equal(this.watch.requestId, requestId);
    this.receive({ type: 'watch_started', requestId });
    if (this.watch.kind === 'threads') this.receive(vector);
  }
  receive(frame) { queueMicrotask(() => this.onmessage?.({ data: JSON.stringify(frame) })); }
  close() { this.readyState = 3; this.onclose?.({ code: 1000 }); }
}
globalThis.WebSocket = FakeWebSocket;
const speechCalls = { speak: 0, cancel: 0 };
const speechSynthesis = {
  getVoices: () => [{ voiceURI: 'local-ru', lang: 'ru-RU', localService: true }],
  addEventListener() {},
  speak(utterance) { speechCalls.speak++; this.current = utterance; },
  cancel() { speechCalls.cancel++; this.current = null; }
};
class SpeechSynthesisUtterance {
  constructor(text) { this.text = text; }
}
globalThis.window = {
  crypto: cryptoForPage, indexedDB, WebSocket: FakeWebSocket,
  speechSynthesis, SpeechSynthesisUtterance,
  addEventListener() {}
};

const recipients = [];
globalThis.__testComposeRecipient = item => recipients.push(item);
const source = readFileSync(new URL('../workspace.js', import.meta.url), 'utf8')
  .replace("import { setComposeRecipient } from './compose.js';", 'const setComposeRecipient = globalThis.__testComposeRecipient;');
const { startWorkspace } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
const settle = () => new Promise(resolve => setTimeout(resolve, 20));

await startWorkspace('user-1', [{ deviceId, deviceName: 'Рабочий ПК' }]);
await settle();
assert.equal(nodes.get('workspace-status').textContent, 'Открытых сессий Codex пока нет.');
assert.equal(nodes.get('workspace-live').hidden, false);
assert.equal(nodes.get('workspace-content').hidden, true);

const listSocket = FakeWebSocket.instances[0];
listSocket.receive(await packet(listSocket.watch, 2, 10, {
  kind: 'threads', capturedAt: 2,
  threads: [{ threadId, title: 'Работа над проектом', updatedAt: 2 }]
}));
await settle();
assert.equal(nodes.get('session-list').children.length, 1);
assert.equal(nodes.get('conversation-title').textContent, 'Работа над проектом');

const threadSocket = FakeWebSocket.instances[1];
assert.equal(threadSocket.watch.threadId, threadId);
threadSocket.receive(await packet(threadSocket.watch, 1, 11, {
  kind: 'thread', threadId, capturedAt: 3,
  messages: [
    { role: 'user', text: '<script>тест</script>', createdAt: null },
    { role: 'assistant', text: 'Ответ готов', createdAt: 3, truncated: true }
  ]
}));
await settle();
assert.equal(nodes.get('message-list').children.length, 2);
assert.equal(recipients.at(-1).threadId, threadId);
assert.equal(nodes.get('message-list').children[0].children[1].textContent, '<script>тест</script>');
const assistantRow = nodes.get('message-list').children[1];
assert.equal(assistantRow.children[3].textContent, 'Текст сокращён на компьютере');
const listen = assistantRow.children[2];
listen.click();
assert.equal(listen.textContent, 'Остановить');
assert.equal(speechCalls.speak, 1);
listen.click();
assert.equal(listen.textContent, 'Слушать снова');
assert.equal(speechCalls.cancel, 1);
listen.click();
assert.equal(speechCalls.speak, 2);

listSocket.receive(await packet(listSocket.watch, 3, 12, {
  kind: 'threads', capturedAt: 4,
  threads: [{ threadId, title: 'Новое название сессии', updatedAt: 4 }]
}));
await settle();
assert.equal(nodes.get('conversation-title').textContent, 'Новое название сессии');
assert.equal(recipients.at(-1).title, 'Новое название сессии');
assert.equal(FakeWebSocket.instances.length, 2);

listSocket.receive(await packet(listSocket.watch, 4, 13, {
  kind: 'threads', capturedAt: 5,
  threads: [{ threadId: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee', title: 'Другая сессия', updatedAt: 5 }]
}));
await settle();
assert.equal(nodes.get('conversation').hidden, true);
assert.equal(recipients.at(-1), null);
assert.equal(nodes.get('message-list').children.length, 0);
assert.equal(nodes.get('session-list').children.length, 1);
assert.equal(nodes.get('session-list').children[0]['aria-pressed'], 'false');
assert.equal(FakeWebSocket.instances.length, 2);
assert.equal(speechCalls.cancel, 2);

listSocket.receive({ type: 'device_offline', requestId });
await settle();
assert.equal(nodes.get('workspace-content').hidden, true);
assert.equal(recipients.at(-1), null);
assert.equal(nodes.get('message-list').children.length, 0);
assert.equal(speechCalls.cancel, 2);
document.visibilityState = 'hidden';
listeners.get('visibilitychange')?.();
console.log('WORKSPACE_SMOKE_OK');
