/**
 * 前端静态检查：找出「用到了、但本文件既没定义也没 import」的**全大写常量名**。
 *
 * 为什么单独做这一项：`sh/check-imports.js` 只查 import 与 export 是否对得上，
 * `node --check` 只管语法——**「用了没定义」这一类它俩都查不出来**，
 * 浏览器里表现为那一块界面直接渲染不出来（`XXX is not defined` 抛在渲染函数里）。
 *
 * 真实案例（2026-10-10）：把「系统设置」一页拆成四个页面时，
 * 搬 `function xxx()` 的脚本漏掉了 `const BACKUP_TYPE_LABELS = {...}`，
 * 结果是备份列表整块空白——语法、构建、import 检查全绿，只有点到那一页才看得见。
 *
 * 误报怎么处理的：先剥掉注释与字符串（大写词常出现在文案里，如「主库 / WAL / SHM」），
 * 再跳过对象字面量的键（`NAME: ...`）与成员访问（`.NAME`）。
 *
 * 用法：
 *   node sh/check-consts.js                    # 默认检查 wwwroot/js
 *   node sh/check-consts.js <某个 js 目录>
 *
 * 退出码：0 = 没发现问题；1 = 有可疑项（可接进 CI）。
 */
const fs = require('fs');
const path = require('path');

const root = process.argv[2]
  || path.join(__dirname, '..', 'src', 'ControlHub.Server', 'wwwroot', 'js');

/** 少数确实来自外部的名字（浏览器 / 协议 / 业务缩写），不作为「未定义」处理。 */
const KNOWN = new Set([
  'JSON', 'URL', 'DOM', 'CSS', 'HTML', 'UUID', 'IP', 'OK', 'CSV', 'JS',
  'HTTP', 'HTTPS', 'UTF', 'UDP', 'NTP', 'WAL', 'SHM', 'VACUUM', 'API', 'AI',
  'TOTP', 'CSES', 'SQLite', 'CSP', 'PUT', 'GET', 'POST', 'DELETE', 'PATCH',
]);

/** 把注释与字符串字面量替换成等长空白（保留换行，行号不变）。 */
function stripNoise(src) {
  return src
    .replace(/\/\*[\s\S]*?\*\//g, (m) => m.replace(/[^\n]/g, ' '))
    .replace(/(^|[^:\\])\/\/[^\n]*/g, (m, p1) => p1 + ' '.repeat(m.length - p1.length))
    .replace(/`(?:\\[\s\S]|[^`\\])*`/g, (m) => m.replace(/[^\n]/g, ' '))
    .replace(/'(?:\\[\s\S]|[^'\\\n])*'/g, (m) => ' '.repeat(m.length))
    .replace(/"(?:\\[\s\S]|[^"\\\n])*"/g, (m) => ' '.repeat(m.length));
}

function walk(dir, out = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) walk(full, out);
    else if (entry.name.endsWith('.js')) out.push(full);
  }
  return out;
}

let problems = 0;
let checked = 0;

for (const file of walk(root)) {
  const src = stripNoise(fs.readFileSync(file, 'utf8'));
  checked += 1;

  const defined = new Set();
  for (const m of src.matchAll(/(?:^|[\n;{])\s*(?:export\s+)?(?:const|let|var|function|class)\s+([A-Za-z_$][\w$]*)/g)) {
    defined.add(m[1]);
  }
  for (const m of src.matchAll(/import\s*\{([^}]+)\}/g)) {
    for (const raw of m[1].split(',')) {
      const name = raw.trim().split(/\s+as\s+/).pop().trim();
      if (name) defined.add(name);
    }
  }
  for (const m of src.matchAll(/(?:const|let)\s*\{([^}]+)\}\s*=/g)) {
    for (const raw of m[1].split(',')) {
      const name = raw.trim().split(':').pop().split('=')[0].trim();
      if (name) defined.add(name);
    }
  }

  const missing = new Map();
  for (const m of src.matchAll(/(?<![\w.$])([A-Z][A-Z0-9_]{2,})(?![\w$])/g)) {
    const name = m[1];
    if (KNOWN.has(name) || defined.has(name)) continue;
    // 对象字面量的键（`KEY: value`）与成员访问（`.KEY`）不算引用。
    const after = src.slice(m.index + name.length);
    if (/^\s*:/.test(after)) continue;
    missing.set(name, (missing.get(name) || 0) + 1);
  }

  if (missing.size > 0) {
    problems += 1;
    console.log(`[可能未定义] ${path.relative(root, file)}：`
      + [...missing].map(([n, c]) => `${n}×${c}`).join(' '));
  }
}

console.log(`检查 ${checked} 个文件，可疑 ${problems} 处。`);
process.exit(problems === 0 ? 0 : 1);
