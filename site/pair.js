import { captureFragment } from './pairing.js';
import { mountPairing } from './pairing-ui.js';

const statusNode = document.getElementById('pair-status');
const confirmationNode = document.getElementById('pair-confirmation');
const waitingNode = document.getElementById('pair-waiting');
const submitButton = document.getElementById('pair-submit');

function setStatus(message, error = false) {
  statusNode.textContent = message;
  statusNode.classList.toggle('pair-panel__status--error', error);
}

async function main() {
  captureFragment();
  const response = await fetch('/api/v1/me', { credentials: 'same-origin', cache: 'no-store' });
  if (response.status === 401) {
    location.replace('/api/v1/auth/github/start?next=%2Fpair.html');
    return;
  }
  if (!response.ok) throw new Error('Не удалось проверить вход в аккаунт.');
  const account = await response.json();
  if (!account.user?.id) throw new Error('Не удалось проверить вход в аккаунт.');
  const controller = new AbortController();
  globalThis.addEventListener?.('pagehide', () => controller.abort(), { once: true });
  const visible = await mountPairing({ ownerId: account.user.id, button: submitButton,
    clientName: 'Браузер телефона', signal: controller.signal, setStatus,
    onReady(context) {
      document.getElementById('pair-fingerprint').textContent = context.fingerprintText;
      confirmationNode.hidden = false;
    },
    onWaiting() {
      confirmationNode.hidden = true;
      waitingNode.hidden = false;
      document.querySelector('.pair-panel__step').textContent = '02 / 02';
    },
    onActive() {
      confirmationNode.hidden = true;
      waitingNode.hidden = false;
      waitingNode.querySelector('p').textContent = 'Подключение защищено ключами этого браузера и ПК.';
    }
  });
  if (!visible) throw new Error('Ссылка не найдена. Откройте QR-код или ЛК из приложения на ПК.');
}

main().catch(error => setStatus(error.message || 'Не удалось подключить компьютер.', true));
