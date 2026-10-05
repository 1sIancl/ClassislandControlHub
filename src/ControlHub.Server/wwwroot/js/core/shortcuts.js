/**
 * 键盘快捷键（#46）与快捷键帮助面板（#50）。
 *
 * 设计取舍：
 *   - **跳页用 Alt + 数字**，不用 `g` 前缀：数字好记，Alt 组合也几乎不会和输入冲突；
 *     序号按侧边栏顺序、只算当前账号有权限的页面（无权限的页面根本不在导航里）。
 *   - 只在「焦点不在输入框、没有弹窗打开」时响应，免得抢走正常输入；
 *     Esc 仍由弹窗自己的逻辑处理（见 core/ui.js 的 modal）。
 *   - `?` 打开帮助面板：**快捷键必须能被发现**，否则等于没做——侧边栏每个页面也带悬停提示。
 */

import { h, modal, toast } from './ui.js?v=56';
import { openSearch } from './search.js?v=56';

/** 快捷键清单：帮助面板与侧边栏提示共用这一处，避免两边写得不一致。 */
const SHORTCUTS = [
  { keys: 'Ctrl + K', desc: '全局搜索：一个关键字同时查设备 / 分组 / 档案 / 账号（#41）' },
  { keys: 'Alt + 1 … 9', desc: '跳到侧边栏第 N 个页面（只算你当前有权限的页面）' },
  { keys: '/', desc: '聚焦本页搜索框（设备管理 / 审计日志）' },
  { keys: '?', desc: '打开这份快捷键帮助（Shift + /）' },
  { keys: 'Esc', desc: '关闭弹窗、取消当前操作' },
  { keys: 'Enter', desc: '在弹出的对话框里确认（跟「确定」按钮等效）' },
];

/** 当前可见的导航项（顺序与侧边栏一致）。 */
function visibleNavItems() {
  return [...document.querySelectorAll('.nav-item')].filter((el) => el.offsetParent !== null);
}

/** 焦点是否落在可输入元素上——此时不该抢快捷键。 */
function isTyping(target) {
  if (!target) return false;
  const tag = target.tagName;
  return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || target.isContentEditable === true;
}

/** 是否已有弹窗打开（打开时只让 Esc 生效）。 */
function modalOpen() {
  const host = document.getElementById('modalHost');
  return !!host && host.hidden === false;
}

/** `?`：快捷键帮助面板。 */
export function openShortcutHelp() {
  modal({
    title: '键盘快捷键',
    width: 'wide',
    hideFooter: true,
    body: h('div',
      h('div.config-panel', ...SHORTCUTS.map((s) => h('div.config-item',
        h('kbd.shortcut-key', s.keys),
        h('span.item-label', s.desc),
      ))),
      h('div.notice.notice-info', { style: { marginTop: '14px' } },
        h('span.notice-icon', 'i'),
        h('div', '侧边栏每个页面也带快捷键提示，鼠标悬停即可看到。'),
      ),
    ),
  });
}

/** `/`：聚焦本页搜索框。找不到就如实说一声，别让人以为快捷键坏了。 */
function focusSearch() {
  const box = document.querySelector('.toolbar-search, input[type="search"], input[type="text"][placeholder*="搜索"]');
  if (!box || box.offsetParent === null) {
    toast('info', '本页没有搜索框', '设备管理、审计日志页可以用 / 直接定位搜索框。', 4000);
    return;
  }
  box.focus();
  if (typeof box.select === 'function') {
    box.select();
  }
}

/** 注册全局快捷键（由 app.js 的外壳初始化调用一次）。 */
export function initShortcuts() {
  window.addEventListener('keydown', (event) => {
    // Ctrl / Cmd + K 是「命令面板」的通用约定：即使焦点正在某个输入框里也应该响应
    // （用户在页面搜索框里想换成全局搜索是很自然的动作），所以这一条放在 isTyping 之前。
    if ((event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLowerCase() === 'k') {
      event.preventDefault();
      openSearch();
      return;
    }

    if (isTyping(event.target)) return;

    // Esc 交给弹窗自己的处理（core/ui.js 的 modal 已监听），这里不重复。
    if (event.key === 'Escape') return;

    // 弹窗打开时，除 Esc 外不响应其它快捷键，避免在对话框里误跳页。
    if (modalOpen()) return;

    if (event.key === '?' || (event.shiftKey && event.key === '/')) {
      event.preventDefault();
      openShortcutHelp();
      return;
    }

    if (event.key === '/' && !event.ctrlKey && !event.metaKey && !event.altKey) {
      event.preventDefault();
      focusSearch();
      return;
    }

    if (event.altKey && !event.ctrlKey && !event.metaKey && /^[1-9]$/.test(event.key)) {
      const item = visibleNavItems()[Number(event.key) - 1];
      if (!item) return;
      event.preventDefault();
      item.click();
    }
  });
}

/** 供 renderNav 使用：告诉某个导航项是第几个（从 1 开始）。 */
export function shortcutHint(index) {
  return `Alt + ${index}`;
}
