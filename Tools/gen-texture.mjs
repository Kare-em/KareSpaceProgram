/*
 * Генерация текстуры по описанию через NordRouter (как tools/ в Car_Train_Simulator).
 *   node Tools/gen-texture.mjs <выход.png|jpg> "<промпт>"
 *
 * Ключ — переменная окружения image_generator_api (или IMAGE_API_KEY); в лог не печатается.
 * Модель тайлинг не гарантирует — шов снимает Tools/make-tileable.py.
 */
import { mkdir, writeFile } from 'node:fs/promises';
import { dirname } from 'node:path';

const BASE = (process.env.IMAGE_API_BASE || 'https://nordrouter.com/v1').replace(/\/+$/, '');
const KEY = process.env.image_generator_api || process.env.IMAGE_API_KEY || '';
const MODEL = process.env.IMAGE_MODEL || 'google/gemini-3.1-flash-image-preview';

const scrub = (s) => (KEY ? String(s).split(KEY).join('sk-***') : String(s));
const fail = (m) => { console.error('ОШИБКА:', scrub(m)); process.exit(1); };

const [out, prompt] = process.argv.slice(2);
if (!out || !prompt) fail('использование: node Tools/gen-texture.mjs <выход> "<промпт>"');
if (!KEY) fail('не задан ключ: ожидается image_generator_api в окружении');

const t0 = Date.now();
const res = await fetch(`${BASE}/images/generations`, {
  method: 'POST',
  headers: { Authorization: `Bearer ${KEY}`, 'Content-Type': 'application/json' },
  body: JSON.stringify({ model: MODEL, prompt, n: 1 }),
  signal: AbortSignal.timeout(180_000)
}).catch((e) => fail(`сервис недоступен: ${e.message}`));
const g = await res.json().catch(() => null);
if (!res.ok) fail(`HTTP ${res.status}: ${JSON.stringify(g).slice(0, 300)}`);
const item = g?.data?.[0];
const bytes = item?.b64_json
  ? Buffer.from(item.b64_json, 'base64')
  : item?.url ? Buffer.from(await (await fetch(item.url)).arrayBuffer()) : null;
if (!bytes?.length) fail('ответ без картинки');
await mkdir(dirname(out), { recursive: true });
await writeFile(out, bytes);
console.log(`готово: ${out}, ${bytes.length} байт, ${Date.now() - t0} мс`);
