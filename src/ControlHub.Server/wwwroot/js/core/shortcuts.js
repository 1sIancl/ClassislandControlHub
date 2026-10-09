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

import { h, modal, toast } from './ui.js?v=82';
import { openSearch } from './search.js?v=82';

/**
 * 由 app.js 注入的「当前账号可见的页面清单」。
 * <para>刻意用注入而不是让 shortcuts.js 自己 import app.js：那样会形成循环依赖
 * （app → shortcuts → app），在 ES module 下这类环往往表现为「某个模块拿到 undefined」，
 * 排查起来很费时间。</para>
 */
let listPages = () => [];

/** app.js 在启动时调用一次，传入页面清单。 */
export function setPageLister(fn) {
  listPages = typeof fn === 'function' ? fn : () => [];
}

/** 快捷键清单：帮助面板与侧边栏提示共用这一处，避免两边写得不一致。 */
const SHORTCUTS = [
  { keys: 'Ctrl + K', desc: '命令与搜索面板：空闲时列出命令，输入后同时搜命令与设备 / 分组 / 档案 / 账号' },
  { keys: 'Alt + 1 … 9', desc: '跳到侧边栏第 N 个页面（只算你当前有权限的页面）' },
  { keys: 'N', desc: '发送通知（跳到设备管理并预置「在线」筛选）' },
  { keys: 'P', desc: '下发配置' },
  { keys: '/', desc: '聚焦本页搜索框（设备管理 / 审计日志）' },
  { keys: 'J / K', desc: '在当前列表里上下移动（设备卡片、设备表格行）' },
  { keys: 'X', desc: '勾选 / 取消勾选当前行（配合底部批量操作条）' },
  { keys: 'Enter', desc: '打开当前行（设备详情 / 编辑）' },
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
      // 页面清单由 app.js 传入（它才有 NAV 与权限判断）。
      // 这里不能自己从 DOM 反查：顶栏导航的页面项收在**下拉里**，
      // 收起时 offsetParent 为 null，会被 visibleNavItems() 过滤掉——
      // 结果就是「有时列得出页面，有时列不出」。
      openSearch(listPages());
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
      // 用注入的页面清单而不是 visibleNavItems()：
      // 两级导航后 .nav-item 同时存在于顶栏下拉与侧边栏，按 DOM 查会拿两份、序号错位；
      // 侧边栏又只显示当前分组（例如只有 3 项），Alt+4..9 会凭空失效。
      const page = listPages()[Number(event.key) - 1];
      if (!page) return;
      event.preventDefault();
      window.location.hash = page.hash;
      return;
    }

    // 列表导航：J/K 上下移动、X 勾选、Enter 打开。
    // 刻意**不给列表项加 tabindex**、也不真的 focus：那要改每一处渲染，
    // 而用「高亮 + 一个模块级下标」就能达到同样的效果，且不会干扰 Tab 顺序。
    if (!isTyping(event.target) && !event.ctrlKey && !event.metaKey && !event.altKey) {
      const key = event.key.toLowerCase();

      // N / P：与命令面板（core/search.js 里 ACTION_COMMANDS）里显示的键位**必须一致**。
      // 面板上写着「发送通知 N」而按 N 没反应，比不写这个提示更糟——用户会以为面板坏了。
      if (key === 'n') {
        event.preventDefault();
        window.location.hash = '#/devices?status=online';
        return;
      }

      if (key === 'p') {
        event.preventDefault();
        window.location.hash = '#/deploy';
        return;
      }

      if (key === 'j' || key === 'k') {
        // 当前页没有可导航项时**什么都不做**（更不该吃掉这次按键）。
        if (moveNavCursor(key === 'j' ? 1 : -1)) {
          event.preventDefault();
        }

        return;
      }

      if (key === 'x') {
        if (toggleNavCursor()) {
          event.preventDefault();
        }

        return;
      }

      if (event.key === 'Enter' && activateNavCursor()) {
        event.preventDefault();
      }
    }
  });
}

/**
 * 列表导航选择器：设备看板的教室卡片（`.dchip`，见 devices.js）与设备表格行。
 * <para>类名写死在这里是个小隐患——改了 devices.js 的类名这里会静默失效。
 * 但为它引入一层间接（页面注册「我可被导航的元素」）不值得：
 * 失效时的表现是「按 J 没反应」，不是错数据。</para>
 */
const NAV_SELECTOR = '.dchip, table.data tbody tr';

/** 当前高亮项在列表中的下标；-1 表示还没开始导航。 */
let navCursor = -1;

function navItems() {
  return [...document.querySelectorAll(NAV_SELECTOR)]
    // 过滤掉不可见的（分页/筛选后仍在 DOM 里但隐藏的行），
    // 否则「J」会跳到看不见的地方，看起来像失灵。
    .filter((el) => el.offsetParent !== null);
}

/** 移动高亮；返回是否真的移动了（没有可导航项时为 false）。 */
function moveNavCursor(delta) {
  const items = navItems();
  if (items.length === 0) {
    return false;
  }

  navCursor = Math.min(Math.max(navCursor + delta, 0), items.length - 1);
  items.forEach((el, i) => el.classList.toggle('nav-current', i === navCursor));
  items[navCursor].scrollIntoView({ block: 'nearest' });
  return true;
}

/** 勾选 / 取消勾选当前行；返回是否处理了。 */
function toggleNavCursor() {
  const item = navItems()[navCursor];
  if (!item) {
    return false;
  }

  // 勾选框优先：表格行里的复选框才是「选中」的真相来源。
  const box = item.matches('tr') ? item.querySelector('input[type="checkbox"]') : null;
  if (box) {
    box.click();
    return true;
  }

  item.click();
  return true;
}

/** 打开当前行：优先点行内的「编辑」按钮，没有就点这一行本身。 */
function activateNavCursor() {
  const item = navItems()[navCursor];
  if (!item) {
    return false;
  }

  // 看板卡片（.dchip）要发 **dblclick** 而不是 click：
  // 卡片的单击语义是「选中」（见 devices.js 的 deviceChip），双击才是「打开详情」。
  // 早先这里发 click 是正确的，卡片改成交互语义后不跟着改，Enter 就会变成「选中」——
  // 按键行为悄悄改变，用户只会觉得「Enter 坏了」。
  if (item.classList.contains('dchip')) {
    item.dispatchEvent(new MouseEvent('dblclick', { bubbles: true, cancelable: true }));
    return true;
  }

  const action = [...item.querySelectorAll('button')]
    .find((b) => /编辑|详情|查看/.test(b.textContent));
  (action || item).click();
  return true;
}

/** 供 renderNav 使用：告诉某个导航项是第几个（从 1 开始）。 */
export function shortcutHint(index) {
  return `Alt + ${index}`;
}
