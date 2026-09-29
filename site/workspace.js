import { setComposeRecipient } from './compose.js';

const statusNode = document.getElementById('workspace-status');
const liveNode = document.getElementById('workspace-live');
const contentNode = document.getElementById('workspace-content');
const sessionList = document.getElementById('session-list');
const conversation = document.getElementById('conversation');
const conversationTitle = document.getElementById('conversation-title');
const conversationDevice = document.getElementById('conversation-device');
const conversationStatus = document.getElementById('conversation-status');
const messageList = document.getElementById('message-list');
const idPattern = /^[0-9a-f]{32}$/;
const threadPattern = /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/;
const encoder = new TextEncoder();
const decoder = new TextDecoder('utf-8', { fatal: true });
const dateFormat = new Intl.DateTimeFormat('ru-RU', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });

let pairs = [];
let watches = [];
let selected = null;
let historyWatch = null;
let renderedSessions = '';
let renderedMessages = '';
let initialized = false;
const revokedDevices = new Set();
let activeSpeech = null;
let voiceSignature = '';
let initialSelectionPending = true;

function setStatus(node, message, error = false) {
  node.textContent = message;
  node.classList.toggle('account-inline-status--error', error);
  node.hidden = false;
}

function randomId() {
  return Array.from(crypto.getRandomValues(new Uint8Array(16)), byte => byte.toString(16).padStart(2, '0')).join('');
}

function decodeBase64Url(value, maximumBytes, exactBytes = null) {
  if (typeof value !== 'string' || !/^[A-Za-z0-9_-]+$/.test(value)
      || value.length > Math.ceil(maximumBytes * 4 / 3) + 4) throw new Error('invalid-base64url');
  const binary = atob(value.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(value.length / 4) * 4, '='));
  const bytes = Uint8Array.from(binary, character => character.charCodeAt(0));
  if (bytes.length > maximumBytes || (exactBytes !== null && bytes.length !== exactBytes)
      || btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '') !== value) {
    throw new Error('invalid-base64url');
  }
  return bytes;
}

function openPairDb() {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open('cli.voice', 1);
    request.onupgradeneeded = () => {
      const db = request.result;
      if (!db.objectStoreNames.contains('pairs')) db.createObjectStore('pairs', { keyPath: 'pairId' });
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error || new Error('pair-db-unavailable'));
  });
}

async function activePairs(ownerId, devices) {
  const db = await openPairDb();
  try {
    const records = await new Promise((resolve, reject) => {
      const request = db.transaction('pairs', 'readonly').objectStore('pairs').getAll();
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error || new Error('pair-db-unavailable'));
    });
    const deviceNames = new Map(devices.map(item => [item.deviceId, item.deviceName]));
    return records.filter(item => item.ownerId === ownerId && item.status === 'active'
      && idPattern.test(item.deviceId) && idPattern.test(item.clientId)
      && deviceNames.has(item.deviceId) && !revokedDevices.has(item.deviceId)
      && item.key && typeof item.key === 'object')
      .sort((a, b) => (b.pairedAt || 0) - (a.pairedAt || 0))
      .filter((item, index, all) => all.findIndex(other => other.deviceId === item.deviceId) === index)
      .slice(0, 10)
      .map(item => ({ ...item, deviceName: deviceNames.get(item.deviceId) || 'Компьютер' }));
  } finally { db.close(); }
}

function formatTime(value) {
  if (value === null || value === undefined) return '';
  const date = typeof value === 'number' ? new Date(value > 1e12 ? value : value * 1000) : new Date(value);
  return Number.isFinite(date.getTime()) ? dateFormat.format(date) : '';
}

function localVoiceFor(text) {
  if (!window.speechSynthesis || !window.SpeechSynthesisUtterance) return null;
  let voices;
  try { voices = window.speechSynthesis.getVoices().filter(voice => voice.localService === true); }
  catch { return null; }
  const language = /[А-Яа-яЁё]/.test(text) ? 'ru' : 'en';
  return voices.find(voice => voice.lang?.toLowerCase().startsWith(language)) || null;
}

function stopSpeech() {
  if (!activeSpeech) return;
  const { utterance, button } = activeSpeech;
  activeSpeech = null;
  utterance.onend = null;
  utterance.onerror = null;
  button.textContent = 'Слушать снова';
  button.setAttribute('aria-pressed', 'false');
  button.setAttribute('aria-label', 'Слушать ответ Codex снова');
  window.speechSynthesis.cancel();
}

function listenButton(text) {
  if (!localVoiceFor(text)) return null;
  const button = document.createElement('button');
  button.className = 'message-list__listen';
  button.type = 'button';
  button.textContent = 'Слушать';
  button.setAttribute('aria-label', 'Слушать ответ Codex');
  button.setAttribute('aria-pressed', 'false');
  button.addEventListener('click', () => {
    if (activeSpeech?.button === button) return stopSpeech();
    stopSpeech();
    const voice = localVoiceFor(text);
    if (!voice) { button.hidden = true; return; }
    const utterance = new window.SpeechSynthesisUtterance(text);
    utterance.voice = voice;
    utterance.lang = voice.lang;
    utterance.onend = () => {
      if (activeSpeech?.utterance !== utterance) return;
      activeSpeech = null;
      button.textContent = 'Слушать снова';
      button.setAttribute('aria-pressed', 'false');
      button.setAttribute('aria-label', 'Слушать ответ Codex снова');
    };
    utterance.onerror = () => {
      if (activeSpeech?.utterance !== utterance) return;
      activeSpeech = null;
      button.textContent = 'Повторить';
      button.setAttribute('aria-pressed', 'false');
      button.setAttribute('aria-label', 'Повторить прослушивание ответа Codex');
    };
    activeSpeech = { utterance, button, voice };
    button.textContent = 'Остановить';
    button.setAttribute('aria-pressed', 'true');
    button.setAttribute('aria-label', 'Остановить прослушивание ответа Codex');
    try { window.speechSynthesis.speak(utterance); }
    catch { utterance.onerror(); }
  });
  return button;
}

function validTimestamp(value) {
  return value === null || (Number.isSafeInteger(value) && value >= 0);
}

function makeSocketUrl() {
  return `${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/api/v1/ws/history`;
}

function schedule(watch, delay, callback) {
  clearTimeout(watch.timer);
  if (watch.stopped || document.visibilityState === 'hidden') return;
  watch.timer = setTimeout(() => {
    watch.timer = null;
    if (!watch.stopped && document.visibilityState !== 'hidden') callback();
  }, delay);
}

function startRequest(watch) {
  if (watch.stopped || watch.socket?.readyState !== WebSocket.OPEN) return;
  watch.requestId = randomId();
  watch.seq = 0;
  watch.items = [];
  watch.state = 'waiting';
  watch.onChange();
  watch.socket.send(JSON.stringify({ type: 'watch_history', requestId: watch.requestId,
    deviceId: watch.pair.deviceId, clientId: watch.pair.clientId, kind: watch.kind,
    threadId: watch.threadId }));
}

function terminalState(watch, state) {
  watch.state = state;
  watch.items = [];
  watch.onChange();
  schedule(watch, state === 'unavailable' ? 30000 : 5000, () => {
    if (watch.socket?.readyState === WebSocket.OPEN) startRequest(watch);
    else connectWatch(watch);
  });
}

function failWatch(watch) {
  watch.fatal = true;
  watch.state = 'invalid';
  watch.items = [];
  watch.onChange();
  watch.socket?.close();
}

function validateSnapshot(watch, payload) {
  if (!payload || payload.kind !== watch.kind || !validTimestamp(payload.capturedAt)) {
    throw new Error('invalid-snapshot');
  }
  if (watch.kind === 'threads') {
    if (!Array.isArray(payload.threads) || payload.threads.length > 100) throw new Error('invalid-threads');
    return payload.threads.map(item => {
      if (!threadPattern.test(item?.threadId) || typeof item.title !== 'string'
          || !validTimestamp(item.updatedAt)) throw new Error('invalid-thread');
      return { threadId: item.threadId, title: item.title.trim().slice(0, 200),
        updatedAt: item.updatedAt, pair: watch.pair };
    });
  }
  if (payload.threadId !== watch.threadId || !Array.isArray(payload.messages)
      || payload.messages.length > 3) throw new Error('invalid-messages');
  return payload.messages.map(item => {
    if (!['user', 'assistant'].includes(item?.role) || typeof item.text !== 'string'
        || !validTimestamp(item.createdAt)
        || (item.truncated !== undefined && typeof item.truncated !== 'boolean')) throw new Error('invalid-message');
    return { role: item.role, text: item.text, createdAt: item.createdAt,
      truncated: item.truncated === true };
  });
}

async function handleFrame(watch, socket, raw) {
  if (watch.stopped || watch.socket !== socket) return;
  if (typeof raw !== 'string' || raw.length > 131072) return failWatch(watch);
  let frame;
  try { frame = JSON.parse(raw); } catch { return failWatch(watch); }
  if (!frame || frame.requestId !== watch.requestId) return;
  if (frame.type === 'watch_started') return;
  if (frame.type === 'device_offline') return terminalState(watch, 'offline');
  if (frame.type === 'history_timeout') return terminalState(watch, 'timeout');
  if (frame.type === 'history_unavailable') return terminalState(watch, 'unavailable');
  if (frame.type !== 'history_packet') return failWatch(watch);
  try {
    if (frame.deviceId !== watch.pair.deviceId || frame.clientId !== watch.pair.clientId
        || frame.kind !== watch.kind || frame.threadId !== watch.threadId
        || !Number.isSafeInteger(frame.seq) || frame.seq !== watch.seq + 1) throw new Error('invalid-envelope');
    const nonce = decodeBase64Url(frame.nonce, 12, 12);
    const ciphertext = decodeBase64Url(frame.ciphertext, 96000);
    if (ciphertext.length < 16) throw new Error('invalid-ciphertext');
    const aad = encoder.encode(`CLI Voice history v1\0${frame.deviceId}\0${frame.clientId}\0${frame.requestId}\0${frame.kind}\0${frame.threadId || ''}\0${frame.seq}`);
    const plaintext = await crypto.subtle.decrypt({ name: 'AES-GCM', iv: nonce,
      additionalData: aad, tagLength: 128 }, watch.pair.key, ciphertext);
    const payload = JSON.parse(decoder.decode(plaintext));
    const items = validateSnapshot(watch, payload);
    if (watch.stopped || watch.socket !== socket || frame.requestId !== watch.requestId) return;
    watch.seq = frame.seq;
    watch.items = items;
    watch.state = 'online';
    watch.onChange();
  } catch { failWatch(watch); }
}

function connectWatch(watch) {
  if (watch.stopped || document.visibilityState === 'hidden') return;
  watch.state = 'connecting';
  watch.items = [];
  watch.onChange();
  const socket = new WebSocket(makeSocketUrl());
  watch.socket = socket;
  socket.onopen = () => startRequest(watch);
  socket.onmessage = event => {
    watch.queue = watch.queue.then(() => handleFrame(watch, socket, event.data))
      .catch(() => failWatch(watch));
  };
  socket.onclose = event => {
    if (watch.stopped || watch.socket !== socket) return;
    if (!watch.fatal) {
      watch.state = event.code === 1008 ? 'denied' : 'offline';
      watch.items = [];
      watch.onChange();
      if (event.code !== 1008) schedule(watch, 5000, () => connectWatch(watch));
    }
  };
}

function newWatch(pair, kind, threadId, onChange) {
  const watch = { pair, kind, threadId, onChange, socket: null, timer: null,
    queue: Promise.resolve(), stopped: false, fatal: false, state: 'connecting',
    requestId: null, seq: 0, items: [] };
  connectWatch(watch);
  return watch;
}

function stopWatch(watch) {
  if (!watch || watch.stopped) return;
  watch.stopped = true;
  clearTimeout(watch.timer);
  watch.socket?.close();
}

function sessionKey(item) { return `${item.pair.deviceId}:${item.threadId}`; }

function availableSessions() {
  const byKey = new Map();
  for (const watch of watches) {
    if (watch.state !== 'online') continue;
    for (const item of watch.items) {
      const key = sessionKey(item);
      if (!byKey.has(key)) byKey.set(key, item);
    }
  }
  return [...byKey.values()].sort((a, b) => {
    const byTime = (b.updatedAt ?? 0) - (a.updatedAt ?? 0);
    return Number.isFinite(byTime) && byTime !== 0 ? byTime : a.threadId.localeCompare(b.threadId);
  });
}

function renderMessages() {
  setComposeRecipient(selected && historyWatch?.state === 'online' ? selected : null);
  if (!selected) { stopSpeech(); conversation.hidden = true; return; }
  conversation.hidden = false;
  conversationTitle.textContent = selected.title || `Сессия ${selected.threadId.slice(0, 8)}`;
  conversationDevice.textContent = `${selected.pair.deviceName} · ${selected.threadId.slice(0, 8)}`;
  const watch = historyWatch;
  if (!watch || watch.state !== 'online') {
    stopSpeech();
    messageList.hidden = true;
    messageList.replaceChildren();
    renderedMessages = '';
    const messages = { connecting: 'Подключаем историю…', waiting: 'Загружаем последние сообщения…',
      offline: 'Компьютер не в сети. Ждём подключения…', timeout: 'Компьютер не ответил. Повторяем запрос…',
      unavailable: 'История этой сессии сейчас недоступна.', denied: 'Доступ к истории отклонён. Обновите страницу.',
      invalid: 'Не удалось проверить данные истории. Переподключите компьютер.' };
    setStatus(conversationStatus, messages[watch?.state] || 'Сессия закрыта на ПК. Выберите другую.',
      watch?.state === 'denied' || watch?.state === 'invalid');
    return;
  }
  if (!watch.items.length) {
    stopSpeech();
    messageList.hidden = true;
    messageList.replaceChildren();
    renderedMessages = '';
    setStatus(conversationStatus, 'В этой сессии пока нет сообщений.');
    return;
  }
  conversationStatus.hidden = true;
  const signature = JSON.stringify(watch.items);
  if (signature === renderedMessages) { messageList.hidden = false; return; }
  stopSpeech();
  renderedMessages = signature;
  messageList.replaceChildren();
  for (const item of watch.items) {
    const row = document.createElement('li');
    const head = document.createElement('div');
    head.className = 'message-list__head';
    const author = document.createElement('strong');
    author.textContent = item.role === 'user' ? 'Вы' : 'Codex';
    const time = document.createElement('time');
    time.textContent = formatTime(item.createdAt);
    if (time.textContent) time.dateTime = new Date(item.createdAt * 1000).toISOString();
    const body = document.createElement('p');
    body.className = 'message-list__body';
    body.textContent = item.text;
    head.append(author, time);
    row.append(head, body);
    if (item.role === 'assistant') {
      const button = listenButton(item.text);
      if (button) row.append(button);
    }
    if (item.truncated) {
      const note = document.createElement('small');
      note.className = 'message-list__truncated';
      note.textContent = 'Текст сокращён на компьютере';
      row.append(note);
    }
    messageList.append(row);
  }
  messageList.hidden = false;
}

function selectSession(item) {
  const previous = selected && sessionKey(selected);
  selected = item;
  initialSelectionPending = false;
  if (previous !== sessionKey(item) || historyWatch?.pair.clientId !== item.pair.clientId) {
    stopWatch(historyWatch);
    historyWatch = null;
    historyWatch = newWatch(item.pair, 'thread', item.threadId, renderMessages);
    renderedMessages = '';
  }
  renderedSessions = '';
  renderWorkspace();
}

function renderWorkspace() {
  const sessions = availableSessions();
  const online = watches.some(watch => watch.state === 'online');
  const waiting = watches.some(watch => watch.state === 'connecting' || watch.state === 'waiting');
  const invalid = watches.some(watch => watch.state === 'invalid' || watch.state === 'denied');
  liveNode.hidden = !online;
  if (online && sessions.length) setStatus(statusNode, 'Список обновляется с компьютера.');
  else if (online) setStatus(statusNode, 'Открытых сессий Codex пока нет.');
  else if (waiting) setStatus(statusNode, 'Получаем список с компьютера…');
  else if (invalid) setStatus(statusNode, 'Не удалось проверить доступ к истории. Переподключите компьютер.', true);
  else if (watches.some(watch => watch.state === 'unavailable')) setStatus(statusNode, 'История недоступна на компьютере. Проверьте версию приложения.');
  else if (watches.some(watch => watch.state === 'timeout')) setStatus(statusNode, 'Компьютер не ответил. Повторяем запрос…');
  else setStatus(statusNode, 'Компьютер не в сети. Сессии появятся после запуска приложения.');
  contentNode.hidden = !sessions.length;
  if (!sessions.length) {
    stopWatch(historyWatch);
    historyWatch = null;
    if (online) selected = null;
    sessionList.replaceChildren();
    renderedSessions = '';
    renderMessages();
    return;
  }
  if (!selected && initialSelectionPending && sessions.length === 1) return selectSession(sessions[0]);
  const current = selected && sessions.find(item => sessionKey(item) === sessionKey(selected));
  if (selected && !current) {
    stopWatch(historyWatch);
    historyWatch = null;
    selected = null;
    initialSelectionPending = false;
    stopSpeech();
    messageList.replaceChildren();
    renderedMessages = '';
  } else if (current && historyWatch && historyWatch.pair.clientId !== current.pair.clientId) {
    stopWatch(historyWatch);
    selected = current;
    historyWatch = newWatch(current.pair, 'thread', current.threadId, renderMessages);
  } else if (current && !historyWatch) {
    selected = current;
    historyWatch = newWatch(current.pair, 'thread', current.threadId, renderMessages);
  } else if (current) {
    selected = current;
  }
  const signature = JSON.stringify(sessions.map(item => [sessionKey(item), item.title, item.pair.deviceName]))
    + (selected ? sessionKey(selected) : '');
  if (signature !== renderedSessions) {
    const focusedKey = document.activeElement?.dataset?.sessionKey;
    renderedSessions = signature;
    sessionList.replaceChildren();
    for (const item of sessions) {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'session-choice';
      button.dataset.sessionKey = sessionKey(item);
      button.setAttribute('aria-pressed', String(Boolean(selected && sessionKey(selected) === sessionKey(item))));
      const title = document.createElement('strong');
      title.textContent = item.title || `Сессия ${item.threadId.slice(0, 8)}`;
      const meta = document.createElement('span');
      meta.textContent = `${item.pair.deviceName} · ${item.threadId.slice(0, 8)}`;
      button.append(title, meta);
      button.addEventListener('click', () => selectSession(item));
      sessionList.append(button);
    }
    if (focusedKey) [...sessionList.children].find(button => button.dataset.sessionKey === focusedKey)?.focus({ preventScroll: true });
  }
  renderMessages();
}

function stopAll() {
  stopSpeech();
  setComposeRecipient(null);
  for (const watch of watches) stopWatch(watch);
  watches = [];
  stopWatch(historyWatch);
  historyWatch = null;
  liveNode.hidden = true;
  contentNode.hidden = true;
  messageList.replaceChildren();
  renderedSessions = '';
  renderedMessages = '';
}

function connectAll() {
  stopAll();
  if (!pairs.length) return;
  setStatus(statusNode, 'Получаем список с компьютера…');
  watches = pairs.map(pair => newWatch(pair, 'threads', null, renderWorkspace));
  renderWorkspace();
}

export async function startWorkspace(ownerId, devices) {
  if (initialized) return;
  initialized = true;
  if (!Array.isArray(devices)) return setStatus(statusNode, 'Не удалось проверить компьютеры. Обновите страницу.', true);
  if (!devices.length) return setStatus(statusNode, 'Подключённых компьютеров пока нет. Откройте «Подключить телефон» на ПК.');
  if (!window.crypto?.subtle || !window.indexedDB || !window.WebSocket) {
    return setStatus(statusNode, 'Для истории нужен современный браузер с защищённым соединением.', true);
  }
  try { pairs = await activePairs(ownerId, devices); }
  catch { return setStatus(statusNode, 'Не удалось открыть ключ этого браузера. Проверьте настройки хранения данных.', true); }
  if (!pairs.length) return setStatus(statusNode, 'Этот браузер ещё не связан с ПК. Откройте новый QR-код в приложении на компьютере.');
  if (window.speechSynthesis?.addEventListener) {
    window.speechSynthesis.addEventListener('voiceschanged', () => {
      let next;
      let availableVoices = [];
      try {
        availableVoices = window.speechSynthesis.getVoices().filter(voice => voice.localService === true);
        next = availableVoices.map(voice => `${voice.voiceURI}:${voice.lang}`).join('|');
      } catch { next = ''; }
      if (next === voiceSignature) return;
      voiceSignature = next;
      if (activeSpeech && !availableVoices.some(voice =>
        voice.voiceURI === activeSpeech.voice.voiceURI && voice.lang === activeSpeech.voice.lang)) stopSpeech();
      if (historyWatch?.state === 'online' && !activeSpeech) {
        renderedMessages = '';
        renderMessages();
      }
    });
  }
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'hidden') {
      stopAll();
      setStatus(statusNode, 'Показ приостановлен, пока вкладка неактивна.');
    } else connectAll();
  });
  window.addEventListener('pagehide', stopAll);
  window.addEventListener('pageshow', event => { if (event.persisted) connectAll(); });
  if (document.visibilityState !== 'hidden') connectAll();
}

export function removeWorkspaceDevice(deviceId) {
  revokedDevices.add(deviceId);
  pairs = pairs.filter(pair => pair.deviceId !== deviceId);
  if (selected?.pair.deviceId === deviceId) {
    selected = null;
    initialSelectionPending = false;
  }
  if (document.visibilityState !== 'hidden') connectAll();
  if (!pairs.length) setStatus(statusNode, 'Подключённых к этому браузеру компьютеров нет.');
}
