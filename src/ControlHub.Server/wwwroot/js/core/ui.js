/**
 * 轻量 DOM 构建与通用交互组件。
 * 不引入任何框架：h() 负责生成元素，toast/modal 提供统一反馈，
 * 使各视图代码保持声明式且便于维护。
 */

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
    if (token.startsWith('.')) el.classList.add(token.slice(1));
    else if (token.startsWith('#')) el.id = token.slice(1);
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
      el.addEventListener(key.slice(2).toLowerCase(), value);
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
  for (const child of children.flat(Infinity)) {
    if (child === null || child === undefined || child === false) continue;
    parent.appendChild(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return parent;
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
  if (!value) return '从未';
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return '—';
  const diff = Math.floor((Date.now() - d.getTime()) / 1000);
  if (diff < 5) return '刚刚';
  if (diff < 60) return `${diff} 秒前`;
  if (diff < 3600) return `${Math.floor(diff / 60)} 分钟前`;
  if (diff < 86400) return `${Math.floor(diff / 3600)} 小时前`;
  if (diff < 2592000) return `${Math.floor(diff / 86400)} 天前`;
  return formatDateTime(value);
}

/** 把秒数格式化为「x 天 y 小时 z 分」。 */
export function formatDuration(seconds) {
  const s = Math.max(0, Math.floor(seconds || 0));
  const days = Math.floor(s / 86400);
  const hours = Math.floor((s % 86400) / 3600);
  const minutes = Math.floor((s % 3600) / 60);
  if (days > 0) return `${days} 天 ${hours} 小时`;
  if (hours > 0) return `${hours} 小时 ${minutes} 分`;
  if (minutes > 0) return `${minutes} 分 ${s % 60} 秒`;
  return `${s} 秒`;
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

  const el = h(`div.toast.${type === 'info' ? '' : type}`,
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
 *          cancelText?:string, danger?:boolean, onConfirm?:Function, hideFooter?:boolean}} options
 * @returns {{close:Function, el:HTMLElement, bodyEl:HTMLElement}}
 */
export function modal(options) {
  const host = document.getElementById('modalHost');
  const bodyEl = h('div.modal-body');
  append(bodyEl, [options.body || []]);

  const close = () => {
    host.hidden = true;
    clear(host);
    document.removeEventListener('keydown', onKey);
  };

  const onKey = (e) => {
    if (e.key === 'Escape') close();
  };

  const confirmBtn = h('button.btn',
    options.danger ? 'btn-danger' : 'btn-primary',
    { type: 'button' },
    options.confirmText || '确定',
  );

  confirmBtn.addEventListener('click', async () => {
    if (!options.onConfirm) {
      close();
      return;
    }
    confirmBtn.disabled = true;
    const original = confirmBtn.textContent;
    confirmBtn.textContent = '处理中…';
    try {
      const result = await options.onConfirm();
      if (result !== false) close();
    } catch (err) {
      toast('error', '操作失败', err.message || String(err));
    } finally {
      confirmBtn.disabled = false;
      confirmBtn.textContent = original;
    }
  });

  const modalEl = h(`div.modal${options.width ? '.' + options.width : ''}`,
    h('div.modal-head',
      h('h3', options.title || ''),
      h('button.btn.btn-ghost.btn-sm', { type: 'button', onClick: close }, '✕'),
    ),
    bodyEl,
    options.hideFooter
      ? null
      : h('div.modal-foot',
        h('button.btn', { type: 'button', onClick: close }, options.cancelText || '取消'),
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

  const firstInput = bodyEl.querySelector('input, select, textarea');
  if (firstInput) setTimeout(() => firstInput.focus(), 60);

  return { close, el: modalEl, bodyEl };
}

/** 确认对话框。 */
export function confirmDialog(title, message, confirmText = '确定', danger = false) {
  return new Promise((resolve) => {
    modal({
      title,
      width: 'wide',
      body: h('div', { style: { fontSize: '13.5px', lineHeight: '1.75' } }, message),
      confirmText,
      danger,
      cancelText: '取消',
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
export function promptDialog(title, label, defaultValue = '', placeholder = '') {
  return new Promise((resolve) => {
    const input = h('input', { type: 'text', value: defaultValue, placeholder });
    const dialog = modal({
      title,
      width: 'wide',
      body: h('label.field', h('span', label), input),
      confirmText: '确定',
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
export async function copyText(text, label = '已复制') {
  try {
    await navigator.clipboard.writeText(text);
    toast('ok', label);
  } catch {
    toast('warn', '复制失败', '当前浏览器不允许访问剪贴板，请手动选择复制。');
  }
}

// ── 业务展示辅助 ────────────────────────────────────────────────────────

/** 设备状态徽标。 */
export function deviceStateBadge(device) {
  if (device.revoked) {
    return h('span.badge.badge-danger', h('span.dot'), '已停用');
  }
  if (!device.online) {
    return h('span.badge.badge-neutral', h('span.dot'), '离线');
  }
  switch (device.state) {
    case 'syncing':
      return h('span.badge.badge-info', h('span.dot'), '同步中');
    case 'error':
      return h('span.badge.badge-danger', h('span.dot'), '异常');
    default:
      return h('span.badge.badge-ok', h('span.dot'), '在线');
  }
}

/** 同步状态徽标。 */
export function syncBadge(device) {
  return device.upToDate
    ? h('span.badge.badge-ok', '已是最新')
    : h('span.badge.badge-warn', `待同步 · 版本 ${device.appliedRevision}`);
}

/** 空状态块。 */
export function emptyState(icon, title, description, action) {
  return h('div.empty',
    h('div.empty-icon', icon),
    h('h3', title),
    description ? h('p', description) : null,
    action || null,
  );
}

/** 加载占位。 */
export function loadingBlock(text = '正在加载…') {
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
