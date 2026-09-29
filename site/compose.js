import { microphoneAvailable, startMicrophoneCapture, MAX_RECORDING_SECONDS } from './audio-capture.js';
import { runCommand } from './command-client.js';

const form = document.getElementById('compose');
const recipientNode = document.getElementById('compose-recipient');
const draftInput = document.getElementById('message-draft');
const recordButton = document.getElementById('record-voice');
const recordStatus = document.getElementById('record-status');
const previewWrap = document.getElementById('voice-preview-wrap');
const previewAudio = document.getElementById('voice-preview');
const transcribeButton = document.getElementById('transcribe-voice');
const discardButton = document.getElementById('discard-voice');
const sendButton = document.getElementById('send-message');
const sendStatus = document.getElementById('send-status');
const encoder = new TextEncoder();
const shellCharacters = new Set(['&', '|', '<', '>', '^', '%', '!', '$', '`', '"', "'", ';', '(', ')', '{', '}', '[', ']', '#', '@', '*', '\\']);
const drafts = new Map();

function safeSingleLine(text) {
  if (!text || encoder.encode(text).length > 8192) return false;
  for (let index = 0; index < text.length; index++) {
    const character = text[index];
    if (/\p{White_Space}/u.test(character) && character !== ' ') return false;
    if (/[\p{Cc}\p{Cf}\p{Co}\p{Cn}\p{Cs}]/u.test(character)
        || shellCharacters.has(character)) return false;
  }
  return true;
}

let recipient = null;
let recording = null;
let startingRecording = false;
let finishingRecording = false;
let recordingTick = null;
let activeCommand = null;

function keyOf(item) { return `${item.pair.deviceId}:${item.threadId}`; }
function draftFor(key) {
  if (!drafts.has(key)) drafts.set(key, { text: '', status: null, recorded: null });
  return drafts.get(key);
}

function setStatus(draft, text, error = false) {
  draft.status = { text, error };
  if (recipient && draftFor(keyOf(recipient)) === draft) render();
}

function recordedForCurrent() { return recipient && draftFor(keyOf(recipient)).recorded; }

function render() {
  form.hidden = !recipient;
  if (!recipient) {
    sendButton.disabled = true;
    transcribeButton.disabled = true;
    return;
  }
  const key = keyOf(recipient);
  const draft = draftFor(key);
  recipientNode.textContent = `${recipient.pair.deviceName} · ${recipient.title || 'Сессия'} · ${recipient.threadId.slice(0, 8)}`;
  if (draftInput.value !== draft.text) draftInput.value = draft.text;
  const busyHere = activeCommand?.key === key;
  const busy = Boolean(activeCommand);
  draftInput.disabled = busyHere;
  const byteLength = encoder.encode(draft.text.trim()).length;
  sendButton.disabled = busy || Boolean(recording) || startingRecording || finishingRecording || byteLength < 1 || byteLength > 8192;
  if (draft.status) {
    sendStatus.textContent = draft.status.text;
    sendStatus.classList.toggle('account-inline-status--error', draft.status.error);
    sendStatus.hidden = false;
  } else if (byteLength > 8192) {
    sendStatus.textContent = 'Текст слишком длинный для отправки. Сократите его до 8192 байт.';
    sendStatus.classList.add('account-inline-status--error');
    sendStatus.hidden = false;
  } else sendStatus.hidden = true;
  recordButton.disabled = busy || startingRecording || finishingRecording || (!recording && !microphoneAvailable());
  recordButton.textContent = startingRecording ? 'Запрашиваем микрофон…'
    : recording ? 'Остановить запись'
      : draft.recorded ? 'Записать заново' : 'Записать голос';
  previewWrap.hidden = !draft.recorded;
  if (draft.recorded) {
    if (!draft.recorded.url) draft.recorded.url = URL.createObjectURL(draft.recorded.wav);
    if (previewAudio.src !== draft.recorded.url) previewAudio.src = draft.recorded.url;
  } else if (previewAudio.hasAttribute('src')) {
    previewAudio.removeAttribute('src');
    previewAudio.load?.();
  }
  transcribeButton.disabled = busy || Boolean(recording) || !draft.recorded;
  discardButton.disabled = busy || Boolean(recording) || !draft.recorded;
  if (recording?.key === key) {
    const seconds = Math.min(MAX_RECORDING_SECONDS, Math.floor((Date.now() - recording.startedAt) / 1000));
    recordStatus.textContent = `Идёт запись · ${String(Math.floor(seconds / 60)).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}`;
    recordStatus.hidden = false;
  } else if (startingRecording) {
    recordStatus.textContent = 'Ожидаем разрешение микрофона…';
    recordStatus.hidden = false;
  } else if (!microphoneAvailable()) {
    recordStatus.textContent = 'Микрофон доступен в Safari через HTTPS.';
    recordStatus.hidden = false;
  } else recordStatus.hidden = true;
}

function discardRecorded(draft) {
  if (!draft.recorded) return;
  if (draft.recorded.url) URL.revokeObjectURL(draft.recorded.url);
  draft.recorded = null;
}

async function cancelRecording(message = null) {
  const current = recording;
  recording = null;
  clearInterval(recordingTick);
  recordingTick = null;
  if (!current) return;
  finishingRecording = true;
  render();
  try {
    await current.capture.cancel();
    if (message) setStatus(draftFor(current.key), message, true);
  } finally {
    finishingRecording = false;
    render();
  }
}

async function stopRecording(limitReached = false) {
  const current = recording;
  if (!current) return;
  recording = null;
  finishingRecording = true;
  clearInterval(recordingTick);
  recordingTick = null;
  const draft = draftFor(current.key);
  setStatus(draft, 'Завершаем запись…');
  try {
    const result = await current.capture.stop();
    discardRecorded(draft);
    draft.recorded = { ...result, url: null };
    setStatus(draft, limitReached
      ? 'Достигнут предел 45 секунд. Прослушайте запись и распознайте её на ПК.'
      : 'Запись готова. Прослушайте её и нажмите «Распознать на ПК».');
  } catch (error) {
    setStatus(draft, error.message || 'Запись не получилась. Попробуйте ещё раз.', true);
  } finally {
    finishingRecording = false;
    render();
  }
}

async function startRecording() {
  if (!recipient || startingRecording || finishingRecording || activeCommand) return;
  const key = keyOf(recipient);
  const draft = draftFor(key);
  startingRecording = true;
  setStatus(draft, 'Разрешите доступ к микрофону…');
  render();
  try {
    const capture = await startMicrophoneCapture({
      onLimit: () => { if (recording?.key === key) stopRecording(true); },
      onInterrupted: () => { if (recording?.key === key) cancelRecording('Микрофон отключился. Запись остановлена.'); }
    });
    if (!recipient || keyOf(recipient) !== key || document.visibilityState === 'hidden') {
      await capture.cancel();
      setStatus(draft, 'Запись отменена после смены страницы или сессии.');
      return;
    }
    discardRecorded(draft);
    recording = { capture, key, startedAt: Date.now() };
    recordingTick = setInterval(render, 1000);
    draft.status = null;
  } catch (error) {
    setStatus(draft, error.message || 'Не удалось включить микрофон.', true);
  } finally {
    startingRecording = false;
    render();
  }
}

function stageText(stage, op) {
  if (stage.stage === 'connecting') return 'Подключаемся к компьютеру…';
  if (stage.stage === 'route_started') return 'Связь с компьютером установлена…';
  if (stage.stage === 'accepted') return op === 'transcribe_audio'
    ? 'Компьютер принял запись.' : 'Компьютер принял сообщение.';
  if (stage.stage === 'uploading') return op === 'transcribe_audio'
    ? `Передаём запись · ${Math.floor(stage.uploadedBytes / stage.totalBytes * 100)}%`
    : 'Передаём текст в выбранную сессию…';
  if (stage.stage === 'recognizing') return 'Распознаём запись на компьютере…';
  if (stage.stage === 'awaiting_receipt') return 'Ожидаем ответ приложения на ПК…';
  if (stage.stage === 'submitted') return 'Ввод выполнен. Проверяем получение в Codex…';
  return null;
}

function failureText(result, op) {
  if (result.stage === 'unavailable') return 'Компьютер сейчас недоступен. Ничего не отправлено.';
  if (result.stage === 'unknown') return op === 'transcribe_audio'
    ? 'Распознавание прервалось. Запись сохранена; повторить можно вручную.'
    : 'Доставка не подтверждена. Проверьте вкладку ПК перед повтором.';
  if (result.reason === 'locked') return 'ПК заблокирован. Разблокируйте его и повторите вручную.';
  if (result.reason === 'session_mismatch' || result.reason === 'not_visible') {
    return 'Выбранная сессия недоступна на ПК. Откройте нужную вкладку и повторите вручную.';
  }
  if (result.reason === 'input_failed') return 'Не отправлено; проверьте текст во вкладке ПК перед повтором.';
  if (result.reason === 'asr_failed') return 'Не удалось распознать запись на ПК. Запись сохранена.';
  return 'Компьютер отказал в операции. Черновик сохранён.';
}

async function execute(op, sourceBytes, sampleRate = null) {
  if (!recipient || activeCommand) return;
  const target = recipient;
  const key = keyOf(target);
  const draft = draftFor(key);
  const controller = new AbortController();
  activeCommand = { key, controller, op };
  setStatus(draft, op === 'transcribe_audio' ? 'Подготавливаем запись…' : 'Подготавливаем отправку…');
  render();
  try {
    const result = await runCommand({ pair: target.pair, op,
      threadId: op === 'send_text' ? target.threadId : null, bytes: sourceBytes, sampleRate,
      signal: controller.signal,
      onStage: stage => {
        const text = stageText(stage, op);
        if (text) setStatus(draft, text);
      } });
    if (result.stage === 'transcribed') {
      const text = result.text.replace(/\s+/g, ' ').trim();
      if (text) {
        draft.text = draft.text.trim() ? `${draft.text.trimEnd()} ${text}` : text;
        setStatus(draft, 'Текст готов. Проверьте его и нажмите «Отправить».');
      } else setStatus(draft, 'Речь не распознана. Запись сохранена для повторной попытки.', true);
    } else if (result.stage === 'confirmed') {
      draft.text = '';
      setStatus(draft, 'Подтверждено в выбранной сессии Codex.');
    } else setStatus(draft, failureText(result, op), true);
  } catch (error) {
    setStatus(draft, error.message || 'Не удалось выполнить действие. Черновик сохранён.', true);
  } finally {
    if (activeCommand?.controller === controller) activeCommand = null;
    render();
  }
}

export function setComposeRecipient(next) {
  const previousKey = recipient && keyOf(recipient);
  const nextKey = next && keyOf(next);
  if (previousKey && previousKey !== nextKey) draftFor(previousKey).text = draftInput.value;
  recipient = next;
  if (previousKey !== nextKey) {
    previewAudio.pause?.();
    if (recording && recording.key !== nextKey) cancelRecording('Запись остановлена после смены сессии.');
    draftInput.value = nextKey ? draftFor(nextKey).text : '';
  }
  render();
}

draftInput.addEventListener('input', () => {
  if (!recipient) return;
  const draft = draftFor(keyOf(recipient));
  draft.text = draftInput.value;
  draft.status = null;
  render();
});
recordButton.addEventListener('click', () => recording ? stopRecording() : startRecording());
discardButton.addEventListener('click', () => {
  if (!recipient || activeCommand) return;
  const draft = draftFor(keyOf(recipient));
  discardRecorded(draft);
  setStatus(draft, 'Запись удалена. Текстовый черновик сохранён.');
});
transcribeButton.addEventListener('click', () => {
  const recorded = recordedForCurrent();
  if (!recorded || activeCommand) return;
  execute('transcribe_audio', recorded.pcm, recorded.sampleRate);
});
form.addEventListener('submit', event => {
  event.preventDefault();
  if (!recipient || sendButton.disabled) return;
  const draft = draftFor(keyOf(recipient));
  const text = draft.text.trim();
  if (!safeSingleLine(text)) {
    setStatus(draft, 'В этом режиме можно отправить только одну строку без командных символов и эмодзи. Черновик сохранён.', true);
    return;
  }
  execute('send_text', encoder.encode(text));
});
document.addEventListener('visibilitychange', () => {
  if (document.visibilityState !== 'hidden') return;
  if (recording) cancelRecording('Запись остановлена после ухода со страницы.');
  activeCommand?.controller.abort();
});
window.addEventListener('pagehide', () => {
  if (recording) cancelRecording();
  activeCommand?.controller.abort();
  for (const draft of drafts.values()) {
    if (draft.recorded?.url) {
      URL.revokeObjectURL(draft.recorded.url);
      draft.recorded.url = null;
    }
  }
});

render();
