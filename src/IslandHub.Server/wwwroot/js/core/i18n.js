/**
 * 界面文案的读取与切换（路线图 #48）。
 *
 * 三个设计取舍，都是踩过才知道的：
 *   ① **字典用静态 import**（就两个文件、几十条）：导航表是模块加载时构建的，异步载字典会让它
 *      先渲染成空文字再回填，肉眼能看见闪一下。
 *   ② **切换语言靠重载页面**：文案在启动时被取用（导航表、静态 HTML、各页面），重载是唯一能保证
 *      三处一致的作法；只重画一部分会出现「侧栏英文、页面中文」这种半截状态。
 *   ③ **缺键回退中文**：翻译做一半时不会出现空白或键名，先把外壳翻完就能用。
 */
import zhCN from '../locales/zh-CN.js?v=149';
import enUS from '../locales/en-US.js?v=149';

const DICTS = { 'zh-CN': zhCN, 'en-US': enUS };
const FALLBACK = 'zh-CN';
const STORAGE_KEY = 'controlhub.ui.locale';

let current = null;

/** 当前语言代码（读一次就缓存）。 */
export function getLocale() {
  if (current) {
    return current;
  }

  let saved = null;
  try {
    saved = localStorage.getItem(STORAGE_KEY);
  } catch {
    // 隐私模式下读不了 localStorage：用默认语言就是了。
  }

  current = Object.hasOwn(DICTS, saved || '') ? saved : FALLBACK;
  return current;
}

/** 可选语言列表（给下拉框用）。 */
export function availableLocales() {
  return Object.entries(DICTS).map(([code, dict]) => ({ code, label: dict['lang.name'] || code }));
}

/**
 * 取文案。`{name}` 占位符用 `params` 替换。
 * <para>缺键先回退中文，再缺就返回键名本身——键名露在界面上比空白更容易被发现。</para>
 */
export function t(key, params) {
  const dict = DICTS[getLocale()] || DICTS[FALLBACK];
  const template = dict[key] ?? DICTS[FALLBACK][key] ?? key;
  if (!params) {
    return template;
  }

  return template.replace(/\{(\w+)\}/g, (match, name) => (
    params[name] === undefined ? match : String(params[name])
  ));
}

/** 切换语言（重载页面，见文件头②）。 */
export function setLocale(code) {
  if (!Object.hasOwn(DICTS, code)) {
    return;
  }

  try {
    localStorage.setItem(STORAGE_KEY, code);
  } catch {
    // 写不进去也没关系：本次会话看不到效果，但不会崩。
  }

  window.location.reload();
}

/**
 * 给静态 HTML 填文案：`data-i18n`（文字）、`data-i18n-placeholder`、`data-i18n-title`、`data-i18n-aria`。
 * <para>动态生成的界面用 `t()` 直接取值，不走这里。</para>
 */
export function applyStatic(root = document) {
  root.querySelectorAll('[data-i18n]').forEach((el) => {
    el.textContent = t(el.dataset.i18n);
  });
  root.querySelectorAll('[data-i18n-placeholder]').forEach((el) => {
    el.placeholder = t(el.dataset.i18nPlaceholder);
  });
  root.querySelectorAll('[data-i18n-title]').forEach((el) => {
    el.title = t(el.dataset.i18nTitle);
  });
  root.querySelectorAll('[data-i18n-aria]').forEach((el) => {
    el.setAttribute('aria-label', t(el.dataset.i18nAria));
  });
  document.documentElement.lang = getLocale();
}

/** 把语言下拉框装好并接上切换。 */
export function mountLocalePicker(select) {
  if (!select) {
    return;
  }

  select.replaceChildren(...availableLocales().map(({ code, label }) => {
    const option = document.createElement('option');
    option.value = code;
    option.textContent = label;
    return option;
  }));
  select.value = getLocale();
  select.addEventListener('change', () => setLocale(select.value));
}
