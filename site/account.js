import { removeWorkspaceDevice, startWorkspace } from './workspace.js';

const statusNode = document.getElementById('account-status');
const devicesStatus = document.getElementById('devices-status');
const deviceList = document.getElementById('device-list');
const logoutButton = document.getElementById('logout');

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
    startWorkspace(data.user.id, devices);
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
