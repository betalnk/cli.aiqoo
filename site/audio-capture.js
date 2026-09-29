export const MAX_RECORDING_SECONDS = 45;
export const MAX_PCM_BYTES = 4_320_000;

export function wavFromPcm(pcm, sampleRate) {
  if (!(pcm instanceof Uint8Array) || !pcm.length || (pcm.length & 1) !== 0
      || pcm.length > MAX_PCM_BYTES || !Number.isInteger(sampleRate)
      || sampleRate < 8000 || sampleRate > 48000) throw new Error('Недопустимые данные записи.');
  const header = new ArrayBuffer(44);
  const view = new DataView(header);
  const write = (offset, value) => {
    for (let index = 0; index < value.length; index++) view.setUint8(offset + index, value.charCodeAt(index));
  };
  write(0, 'RIFF');
  view.setUint32(4, 36 + pcm.length, true);
  write(8, 'WAVE');
  write(12, 'fmt ');
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, 1, true);
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, sampleRate * 2, true);
  view.setUint16(32, 2, true);
  view.setUint16(34, 16, true);
  write(36, 'data');
  view.setUint32(40, pcm.length, true);
  return new Blob([header, pcm], { type: 'audio/wav' });
}

export function pcm16FromFloatChunks(chunks, maximumSamples) {
  let samples = 0;
  for (const chunk of chunks) {
    if (!(chunk instanceof Float32Array)) throw new Error('Недопустимые данные записи.');
    samples += chunk.length;
  }
  if (!Number.isSafeInteger(maximumSamples) || maximumSamples < 1
      || samples < 1 || samples > maximumSamples || samples * 2 > MAX_PCM_BYTES) {
    throw new Error('Запись слишком длинная или пустая.');
  }
  const pcm = new Uint8Array(samples * 2);
  const view = new DataView(pcm.buffer);
  let offset = 0;
  for (const chunk of chunks) {
    for (const sample of chunk) {
      if (!Number.isFinite(sample)) throw new Error('Не удалось обработать звук.');
      const clamped = Math.max(-1, Math.min(1, sample));
      view.setInt16(offset, clamped < 0 ? Math.round(clamped * 32768)
        : Math.round(clamped * 32767), true);
      offset += 2;
    }
  }
  return pcm;
}

export function microphoneAvailable() {
  return Boolean(navigator.mediaDevices?.getUserMedia
    && (window.AudioContext || window.webkitAudioContext)
    && (window.AudioWorkletNode || window.AudioContext?.prototype?.createScriptProcessor
      || window.webkitAudioContext?.prototype?.createScriptProcessor));
}

export async function startMicrophoneCapture({ onLimit, onInterrupted } = {}) {
  if (!microphoneAvailable()) throw new Error('Микрофон недоступен в этом браузере. Откройте сайт через HTTPS.');
  const Context = window.AudioContext || window.webkitAudioContext;
  let context;
  try { context = new Context({ sampleRate: 48000, latencyHint: 'interactive' }); }
  catch { context = new Context(); }
  let stream;
  let source;
  let processor;
  let silent;
  let stopped = false;
  let finished = false;
  let reachedLimit = false;
  const chunks = [];
  let totalSamples = 0;
  let flushResolve;
  const sampleRate = context.sampleRate;
  const maximumSamples = Math.min(sampleRate * MAX_RECORDING_SECONDS, MAX_PCM_BYTES / 2);

  const stopTracks = () => stream?.getTracks().forEach(track => track.stop());
  const closeResources = async () => {
    source?.disconnect();
    processor?.disconnect();
    if (processor) processor.onaudioprocess = null;
    if (processor?.port) processor.port.onmessage = null;
    silent?.disconnect();
    stopTracks();
    try { await context.close(); } catch { /* Context may already be closed. */ }
  };
  const addChunk = chunk => {
    if (stopped || !chunk?.length || reachedLimit) return;
    const remaining = maximumSamples - totalSamples;
    if (remaining <= 0) return;
    const accepted = chunk.length > remaining ? chunk.subarray(0, remaining) : chunk;
    chunks.push(new Float32Array(accepted));
    totalSamples += accepted.length;
    if (totalSamples >= maximumSamples) {
      reachedLimit = true;
      onLimit?.();
    }
  };

  try {
    if (!Number.isInteger(sampleRate) || sampleRate < 8000 || sampleRate > 48000) {
      throw new Error('Частота микрофона не поддерживается. Выберите другой микрофон.');
    }
    // Both calls begin in the click handler, before Safari can lose the user gesture.
    const resume = context.resume();
    const permission = navigator.mediaDevices.getUserMedia({ audio: {
      channelCount: { ideal: 1 }, echoCancellation: true, noiseSuppression: true,
      autoGainControl: true } }).then(media => {
      if (finished) media.getTracks().forEach(track => track.stop());
      else stream = media;
      return media;
    });
    await Promise.all([permission, resume]);
    for (const track of stream.getAudioTracks()) {
      track.addEventListener('ended', () => { if (!finished) onInterrupted?.(); }, { once: true });
    }
    source = context.createMediaStreamSource(stream);
    silent = context.createGain();
    silent.gain.value = 0;
    if (context.audioWorklet && window.AudioWorkletNode) {
      try {
        await context.audioWorklet.addModule('/pcm-worklet.js');
        processor = new window.AudioWorkletNode(context, 'cli-pcm-capture');
        processor.port.onmessage = event => {
          if (event.data?.type === 'chunk') addChunk(new Float32Array(event.data.buffer));
          if (event.data?.type === 'flushed') flushResolve?.();
        };
      } catch { processor = null; }
    }
    if (!processor) {
      if (typeof context.createScriptProcessor !== 'function') throw new Error('Запись PCM не поддерживается в этом браузере.');
      processor = context.createScriptProcessor(4096, 1, 1);
      processor.onaudioprocess = event => {
        addChunk(event.inputBuffer.getChannelData(0));
        event.outputBuffer.getChannelData(0).fill(0);
      };
    }
    source.connect(processor);
    processor.connect(silent);
    silent.connect(context.destination);
  } catch (error) {
    finished = true;
    await closeResources();
    if (error?.name === 'NotAllowedError' || error?.name === 'PermissionDeniedError') {
      throw new Error('Разрешите доступ к микрофону в Safari и попробуйте снова.');
    }
    throw error;
  }

  return {
    sampleRate,
    get seconds() { return Math.min(MAX_RECORDING_SECONDS, totalSamples / sampleRate); },
    async stop() {
      if (finished) throw new Error('Запись уже завершена.');
      finished = true;
      source.disconnect();
      if (processor.port) {
        await new Promise((resolve, reject) => {
          const timeout = setTimeout(() => reject(new Error('Не удалось завершить запись.')), 1500);
          flushResolve = () => { clearTimeout(timeout); resolve(); };
          processor.port.postMessage({ type: 'flush' });
        }).catch(async error => { stopped = true; await closeResources(); throw error; });
      }
      stopped = true;
      await closeResources();
      const pcm = pcm16FromFloatChunks(chunks, maximumSamples);
      return { pcm, sampleRate, duration: pcm.length / 2 / sampleRate,
        wav: wavFromPcm(pcm, sampleRate) };
    },
    async cancel() {
      if (finished) return;
      finished = true;
      stopped = true;
      await closeResources();
    }
  };
}
