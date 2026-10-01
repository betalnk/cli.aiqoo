// Shared account/QR pairing. The relay never receives the fragment secret.
export const fragmentKey = 'cli.pair.fragment';
export const pendingIdKey = 'cli.pair.pendingId';
const idPattern = /^[0-9a-f]{32}$/;
const codePattern = /^[2-9A-HJKMNP-TV-Z]{12}$/;

function pairingError(message, code, terminal = true) {
  return Object.assign(new Error(message), { code, terminal });
}

export function toBase64Url(bytes) {
  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function fromBase64Url(value, expectedLength) {
  if (typeof value !== 'string' || !/^[A-Za-z0-9_-]+$/.test(value) || value.length > 400) {
    throw pairingError('Ссылка повреждена. Откройте ЛК из приложения на ПК заново.', 'invalid_fragment');
  }
  let binary;
  try {
    binary = atob(value.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(value.length / 4) * 4, '='));
  } catch {
    throw pairingError('Ссылка повреждена. Откройте ЛК из приложения на ПК заново.', 'invalid_fragment');
  }
  const bytes = Uint8Array.from(binary, character => character.charCodeAt(0));
  if (bytes.length !== expectedLength || toBase64Url(bytes) !== value) {
    throw pairingError('Ссылка повреждена. Откройте ЛК из приложения на ПК заново.', 'invalid_fragment');
  }
  return bytes;
}

export function parseFragment(raw) {
  const params = new URLSearchParams(raw.replace(/^#/, ''));
  if (['v', 'pairId', 'code', 'secret', 'fp'].some(key => params.getAll(key).length !== 1)
      || params.get('v') !== '1' || params.size !== 5) {
    throw pairingError('Ссылка недействительна. Откройте ЛК из приложения на ПК заново.', 'invalid_fragment');
  }
  const pairId = params.get('pairId');
  const code = params.get('code').replace(/-/g, '').toUpperCase();
  if (!idPattern.test(pairId) || !codePattern.test(code)) {
    throw pairingError('Ссылка недействительна. Откройте ЛК из приложения на ПК заново.', 'invalid_fragment');
  }
  return { pairId, code, secret: fromBase64Url(params.get('secret'), 32),
    fingerprint: fromBase64Url(params.get('fp'), 32), fingerprintText: params.get('fp') };
}

export function captureFragment(location = globalThis.location, storage = globalThis.sessionStorage,
  history = globalThis.history) {
  if (!location.hash) return;
  const raw = location.hash;
  try {
    storage.removeItem(fragmentKey);
    storage.removeItem(pendingIdKey);
    const pair = parseFragment(raw);
    pair.secret.fill(0);
    storage.setItem(fragmentKey, raw);
  } finally {
    history.replaceState(history.state, '', location.pathname + location.search);
  }
}

function openPairDb() {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open('cli.voice', 1);
    request.onupgradeneeded = () => {
      const db = request.result;
      if (!db.objectStoreNames.contains('pairs')) db.createObjectStore('pairs', { keyPath: 'pairId' });
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error || new Error('Не удалось открыть хранилище браузера.'));
  });
}

async function pairRecord(mode, pairId, record) {
  const db = await openPairDb();
  try {
    return await new Promise((resolve, reject) => {
      const transaction = db.transaction('pairs', mode);
      const store = transaction.objectStore('pairs');
      let result = null;
      const request = record === undefined ? store.get(pairId)
        : record === null ? store.delete(pairId) : store.put(record);
      request.onsuccess = () => { result = request.result ?? null; };
      request.onerror = () => reject(request.error || new Error('Не удалось сохранить подключение.'));
      transaction.onabort = () => reject(transaction.error || new Error('Не удалось сохранить подключение.'));
      transaction.oncomplete = () => resolve(result);
    });
  } finally { db.close(); }
}

function asBuffer(bytes) {
  const copy = new Uint8Array(bytes.length);
  copy.set(bytes);
  return copy.buffer;
}

function delay(milliseconds, signal) {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) return reject(new DOMException('Aborted', 'AbortError'));
    const abort = () => { clearTimeout(timer); reject(new DOMException('Aborted', 'AbortError')); };
    const timer = setTimeout(() => { signal?.removeEventListener('abort', abort); resolve(); }, milliseconds);
    signal?.addEventListener('abort', abort, { once: true });
  });
}

export function createPairingClient(options = {}) {
  const crypto = options.crypto ?? globalThis.crypto;
  const fetch = options.fetch ?? globalThis.fetch.bind(globalThis);
  const storage = options.storage ?? globalThis.sessionStorage;
  const read = options.read ?? (pairId => pairRecord('readonly', pairId));
  const write = options.write ?? (record => pairRecord('readwrite', record.pairId, record));
  const remove = options.remove ?? (pairId => pairRecord('readwrite', pairId, null));
  const now = options.now ?? Date.now;
  const sleep = options.sleep ?? delay;
  const checkedPrivateKeys = new WeakMap();
  let binding = null;

  async function apiJson(path, request = {}) {
    const response = await fetch(path, { credentials: 'same-origin', cache: 'no-store', ...request });
    let data = null;
    try { data = await response.json(); } catch { /* Error bodies can be empty. */ }
    if (!response.ok) {
      const messages = {
        pairing_unavailable: 'Запрос истёк или уже использован. Откройте ЛК из приложения на ПК заново.',
        too_many_attempts: 'Для этого кода было слишком много попыток. Создайте новый код на ПК.',
        rate_limited: 'Слишком много запросов. Подождите немного и повторите.',
        unauthorized: 'Вход в аккаунт истёк. Обновите страницу и войдите снова.',
        invalid_request: 'Данные подключения неверны. Откройте ЛК из приложения на ПК заново.'
      };
      throw Object.assign(pairingError(messages[data?.error] || 'Сервер недоступен. Повторите попытку.',
        data?.error, response.status < 500 && response.status !== 429), { httpStatus: response.status });
    }
    return data;
  }

  function clearContext(pairId) {
    if (storage.getItem(pendingIdKey) === pairId) storage.removeItem(pendingIdKey);
    const raw = storage.getItem(fragmentKey);
    if (raw && new URLSearchParams(raw.replace(/^#/, '')).get('pairId') === pairId) storage.removeItem(fragmentKey);
  }

  async function discard(record) {
    if (record.status !== 'active') await remove(record.pairId);
    record.secret?.fill(0);
    clearContext(record.pairId);
  }

  async function fingerprintMatches(publicSpki, expected) {
    const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', asBuffer(fromBase64Url(publicSpki, 91))));
    return expected?.length === digest.length && digest.every((byte, index) => byte === expected[index]);
  }

  async function privateKeyMatches(record) {
    if (!record.privateKey || record.privateKey.extractable
      || record.privateKey.algorithm?.name !== 'ECDH'
      || record.privateKey.algorithm?.namedCurve !== 'P-256') return false;
    if (checkedPrivateKeys.get(record.privateKey) === record.clientPublicKey) return true;
    const publicKey = await crypto.subtle.importKey('spki', asBuffer(fromBase64Url(record.clientPublicKey, 91)),
      { name: 'ECDH', namedCurve: 'P-256' }, false, []);
    const probe = await crypto.subtle.generateKey({ name: 'ECDH', namedCurve: 'P-256' }, false, ['deriveBits']);
    const [left, right] = await Promise.all([
      crypto.subtle.deriveBits({ name: 'ECDH', public: probe.publicKey }, record.privateKey, 256),
      crypto.subtle.deriveBits({ name: 'ECDH', public: publicKey }, probe.privateKey, 256)
    ]);
    const a = new Uint8Array(left), b = new Uint8Array(right);
    try {
      let difference = a.length ^ b.length;
      for (let index = 0; index < a.length; index++) difference |= a[index] ^ b[index];
      if (difference) return false;
      checkedPrivateKeys.set(record.privateKey, record.clientPublicKey);
      return true;
    } finally { a.fill(0); b.fill(0); }
  }

  async function identity(record, state) {
    if (state?.pairId !== record.pairId || !idPattern.test(state.clientId) || !idPattern.test(state.deviceId)
      || state.accountId !== record.ownerId || state.clientPublicKey !== record.clientPublicKey
      || state.proof !== record.proof || (record.clientId && state.clientId !== record.clientId)
      || (record.deviceId && state.deviceId !== record.deviceId)
      || (record.devicePublicKey && state.devicePublicKey !== record.devicePublicKey)
      || !Number.isSafeInteger(state.expiresAt)) {
      throw pairingError('Запрос принадлежит другому подключению. Откройте ЛК из приложения на ПК заново.',
        'pairing_mismatch');
    }
    let keyMatches = false;
    try { keyMatches = await privateKeyMatches(record); } catch { /* Corrupt local key is never reused. */ }
    if (!keyMatches) {
      throw pairingError('Ключ браузера изменился. Откройте ЛК из приложения на ПК заново.', 'browser_key_mismatch');
    }
    let matches = false;
    try { matches = await fingerprintMatches(state.devicePublicKey, record.fingerprint); } catch { /* Reject malformed keys. */ }
    if (!matches) {
      throw pairingError('Ключ компьютера не совпал. Откройте ЛК из приложения на ПК заново.', 'fingerprint_mismatch');
    }
    if (state.status === 'revoked') throw pairingError('Запрос отклонён на ПК. Откройте ЛК из приложения заново.', 'revoked');
    if (state.status === 'expired') throw pairingError('Время подтверждения истекло. Откройте ЛК из приложения заново.', 'expired');
    if (!['awaiting_device_accept', 'active'].includes(state.status)) {
      throw pairingError('Состояние запроса изменилось. Откройте ЛК из приложения на ПК заново.', 'pairing_mismatch');
    }
    return { ...record, clientId: state.clientId, deviceId: state.deviceId, deviceName: state.deviceName,
      devicePublicKey: state.devicePublicKey, expiresAt: state.expiresAt, status: state.status };
  }

  async function recover(record, signal) {
    const state = await apiJson('/api/v1/pairings/' + record.pairId, { signal });
    const matched = await identity(record, state);
    // Keep the private key until activation, even when the relay already says active.
    const saved = { ...matched, status: 'awaiting_device_accept' };
    await write(saved);
    storage.setItem(pendingIdKey, record.pairId);
    storage.removeItem(fragmentKey);
    return { record: saved, state };
  }

  async function inspect(ownerId, signal) {
    const raw = storage.getItem(fragmentKey);
    const pair = raw ? parseFragment(raw) : null;
    const pendingId = storage.getItem(pendingIdKey);
    if (!pair && (!pendingId || !idPattern.test(pendingId))) return { kind: 'none' };
    if (!crypto?.subtle || !globalThis.indexedDB && !options.read) {
      throw pairingError('Для подключения нужен современный браузер с защищённым хранилищем.', 'unsupported_browser');
    }
    const record = await read(pair?.pairId || pendingId);
    if (record && record.ownerId !== ownerId) {
      throw pairingError('Запрос начат в другом аккаунте. Откройте ЛК из приложения на ПК заново.', 'owner_mismatch');
    }
    if (record?.status === 'active') {
      clearContext(record.pairId);
      return { kind: 'active', record };
    }
    if (record && record.status !== 'claiming' && record.status !== 'awaiting_device_accept') {
      throw pairingError('Запрос изменился. Откройте ЛК из приложения на ПК заново.', 'pairing_mismatch');
    }
    if (record) {
      try {
        const recovered = await recover(record, signal);
        return { kind: 'waiting', ...recovered };
      } catch (error) {
        // A saved request may not have reached the relay. Only another explicit click can submit it.
        if (record.status !== 'claiming' || error.httpStatus !== 404) {
          if (error.terminal) await discard(record);
          throw error;
        }
      }
    }
    if (!pair && !record) throw pairingError('Запрос не найден. Откройте ЛК из приложения на ПК заново.', 'missing_pair');
    return { kind: 'ready', pair, record,
      fingerprintText: pair?.fingerprintText || toBase64Url(record.fingerprint) };
  }

  async function createClient(pair, ownerId) {
    const keys = await crypto.subtle.generateKey({ name: 'ECDH', namedCurve: 'P-256' }, false, ['deriveBits']);
    const publicSpki = toBase64Url(new Uint8Array(await crypto.subtle.exportKey('spki', keys.publicKey)));
    const hmacKey = await crypto.subtle.importKey('raw', asBuffer(pair.secret),
      { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
    const prefix = new TextEncoder().encode('CLI Voice pair v1\0' + pair.pairId + '\0');
    const spki = fromBase64Url(publicSpki, 91);
    const input = new Uint8Array(prefix.length + spki.length);
    input.set(prefix); input.set(spki, prefix.length);
    const proof = toBase64Url(new Uint8Array(await crypto.subtle.sign('HMAC', hmacKey, asBuffer(input))));
    return { pairId: pair.pairId, ownerId, code: pair.code, secret: pair.secret,
      fingerprint: pair.fingerprint, privateKey: keys.privateKey, clientPublicKey: publicSpki,
      proof, status: 'claiming' };
  }

  async function claim(record, clientName, signal) {
    let state;
    try {
      state = await apiJson('/api/v1/pairings/claim', {
        method: 'POST', signal, headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ pairId: record.pairId, code: record.code,
          clientName, clientPublicKey: record.clientPublicKey, proof: record.proof })
      });
    } catch (error) {
      if (error.name === 'AbortError') throw error;
      try { return await recover(record, signal); }
      catch (recoveryError) {
        if (recoveryError.httpStatus === 404) throw error;
        if (recoveryError.terminal) await discard(record);
        throw recoveryError;
      }
    }
    const matched = await identity(record, state);
    if (state.status !== 'awaiting_device_accept') {
      throw pairingError('Сервер вернул неожиданное подтверждение. Откройте ЛК из приложения заново.', 'pairing_mismatch');
    }
    await write(matched);
    storage.setItem(pendingIdKey, record.pairId);
    storage.removeItem(fragmentKey);
    return { record: matched, state };
  }

  async function activate(record, state) {
    const matched = await identity(record, state);
    if (state.status !== 'active') throw pairingError('Ожидаем подтверждения на ПК.', 'not_active');
    const publicKey = await crypto.subtle.importKey('spki', asBuffer(fromBase64Url(matched.devicePublicKey, 91)),
      { name: 'ECDH', namedCurve: 'P-256' }, false, []);
    const shared = await crypto.subtle.deriveBits({ name: 'ECDH', public: publicKey }, record.privateKey, 256);
    try {
      const material = await crypto.subtle.importKey('raw', shared, 'HKDF', false, ['deriveKey']);
      const key = await crypto.subtle.deriveKey({ name: 'HKDF', hash: 'SHA-256',
        salt: asBuffer(record.secret),
        info: asBuffer(new TextEncoder().encode('CLI Voice v1\0' + record.pairId + '\0' + matched.clientId)) },
      material, { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
      const active = { pairId: matched.pairId, ownerId: matched.ownerId, clientId: matched.clientId,
        deviceId: matched.deviceId, deviceName: matched.deviceName, devicePublicKey: matched.devicePublicKey,
        clientPublicKey: record.clientPublicKey, key, status: 'active', pairedAt: now() };
      await write(active);
      record.secret.fill(0);
      clearContext(record.pairId);
      return active;
    } finally { new Uint8Array(shared).fill(0); }
  }

  async function wait(record, firstState, callbacks) {
    callbacks.onWaiting?.(record);
    const deadline = Math.min(record.expiresAt * 1000, now() + 5 * 60_000);
    let state = firstState;
    try {
      // An OK accepted before expiry stays authorised if the browser resumes later.
      if (state) {
        await identity(record, state);
        if (state.status === 'active') return await activate(record, state);
      }
      while (now() < deadline) {
        if (callbacks.signal?.aborted) throw new DOMException('Aborted', 'AbortError');
        state ??= await apiJson('/api/v1/pairings/' + record.pairId, { signal: callbacks.signal });
        await identity(record, state);
        if (state.status === 'active') return await activate(record, state);
        await sleep(1800, callbacks.signal);
        state = null;
      }
      throw pairingError('Время подтверждения истекло. Откройте ЛК из приложения на ПК заново.', 'expired');
    } catch (error) {
      if (error.terminal) await discard(record);
      throw error;
    }
  }

  function bind(ownerId, callbacks = {}) {
    if (binding) return binding;
    binding = (async () => {
      const context = await inspect(ownerId, callbacks.signal);
      if (context.kind === 'none') throw pairingError('Откройте ЛК из приложения на ПК для привязки.', 'missing_pair');
      if (context.kind === 'active') return context.record;
      if (context.kind === 'waiting') return wait(context.record, context.state, callbacks);
      const record = context.record || await createClient(context.pair, ownerId);
      await write(record);
      storage.setItem(pendingIdKey, record.pairId);
      try {
        const claimed = await claim(record, callbacks.clientName || 'Браузер', callbacks.signal);
        return await wait(claimed.record, claimed.state, callbacks);
      } catch (error) {
        if (error.terminal) await discard(record);
        throw error;
      }
    })().finally(() => { binding = null; });
    return binding;
  }

  return { inspect, bind, resume: (context, callbacks = {}) => wait(context.record, context.state, callbacks) };
}
