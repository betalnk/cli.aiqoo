import assert from 'node:assert/strict';
import { webcrypto } from 'node:crypto';
import { createPairingClient, fragmentKey } from '../pairing.js';

export const crypto = webcrypto;
export const encoded = bytes => Buffer.from(bytes).toString('base64url');
export function memoryStorage() {
  const values = new Map();
  return { getItem: key => values.get(key) ?? null, setItem: (key, value) => values.set(key, String(value)),
    removeItem: key => values.delete(key) };
}

export async function fixture() {
  const deviceKeys = await crypto.subtle.generateKey({ name: 'ECDH', namedCurve: 'P-256' }, false, ['deriveBits']);
  const deviceSpki = await crypto.subtle.exportKey('spki', deviceKeys.publicKey);
  const secret = crypto.getRandomValues(new Uint8Array(32));
  const f = { pairId: '0123456789abcdef0123456789abcdef', deviceId: 'a'.repeat(32),
    clientId: 'b'.repeat(32), ownerId: 'account-one', deviceKeys, secret, records: new Map(),
    storage: memoryStorage(), calls: [], time: Date.now(), state: null,
    dropReply: false, failBeforeSend: false, afterCommit: null, onSleep: null };
  f.devicePublicKey = encoded(deviceSpki);
  f.fragment = '#v=1&pairId=' + f.pairId + '&code=23456789ABCD&secret=' + encoded(secret)
    + '&fp=' + encoded(await crypto.subtle.digest('SHA-256', deviceSpki));
  f.storage.setItem(fragmentKey, f.fragment);
  const reply = (status, value) => ({ status, ok: status >= 200 && status < 300,
    json: async () => structuredClone(value) });
  f.fetch = async (path, options = {}) => {
    f.calls.push({ path, method: options.method || 'GET' });
    assert.equal(options.credentials, 'same-origin');
    assert.equal(options.cache, 'no-store');
    if (path === '/api/v1/pairings/claim') {
      if (f.failBeforeSend) throw new TypeError('Network interrupted before commit');
      const body = JSON.parse(options.body);
      assert.deepEqual(Object.keys(body).sort(), ['clientName', 'clientPublicKey', 'code', 'pairId', 'proof']);
      assert.equal(body.pairId, f.pairId);
      if (f.state) return reply(404, { error: 'pairing_unavailable' });
      f.state = { pairId: f.pairId, deviceId: f.deviceId, clientId: f.clientId, accountId: f.ownerId,
        clientPublicKey: body.clientPublicKey, proof: body.proof, devicePublicKey: f.devicePublicKey,
        deviceName: 'Fixture PC', expiresAt: Math.floor(f.time / 1000) + 300, status: 'awaiting_device_accept' };
      f.afterCommit?.(f.state);
      if (f.dropReply) throw new TypeError('Reply lost after commit');
      return reply(200, f.state);
    }
    assert.equal(path, '/api/v1/pairings/' + f.pairId);
    return f.state ? reply(200, f.state) : reply(404, { error: 'pairing_unavailable' });
  };
  f.approve = async () => {
    assert.ok(f.state);
    const clientSpki = Buffer.from(f.state.clientPublicKey, 'base64url');
    const hmac = await crypto.subtle.importKey('raw', secret, { name: 'HMAC', hash: 'SHA-256' }, false, ['verify']);
    const message = Buffer.concat([Buffer.from('CLI Voice pair v1\0' + f.pairId + '\0'), clientSpki]);
    assert.equal(await crypto.subtle.verify('HMAC', hmac, Buffer.from(f.state.proof, 'base64url'), message), true);
    const clientPublic = await crypto.subtle.importKey('spki', clientSpki,
      { name: 'ECDH', namedCurve: 'P-256' }, false, []);
    const shared = await crypto.subtle.deriveBits({ name: 'ECDH', public: clientPublic }, deviceKeys.privateKey, 256);
    const material = await crypto.subtle.importKey('raw', shared, 'HKDF', false, ['deriveKey']);
    f.desktopKey = await crypto.subtle.deriveKey({ name: 'HKDF', hash: 'SHA-256', salt: secret,
      info: Buffer.from('CLI Voice v1\0' + f.pairId + '\0' + f.clientId) },
    material, { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
    new Uint8Array(shared).fill(0);
    f.state.status = 'active';
  };
  f.newClient = () => createPairingClient({
    crypto, fetch: f.fetch, storage: f.storage, now: () => f.time,
    read: async id => structuredClone(f.records.get(id) ?? null),
    write: async record => { f.records.set(record.pairId, structuredClone(record)); },
    remove: async id => { f.records.delete(id); },
    sleep: async milliseconds => { f.time += milliseconds; await f.onSleep?.(); }
  });
  f.posts = () => f.calls.filter(call => call.method === 'POST').length;
  f.pausePending = async () => {
    f.onSleep = () => { throw new DOMException('Test page closed', 'AbortError'); };
    await assert.rejects(f.newClient().bind(f.ownerId), { name: 'AbortError' });
    const saved = f.records.get(f.pairId);
    assert.equal(saved.status, 'awaiting_device_accept');
    assert.equal(saved.key, undefined);
    f.onSleep = null;
    return structuredClone(saved);
  };
  f.verifyEncryption = async active => {
    assert.equal(active.key.extractable, false);
    const nonce = crypto.getRandomValues(new Uint8Array(12));
    const aad = Buffer.from('CLI fixture history\0' + f.deviceId + '\0' + f.clientId);
    const message = Buffer.from('Синтетический ответ выбранной сессии');
    const cipher = await crypto.subtle.encrypt({ name: 'AES-GCM', iv: nonce, additionalData: aad },
      f.desktopKey, message);
    const plain = await crypto.subtle.decrypt({ name: 'AES-GCM', iv: nonce, additionalData: aad }, active.key, cipher);
    assert.deepEqual(Buffer.from(plain), message);
    await assert.rejects(crypto.subtle.decrypt({ name: 'AES-GCM', iv: nonce,
      additionalData: Buffer.from('other client') }, active.key, cipher));
    assert.equal(active.secret, undefined);
    assert.equal(active.privateKey, undefined);
  };
  return f;
}
