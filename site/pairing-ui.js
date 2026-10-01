import { createPairingClient } from './pairing.js';

// Both pages use the same explicit control; resuming only polls an existing claim.
export async function mountPairing({ ownerId, button, setStatus, onReady, onWaiting, onActive,
  client = createPairingClient(), clientName = 'Браузер', signal }) {
  const callbacks = { clientName, signal,
    onWaiting(record) {
      button.hidden = true;
      button.disabled = true;
      setStatus('Подтвердите привязку кнопкой «ОК» в приложении на ПК.');
      onWaiting?.(record);
    }
  };
  const finish = async (record, alreadyActive = false) => {
    button.hidden = true;
    setStatus('ПК привязан к этому аккаунту.');
    await onActive?.(record, { alreadyActive });
  };
  const fail = error => {
    if (error.name === 'AbortError') return;
    setStatus(error.message || 'Не удалось привязать ПК. Обновите страницу и повторите.', true);
    button.disabled = !!error.terminal;
    button.hidden = !!error.terminal;
  };
  const context = await client.inspect(ownerId, signal);
  if (context.kind === 'none') return false;
  if (context.kind === 'active') { await finish(context.record, true); return true; }
  if (context.kind === 'waiting') {
    callbacks.onWaiting(context.record);
    client.resume(context, callbacks).then(finish).catch(fail);
    return true;
  }
  button.hidden = false;
  button.disabled = false;
  setStatus('Нажмите «Привязать», затем подтвердите запрос на этом ПК.');
  onReady?.(context);
  let clicked = false;
  button.addEventListener('click', async () => {
    if (clicked) return;
    clicked = true;
    button.disabled = true;
    setStatus('Передаём запрос на ПК…');
    try { await finish(await client.bind(ownerId, callbacks)); }
    catch (error) { fail(error); }
    finally { clicked = false; }
  });
  return true;
}
