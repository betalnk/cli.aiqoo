import assert from 'node:assert/strict';
import { captureFragment, fragmentKey, pendingIdKey } from '../pairing.js';
import { crypto, fixture, memoryStorage } from './pairing-fixture.mjs';

let cases = 0;
async function scenario(name, run) {
  try { await run(); cases++; }
  catch (error) { error.message = name + ': ' + error.message; throw error; }
}

await scenario('capture before auth and remove even malformed fragment', async () => {
  const f = await fixture();
  const location = { hash: f.fragment, pathname: '/account.html', search: '' };
  let cleared = null;
  captureFragment(location, f.storage, { state: null, replaceState(_state, _title, path) { cleared = path; } });
  assert.equal(cleared, '/account.html');
  assert.equal(f.storage.getItem(fragmentKey), f.fragment);
  const storage = memoryStorage();
  assert.throws(() => captureFragment({ ...location, hash: f.fragment + '&secret=duplicate' }, storage,
    { replaceState(_state, _title, path) { cleared = path; } }), { code: 'invalid_fragment' });
  assert.equal(storage.getItem(fragmentKey), null);
  assert.equal(cleared, '/account.html');
});

await scenario('explicit bind, single double-click claim, pending before OK and interoperable crypto', async () => {
  const f = await fixture(), client = f.newClient();
  assert.equal((await client.inspect(f.ownerId)).kind, 'ready');
  assert.equal(f.posts(), 0);
  f.onSleep = async () => {
    assert.equal(f.state.status, 'awaiting_device_accept');
    const pending = f.records.get(f.pairId);
    assert.equal(pending.status, 'awaiting_device_accept');
    assert.equal(pending.key, undefined);
    assert.equal(pending.privateKey.extractable, false);
    await f.approve();
  };
  const first = client.bind(f.ownerId), second = client.bind(f.ownerId);
  assert.equal(first, second);
  const active = await first;
  assert.equal(active.status, 'active');
  assert.equal(f.posts(), 1);
  assert.equal(f.storage.getItem(fragmentKey), null);
  assert.equal(f.storage.getItem(pendingIdKey), null);
  await f.verifyEncryption(active);
});

await scenario('native cancel creates no usable binding', async () => {
  const f = await fixture();
  f.onSleep = () => { f.state.status = 'revoked'; };
  await assert.rejects(f.newClient().bind(f.ownerId), { code: 'revoked' });
  assert.equal(f.records.size, 0);
  assert.equal(f.storage.getItem(pendingIdKey), null);
});

await scenario('awaiting expiry stays rejected', async () => {
  const f = await fixture();
  f.onSleep = () => { f.time += 301_000; };
  await assert.rejects(f.newClient().bind(f.ownerId), { code: 'expired' });
  assert.equal(f.records.size, 0);
});

await scenario('lost committed claim reply recovers the exact original key', async () => {
  const f = await fixture();
  f.dropReply = true;
  f.onSleep = f.approve;
  const active = await f.newClient().bind(f.ownerId);
  assert.equal(f.posts(), 1);
  assert.equal(active.clientPublicKey, f.state.clientPublicKey);
  await f.verifyEncryption(active);
});

await scenario('reload only GETs pending status, preserves key/proof, no automatic POST', async () => {
  const f = await fixture(), saved = await f.pausePending();
  const client = f.newClient(), context = await client.inspect(f.ownerId);
  assert.equal(context.kind, 'waiting');
  assert.equal(f.posts(), 1);
  assert.equal(context.record.clientPublicKey, saved.clientPublicKey);
  assert.equal(context.record.proof, saved.proof);
  f.onSleep = f.approve;
  await f.verifyEncryption(await client.resume(context));
  assert.equal(f.posts(), 1);
});

await scenario('OK accepted in time can recover active after QR expiry', async () => {
  const f = await fixture();
  await f.pausePending();
  await f.approve();
  f.time += 301_000;
  const client = f.newClient(), context = await client.inspect(f.ownerId);
  assert.equal(context.state.status, 'active');
  const active = await client.resume(context);
  assert.equal(f.posts(), 1);
  await f.verifyEncryption(active);
});

await scenario('expired awaiting on reload cannot become active', async () => {
  const f = await fixture();
  await f.pausePending();
  f.time += 301_000;
  const client = f.newClient(), context = await client.inspect(f.ownerId);
  await assert.rejects(client.resume(context), { code: 'expired' });
  assert.equal(f.posts(), 1);
  assert.equal(f.records.size, 0);
});

for (const field of ['accountId', 'clientPublicKey', 'proof']) {
  await scenario('mismatched recovered ' + field + ' fails without POST', async () => {
    const f = await fixture();
    await f.pausePending();
    f.state[field] = field === 'accountId' ? 'another-account'
      : field === 'clientPublicKey' ? f.devicePublicKey : Buffer.alloc(32, 8).toString('base64url');
    await assert.rejects(f.newClient().inspect(f.ownerId), { code: 'pairing_mismatch' });
    assert.equal(f.posts(), 1);
    assert.equal(f.records.size, 0);
  });
}

await scenario('wrong pinned desktop key fails closed', async () => {
  const f = await fixture();
  f.afterCommit = state => { state.devicePublicKey = state.clientPublicKey; };
  await assert.rejects(f.newClient().bind(f.ownerId), { code: 'fingerprint_mismatch' });
  assert.equal(f.records.size, 0);
});

await scenario('local private key must match its saved public key', async () => {
  const f = await fixture();
  await f.pausePending();
  const other = await crypto.subtle.generateKey({ name: 'ECDH', namedCurve: 'P-256' }, false, ['deriveBits']);
  f.records.get(f.pairId).privateKey = other.privateKey;
  await assert.rejects(f.newClient().inspect(f.ownerId), { code: 'browser_key_mismatch' });
  assert.equal(f.posts(), 1);
  assert.equal(f.records.size, 0);
});

await scenario('same-account competing browser cannot hijack lost-response recovery', async () => {
  const f = await fixture();
  f.dropReply = true;
  f.afterCommit = state => { state.clientPublicKey = f.devicePublicKey; };
  await assert.rejects(f.newClient().bind(f.ownerId), { code: 'pairing_mismatch' });
  assert.equal(f.posts(), 1);
  assert.equal(f.records.size, 0);
});

await scenario('uncommitted network failure retries only on explicit click with same saved key', async () => {
  const f = await fixture();
  f.failBeforeSend = true;
  await assert.rejects(f.newClient().bind(f.ownerId), { name: 'TypeError' });
  const saved = structuredClone(f.records.get(f.pairId));
  assert.equal((await f.newClient().inspect(f.ownerId)).kind, 'ready');
  assert.equal(f.posts(), 1);
  f.failBeforeSend = false;
  f.onSleep = f.approve;
  const active = await f.newClient().bind(f.ownerId);
  assert.equal(f.posts(), 2);
  assert.equal(active.clientPublicKey, saved.clientPublicKey);
  assert.equal(f.state.proof, saved.proof);
  await f.verifyEncryption(active);
});

await scenario('unexpected active claim response is not accepted as PC approval', async () => {
  const f = await fixture();
  f.afterCommit = state => { state.status = 'active'; };
  await assert.rejects(f.newClient().bind(f.ownerId), { code: 'pairing_mismatch' });
  assert.equal(f.records.size, 0);
});

console.log('PAIRING_SMOKE_OK (' + cases + ' scenarios)');
