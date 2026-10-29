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

import { api, fetchBlob } from './api.js?v=156';

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
  // 图片走 --app-background: url(...)，不在这里拼（没有上传过就什么都不显示）。
  image: 'none',
};

/**
 * 个人背景图的接口路径（需登录；服务端按账号返回自己那张）。
 * <para>⚠️ 这里是**相对 API 前缀**的路径：`api()` / `fetchBlob()` 都会自己加 `/api/v1`，
 * 写成完整地址会变成 `/api/v1/api/v1/...` → 404（实测踩过）。</para>
 */
export const BACKGROUND_IMAGE_URL = '/admin/ui-preferences/background-image';

/**
 * 背景图为什么要先取成 blob 再显示：
 * <para>
 * CSS 的 `url(…)` 请求**不带 Authorization 头**，直接写这个地址会 401（实测踩过）。
 * 所以用 `fetchBlob()`（带 token）取回二进制，转成 `blob:` 地址再挂到 `--app-background`。
 * 顺带的好处：图不落 HTTP 缓存，换图后刷新立刻是新图。
 * </para>
 */
let currentAppearance = null;
let backgroundBlobUrl = null;
let backgroundLoading = false;

/** 丢掉缓存的 blob（上传/删除后调用，避免继续显示旧图）。 */
export function invalidateBackgroundBlob() {
  if (backgroundBlobUrl) {
    URL.revokeObjectURL(backgroundBlobUrl);
    backgroundBlobUrl = null;
  }
}

/** 取一次背景图（并发调用只发一次请求）。 */
async function ensureBackgroundBlob() {
  if (backgroundBlobUrl) {
    return backgroundBlobUrl;
  }

  if (backgroundLoading) {
    return null;
  }

  backgroundLoading = true;
  try {
    const blob = await fetchBlob(BACKGROUND_IMAGE_URL);
    backgroundBlobUrl = URL.createObjectURL(blob);
    // 取回来后按**当前生效的外观**重放一次。
    // ⚠️ 不要用 `readCachedAppearance()`：localStorage 缓存可能还没跟上（比如偏好刚被接口改过），
    //    用它会把刚设上的图片又冲成默认背景（实测踩过）。
    if (currentAppearance) {
      applyAppearance(currentAppearance);
    }

    return backgroundBlobUrl;
  } catch {
    // 没上传过（404）或读不到：保持默认背景即可，这不算故障，不打扰用户。
    return null;
  } finally {
    backgroundLoading = false;
  }
}

/**
 * 系统是否要求"减少透明"。
 * <para>
 * 命中时**忽略用户设的透明度与磨砂**，强制不透明——这是无障碍偏好，优先级高于外观喜好
 * （半透明 + 磨砂在低视力/晕动症用户那里是真的看不清、真的难受）。
 * </para>
 */
function prefersReducedTransparency() {
  try {
    return window.matchMedia('(prefers-reduced-transparency: reduce)').matches;
  } catch {
    return false;
  }
}

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
  // 记下"当前生效的外观"：背景图是异步取回来的，取回后要按这份重放，
  // 而不是按 localStorage 缓存（缓存可能还没跟上，会把刚设的图冲掉）。
  currentAppearance = appearance;
  const root = document.documentElement;

  // 无障碍优先：系统要求减少透明时，一律按不透明、无磨砂渲染。
  const reduced = prefersReducedTransparency();
  const opacity = reduced ? 1 : appearance.glassOpacity;
  const blur = reduced ? 0 : appearance.glassBlur;

  root.style.setProperty('--glass-alpha', String(opacity));
  root.style.setProperty('--glass-blur', `${blur}px`);
  root.style.setProperty('--glass-tint', reduced ? TINTS.none : TINTS[appearance.glassTint]);

  if (appearance.background === 'image') {
    if (backgroundBlobUrl) {
      root.style.setProperty('--app-background', `url("${backgroundBlobUrl}")`);
    } else {
      // 还没取到：先不铺背景（否则会露出 401 的占位），同时触发一次取图。
      root.style.removeProperty('--app-background');
      void ensureBackgroundBlob();
    }
  } else {
    const background = BACKGROUNDS[appearance.background];
    if (!background || background === 'none') {
      root.style.removeProperty('--app-background');
    } else {
      root.style.setProperty('--app-background', background);
    }
  }

  // 让 `<html>` 上带个标记：样式表据此决定要不要给 body 铺背景（以及是否加磨砂）。
  root.dataset.appearance = appearance.background;
  root.dataset.reducedTransparency = reduced ? 'true' : 'false';
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
 * 上传个人背景图（#127 补完）。成功与否都要让调用方知道，所以不吞异常。
 * @param {File} file 浏览器里选的文件。
 */
export async function uploadBackgroundImage(file) {
  const form = new FormData();
  form.append('file', file);
  const result = await api(BACKGROUND_IMAGE_URL, { method: 'POST', body: form });
  // 换了图：丢掉旧 blob，下次应用时重新取。
  invalidateBackgroundBlob();
  return result;
}

/** 删除个人背景图（幂等；删完把 background 拨回默认由调用方负责）。 */
export async function deleteBackgroundImage() {
  const result = await api(BACKGROUND_IMAGE_URL, { method: 'DELETE' });
  invalidateBackgroundBlob();
  return result;
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
