import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { fixture, crypto } from './pairing-fixture.mjs';
import { fragmentKey } from '../pairing.js';

// Import the unchanged page entry points, including their workspace imports.
// Each page receives an isolated DOM/IndexedDB/HTTP fixture and real WebCrypto.
const mode = process.argv[2];
if (!mode) {
  const modes = ['account-bind', 'qr-bind', 'account-login', 'qr-login',
    'account-cancel', 'qr-cancel', 'account-wrongkey', 'qr-wrongkey',
    'account-existing-devices', 'account-recover-active', 'account-direct'];
  for (const name of modes) {
    const child = spawnSync(process.execPath, [fileURLToPath(import.meta.url), name],
      { encoding: 'utf8', timeout: 12_000 });
    assert.equal(child.status, 0, name + ': ' + (child.stderr || child.stdout || child.error));
  }
  console.log('PAIRING_PAGES_SMOKE_OK (' + modes.length + ' scenarios)');
} else {
  const f = await fixture();
  const account = mode.startsWith('account-');
  const page = account ? 'account' : 'pair';
  const html = readFileSync(new URL('../' + page + '.html', import.meta.url), 'utf8');
  const nodes = new Map(), globalListeners = new Map();
  class Element {
    constructor(attributes = '') {
      this.hidden = /\bhidden\b/.test(attributes);
      this.disabled = /\bdisabled\b/.test(attributes);
      this.children = [];
      this.dataset = {};
      this.textContent = '';
      this.value = '';
      this.listeners = new Map();
      this.classes = new Set();
      this.classList = {
        add: value => this.classes.add(value),
        toggle: (value, enabled) => enabled ? this.classes.add(value) : this.classes.delete(value),
        contains: value => this.classes.has(value)
      };
    }
    get childElementCount() { return this.children.length; }
    append(...children) { this.children.push(...children); }
    replaceChildren(...children) { this.children = children; }
    setAttribute(name, value) { this[name] = value; }
    removeAttribute(name) { delete this[name]; }
    addEventListener(name, listener) {
      if (!this.listeners.has(name)) this.listeners.set(name, []);
      this.listeners.get(name).push(listener);
    }
    querySelector() { return this.paragraph ??= new Element(); }
    focus() { document.activeElement = this; }
    pause() {}
    click() {
      if (this.disabled || this.hidden) return Promise.resolve();
      return Promise.all((this.listeners.get('click') || []).map(listener => listener()));
    }
  }
  for (const tag of html.matchAll(/<[a-z][a-z0-9-]*\b([^>]*)>/gi)) {
    const id = /\bid="([^"]+)"/.exec(tag[1]);
    if (id) nodes.set(id[1], new Element(tag[1]));
  }
  const step = new Element();
  globalThis.document = {
    visibilityState: 'visible', activeElement: null,
    getElementById: id => nodes.get(id) ?? null,
    createElement: () => new Element(),
    querySelector: selector => selector === '.pair-panel__step' ? step : null,
    addEventListener: (name, listener) => globalListeners.set(name, listener)
  };
  Object.defineProperty(globalThis, 'crypto', { configurable: true, value: crypto });
  globalThis.sessionStorage = f.storage;
  globalThis.location = {
    hash: f.fragment, pathname: '/' + page + '.html', search: '',
    protocol: 'https:', host: 'cli.fixture.invalid', redirect: null, reloads: 0,
    replace(path) { this.redirect = path; },
    reload() { this.reloads++; }
  };
  globalThis.history = { state: null, replaceState(_state, _title, path) {
    assert.equal(path, '/' + page + '.html');
    location.hash = '';
  } };
  globalThis.addEventListener = (name, listener) => globalListeners.set(name, listener);
  globalThis.indexedDB = {
    open() {
      const request = {};
      queueMicrotask(() => {
        request.result = {
          objectStoreNames: { contains: () => true }, close() {},
          transaction() {
            const transaction = { objectStore() {
              const operation = work => {
                const result = {};
                queueMicrotask(() => {
                  result.result = structuredClone(work());
                  result.onsuccess?.();
                  transaction.oncomplete?.();
                });
                return result;
              };
              return {
                get: id => operation(() => f.records.get(id) ?? null),
                getAll: () => operation(() => [...f.records.values()]),
                put: record => operation(() => {
                  f.records.set(record.pairId, structuredClone(record)); return record.pairId;
                }),
                delete: id => operation(() => { f.records.delete(id); })
              };
            } };
            return transaction;
          }
        };
        request.onsuccess?.();
      });
      return request;
    }
  };
  class FakeWebSocket {
    static OPEN = 1;
    constructor() { this.readyState = 0; }
    close() { this.readyState = 3; }
  }
  globalThis.WebSocket = FakeWebSocket;
  globalThis.window = { crypto, indexedDB, WebSocket: FakeWebSocket, addEventListener() {} };
  const response = (status, value) => ({ status, ok: status < 400, json: async () => value });
  let authCalls = 0;
  globalThis.fetch = async (path, options) => {
    if (path === '/api/v1/me') {
      authCalls++;
      assert.equal(location.hash, '', 'fragment must be cleared before auth');
      if (mode !== 'account-direct' && mode !== 'account-recover-active') {
        assert.equal(f.storage.getItem(fragmentKey), f.fragment);
      }
      return mode.endsWith('-login') ? response(401, { error: 'unauthorized' })
        : response(200, { user: { id: f.ownerId, email: 'owner@example.test', githubLogin: 'fixture' } });
    }
    if (path === '/api/v1/devices') {
      const devices = mode === 'account-existing-devices' || f.state?.status === 'active'
        ? [{ deviceId: f.deviceId, deviceName: 'Fixture PC' }] : [];
      return response(200, { devices });
    }
    return f.fetch(path, options);
  };
  if (mode === 'account-recover-active') {
    await f.pausePending();
    await f.approve();
    location.hash = '';
  }
  if (mode === 'account-direct') {
    location.hash = '';
    f.storage.removeItem(fragmentKey);
  }
  if (mode.endsWith('-wrongkey')) f.afterCommit = state => { state.devicePublicKey = state.clientPublicKey; };
  const until = async predicate => {
    const deadline = Date.now() + 5000;
    while (!predicate()) {
      assert.ok(Date.now() < deadline, 'page did not reach expected state');
      await new Promise(resolve => setTimeout(resolve, 5));
    }
  };
  await import('../' + page + '.js');
  const button = nodes.get(account ? 'bind-pc' : 'pair-submit');
  const status = nodes.get(account ? 'bind-status' : 'pair-status');
  if (mode.endsWith('-login')) {
    await until(() => location.redirect);
    assert.equal(location.redirect, account ? '/login.html' : '/api/v1/auth/github/start?next=%2Fpair.html');
    assert.equal(f.storage.getItem(fragmentKey), f.fragment);
    assert.equal(f.posts(), 0);
  } else if (mode === 'account-direct') {
    await until(() => nodes.get('account-status').hidden);
    assert.equal(nodes.get('account-pairing').hidden, true);
    assert.equal(f.posts(), 0);
  } else if (mode === 'account-recover-active') {
    await until(() => location.reloads === 1);
    assert.equal(f.posts(), 1, 'reload recovery must never replay claim');
    await f.verifyEncryption(f.records.get(f.pairId));
  } else {
    await until(() => !button.disabled && !button.hidden && button.listeners.has('click'));
    assert.equal(f.posts(), 0, 'page load cannot claim');
    assert.equal(location.hash, '');
    assert.equal(authCalls, 1);
    if (mode === 'account-existing-devices') {
      assert.match(nodes.get('workspace-status').textContent, /Этот браузер ещё не связан/);
    } else {
      if (account) assert.match(nodes.get('workspace-status').textContent, /Сначала привяжите этот ПК/);
      const first = button.click(), second = button.click();
      if (mode.endsWith('-wrongkey')) {
        await Promise.all([first, second]);
        assert.match(status.textContent, /Ключ компьютера не совпал/);
        assert.equal(button.hidden, true);
        assert.equal(f.records.size, 0);
      } else {
        await until(() => f.records.get(f.pairId)?.status === 'awaiting_device_accept'
          && status.textContent.includes('«ОК»'));
        assert.equal(f.posts(), 1);
        assert.equal(f.records.get(f.pairId).key, undefined);
        assert.equal(location.reloads, 0, 'cannot activate workspace before PC OK');
        if (account) assert.match(nodes.get('workspace-status').textContent, /Ожидаем «ОК»/);
        if (mode.endsWith('-cancel')) f.state.status = 'revoked';
        else await f.approve();
        await Promise.all([first, second]);
        if (mode.endsWith('-cancel')) {
          assert.match(status.textContent, /отклонён на ПК/);
          assert.equal(f.records.size, 0);
          assert.equal(location.reloads, 0);
        } else {
          assert.match(status.textContent, /ПК привязан/);
          await f.verifyEncryption(f.records.get(f.pairId));
          assert.equal(location.reloads, account ? 1 : 0);
        }
      }
      assert.equal(f.posts(), 1, 'double-click sends exactly one claim');
    }
  }
}
