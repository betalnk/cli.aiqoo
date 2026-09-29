const fragmentKey = 'cli.pair.fragment';
const pendingIdKey = 'cli.pair.pendingId';
const pairIdPattern = /^[0-9a-f]{32}$/;
const codePattern = /^[2-9A-HJKMNP-TV-Z]{12}$/;
const statusNode = document.getElementById('pair-status');
const confirmationNode = document.getElementById('pair-confirmation');
const waitingNode = document.getElementById('pair-waiting');
const submitButton = document.getElementById('pair-submit');

function setStatus(message, error = false) {
  statusNode.textContent = message;
  statusNode.classList.toggle('pair-panel__status--error', error);
}

function toBase64Url(bytes) {
  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function fromBase64Url(value, expectedLength) {
  if (typeof value !== 'string' || !/^[A-Za-z0-9_-]+$/.test(value) || value.length > 400) {
    throw new Error('Ссылка на подключение повреждена. Откройте новый QR-код на ПК.');
  }
  let binary;
  try {
    binary = atob(value.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(value.length / 4) * 4, '='));
  } catch {
    throw new Error('Ссылка на подключение повреждена. Откройте новый QR-код на ПК.');
  }
  const bytes = Uint8Array.from(binary, character => character.charCodeAt(0));
  if (bytes.length !== expectedLength || toBase64Url(bytes) !== value) {
    throw new Error('Ссылка на подключение повреждена. Откройте новый QR-код на ПК.');
  }
  return bytes;
}

function parseFragment(raw) {
  const params = new URLSearchParams(raw.replace(/^#/, ''));
  if (['v', 'pairId', 'code', 'secret', 'fp'].some(key => params.getAll(key).length !== 1)
      || params.get('v') !== '1' || params.size !== 5) {
    throw new Error('Ссылка на подключение недействительна. Откройте новый QR-код на ПК.');
  }
  const pairId = params.get('pairId');
  const code = params.get('code').replace(/-/g, '').toUpperCase();
  if (!pairIdPattern.test(pairId) || !codePattern.test(code)) {
    throw new Error('Ссылка на подключение недействительна. Откройте новый QR-код на ПК.');
  }
  return { pairId, code, secret: fromBase64Url(params.get('secret'), 32),
    fingerprint: fromBase64Url(params.get('fp'), 32), fingerprintText: params.get('fp') };
}

function rememberFragment() {
  if (!location.hash) return;
  try { sessionStorage.setItem(fragmentKey, location.hash); }
  finally { history.replaceState(history.state, '', location.pathname + location.search); }
}

function openPairDb() {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open('cli.voice', 1);
    request.onupgradeneeded = () => {
      const db = request.result;
      if (!db.objectStoreNames.contains('pairs')) db.createObjectStore('pairs', { keyPath: 'pairId' });
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error || new Error('Не удалось открыть защищённое хранилище браузера.'));
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

async function fingerprintMatches(publicSpki, expected) {
  const spki = fromBase64Url(publicSpki, 91);
  const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', asBuffer(spki)));
  return digest.every((byte, index) => byte === expected[index]);
}

async function createClient(pair, ownerId) {
  const keys = await crypto.subtle.generateKey({ name: 'ECDH', namedCurve: 'P-256' }, false, ['deriveBits']);
  const publicSpki = toBase64Url(new Uint8Array(await crypto.subtle.exportKey('spki', keys.publicKey)));
  const hmacKey = await crypto.subtle.importKey('raw', asBuffer(pair.secret),
    { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  const prefix = new TextEncoder().encode(`CLI Voice pair v1\0${pair.pairId}\0`);
  const spki = fromBase64Url(publicSpki, 91);
  const input = new Uint8Array(prefix.length + spki.length);
  input.set(prefix);
  input.set(spki, prefix.length);
  const proof = toBase64Url(new Uint8Array(await crypto.subtle.sign('HMAC', hmacKey, asBuffer(input))));
  return { pairId: pair.pairId, ownerId, code: pair.code, secret: pair.secret,
    fingerprint: pair.fingerprint, privateKey: keys.privateKey, clientPublicKey: publicSpki,
    proof, status: 'claiming' };
}

async function apiJson(path, options = {}) {
  const response = await fetch(path, { credentials: 'same-origin', cache: 'no-store', ...options });
  let data = null;
  try { data = await response.json(); } catch { /* Error bodies may be empty. */ }
  if (!response.ok) {
    const messages = {
      pairing_unavailable: 'Код истёк или уже использован. Откройте новый QR-код на ПК.',
      too_many_attempts: 'Для этого кода было слишком много попыток. Создайте новый код на ПК.',
      rate_limited: 'Слишком много запросов. Подождите немного и повторите.',
      unauthorized: 'Вход в аккаунт истёк. Войдите снова.',
      invalid_request: 'Данные подключения неверны. Откройте новый QR-код на ПК.'
    };
    const error = new Error(messages[data?.error] || 'Сервер временно недоступен.');
    error.code = data?.error;
    error.httpStatus = response.status;
    throw error;
  }
  return data;
}

async function claim(record) {
  let data;
  try {
    data = await apiJson('/api/v1/pairings/claim', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ pairId: record.pairId, code: record.code,
        clientName: 'Браузер телефона', clientPublicKey: record.clientPublicKey, proof: record.proof })
    });
  } catch (error) {
    // The reply may have been lost after the server accepted the one-time claim.
    const state = await apiJson(`/api/v1/pairings/${record.pairId}`);
    if (state.pairId !== record.pairId || !pairIdPattern.test(state.clientId)
      || !pairIdPattern.test(state.deviceId)) throw error;
    Object.assign(record, { clientId: state.clientId, deviceId: state.deviceId,
      status: state.status });
    await pairRecord('readwrite', record.pairId, record);
    sessionStorage.removeItem(fragmentKey);
    return record;
  }
  if (data.pairId !== record.pairId || !pairIdPattern.test(data.clientId)
    || !pairIdPattern.test(data.deviceId) || data.status !== 'awaiting_device_accept') {
    throw new Error('Сервер вернул другое подключение. Остановлено.');
  }
  let matches = false;
  try { matches = await fingerprintMatches(data.devicePublicKey, record.fingerprint); }
  catch { /* A malformed public key is never trusted. */ }
  if (!matches) throw new Error('Ключ компьютера не совпал с QR-кодом. Подключение остановлено.');
  Object.assign(record, { clientId: data.clientId, deviceId: data.deviceId,
    deviceName: data.deviceName, devicePublicKey: data.devicePublicKey,
    status: 'awaiting_device_accept' });
  await pairRecord('readwrite', record.pairId, record);
  sessionStorage.removeItem(fragmentKey);
  return record;
}

async function activate(record) {
  if (!record.devicePublicKey) {
    const result = await apiJson('/api/v1/devices');
    const device = result.devices?.find(item => item.deviceId === record.deviceId);
    if (!device) throw new Error('Компьютер не найден в этом аккаунте.');
    record.devicePublicKey = device.devicePublicKey;
    record.deviceName = device.deviceName;
  }
  if (!(await fingerprintMatches(record.devicePublicKey, record.fingerprint))) {
    throw new Error('Ключ компьютера не совпал с QR-кодом. Подключение остановлено.');
  }
  const devicePublicKey = await crypto.subtle.importKey('spki',
    asBuffer(fromBase64Url(record.devicePublicKey, 91)), { name: 'ECDH', namedCurve: 'P-256' }, false, []);
  const shared = await crypto.subtle.deriveBits({ name: 'ECDH', public: devicePublicKey }, record.privateKey, 256);
  const material = await crypto.subtle.importKey('raw', shared, 'HKDF', false, ['deriveKey']);
  const key = await crypto.subtle.deriveKey({ name: 'HKDF', hash: 'SHA-256',
    salt: asBuffer(record.secret), info: asBuffer(new TextEncoder().encode(`CLI Voice v1\0${record.pairId}\0${record.clientId}`)) },
    material, { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
  await pairRecord('readwrite', record.pairId, { pairId: record.pairId,
    ownerId: record.ownerId, clientId: record.clientId, deviceId: record.deviceId,
    deviceName: record.deviceName, devicePublicKey: record.devicePublicKey,
    clientPublicKey: record.clientPublicKey, key, status: 'active', pairedAt: Date.now() });
  sessionStorage.removeItem(pendingIdKey);
  setStatus('Компьютер подключён к этому аккаунту.');
  waitingNode.hidden = false;
  waitingNode.querySelector('p').textContent = 'Подключение защищено ключами этого браузера и ПК.';
}

async function waitForAcceptance(record) {
  confirmationNode.hidden = true;
  waitingNode.hidden = false;
  document.querySelector('.pair-panel__step').textContent = '02 / 02';
  setStatus('Запрос передан на компьютер.');
  const deadline = Date.now() + 5 * 60_000;
  while (Date.now() < deadline) {
    const state = await apiJson(`/api/v1/pairings/${record.pairId}`);
    if (state.pairId !== record.pairId || state.clientId !== record.clientId
      || state.deviceId !== record.deviceId) throw new Error('Данные подключения изменились. Покажите новый QR-код на ПК.');
    if (state.status === 'active') return activate(record);
    if (state.status === 'expired' || state.status === 'revoked') break;
    if (state.status !== 'awaiting_device_accept') throw new Error('Подключение изменилось. Покажите новый QR-код на ПК.');
    await new Promise(resolve => setTimeout(resolve, 1800));
  }
  throw new Error('Время подключения истекло. Покажите новый QR-код на ПК.');
}

async function main() {
  rememberFragment();
  let pair = null;
  const raw = sessionStorage.getItem(fragmentKey);
  if (raw) pair = parseFragment(raw);
  const pendingId = sessionStorage.getItem(pendingIdKey);
  if (!pair && (!pendingId || !pairIdPattern.test(pendingId))) {
    throw new Error('Ссылка на подключение не найдена. Откройте QR-код в приложении на ПК.');
  }
  if (!crypto?.subtle || !indexedDB) throw new Error('Для подключения нужен современный браузер с защищённым хранилищем.');
  const response = await fetch('/api/v1/me', { credentials: 'same-origin', cache: 'no-store' });
  if (response.status === 401) {
    location.replace('/api/v1/auth/github/start?next=%2Fpair.html');
    return;
  }
  if (!response.ok) throw new Error('Не удалось проверить вход в аккаунт.');
  const account = await response.json();
  if (!account.user?.id) throw new Error('Не удалось проверить вход в аккаунт.');
  const record = await pairRecord('readonly', pair?.pairId || pendingId);
  if (record && record.ownerId !== account.user.id) {
    throw new Error('Подключение было начато в другом аккаунте. Откройте новый QR-код на ПК.');
  }
  if (record?.status === 'active') {
    setStatus('Компьютер уже подключён к этому аккаунту.');
    waitingNode.hidden = false;
    return;
  }
  if (record?.clientId) return waitForAcceptance(record);
  if (record?.status === 'claiming') {
    setStatus('Восстанавливаем начатое подключение…');
    return waitForAcceptance(await claim(record));
  }
  if (!pair) throw new Error('Ссылка на подключение не найдена. Откройте новый QR-код на ПК.');
  document.getElementById('pair-fingerprint').textContent = pair.fingerprintText;
  confirmationNode.hidden = false;
  setStatus('Проверьте QR-код на своём компьютере.');
  submitButton.addEventListener('click', async () => {
    submitButton.disabled = true;
    setStatus('Создаём защищённое подключение…');
    try {
      const next = record || await createClient(pair, account.user.id);
      await pairRecord('readwrite', next.pairId, next);
      sessionStorage.setItem(pendingIdKey, next.pairId);
      await waitForAcceptance(await claim(next));
    } catch (error) {
      setStatus(error.message || 'Не удалось подключить компьютер.', true);
    }
  }, { once: true });
}

main().catch(error => setStatus(error.message || 'Не удалось подключить компьютер.', true));
