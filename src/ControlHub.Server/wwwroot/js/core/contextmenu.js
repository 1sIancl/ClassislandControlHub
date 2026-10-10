/**
 * 右键菜单（#10 界面层）。
 *
 * 用途：设备卡片上右键直接给出这台设备的常用操作，省得「先点开详情 → 再找按钮」。
 *
 * 两个刻意的设计：
 *   1) **菜单项由调用方传入**，本模块不认识任何业务概念。它只负责定位、渲染、收起，
 *      所以设备页、课表页将来都能用同一套。
 *   2) 菜单**贴边时向内收**：不收的话，在屏幕右下角右键弹出的菜单会有一半在视口外，
 *      而用户看不见也点不到。
 */

import { h, clear } from './ui.js?v=90';

let host = null;
let onDocPointerDown = null;
let onDocKeyDown = null;

/**
 * 在屏幕坐标处弹出菜单。
 * @param {number} x 视口横坐标。
 * @param {number} y 视口纵坐标。
 * @param {Array<{label: string, action: Function, danger?: boolean} | '-' | {type: 'separator'}>} items
 *   菜单项；`'-'` 或 `{ type: 'separator' }` 渲染为分隔线。
 */
export function showContextMenu(x, y, items) {
  closeContextMenu();

  if (!host) {
    host = h('div.context-menu', { hidden: true, role: 'menu' });
    document.body.appendChild(host);
  }

  clear(host);
  for (const item of items) {
    if (item === '-' || item?.type === 'separator') {
      host.appendChild(h('div.context-menu-sep'));
      continue;
    }

    host.appendChild(h('button', {
      type: 'button',
      role: 'menuitem',
      class: `context-menu-item${item.danger ? ' danger' : ''}`,
      onClick: () => {
        // 先收起再执行：菜单项的动作多半会弹窗或跳页，
        // 留着菜单会挡住新弹出的东西。
        closeContextMenu();
        item.action();
      },
    }, item.label));
  }

  host.hidden = false;

  // 必须先让它有尺寸再定位，否则算不出「贴边时该向内收多少」。
  const rect = host.getBoundingClientRect();
  host.style.left = `${Math.max(4, Math.min(x, window.innerWidth - rect.width - 8))}px`;
  host.style.top = `${Math.max(4, Math.min(y, window.innerHeight - rect.height - 8))}px`;

  // 用 pointerdown 而不是 click：按下即收起，点在菜单项上时也能正常触发（那也在文档上）。
  onDocPointerDown = (event) => {
    if (host && !host.contains(event.target)) {
      closeContextMenu();
    }
  };
  onDocKeyDown = (event) => {
    if (event.key === 'Escape') {
      closeContextMenu();
    }
  };

  // 延到下一轮再监听：否则「这一次 pointerdown」会立刻把刚开的菜单关掉。
  setTimeout(() => {
    document.addEventListener('pointerdown', onDocPointerDown, true);
    document.addEventListener('keydown', onDocKeyDown, true);
  }, 0);

  // 页面滚动时菜单会「留在原地」，与内容脱节，所以直接收起。
  window.addEventListener('scroll', closeContextMenu, true);
  window.addEventListener('blur', closeContextMenu);
}

/** 收起菜单（重复调用安全）。 */
export function closeContextMenu() {
  if (!host) {
    return;
  }

  host.hidden = true;
  if (onDocPointerDown) {
    document.removeEventListener('pointerdown', onDocPointerDown, true);
    onDocPointerDown = null;
  }

  if (onDocKeyDown) {
    document.removeEventListener('keydown', onDocKeyDown, true);
    onDocKeyDown = null;
  }

  window.removeEventListener('scroll', closeContextMenu, true);
  window.removeEventListener('blur', closeContextMenu);
}
