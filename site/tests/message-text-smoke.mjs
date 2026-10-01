import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
const source = readFileSync(new URL('../message-text.js', import.meta.url), 'utf8');
const { validMessageText } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
for (const text of ['Текст с (кавычками): "да"!', 'строка 1\nстрока 2', 'строка 1\r\n\tстрока 2',
  'console.log("Привет🙂"); && $value', '👩🏽‍💻 👨‍👩‍👧‍👦', 'почта user@example.test', '<div>{value}</div>']) {
  assert.equal(validMessageText(text), true, text);
}
for (const text of ['', ' \n\t', 'a'.repeat(8193), '\0', '\x1b[0m', '\ud800', '\udc00']) {
  assert.equal(validMessageText(text), false);
}
assert.equal(validMessageText('a'.repeat(8192)), true);
assert.equal(validMessageText('🙂'.repeat(2048)), true);
assert.equal(validMessageText('🙂'.repeat(2049)), false);
console.log('MESSAGE_TEXT_SMOKE_OK');
