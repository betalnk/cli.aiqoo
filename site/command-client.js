const encoder = new TextEncoder();
const decoder = new TextDecoder('utf-8', { fatal: true });
const idPattern = /^[0-9a-f]{32}$/;
const threadPattern = /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/;
const CHUNK_BYTES = 8192;
const MAX_AUDIO_BYTES = 4_320_000;
const reasons = new Set(['locked', 'session_mismatch', 'not_visible', 'unavailable',
  'invalid_payload', 'asr_failed', 'input_failed', 'receipt_timeout', 'device_error']);

function base64Url(bytes) {
  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function fromBase64Url(value, maximum, exact = null) {
  if (typeof value !== 'string' || !/^[A-Za-z0-9_-]+$/.test(value)
      || value.length > Math.ceil(maximum * 4 / 3) + 4) throw new Error('invalid-base64url');
  const binary = atob(value.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(value.length / 4) * 4, '='));
  const bytes = Uint8Array.from(binary, character => character.charCodeAt(0));
  if (bytes.length > maximum || (exact !== null && bytes.length !== exact)
      || base64Url(bytes) !== value) throw new Error('invalid-base64url');
  return bytes;
}

function requestId() {
  return Array.from(crypto.getRandomValues(new Uint8Array(16)), byte => byte.toString(16).padStart(2, '0')).join('');
}

function aadFor(command, direction, frame, seq, stage = '') {
  return encoder.encode(`CLI Voice command v1\0${command.deviceId}\0${command.clientId}\0${command.requestId}\0${command.op}\0${command.threadId || ''}\0${direction}\0${frame}\0${seq}\0${stage}`);
}

async function encrypt(command, frame, seq, plaintext) {
  const nonce = crypto.getRandomValues(new Uint8Array(12));
  const ciphertext = await crypto.subtle.encrypt({ name: 'AES-GCM', iv: nonce,
    additionalData: aadFor(command, 'browser', frame, seq), tagLength: 128 },
  command.pair.key, plaintext);
  return { nonce: base64Url(nonce), ciphertext: base64Url(new Uint8Array(ciphertext)) };
}

function exactKeys(value, fields) {
  return value && typeof value === 'object' && !Array.isArray(value)
    && Object.keys(value).sort().join('|') === [...fields].sort().join('|');
}

async function decryptAck(command, frame) {
  if (frame.requestId !== command.requestId || frame.deviceId !== command.deviceId
      || frame.clientId !== command.clientId || frame.op !== command.op
      || frame.threadId !== command.threadId || !Number.isSafeInteger(frame.ackSeq)) {
    throw new Error('wrong-ack-route');
  }
  const nonce = fromBase64Url(frame.nonce, 12, 12);
  const ciphertext = fromBase64Url(frame.ciphertext, 9216);
  if (ciphertext.length < 16) throw new Error('invalid-ack');
  const plain = await crypto.subtle.decrypt({ name: 'AES-GCM', iv: nonce,
    additionalData: aadFor(command, 'device', 'ack', frame.ackSeq, frame.stage),
    tagLength: 128 }, command.pair.key, ciphertext);
  const payload = JSON.parse(decoder.decode(plain));
  if (payload?.v !== 1 || payload.stage !== frame.stage) throw new Error('wrong-ack-stage');
  if (frame.stage === 'ready' || frame.stage === 'submitted' || frame.stage === 'confirmed') {
    if (!exactKeys(payload, ['v', 'stage'])) throw new Error('invalid-ack');
  } else if (frame.stage === 'rejected' || frame.stage === 'unknown') {
    if (!exactKeys(payload, ['v', 'stage', 'reason', 'maybeEntered'])
        || !reasons.has(payload.reason)
        || payload.maybeEntered !== (frame.stage === 'unknown')) throw new Error('invalid-ack');
  } else if (frame.stage === 'transcribed') {
    if (!exactKeys(payload, ['v', 'stage', 'text']) || typeof payload.text !== 'string'
        || encoder.encode(payload.text).length > 8192) throw new Error('invalid-ack');
  } else throw new Error('invalid-ack');
  return payload;
}

function validateInput(pair, op, threadId, bytes, sampleRate) {
  if (!pair?.key || !idPattern.test(pair.deviceId) || !idPattern.test(pair.clientId)
      || !(bytes instanceof Uint8Array) || !bytes.length) throw new Error('Недопустимые данные отправки.');
  if (op === 'send_text') {
    if (!threadPattern.test(threadId) || bytes.length > 8192) throw new Error('Сообщение слишком длинное.');
  } else if (op === 'transcribe_audio') {
    if (threadId !== null || !Number.isInteger(sampleRate) || sampleRate < 8000 || sampleRate > 48000
        || bytes.length < 2 || bytes.length > MAX_AUDIO_BYTES || (bytes.length & 1) !== 0
        || bytes.length > sampleRate * 2 * 45) throw new Error('Запись слишком длинная или повреждена.');
  } else throw new Error('Недопустимая операция.');
}

function wsUrl() { return `${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/api/v1/ws/history`; }
function delay(ms) { return new Promise(resolve => setTimeout(resolve, ms)); }

export async function runCommand({ pair, op, threadId, bytes, sampleRate = null, onStage = () => {}, signal = null }) {
  validateInput(pair, op, threadId, bytes, sampleRate);
  if (signal?.aborted) return { stage: 'unavailable', reason: 'cancelled_before_start' };
  const command = { pair, op, threadId, deviceId: pair.deviceId, clientId: pair.clientId,
    requestId: requestId(), totalBytes: bytes.length, totalChunks: Math.ceil(bytes.length / CHUNK_BYTES) };
  const metadata = op === 'send_text'
    ? { v: 1, op, threadId, totalBytes: command.totalBytes, totalChunks: command.totalChunks }
    : { v: 1, op, threadId: null, totalBytes: command.totalBytes, totalChunks: command.totalChunks,
      format: 'pcm_s16le', sampleRate, channels: 1 };
  const startEnvelope = await encrypt(command, 'start', 0, encoder.encode(JSON.stringify(metadata)));
  if (signal?.aborted) return { stage: 'unavailable', reason: 'cancelled_before_start' };

  return new Promise(resolve => {
    let socket;
    let done = false;
    let startSent = false;
    let routeStarted = false;
    let ready = false;
    let endSent = false;
    let submitted = false;
    let lastAck = 0;
    let chain = Promise.resolve();
    const notify = state => { try { onStage({ requestId: command.requestId, ...state }); } catch { /* UI callback must not alter delivery. */ } };
    const finish = outcome => {
      if (done) return;
      done = true;
      clearTimeout(watchdog);
      signal?.removeEventListener('abort', abort);
      try { socket?.close(); } catch { /* Socket may already be closed. */ }
      resolve({ requestId: command.requestId, ...outcome });
    };
    const unknown = reason => finish({ stage: 'unknown', reason });
    const abort = () => {
      if (startSent && socket?.readyState === WebSocket.OPEN) {
        try { socket.send(JSON.stringify({ type: 'command_cancel', requestId: command.requestId })); }
        catch { /* The route state is already uncertain. */ }
      }
      finish(startSent ? { stage: 'unknown', reason: 'cancelled' }
        : { stage: 'unavailable', reason: 'cancelled_before_start' });
    };
    const watchdog = setTimeout(() => finish(startSent
      ? { stage: 'unknown', reason: 'timeout' } : { stage: 'unavailable', reason: 'connection_timeout' }),
    op === 'transcribe_audio' ? 305000 : 185000);

    async function upload() {
      for (let index = 0; index < command.totalChunks; index++) {
        if (done) return;
        const first = index * CHUNK_BYTES;
        const chunk = bytes.subarray(first, Math.min(first + CHUNK_BYTES, bytes.length));
        const envelope = await encrypt(command, 'chunk', index + 1, chunk);
        while (!done && socket.readyState === WebSocket.OPEN && socket.bufferedAmount > 65536) await delay(50);
        if (done) return;
        if (socket.readyState !== WebSocket.OPEN) return unknown('socket_closed');
        socket.send(JSON.stringify({ type: 'command_chunk', requestId: command.requestId,
          seq: index + 1, ...envelope }));
        if (index === 0 || index === command.totalChunks - 1 || index % 8 === 7) {
          notify({ stage: 'uploading', uploadedBytes: first + chunk.length, totalBytes: bytes.length });
        }
        if (index + 1 < command.totalChunks) await delay(34);
      }
      if (done) return;
      socket.send(JSON.stringify({ type: 'command_end', requestId: command.requestId }));
      endSent = true;
      notify({ stage: op === 'transcribe_audio' ? 'recognizing' : 'awaiting_receipt' });
    }

    async function handle(raw) {
      if (done) return;
      if (typeof raw !== 'string' || raw.length > 16384) return unknown('invalid_frame');
      let frame;
      try { frame = JSON.parse(raw); } catch { return unknown('invalid_frame'); }
      if (!frame || frame.requestId !== command.requestId) return;
      if (frame.type === 'command_started') {
        routeStarted = true;
        notify({ stage: 'route_started' });
        return;
      }
      if (frame.type === 'command_unavailable') {
        return finish(routeStarted || ready ? { stage: 'unknown', reason: 'route_lost' }
          : { stage: 'unavailable', reason: 'pre_forward' });
      }
      if (frame.type === 'command_unknown') return unknown(frame.reason || 'route_lost');
      if (frame.type !== 'command_ack') return unknown('invalid_frame');
      let ack;
      try { ack = await decryptAck(command, frame); } catch { return unknown('invalid_ack'); }
      if (frame.stage === 'rejected') {
        if (frame.ackSeq !== lastAck + 1 || (frame.ackSeq === 1 && ready)
            || (frame.ackSeq === 2 && !ready) || frame.ackSeq > 2) return unknown('invalid_ack');
        return finish({ stage: 'rejected', reason: ack.reason, maybeEntered: false });
      }
      if (frame.ackSeq !== lastAck + 1) return unknown('invalid_ack');
      if (frame.ackSeq === 1 && frame.stage === 'ready' && !ready) {
        ready = true;
        lastAck = 1;
        notify({ stage: 'accepted' });
        upload().catch(() => unknown('upload_failed'));
        return;
      }
      if (!ready || !endSent) return unknown('invalid_ack');
      if (op === 'transcribe_audio' && frame.ackSeq === 2 && frame.stage === 'transcribed') {
        return finish({ stage: 'transcribed', text: ack.text });
      }
      if (op === 'send_text' && frame.ackSeq === 2 && frame.stage === 'submitted') {
        submitted = true;
        lastAck = 2;
        notify({ stage: 'submitted' });
        return;
      }
      if (op === 'send_text' && frame.stage === 'unknown'
          && ((frame.ackSeq === 2 && !submitted) || (frame.ackSeq === 3 && submitted))) {
        return unknown(ack.reason);
      }
      if (op === 'send_text' && frame.ackSeq === 3 && submitted && frame.stage === 'confirmed') {
        return finish({ stage: 'confirmed' });
      }
      return unknown('invalid_ack');
    }

    signal?.addEventListener('abort', abort, { once: true });
    if (signal?.aborted) return abort();
    try { socket = new WebSocket(wsUrl()); }
    catch { return finish({ stage: 'unavailable', reason: 'connection_failed' }); }
    socket.onopen = () => {
      if (done) return;
      try {
        socket.send(JSON.stringify({ type: 'command_start', requestId: command.requestId,
          deviceId: command.deviceId, clientId: command.clientId, op, threadId,
          totalBytes: command.totalBytes, totalChunks: command.totalChunks, ...startEnvelope }));
        startSent = true;
        notify({ stage: 'connecting' });
      } catch { unknown('start_failed'); }
    };
    socket.onmessage = event => {
      chain = chain.then(() => handle(event.data)).catch(() => unknown('invalid_frame'));
    };
    socket.onclose = () => {
      if (done) return;
      finish(startSent ? { stage: 'unknown', reason: 'socket_closed' }
        : { stage: 'unavailable', reason: 'connection_failed' });
    };
  });
}
