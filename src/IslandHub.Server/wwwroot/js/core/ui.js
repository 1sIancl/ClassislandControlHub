/** 轻量 DOM 构建与通用交互组件，无框架依赖。 */

// 只引「错误码 → 怎么办」的纯映射（#51）：本文件是最底层模块，不能反过来依赖 errors.js。
import { formatErrorText, hasErrorHint } from './error-hints.js?v=155';
// 文案资源（#48）：本文件是**全站共用**的交互件（弹窗 / 提示 / 徽章），
// 所以这里的字符串进资源后收益最大——一次覆盖所有页面。
import { t } from './i18n.js?v=155';

/**
 * 创建元素。
 * @param {string} tag 标签名，支持 `div.card` / `span#id.cls` 简写。
 * @param {object} attrs 属性。`class`、`style`、`on*` 事件、`dataset` 特殊处理。
 * @param {...any} children 子节点（字符串、节点或它们的数组；null/undefined 会被忽略）。
 */
export function h(tag, attrs = {}, ...children) {
  const [name, ...classAndId] = String(tag).split(/(?=[.#])/);
  const el = document.createElement(name || 'div');

  for (const token of classAndId) {
    // `token.length > 1` 不是多余的判断：类名写成 `div.toast.` 这种（末尾多一个点）
    // 会让 classList.add('') 抛 SyntaxError，而异常发生在**构建阶段**，
    // 表现是「这个提示/面板整个不出现」——排查时很难想到是 tag 字符串多了个点。
    if (token.startsWith('.') && token.length > 1) el.classList.add(token.slice(1));
    else if (token.startsWith('#') && token.length > 1) el.id = token.slice(1);
  }

  // 兼容旧写法：第二个参数若非「普通属性对象」（字符串 / Node / 数组），
  // 视为子节点，避免 h('span', '文字') 把字符串当 attrs 遍历导致文字丢失。
  if (attrs !== null && (typeof attrs !== 'object' || Array.isArray(attrs) || attrs instanceof Node)) {
    children.unshift(attrs);
    attrs = {};
  }

  for (const [key, value] of Object.entries(attrs || {})) {
    if (value === undefined || value === null || value === false) continue;
    if (key === 'class' || key === 'className') {
      String(value).split(/\s+/).filter(Boolean).forEach((c) => el.classList.add(c));
    } else if (key === 'style' && typeof value === 'object') {
      Object.assign(el.style, value);
    } else if (key === 'dataset' && typeof value === 'object') {
      Object.assign(el.dataset, value);
    } else if (key === 'html') {
      el.innerHTML = value;
    } else if (key.startsWith('on') && typeof value === 'function') {
      el.addEventListener(key.slice(2).toLowerCase(), guardHandler(value));
    } else if (key === 'value' && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.tagName === 'SELECT')) {
      el.value = value;
    } else if (value === true) {
      el.setAttribute(key, '');
    } else {
      el.setAttribute(key, value);
    }
  }

  append(el, children);
  return el;
}

/** 递归追加子节点。 */
export function append(parent, children) {
  // 容错：调用方可能漏写数组括号、直接传单个节点 / 字符串（历史上这么踩过两次——
  // 仪表盘「自定义」与设备表「自定义列」两个面板因此「点了没反应」，错误只留在控制台里）。
  // 数组路径的行为完全不变。
  const list = (Array.isArray(children) ? children : [children]).flat(Infinity);
  for (const child of list) {
    if (child === null || child === undefined || child === false) continue;
    parent.appendChild(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return parent;
}

// ── 事件处理器兜底 ──────────────────────────────────────────────────────

/**
 * 把错误报给用户（而不是只留在控制台）。
 * <para>「点了没反应」是本项目踩过两次的坑（漏 import、`append()` 收到单个节点），
 * 现场只能靠猜；统一弹一次原因，截图即可定位。</para>
 */
function reportHandlerError(err) {
  // 控制台也留一条：toast 会自己消失，而 `ReferenceError: modal is not defined` 这类
  // **纯前端编程错误**本该在开发/诊断时一眼看到——插件市场的「详情」与「安装到教室」
  // 两个弹窗就因此静默坏了很久，用户只看到一句「操作失败」。
  console.error('[ui] handler failed:', err);

  // 这里是所有「没被单独接住」的操作的实际出口，光转述服务端原文不够用（#51）：
  // 按错误码补一句「怎么办」，带建议的多停留一会儿。
  toast('error', t('ui.actionFailed'), formatErrorText(err), hasErrorHint(err) ? 8000 : 4200);
}

/**
 * 给事件处理器套一层兜底：同步抛错与异步 rejection 都会弹出原因。
 * <para>`h()` 会丢弃处理器的返回值，所以 `onClick: async () => { await api(...) }` 一旦失败
 * 就是未处理的 Promise rejection——界面毫无反应。这里统一接住。</para>
 */
function guardHandler(handler) {
  return (event) => {
    try {
      const result = handler(event);
      if (result && typeof result.catch === 'function') {
        result.catch(reportHandlerError);
      }
      return result;
    } catch (err) {
      reportHandlerError(err);
      return undefined;
    }
  };
}

/**
 * 手动兜底（需要自定义提示文案时用），例如：
 * <code>onClick: () => guard('打开自定义仪表盘', () => openCustomize())</code>。
 * <para>没有它也能被 `h()` 的安全网接住，只是提示文案会退化成通用的「操作失败」。</para>
 */
export function guard(label, action) {
  try {
    const result = action();
    if (result && typeof result.catch === 'function') {
      result.catch((err) => toast('error', t('ui.actionFailedWith', { label }), formatErrorText(err), hasErrorHint(err) ? 8000 : 4200));
    }
    return result;
  } catch (err) {
    toast('error', t('ui.actionFailedWith', { label }), formatErrorText(err), hasErrorHint(err) ? 8000 : 4200);
    return undefined;
  }
}

/** 生成文档片段。 */
export function frag(...children) {
  const f = document.createDocumentFragment();
  append(f, children);
  return f;
}

/** 清空元素。 */
export function clear(el) {
  while (el.firstChild) el.removeChild(el.firstChild);
  return el;
}

/** HTML 转义（用于确需 innerHTML 的场合）。 */
export function escapeHtml(text) {
  return String(text ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}

// ── 时间格式化 ──────────────────────────────────────────────────────────

/** 格式化为 `YYYY-MM-DD HH:mm:ss`。 */
export function formatDateTime(value) {
  if (!value) return '—';
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return '—';
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
}

/** 格式化为 `HH:mm:ss`。 */
export function formatTime(value) {
  if (!value) return '—';
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return '—';
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
}

/** 相对时间，例如「3 分钟前」。 */
export function relativeTime(value) {
  if (!value) return t('ui.never');
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return '—';
  const diff = Math.floor((Date.now() - d.getTime()) / 1000);
  if (diff < 5) return t('ui.justNow');
  if (diff < 60) return t('ui.secondsAgo', { count: diff });
  if (diff < 3600) return t('ui.minutesAgo', { count: Math.floor(diff / 60) });
  if (diff < 86400) return t('ui.hoursAgo', { count: Math.floor(diff / 3600) });
  if (diff < 2592000) return t('ui.daysAgo', { count: Math.floor(diff / 86400) });
  return formatDateTime(value);
}

/** 把秒数格式化为「x 天 y 小时 z 分」。 */
export function formatDuration(seconds) {
  const s = Math.max(0, Math.floor(seconds || 0));
  const days = Math.floor(s / 86400);
  const hours = Math.floor((s % 86400) / 3600);
  const minutes = Math.floor((s % 3600) / 60);
  if (days > 0) return t('ui.durationDays', { days, hours });
  if (hours > 0) return t('ui.durationHours', { hours, minutes });
  if (minutes > 0) return t('ui.durationMinutes', { minutes, seconds: s % 60 });
  return t('ui.durationSeconds', { seconds: s });
}

function pad(n) {
  return String(n).padStart(2, '0');
}

// ── Toast ───────────────────────────────────────────────────────────────

/**
 * 显示浮动提示。
 * @param {'ok'|'warn'|'error'|'info'} type 类型。
 * @param {string} title 标题。
 * @param {string} [body] 正文。
 * @param {number} [timeout] 自动关闭毫秒数，0 表示不自动关闭。
 */
export function toast(type, title, body = '', timeout = 3600) {
  const host = document.getElementById('toastHost');
  if (!host) return;

  // `info` 用默认的 .toast 样式（不加修饰类）：原先写成 `div.toast.${type === 'info' ? '' : type}`，
  // 于是 info 类提示的 tag 是 `div.toast.`——末尾那个点让 classList.add('') 抛异常，
  // **所有 info 提示都显示不出来**（`/` 找不到搜索框时的提示就是其中一个）。
  const el = h(`div.toast${type === 'info' ? '' : `.${type}`}`,
    h('div.toast-title', title),
    body ? h('div.toast-body', body) : null,
  );

  host.appendChild(el);
  const close = () => {
    el.style.opacity = '0';
    el.style.transform = 'translateX(20px)';
    el.style.transition = 'all .18s';
    setTimeout(() => el.remove(), 190);
  };
  el.addEventListener('click', close);
  if (timeout > 0) setTimeout(close, timeout);
}

// ── 弹窗 ────────────────────────────────────────────────────────────────

/**
 * 打开弹窗。
 * @param {{title:string, body:Node|Node[], width?:'wide'|'xwide', confirmText?:string,
 *          cancelText?:string, danger?:boolean, onConfirm?:Function, hideFooter?:boolean,
 *          onClose?:Function}} options
 *   <para><c>onClose</c> 在**任何**关闭路径（确定 / 取消 / ✕ / 点遮罩 / Esc）后都会调用一次，
 *   供调用方做清理（清定时器、复位「已打开」标记等）——只包装返回的 <c>close</c> 是盖不全的。</para>
 * @returns {{close:Function, el:HTMLElement, bodyEl:HTMLElement}}
 */
export function modal(options) {
  const host = document.getElementById('modalHost');
  const bodyEl = h('div.modal-body');
  append(bodyEl, [options.body || []]);

  // 焦点管理（#49）：记住触发弹窗的元素，关掉之后把焦点还回去。
  // 不做这件事的话，键盘用户一关弹窗焦点就「掉」到页面开头，得重新 Tab 一路找回来。
  const opener = document.activeElement instanceof HTMLElement ? document.activeElement : null;

  const close = () => {
    host.hidden = true;
    clear(host);
    document.removeEventListener('keydown', onKey);
    if (opener && document.contains(opener)) {
      try {
        opener.focus();
      } catch {
        // 触发元素已经不在 DOM 里（例如点了删除）——忽略即可。
      }
    }

    try {
      options.onClose?.();
    } catch {
      // 关闭回调里的异常不该影响「窗口已经关掉」这个事实。
    }
  };

  const onKey = (e) => {
    if (e.key === 'Escape') {
      close();
      return;
    }

    if (e.key !== 'Tab') {
      return;
    }

    // 焦点陷阱：Tab 只在弹窗内循环，不会跑到背后的页面上（视觉上「弹窗盖住了页面，
    // 键盘却能点到底下的按钮」是最典型的无障碍缺陷）。
    const focusables = [...modalEl.querySelectorAll(
      'button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])')]
      .filter((el) => !el.disabled && el.offsetParent !== null);

    if (focusables.length === 0) {
      return;
    }

    const first = focusables[0];
    const last = focusables[focusables.length - 1];
    const active = document.activeElement;
    const inside = modalEl.contains(active);

    if (e.shiftKey && (active === first || !inside)) {
      e.preventDefault();
      last.focus();
    } else if (!e.shiftKey && (active === last || !inside)) {
      e.preventDefault();
      first.focus();
    }
  };

  const confirmBtn = h(`button.btn.${options.danger ? 'btn-danger' : 'btn-primary'}`,
    { type: 'button' },
    options.confirmText || t('ui.ok'));

  confirmBtn.addEventListener('click', async () => {
    if (!options.onConfirm) {
      close();
      return;
    }
    confirmBtn.disabled = true;
    const original = confirmBtn.textContent;
    confirmBtn.textContent = t('ui.processing');
    try {
      const result = await options.onConfirm();
      if (result !== false) close();
    } catch (err) {
      // 所有弹窗的「确定」都走这里（保存 / 确认类操作），同样带上「怎么办」（#51）。
      toast('error', t('ui.actionFailed'), formatErrorText(err), hasErrorHint(err) ? 8000 : 4200);
    } finally {
      confirmBtn.disabled = false;
      confirmBtn.textContent = original;
    }
  });

  const modalEl = h(`div.modal${options.width ? '.' + options.width : ''}`,
    {
      role: 'dialog',
      'aria-modal': 'true',
      'aria-label': options.title || t('ui.dialog'),
    },
    h('div.modal-head',
      h('h3', options.title || ''),
      // 纯图标按钮必须有无障碍名（#49）：屏幕阅读器读的是 aria-label，不是里面的 SVG。
      h('button.btn.btn-ghost.btn-sm', {
        type: 'button', title: t('ui.close'), 'aria-label': t('ui.close'), onClick: close,
      }, icon('close', 15)),
    ),
    bodyEl,
    options.hideFooter
      ? null
      : h('div.modal-foot',
        h('button.btn', { type: 'button', onClick: close }, options.cancelText || t('ui.cancel')),
        confirmBtn,
      ),
  );

  host.hidden = false;
  clear(host);
  host.appendChild(modalEl);
  host.onclick = (e) => {
    if (e.target === host) close();
  };
  document.addEventListener('keydown', onKey);

  // 打开时把焦点送进弹窗：有输入框就进输入框，纯文本确认框则给「确定」按钮
  // （否则焦点留在触发按钮上，Tab 会先跑到背景页面去）。
  const firstInput = bodyEl.querySelector('input, select, textarea');
  setTimeout(() => (firstInput ?? confirmBtn).focus(), 60);

  return { close, el: modalEl, bodyEl };
}

/** 确认对话框。 */
export function confirmDialog(title, message, confirmText = t('ui.ok'), danger = false) {
  return new Promise((resolve) => {
    modal({
      title,
      width: 'wide',
      body: h('div', { style: { fontSize: '13.5px', lineHeight: '1.75' } }, message),
      confirmText,
      danger,
      cancelText: t('ui.cancel'),
      onConfirm: () => {
        resolve(true);
        return true;
      },
    });
    // 用户点击取消/关闭时按未确认处理。
    const host = document.getElementById('modalHost');
    const observer = new MutationObserver(() => {
      if (host.hidden) {
        observer.disconnect();
        resolve(false);
      }
    });
    observer.observe(host, { attributes: true, attributeFilter: ['hidden'] });
  });
}

/** 输入框对话框，返回输入值或 null。 */
/**
 * 「撤销窗口」提示条：右下角显示倒计时，点「撤销」即执行回调。
 * <para>用于关机这类不可逆操作——服务端会延迟派发，这段时间内可以取消。</para>
 * @returns {{ close: () => void }}
 */
export function undoBar(title, seconds, onUndo) {
  const host = document.getElementById('toastHost') || document.body;
  const remain = h('span.undo-remain', `${seconds}s`);

  const bar = h('div.undo-bar',
    h('span.undo-title', title),
    remain,
    h('button.btn.btn-sm.btn-primary', {
      type: 'button',
      onClick: async () => {
        clearInterval(timer);
        bar.remove();
        await onUndo();
      },
    }, t('ui.undo')),
    h('button.btn.btn-sm.btn-ghost', {
      type: 'button',
      onClick: () => {
        clearInterval(timer);
        bar.remove();
      },
    }, t('ui.gotIt')),
  );

  let left = seconds;
  const timer = setInterval(() => {
    left -= 1;
    if (left <= 0) {
      clearInterval(timer);
      bar.remove();
      return;
    }

    remain.textContent = `${left}s`;
  }, 1000);

  host.appendChild(bar);
  return {
    close: () => {
      clearInterval(timer);
      bar.remove();
    },
  };
}

export function promptDialog(title, label, defaultValue = '', placeholder = '') {
  return new Promise((resolve) => {
    const input = h('input', { type: 'text', value: defaultValue, placeholder });
    const dialog = modal({
      title,
      width: 'wide',
      body: h('label.field', h('span', label), input),
      confirmText: t('ui.ok'),
      onConfirm: () => {
        resolve(input.value.trim());
        return true;
      },
    });
    const host = document.getElementById('modalHost');
    const observer = new MutationObserver(() => {
      if (host.hidden) {
        observer.disconnect();
        resolve(null);
      }
    });
    observer.observe(host, { attributes: true, attributeFilter: ['hidden'] });
    input.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') {
        e.preventDefault();
        dialog.close();
        resolve(input.value.trim());
      }
    });
  });
}

/** 复制文本到剪贴板。 */
export async function copyText(text, label = t('ui.copied')) {
  try {
    await navigator.clipboard.writeText(text);
    toast('ok', label);
  } catch {
    toast('warn', t('ui.copyFailed'), t('ui.copyBlocked'));
  }
}

// ── 业务展示辅助 ────────────────────────────────────────────────────────

/** 设备状态徽标。 */
export function deviceStateBadge(device) {
  if (device.revoked) {
    return h('span.badge.badge-danger', h('span.dot'), t('ui.stateRevoked'));
  }
  if (!device.online) {
    return h('span.badge.badge-neutral', h('span.dot'), t('ui.stateOffline'));
  }
  switch (device.state) {
    case 'syncing':
      return h('span.badge.badge-info', h('span.dot'), t('ui.stateSyncing'));
    case 'error':
      return h('span.badge.badge-danger', h('span.dot'), t('ui.stateError'));
    default:
      return h('span.badge.badge-ok', h('span.dot'), t('ui.stateOnline'));
  }
}

/** 同步状态徽标。 */
export function syncBadge(device) {
  return device.upToDate
    ? h('span.badge.badge-ok', t('ui.syncUpToDate'))
    : h('span.badge.badge-warn', t('ui.syncPending', { revision: device.appliedRevision }));
}

// ── 图标 ────────────────────────────────────────────────────────────────

const SVG_NS = 'http://www.w3.org/2000/svg';

/** 24×24 视窗的线性图标路径，统一继承 currentColor。 */
const ICON_PATHS = {
  // 导航
  dashboard: 'M3.2 3.2h6.6v6.6H3.2zM14.2 3.2h6.6v6.6h-6.6zM14.2 14.2h6.6v6.6h-6.6zM3.2 14.2h6.6v6.6H3.2z',
  profiles: 'M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8zM14 2v6h6M8 13h8M8 17h5',
  monitor: 'M3.5 5.2h17v11h-17zM9 19.8h6M12 16.2v3.6',
  folder: 'M3.5 7.3A2.3 2.3 0 0 1 5.8 5h3.4l2 2.3h7a2.3 2.3 0 0 1 2.3 2.3v7.1a2.3 2.3 0 0 1-2.3 2.3H5.8a2.3 2.3 0 0 1-2.3-2.3z',
  send: 'M22 2L11 13M22 2l-7 20-4-9-9-4z',
  list: 'M8.5 6h12M8.5 12h12M8.5 18h12M3.5 6h.01M3.5 12h.01M3.5 18h.01',
  bell: 'M18 8.6a6 6 0 1 0-12 0c0 6-2.4 7.4-2.4 7.4h16.8S18 14.6 18 8.6zM10.3 20.2a2 2 0 0 0 3.4 0',
  help: 'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18zM9.6 9.4a2.5 2.5 0 1 1 3.4 2.3c-.7.3-1 .9-1 1.6v.3M12 17h.01',
  gear: 'M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09a1.65 1.65 0 0 0-1-1.51 1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09a1.65 1.65 0 0 0 1.51-1 1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33h.01a1.65 1.65 0 0 0 1-1.51V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51h.01a1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82v.01a1.65 1.65 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z',

  // 状态
  checkCircle: 'M12 3a9 9 0 1 1 0 18a9 9 0 0 1 0-18M8.5 12l2.5 2.5 4.5-5',
  clock: 'M12 3a9 9 0 1 1 0 18a9 9 0 0 1 0-18M12 7.4v5l3.4 2',
  alert: 'M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0zM12 9v4M12 17h.01',
  plusCircle: 'M12 3a9 9 0 1 1 0 18a9 9 0 0 1 0-18M12 8v8M8 12h8',
  inbox: 'M4.5 5.5h15v13h-15zM4.5 13.5H9l1.5 2.5h3l1.5-2.5h4.5',
  search: 'M11 4.2a6.8 6.8 0 1 1 0 13.6a6.8 6.8 0 0 1 0-13.6M20.2 20.2l-4.6-4.6',
  // 天气：一朵云（配一个太阳）——比再引一套图标库划算。
  cloud: 'M6.8 18.5h10.9a3.6 3.6 0 0 0 .3-7.2 5.4 5.4 0 0 0-10.3-1.2 4.2 4.2 0 0 0-.9 8.4zM8.2 6.6 7.4 4.4M12 5.6V3.2M15.8 6.6l.8-2.2',
  link: 'M10.2 13.8a4 4 0 0 0 5.6 0l2.9-2.9a4 4 0 0 0-5.6-5.6l-1 1M13.8 10.2a4 4 0 0 0-5.6 0l-2.9 2.9a4 4 0 0 0 5.6 5.6l1-1',
  clipboard: 'M9.5 3.5h5v2.7h-5zM8.2 4.6H6.5A1.5 1.5 0 0 0 5 6.1v12.6a1.5 1.5 0 0 0 1.5 1.5h11a1.5 1.5 0 0 0 1.5-1.5V6.1a1.5 1.5 0 0 0-1.5-1.5h-1.7',
  database: 'M12 3.2c4.4 0 8 1.1 8 2.5s-3.6 2.5-8 2.5-8-1.1-8-2.5 3.6-2.5 8-2.5M4 5.7v12.6c0 1.4 3.6 2.5 8 2.5s8-1.1 8-2.5V5.7M4 12c0 1.4 3.6 2.5 8 2.5s8-1.1 8-2.5',
  book: 'M19 20.5H6.6A2.6 2.6 0 0 1 4 17.9V6.1A2.6 2.6 0 0 1 6.6 3.5H19zM4 17.9a2.6 2.6 0 0 1 2.6-2.6H19',
  key: 'M8 18.5a4.2 4.2 0 1 1 0-8.4 4.2 4.2 0 0 1 0 8.4M11.3 12.9L20 4.2M16.8 4.2H20v3.2',
  chart: 'M4.5 19.5V9.5M9.8 19.5v-15M15.2 19.5v-6.4M20.5 19.5V7.6M3.5 21h17',
  user: 'M20 20.5v-1.8a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v1.8M12 11.2a4 4 0 1 0 0-8 4 4 0 0 0 0 8',

  // 外观
  menu: 'M3.5 7h17M3.5 12h17M3.5 17h17',
  close: 'M6.5 6.5l11 11M17.5 6.5l-11 11',
  check: 'M5 12.5l4.5 4.5L19 7',
  sun: 'M12 7.8a4.2 4.2 0 1 1 0 8.4 4.2 4.2 0 0 1 0-8.4M12 2.6v2.1M12 19.3v2.1M2.6 12h2.1M19.3 12h2.1M5.4 5.4l1.5 1.5M17.1 17.1l1.5 1.5M18.6 5.4l-1.5 1.5M6.9 17.1l-1.5 1.5',
  moon: 'M20.4 13.3A8.4 8.4 0 1 1 10.7 3.6a6.7 6.7 0 0 0 9.7 9.7z',
  auto: 'M12 3.2a8.8 8.8 0 1 1 0 17.6 8.8 8.8 0 0 1 0-17.6M12 3.2v17.6',
  square: 'M5.8 5.8h12.4v12.4H5.8z',
  rows: 'M4 6.8h16M4 12h16M4 17.2h16',
};

/** 生成一个线性图标。`name` 未命中时回退到 inbox。 */
export function icon(name, size = 18) {
  const svg = document.createElementNS(SVG_NS, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('width', String(size));
  svg.setAttribute('height', String(size));
  svg.setAttribute('fill', 'none');
  svg.setAttribute('stroke', 'currentColor');
  svg.setAttribute('stroke-width', '1.6');
  svg.setAttribute('stroke-linecap', 'round');
  svg.setAttribute('stroke-linejoin', 'round');
  svg.setAttribute('aria-hidden', 'true');

  const path = document.createElementNS(SVG_NS, 'path');
  path.setAttribute('d', ICON_PATHS[name] || ICON_PATHS.inbox);
  svg.appendChild(path);
  return svg;
}

/**
 * 空状态块。
 * @param {string} name 图标名。
 * @param {string} title 标题。
 * @param {string} [description] 一句话说明。
 * @param {Node} [action] 行动按钮（如「生成注册码」）。
 * @param {string[]} [steps] 分步引导：**说清「接下来做什么」比只说「这里没有」有用得多**。
 */
export function emptyState(name, title, description, action, steps = null) {
  return h('div.empty',
    h('div.empty-icon', icon(name, 26)),
    h('h3', title),
    description ? h('p', description) : null,
    steps && steps.length
      ? h('ol.empty-steps', ...steps.map((step) => h('li', step)))
      : null,
    action || null,
  );
}

/**
 * 骨架屏占位（列表加载时用，替代转圈）。
 * <para>与「转圈」的区别：骨架屏能预告「马上会出现几行、多大的内容」，
 * 数据到达时页面不跳动，观感更稳。行数取常见首屏行数，避免加载完成后高度突变。</para>
 * @param {number} [rows] 行数。
 */
export function skeletonRows(rows = 6) {
  return h('div.skeleton-rows',
    ...Array.from({ length: rows }, () => h('div.skeleton.skeleton-row')));
}

/** 加载占位。 */
export function loadingBlock(text = t('ui.loading')) {
  return h('div.empty',
    h('div', { style: { marginBottom: '12px' } }, h('span.spinner')),
    h('p', text),
  );
}

/** 构建表单字段。 */
export function field(label, control, hint) {
  return h('label.field',
    h('span', label),
    control,
    hint ? h('span.hint', hint) : null,
  );
}

/**
 * 字段标签旁的「?」帮助图标（#50）。
 *
 * <para>**为什么不能只靠 `title`**：鼠标悬停看得到，手机上没有悬停——所以点击（或键盘回车）
 * 也会把同一段说明完整弹出来。用途是「较长的说明」：与其在表单里堆一行灰字，
 * 不如收起成图标，需要时再看（短说明继续走 `field()` 的第三个参数）。</para>
 *
 * <para>用法：<c>field(h('span', '字段名', hintIcon('说明')), 控件)</c>——不必改 `field()` 本身，
 * 全站 160 多处调用点因此不受影响。</para>
 */
export function hintIcon(text) {
  if (!text) {
    return null;
  }

  const open = (event) => {
    event.preventDefault();
    event.stopPropagation();
    modal({
      title: t('ui.help'),
      confirmText: t('ui.gotIt'),
      body: h('div', { style: { lineHeight: '1.75', fontSize: '13px' } }, text),
    });
  };

  return h('span.field-help', {
    role: 'img',
    'aria-label': t('ui.helpFor', { text }),
    title: text,
    tabIndex: '0',
    onClick: open,
    onKeyDown: (event) => {
      if (event.key === 'Enter' || event.key === ' ') {
        open(event);
      }
    },
  }, '?');
}

/**
 * 「标签 : 值」的信息行（服务器信息 / 账号信息这类只读卡片里到处在用）。
 *
 * <para>`value` 允许是**节点**（如「已开启 TOTP」徽标）：节点直接挂上去。
 * 早先的实现在这里写死了 `String(value)`，于是「两步验证」那一行在浏览器里
 * 显示成 `[object HTMLSpanElement]`——接口正常、控制台干净，只有肉眼能发现。</para>
 *
 * @param {string} label 左侧标签。
 * @param {any} value 右侧内容（字符串 / 数字 / 节点）。
 * @param {boolean} [mono] 是否用等宽字体（路径、密钥这类）。
 */
export function kvRow(label, value, mono = false) {
  const box = h('span', {
    style: {
      textAlign: 'right',
      wordBreak: 'break-all',
      fontFamily: mono ? 'var(--mono)' : 'inherit',
      fontSize: mono ? '12px' : 'inherit',
    },
  });

  if (value instanceof Node) box.appendChild(value);
  else box.textContent = String(value ?? '—');

  return h('div', { style: { display: 'flex', justifyContent: 'space-between', gap: '16px', alignItems: 'baseline' } },
    h('span', { style: { color: 'var(--text-dim)', flex: 'none' } }, label),
    box,
  );
}

/** 构建下拉框。 */
export function select(options, value, onChange, placeholder) {
  const el = h('select', onChange ? { onChange: (e) => onChange(e.target.value) } : {});
  if (placeholder !== undefined) {
    el.appendChild(h('option', { value: '' }, placeholder));
  }
  for (const opt of options) {
    const o = h('option', { value: opt.value }, opt.label);
    if (String(opt.value) === String(value ?? '')) o.selected = true;
    el.appendChild(o);
  }
  return el;
}

/** 组合操作按钮组。 */
export function actionButtons(buttons) {
  return h('div.card-actions', ...buttons.filter(Boolean));
}
