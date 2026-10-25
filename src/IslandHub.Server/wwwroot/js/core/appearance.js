/**
 * 管理界面外观（#127）：背景 / 玻璃透明度 / 磨砂 / 色调。
 *
 * <p>
 * 这是**按账号**存的个人偏好，不影响其他管理员、也不下发教室端。
 * 存服务端（`/admin/ui-preferences` 的 `appearance` 字段），换台电脑登录同一账号会跟着走；
 * 同时缓存一份到 localStorage —— 页面加载时先按缓存铺上，避免"先白底再变色"的闪一下。
 * </p>
 *
 * <p>
 * 实现方式就是**覆盖 CSS 变量**（`--glass-alpha` / `--glass-blur` / `--glass-tint` 与 body 背景）：
 * 站点级「玻璃材质强度」仍在样式表里当默认值，用户没自定义时不受影响。
 * </p>
 */

import { api } from './api.js?v=151';

/** 缓存键：与账号偏好互为备份（缓存只用于首屏不闪，服务端才是准的）。 */
const CACHE_KEY = 'controlhub.ui.appearance';

/** 默认外观（与 `tokens` / 样式表里的默认值保持一致）。 */
export const DEFAULT_APPEARANCE = Object.freeze({
  background: 'default',
  glassOpacity: 1,
  glassBlur: 0,
  glassTint: 'none',
});

/**
 * 背景预设 → body 的 background 值。
 * <para>`default` 刻意是 `none`：不自定义就保持主题原来那套纯色底，避免"升级完界面突然变了"。</para>
 */
const BACKGROUNDS = {
  default: 'none',
  aurora: 'radial-gradient(ellipse at 0% 0%, rgba(16,185,129,0.18), transparent 55%),'
    + 'radial-gradient(ellipse at 100% 0%, rgba(139,92,246,0.18), transparent 55%),'
    + 'radial-gradient(ellipse at 50% 100%, rgba(236,72,153,0.14), transparent 55%)',
  sunset: 'radial-gradient(ellipse at 25% 25%, rgba(251,146,60,0.20), transparent 55%),'
    + 'radial-gradient(ellipse at 75% 75%, rgba(239,68,68,0.16), transparent 55%)',
  minimal: 'none',
  night: 'linear-gradient(160deg, rgba(8,10,20,0.95), rgba(20,16,40,0.9))',
};

/** 色调 → 叠加在玻璃层上的半透明色。 */
const TINTS = {
  none: 'transparent',
  primary: 'rgba(30, 144, 255, 0.08)',
  warm: 'rgba(251, 146, 60, 0.08)',
  cool: 'rgba(6, 182, 212, 0.08)',
};

const clamp = (value, min, max) => Math.min(max, Math.max(min, value));

/** 把任意输入规整成合法外观（前端也夹一次，免得服务端返回脏数据把界面搞崩）。 */
export function normalize(raw) {
  const source = raw && typeof raw === 'object' ? raw : {};
  return {
    background: Object.hasOwn(BACKGROUNDS, source.background) ? source.background : 'default',
    glassOpacity: clamp(Number(source.glassOpacity ?? DEFAULT_APPEARANCE.glassOpacity), 0.3, 1),
    glassBlur: clamp(Math.round(Number(source.glassBlur ?? 0)), 0, 40),
    glassTint: Object.hasOwn(TINTS, source.glassTint) ? source.glassTint : 'none',
  };
}

/**
 * 应用外观（纯 CSS 变量覆盖，不写库）。
 * @param {object} raw 外观对象。
 */
export function applyAppearance(raw) {
  const appearance = normalize(raw);
  const root = document.documentElement;

  root.style.setProperty('--glass-alpha', String(appearance.glassOpacity));
  root.style.setProperty('--glass-blur', `${appearance.glassBlur}px`);
  root.style.setProperty('--glass-tint', TINTS[appearance.glassTint]);

  const background = BACKGROUNDS[appearance.background];
  if (!background || background === 'none') {
    root.style.removeProperty('--app-background');
  } else {
    root.style.setProperty('--app-background', background);
  }

  // 让 `<html>` 上带个标记：样式表据此决定要不要给 body 铺背景（以及是否加磨砂）。
  root.dataset.appearance = appearance.background;
  return appearance;
}

/** 读缓存（同步，用于首屏）。 */
export function readCachedAppearance() {
  try {
    const raw = localStorage.getItem(CACHE_KEY);
    return raw ? normalize(JSON.parse(raw)) : null;
  } catch {
    // 缓存坏了就当没有：宁可闪一下，也不能因为一段坏 JSON 让整页白屏。
    localStorage.removeItem(CACHE_KEY);
    return null;
  }
}

/** 写缓存（失败无妨，只是下次首屏会闪一下）。 */
function cacheAppearance(appearance) {
  try {
    localStorage.setItem(CACHE_KEY, JSON.stringify(appearance));
  } catch {
    /* 隐私模式 / 配额满：忽略 */
  }
}

/** 首屏应用：先用缓存，没有就用默认值。返回当前生效的外观。 */
export function bootstrapAppearance() {
  return applyAppearance(readCachedAppearance() ?? DEFAULT_APPEARANCE);
}

/** 从服务端拉一次并应用（登录后调用，服务端才是准的）。 */
export async function loadAppearance() {
  try {
    const preferences = await api('/admin/ui-preferences');
    const appearance = normalize(preferences?.appearance ?? DEFAULT_APPEARANCE);
    applyAppearance(appearance);
    cacheAppearance(appearance);
    return appearance;
  } catch {
    // 读不到就保持现状（可能来自缓存），不弹错——外观属于"锦上添花"。
    return readCachedAppearance() ?? DEFAULT_APPEARANCE;
  }
}

/**
 * 保存外观：**先读后合再写**。
 * <para>布局与外观存在同一条偏好里，直接 PUT 外观会把布局清空（反之亦然）。</para>
 */
export async function saveAppearance(raw) {
  const appearance = normalize(raw);
  const current = await api('/admin/ui-preferences');
  await api('/admin/ui-preferences', {
    method: 'PUT',
    body: { layouts: current?.layouts ?? {}, appearance },
  });
  applyAppearance(appearance);
  cacheAppearance(appearance);
  return appearance;
}
