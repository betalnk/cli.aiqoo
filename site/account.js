import { removeWorkspaceDevice, startWorkspace } from './workspace.js';
import { captureFragment } from './pairing.js';
import { mountPairing } from './pairing-ui.js';

let fragmentError = null;
try { captureFragment(); }
catch (error) { fragmentError = error; }

const statusNode = document.getElementById('account-status');
const devicesStatus = document.getElementById('devices-status');
const deviceList = document.getElementById('device-list');
const logoutButton = document.getElementById('logout');

async function setupBinding(ownerId, devices) {
  const section = document.getElementById('account-pairing');
  const status = document.getElementById('bind-status');
  const button = document.getElementById('bind-pc');
  const controller = new AbortController();
  globalThis.addEventListener?.('pagehide', () => controller.abort(), { once: true });
  const setStatus = (message, error = false) => {
    section.hidden = false;
    status.textContent = message;
    status.classList.toggle('account-inline-status--error', error);
  };
  try {
    if (fragmentError) throw fragmentError;
    const visible = await mountPairing({ ownerId, button, setStatus, signal: controller.signal,
      clientName: 'Браузер ЛК',
      onReady() {
        if (devices?.length === 0) document.getElementById('workspace-status').textContent =
          'Сначала привяжите этот ПК и подтвердите запрос в приложении.';
      },
      onWaiting() {
        if (devices?.length === 0) document.getElementById('workspace-status').textContent =
          'Ожидаем «ОК» на ПК. После подтверждения здесь появятся сессии.';
      },
      onActive: (_record, { alreadyActive }) => {
        if (alreadyActive) return;
        // Workspace starts once per page; reload only after the active key is saved.
        location.reload();
      }
    });
    section.hidden = !visible;
  } catch (error) {
    button.hidden = true;
    setStatus(error.message || 'Не удалось проверить запрос. Откройте ЛК из приложения на ПК заново.', true);
  }
}

function showDevicesStatus(message, error = false) {
  devicesStatus.textContent = message;
  devicesStatus.classList.toggle('account-inline-status--error', error);
  devicesStatus.hidden = false;
}

function deviceRow(device) {
  const row = document.createElement('div');
  row.className = 'device-row';
  const detail = document.createElement('div');
  const title = document.createElement('strong');
  title.textContent = device.deviceName;
  const id = document.createElement('span');
  id.textContent = `ID · ${device.deviceId.slice(-8)}`;
  detail.append(title, id);
  const button = document.createElement('button');
  button.className = 'device-row__revoke';
  button.type = 'button';
  button.textContent = 'Отключить';
  button.setAttribute('aria-label', `Отключить ${device.deviceName}`);
  button.addEventListener('click', async () => {
    if (!confirm(`Отключить «${device.deviceName}» от этого аккаунта?`)) return;
    button.disabled = true;
    try {
      const response = await fetch(`/api/v1/devices/${encodeURIComponent(device.deviceId)}`, {
        method: 'DELETE', credentials: 'same-origin', cache: 'no-store'
      });
      if (!response.ok) throw new Error('revoke-failed');
      row.remove();
      removeWorkspaceDevice(device.deviceId);
      if (!deviceList.childElementCount) {
        deviceList.hidden = true;
        showDevicesStatus('Подключённых компьютеров нет.');
      }
    } catch {
      showDevicesStatus('Не удалось отключить компьютер. Повторите попытку.', true);
      button.disabled = false;
    }
  });
  row.append(detail, button);
  return row;
}

async function loadDevices() {
  try {
    const response = await fetch('/api/v1/devices', { credentials: 'same-origin', cache: 'no-store' });
    if (!response.ok) throw new Error('devices-unavailable');
    const data = await response.json();
    if (!Array.isArray(data.devices)) throw new Error('invalid-devices');
    deviceList.replaceChildren();
    for (const device of data.devices) {
      if (typeof device.deviceId !== 'string' || typeof device.deviceName !== 'string') continue;
      deviceList.append(deviceRow(device));
    }
    deviceList.hidden = !deviceList.childElementCount;
    if (deviceList.childElementCount) devicesStatus.hidden = true;
    else showDevicesStatus('Подключённых компьютеров нет.');
    return data.devices;
  } catch {
    showDevicesStatus('Не удалось загрузить компьютеры. Обновите страницу.', true);
    return null;
  }
}

async function loadAccount() {
  try {
    const response = await fetch('/api/v1/me', { credentials: 'same-origin', cache: 'no-store' });
    if (response.status === 401) {
      location.replace('/login.html');
      return;
    }
    if (!response.ok) throw new Error('account-unavailable');
    const data = await response.json();
    if (!data.user?.id || !data.user?.email) throw new Error('invalid-account');
    document.getElementById('account-github').textContent = data.user.githubLogin || '—';
    document.getElementById('account-email').textContent = data.user.email;
    document.getElementById('identity').hidden = false;
    document.getElementById('devices').hidden = false;
    document.getElementById('workspace').hidden = false;
    statusNode.hidden = true;
    logoutButton.disabled = false;
    const devices = await loadDevices();
    await startWorkspace(data.user.id, devices);
    await setupBinding(data.user.id, devices);
  } catch {
    statusNode.textContent = 'Не удалось загрузить аккаунт. Проверьте соединение и обновите страницу.';
    statusNode.classList.add('form-message--error');
  }
}

logoutButton?.addEventListener('click', async () => {
  logoutButton.disabled = true;
  try {
    const response = await fetch('/api/v1/logout', { method: 'POST', credentials: 'same-origin' });
    if (!response.ok) throw new Error('logout-failed');
    location.replace('/login.html');
  } catch {
    statusNode.hidden = false;
    statusNode.textContent = 'Не удалось выйти. Повторите попытку.';
    statusNode.classList.add('form-message--error');
    logoutButton.disabled = false;
  }
});

loadAccount();
