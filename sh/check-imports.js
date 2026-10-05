/**
 * 前端模块 import / export 一致性检查。
 *
 * 检查三类问题（都属于「构建通过、浏览器里才炸」的问题）：
 *   1. **导入的文件不存在**：路径写错，加载时 404。
 *   2. **导入的名字没有被导出**：`SyntaxError: does not provide an export named 'x'`，模块整体加载失败。
 *   3. **同一文件里重复导入同一个名字**：`Identifier 'x' has already been declared`，整个页面白屏。
 *      注意 `node --check` **查不出**这一类（它不做绑定层面的检查，实测返回 0），只能靠这里或浏览器。
 *
 * 另有一类「用了没 import」的 `ReferenceError`（本项目踩过：settings.js 漏导入 `select`，
 * 导致「新建 Webhook 按钮点不开」藏了很久）本脚本**查不出来**——它需要作用域分析。
 * 那一类由 `sh/ui-smoke.js`（真实浏览器加载页面）兜底，别删那个测试。
 *
 * 用法：
 *   node sh/check-imports.js                       # 默认检查 src/ControlHub.Server/wwwroot/js
 *   node sh/check-imports.js <某个 js 目录>
 *
 * 退出码：0 = 全部一致；1 = 发现问题（可接进 CI）。
 */
const fs = require('fs');
const path = require('path');

const root = process.argv[2]
  || path.join(__dirname, '..', 'src', 'ControlHub.Server', 'wwwroot', 'js');

/** 收集一个模块导出的所有名字（function / const / let / var / class / export {...}）。 */
function exportedNames(file) {
  const src = fs.readFileSync(file, 'utf8');
  const names = new Set();
  for (const m of src.matchAll(/export\s+(?:async\s+)?function\s+(\w+)/g)) names.add(m[1]);
  for (const m of src.matchAll(/export\s+(?:const|let|var)\s+(\w+)/g)) names.add(m[1]);
  for (const m of src.matchAll(/export\s+class\s+(\w+)/g)) names.add(m[1]);
  for (const m of src.matchAll(/export\s*\{([^}]+)\}/g)) {
    for (const part of m[1].split(',')) {
      const alias = part.trim().split(/\s+as\s+/);
      const name = (alias[1] || alias[0] || '').trim();
      if (name) names.add(name);
    }
  }
  return names;
}

/** 第 index 个字符所在的行号（从 1 开始），用于报错定位。 */
function lineOf(src, index) {
  return src.slice(0, index).split('\n').length;
}

/**
 * 找出同一文件里重复导入的绑定名。
 * <para>覆盖 `import { a, b as c } from` 与 `import ns from` 两种写法（含跨行的大括号形式）。</para>
 */
function duplicateBindings(src) {
  const seen = new Map();
  const dupes = [];
  for (const m of src.matchAll(/^import\s+(?:\{([^}]*)\}|(\w+))\s+from/gm)) {
    const names = m[1]
      ? m[1].split(',').map((x) => x.trim().split(/\s+as\s+/).pop().trim()).filter(Boolean)
      : [m[2]];
    for (const name of names) {
      if (seen.has(name)) {
        dupes.push(`${name}（第 ${seen.get(name)} 行与第 ${lineOf(src, m.index)} 行）`);
      } else {
        seen.set(name, lineOf(src, m.index));
      }
    }
  }
  return dupes;
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

if (!fs.existsSync(root)) {
  console.error(`目录不存在：${root}`);
  process.exit(1);
}

for (const file of walk(root)) {
  const src = fs.readFileSync(file, 'utf8');

  for (const dupe of duplicateBindings(src)) {
    console.log(`[重复导入] ${path.relative(root, file)}：${dupe}`);
    problems++;
  }

  for (const m of src.matchAll(/import\s*\{([^}]+)\}\s*from\s*'([^']+)'/g)) {
    const rawNames = m[1].split(',').map((x) => x.trim()).filter(Boolean);
    const target = m[2].split('?')[0];
    if (!target.startsWith('.')) continue;
    checked++;

    const targetPath = path.resolve(path.dirname(file), target);
    if (!fs.existsSync(targetPath)) {
      console.log(`[缺失文件] ${path.relative(root, file)} -> ${target}`);
      problems++;
      continue;
    }

    const available = exportedNames(targetPath);
    for (const raw of rawNames) {
      const name = raw.split(/\s+as\s+/)[0].trim();
      if (!available.has(name)) {
        console.log(`[未导出] ${path.relative(root, file)} 导入的 ${name} 不在 ${target} 中`);
        problems++;
      }
    }
  }
}

console.log(`检查 ${checked} 条 import 语句，问题 ${problems} 处。`);
process.exit(problems === 0 ? 0 : 1);
