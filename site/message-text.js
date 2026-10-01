const encoder = new TextEncoder();

export function validMessageText(text) {
  if (typeof text !== 'string' || !text.trim() || encoder.encode(text).length > 8192) return false;
  for (const character of text) {
    const point = character.codePointAt(0);
    if ((point < 32 && ![9, 10, 13].includes(point)) || (point >= 127 && point <= 159)
        || (point >= 0xd800 && point <= 0xdfff)) return false;
  }
  return true;
}
