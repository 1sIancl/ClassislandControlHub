/**
 * UI 偏好管理：主题 / 布局 / 仪表盘模块 / 表格列。
 *
 * 所有自定义功能都在此统一读写 localStorage，前缀 `controlhub.ui.`。
 * 作用范围说明：
 *   - 主题（system | light | dark）  全局，作用于整个界面（CSS 变量 + data-theme）。
 *   - 布局（侧边栏折叠、密度）        全局外壳（app-shell / content）。
 *   - 模块（仪表盘卡片显隐与顺序）     仅「仪表盘」页。
 *   - 字段（表格列显隐）              仅对应表格页（devices / profiles 等）。
 */

const PREFIX = 'controlhub.ui.';

const store = {
  get(key, fallback) {
    try {
      const raw = localStorage.getItem(PREFIX + key);
      if (raw === null) return fallback;
      return JSON.parse(raw);
    } catch {
      return fallback;
    }
  },
  set(key, value) {
    try {
      localStorage.setItem(PREFIX + key, JSON.stringify(value));
    } catch {
      /* 隐私模式等场景静默降级 */
    }
  },
};

const mqLight = window.matchMedia('(prefers-color-scheme: light)');

// ────────────────────────────── 主题 ──────────────────────────────

export const THEMES = [
  { key: 'system', label: '跟随系统', icon: '◐' },
  { key: 'light', label: '浅色', icon: '☀' },
  { key: 'dark', label: '深色', icon: '☾' },
];

export function getTheme() {
  return store.get('theme', 'system');
}

function resolveTheme(theme) {
  if (theme === 'system') {
    return mqLight.matches ? 'light' : 'dark';
  }
  return theme;
}

export function applyTheme(theme) {
  store.set('theme', theme);
  const resolved = resolveTheme(theme);
  document.documentElement.dataset.theme = resolved;
  const meta = document.querySelector('meta[name="color-scheme"]');
  if (meta) meta.setAttribute('content', resolved);
  return resolved;
}

export function initTheme() {
  applyTheme(getTheme());
  // 仅在「跟随系统」模式下响应系统主题切换。
  mqLight.addEventListener('change', () => {
    if (getTheme() === 'system') applyTheme('system');
  });
}

// ────────────────────────────── 布局 ──────────────────────────────

export function getSidebarCollapsed() {
  return store.get('sidebarCollapsed', false);
}
export function setSidebarCollapsed(value) {
  store.set('sidebarCollapsed', !!value);
  document.getElementById('app')?.classList.toggle('collapsed', !!value);
}

export const DENSITIES = [
  { key: 'comfort', label: '舒适' },
  { key: 'dense', label: '紧凑' },
];

export function getDensity() {
  return store.get('density', 'comfort');
}
export function applyDensity() {
  document.body.classList.toggle('dense', getDensity() === 'dense');
}
export function setDensity(value) {
  store.set('density', value);
  applyDensity();
}

// ────────────────────────── 模块 / 列配置（通用） ──────────────────────────
//
// 约定数据结构：数组，元素为 `{ key, enabled, label }` 或纯字符串 key。
// 顺序即展示顺序。

export function getLayout(scope, defaults) {
  const saved = store.get(`layout.${scope}`, null);
  if (!Array.isArray(saved) || saved.length === 0) {
    return defaults.map((d) => (typeof d === 'string' ? { key: d, enabled: true } : { ...d, enabled: d.enabled !== false }));
  }
  // 合并：新增默认项补到末尾，保留已保存的显隐与顺序。
  const map = new Map(saved.map((s) => [s.key, s]));
  const merged = [];
  for (const d of saved) {
    merged.push(d);
  }
  for (const d of defaults) {
    const key = typeof d === 'string' ? d : d.key;
    if (!map.has(key)) {
      merged.push({ key, enabled: true, label: typeof d === 'string' ? key : d.label });
    }
  }
  return merged;
}

export function saveLayout(scope, items) {
  store.set(`layout.${scope}`, items);
}

// ────────────────────────── 外观（强调色 / 字体 / 圆角） ──────────────────────────

export const ACCENTS = [
  { key: 'dodger', label: 'DodgerBlue（CI 默认）', value: '#1e90ff' },
  { key: 'purple', label: '紫罗兰', value: '#7c5cff' },
  { key: 'green', label: '青竹', value: '#16b364' },
  { key: 'orange', label: '暖橙', value: '#f0883e' },
  { key: 'pink', label: '樱粉', value: '#e8578f' },
  { key: 'graphite', label: '石墨', value: '#64748b' },
];

export const FONTS = [
  { key: 'default', label: '系统默认', value: '' },
  { key: 'yahei', label: '微软雅黑', value: '"Microsoft YaHei UI", "Microsoft YaHei"' },
  { key: 'pingfang', label: '苹方 / 思源黑体', value: '"PingFang SC", "Noto Sans CJK SC", "Source Han Sans SC"' },
  { key: 'mono', label: '等宽字体', value: '"Cascadia Mono", Consolas, "Courier New", monospace' },
];

export const RADII = [
  { key: 'sharp', label: '直角' },
  { key: 'default', label: '默认' },
  { key: 'round', label: '圆润' },
];

function hexToRgba(hex, alpha) {
  const m = /^#?([0-9a-f]{6})$/i.exec(String(hex).trim());
  if (!m) return `rgba(30,144,255,${alpha})`;
  const n = parseInt(m[1], 16);
  return `rgba(${(n >> 16) & 255},${(n >> 8) & 255},${n & 255},${alpha})`;
}

export function getAccent() {
  return store.get('accent', '');
}

export function applyAccent() {
  const accent = getAccent();
  const root = document.documentElement;
  if (!accent) {
    root.style.removeProperty('--accent');
    root.style.removeProperty('--accent-hover');
    root.style.removeProperty('--accent-active');
    root.style.removeProperty('--accent-soft');
    root.style.removeProperty('--accent-line');
    return;
  }
  root.style.setProperty('--accent', accent);
  root.style.setProperty('--accent-hover', accent);
  root.style.setProperty('--accent-active', accent);
  root.style.setProperty('--accent-soft', hexToRgba(accent, 0.14));
  root.style.setProperty('--accent-line', hexToRgba(accent, 0.5));
}

export function setAccent(value) {
  store.set('accent', value);
  applyAccent();
}

export function getFont() {
  return store.get('font', '');
}

export function applyFont() {
  const font = getFont();
  document.body.style.fontFamily = font
    ? `${font}, "Segoe UI", system-ui, -apple-system, sans-serif`
    : '';
}

export function setFont(value) {
  store.set('font', value);
  applyFont();
}

export function getRadius() {
  return store.get('radius', 'default');
}

export function applyRadius() {
  const r = getRadius();
  const map = {
    sharp: ['3px', '5px', '7px'],
    default: ['6px', '8px', '12px'],
    round: ['10px', '14px', '20px'],
  };
  const [sm, md, lg] = map[r] || map.default;
  const root = document.documentElement;
  root.style.setProperty('--radius-sm', sm);
  root.style.setProperty('--radius', md);
  root.style.setProperty('--radius-lg', lg);
}

export function setRadius(value) {
  store.set('radius', value);
  applyRadius();
}

/** 一次性应用全部外观偏好（应用启动早期调用，避免闪烁）。 */
export function applyAppearance() {
  applyAccent();
  applyFont();
  applyRadius();
}
