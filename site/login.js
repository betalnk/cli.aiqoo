const errorMessages = {
  github_denied: 'Вход отменён. Можно попробовать ещё раз.',
  verified_email_required: 'GitHub не передал подтверждённую почту. Проверьте адрес в настройках GitHub и повторите вход.',
  invalid_state: 'Срок попытки входа истёк. Начните вход заново.',
  github_unavailable: 'Не удалось связаться с GitHub. Повторите попытку позже.'
};

const errorCode = new URLSearchParams(location.search).get('error');
const errorNode = document.getElementById('login-error');
if (errorNode && errorCode) {
  errorNode.textContent = errorMessages[errorCode] || 'Не удалось войти. Начните вход заново.';
  errorNode.hidden = false;
}
