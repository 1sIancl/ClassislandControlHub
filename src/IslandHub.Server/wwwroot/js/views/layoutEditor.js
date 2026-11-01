/**
 * 时间表编辑器（**独立页面**，不依附任何档案）。
 *
 * <p>
 * 时间表是全局实体（`/admin/time-layouts/{id}`）：一份作息只有一条记录，
 * 多个档案的课表引用它。编辑它不需要先选档案，也不该藏在档案编辑器里。
 * </p>
 *
 * <p>
 * 类型选择刻意**不用每行内嵌的 `<select>`**：那东西窄、样式跟表格对不上，
 * 四个类型还挤在一起看不清。改成「点一下单元格 → 弹出一块铺满的菜单」，
 * 一次看清四个类型并各自带颜色，点哪个体力成本都一样。
 * </p>
 */

import { api } from '../core/api.js?v=170';
import { toastError, errorBlock } from '../core/errors.js?v=170';
import {
  h, clear, toast, loadingBlock, confirmDialog,
} from '../core/ui.js?v=170';
import { t } from './../core/i18n.js?v=170';
// 时间点的附加信息（插件附加设置 / 行动组）编辑：时间表独立后仍然要能编，
// 所以和档案编辑器共用同一套弹窗，而不是各自复制一份。
import {
  openAttachedDialog, openActionSetDialog, attachedCountOf, actionCountOf,
} from './layoutItemDialogs.js?v=170';

export const meta = {
  title: t('layoutEditor.title'),
  subtitle: t('layoutEditor.subtitle'),
};

const KIND_ORDER = ['class', 'break', 'separator', 'action'];

function kindLabels() {
  return {
    class: t('profiles.editor.kindClass'),
    break: t('profiles.editor.kindBreak'),
    separator: t('profiles.editor.kindSeparator'),
    action: t('profiles.editor.kindAction'),
  };
}

function genId() {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }

  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0;
    const v = c === 'x' ? r : ((r & 0x3) | 0x8);
    return v.toString(16);
  });
}

const isPointKind = (kind) => kind === 'separator' || kind === 'action';

/** `08:45` → 525（分钟）；解析不出来返回 null。 */
function minutesOf(text) {
  const [hour, minute] = String(text ?? '').split(':').map((x) => Number(x));
  return Number.isFinite(hour) && Number.isFinite(minute) ? hour * 60 + minute : null;
}

/** 时长（分钟），零时长点或数据不全时返回 null。 */
function durationOf(item) {
  const start = minutesOf(item.startTime);
  const end = minutesOf(item.endTime);
  return start === null || end === null ? null : end - start;
}

export async function render(container, params) {
  clear(container);
  container.appendChild(loadingBlock(t('layoutEditor.loading')));

  let layout;
  try {
    layout = await api(`/admin/time-layouts/${params.layoutId}`);
  } catch (error) {
    clear(container);
    container.appendChild(errorBlock(error, t('layoutEditor.loadFailed')));
    return;
  }

  const draft = {
    id: layout.id ?? params.layoutId,
    name: layout.name ?? '',
    items: (layout.items ?? []).map((item) => ({ ...item })),
  };

  clear(container);

  const nameInput = h('input', {
    type: 'text',
    value: draft.name,
    style: { width: '100%', maxWidth: '280px' },
  });
  const listHost = h('div');
  const statsHost = h('div.cell-sub', { style: { margin: '8px 0 6px' } });
  const dirtyMark = h('span.cell-sub');

  const markDirty = () => {
    dirtyMark.textContent = t('layoutEditor.unsaved');
  };

  // ── 铺满式类型菜单 ──

  let openMenu = null;

  /** 关掉当前打开的菜单（点外面、按 Esc、选完都走这里）。 */
  function closeMenu() {
    if (!openMenu) {
      return;
    }

    openMenu.remove();
    openMenu = null;
    document.removeEventListener('click', onDocumentClick, true);
    document.removeEventListener('keydown', onEsc, true);
  }

  function onDocumentClick(event) {
    if (openMenu && !openMenu.contains(event.target)) {
      closeMenu();
    }
  }

  function onEsc(event) {
    if (event.key === 'Escape') {
      closeMenu();
    }
  }

  /**
   * 在按钮下方弹出一块**铺满按钮宽度、至少 180px** 的类型菜单。
   *
   * <p>
   * 为什么不用 `<select>`：原生下拉在表格里只有窄窄一条，四个类型（上课/课间/分割线/行动）
   * 放一起既看不出区别、也不好点；这里每个选项带自己的颜色块，选中的打勾，
   * 一次就能看清"这一列有哪几种"。
   * </p>
   */
  function openKindMenu(anchor, current, onPick) {
    closeMenu();

    const labels = kindLabels();
    const menu = h('div.kind-menu');

    for (const kind of KIND_ORDER) {
      const option = h('button.kind-menu-item' + (kind === current ? '.active' : ''), {
        type: 'button',
        onClick: () => {
          closeMenu();
          if (kind !== current) {
            onPick(kind);
          }
        },
      }, h('span.kind-dot', { dataset: { kind } }), h('span', labels[kind]));

      menu.appendChild(option);
    }

    document.body.appendChild(menu);

    // 覆盖按钮宽度，但不窄于 180px；贴到视口底部时向上翻，避免被裁掉。
    const rect = anchor.getBoundingClientRect();
    const width = Math.max(rect.width, 180);
    menu.style.minWidth = `${width}px`;

    const menuHeight = menu.offsetHeight || 160;
    const below = window.innerHeight - rect.bottom;
    menu.style.left = `${Math.min(rect.left, window.innerWidth - width - 8)}px`;
    menu.style.top = below < menuHeight + 8
      ? `${Math.max(8, rect.top - menuHeight - 4)}px`
      : `${rect.bottom + 4}px`;

    openMenu = menu;
    // 捕获阶段监听：菜单自己的点击先执行完 closeMenu，不会被这里立刻关掉。
    setTimeout(() => {
      document.addEventListener('click', onDocumentClick, true);
      document.addEventListener('keydown', onEsc, true);
    }, 0);
  }

  /** 类型单元格：一个带色块的按钮，点开就是上面的菜单。 */
  function kindCell(item) {
    const labels = kindLabels();
    return h('button.kind-chip', {
      type: 'button',
      dataset: { kind: item.kind ?? 'class' },
      onClick: (event) => {
        event.stopPropagation();
        openKindMenu(event.currentTarget, item.kind ?? 'class', (kind) => {
          item.kind = kind;
          if (isPointKind(kind)) {
            item.endTime = item.startTime;
          }

          markDirty();
          paintRows();
        });
      },
    }, h('span.kind-dot', { dataset: { kind: item.kind ?? 'class' } }), labels[item.kind ?? 'class']);
  }

  /** 开始 / 结束时间。分割线与行动只有一个时间，结束框跟随开始且禁用。 */
  function timeCell(item, which) {
    const point = isPointKind(item.kind);
    const input = h('input', {
      type: 'text',
      value: (which === 'start' || point ? item.startTime : item.endTime) ?? '',
      placeholder: '08:00',
      style: { width: '78px' },
    });

    if (which === 'end' && point) {
      input.disabled = true;
    }

    input.oninput = () => {
      const value = input.value.trim();
      if (which === 'start') {
        item.startTime = value;
        if (isPointKind(item.kind)) {
          item.endTime = value;
        }
      } else {
        item.endTime = value;
      }

      markDirty();
      paintStats();
    };

    return input;
  }

  function nameCell(item) {
    return h('input', {
      type: 'text',
      value: item.displayName || item.breakName || '',
      placeholder: t('layoutEditor.namePlaceholder'),
      style: { width: '100%' },
      onInput: (event) => {
        const text = event.target.value.trim() || null;
        item.displayName = text;
        if (item.kind === 'break') {
          item.breakName = text;
        }

        markDirty();
      },
    });
  }

  /**
   * 附加信息单元格：行动组（仅行动类型）+ 插件附加设置。
   *
   * <p>
   * 时间点这些数据是**跟随时间点**的，不是档案的——所以时间表独立成页面之后，
   * 编辑入口必须一起搬过来，否则「升到全局库」等于把附加信息变成只读。
   * </p>
   */
  function extrasCell(item) {
    const buttons = [];

    if (item.kind === 'action') {
      const count = actionCountOf(item);
      buttons.push(h('button.btn.btn-sm', {
        type: 'button',
        title: t('profiles.editor.actionHint'),
        onClick: () => openActionSetDialog(item, markDirty),
      }, t('layoutEditor.actionBadge', { count })));
    }

    const attached = attachedCountOf(item);
    buttons.push(h('button.btn.btn-sm' + (attached > 0 ? '' : '.btn-ghost'), {
      type: 'button',
      title: t('profiles.editor.attachedHint', { count: attached }),
      onClick: () => openAttachedDialog(item, markDirty),
    }, attached > 0
      ? t('profiles.editor.attachedBadge', { count: attached })
      : t('layoutEditor.attachAdd')));

    return h('div', { style: { display: 'flex', gap: '6px', flexWrap: 'wrap' } }, ...buttons);
  }

  function hideCell(item) {
    const box = h('input', { type: 'checkbox' });
    box.checked = Boolean(item.isHideDefault);
    box.onchange = () => {
      item.isHideDefault = box.checked;
      markDirty();
    };

    return h('label.checkbox-field', box);
  }

  function removeCell(index) {
    return h('button.btn.btn-sm.btn-ghost', {
      type: 'button',
      title: t('layoutEditor.removeHint'),
      onClick: async () => {
        const ok = await confirmDialog(
          t('layoutEditor.remove'),
          t('layoutEditor.removeConfirm', { index: index + 1 }),
          t('profiles.editor.delete'),
          true,
        );
        if (!ok) {
          return;
        }

        draft.items.splice(index, 1);
        markDirty();
        paintRows();
      },
    }, t('profiles.editor.delete'));
  }

  /** 顶部统计：一眼看清这张表里有多少节课、总时长多少。 */
  function paintStats() {
    const classes = draft.items.filter((item) => item.kind === 'class').length;
    const breaks = draft.items.filter((item) => item.kind === 'break').length;
    const separators = draft.items.filter((item) => item.kind === 'separator').length;
    const actions = draft.items.filter((item) => item.kind === 'action').length;

    const total = draft.items
      .map(durationOf)
      .filter((value) => value !== null)
      .reduce((sum, value) => sum + Math.max(0, value), 0);

    clear(statsHost);
    statsHost.appendChild(h('span', t('layoutEditor.stats', {
      total: draft.items.length,
      classes,
      breaks,
      totalMinutes: total,
    })));

    if (separators > 0 || actions > 0) {
      statsHost.appendChild(h('span', { style: { marginLeft: '10px' } },
        t('layoutEditor.statsMarks', { separators, actions })));
    }
  }

  /** 节次列：只有「上课」才编号，课间/分割线/行动显示为横线。 */
  function ordinalAt(index) {
    let count = 0;
    for (let i = 0; i <= index; i += 1) {
      if (draft.items[i]?.kind === 'class') {
        count += 1;
      }
    }

    return draft.items[index]?.kind === 'class' ? count : null;
  }

  function paintRows() {
    clear(listHost);
    paintStats();

    if (draft.items.length === 0) {
      listHost.appendChild(h('p.card-desc', t('layoutEditor.empty')));
      return;
    }

    const rows = draft.items.map((item, index) => {
      const ordinal = ordinalAt(index);
      const duration = durationOf(item);

      return h('tr',
        h('td', h('span.cell-sub', ordinal === null ? '—' : String(ordinal))),
        h('td', timeCell(item, 'start')),
        h('td', timeCell(item, 'end')),
        h('td', h('span.cell-sub', isPointKind(item.kind)
          ? '—'
          : (duration === null ? '—' : t('layoutEditor.minutes', { count: duration })))),
        h('td', kindCell(item)),
        h('td', nameCell(item)),
        h('td', extrasCell(item)),
        h('td', hideCell(item)),
        h('td', removeCell(index)));
    });

    const head = h('tr',
      h('th', { style: { width: '54px' } }, t('layoutEditor.ordinal')),
      h('th', { style: { width: '100px' } }, t('layoutEditor.start')),
      h('th', { style: { width: '100px' } }, t('layoutEditor.end')),
      h('th', { style: { width: '86px' } }, t('layoutEditor.duration')),
      h('th', { style: { width: '160px' } }, t('layoutEditor.kind')),
      h('th', t('layoutEditor.name')),
      h('th', { style: { width: '190px' } }, t('layoutEditor.extras')),
      h('th', { style: { width: '70px' } }, t('layoutEditor.hidden')),
      h('th', { style: { width: '80px' } }, ''));

    listHost.appendChild(h('div.table-wrap',
      h('table.data.data-compact',
        h('thead', head),
        h('tbody', ...rows))));
  }

  async function save() {
    const name = nameInput.value.trim();
    if (!name) {
      toast('warn', t('layoutEditor.needName'));
      return;
    }

    try {
      await api(`/admin/time-layouts/${draft.id}`, {
        method: 'PUT',
        body: { id: draft.id, name, items: draft.items },
      });

      dirtyMark.textContent = '';
      toast('ok', t('layoutEditor.saved'), t('layoutEditor.savedHint'));
    } catch (error) {
      toastError(error, t('layoutEditor.saveFailed'));
    }
  }

  function addItem(kind = 'class') {
    // 新时间点接在最后一个之后：上课默认 45 分钟，课间默认 10 分钟。
    const last = draft.items[draft.items.length - 1];
    const start = last?.endTime || '08:00';
    const span = kind === 'break' ? 10 : 45;
    const [hour, minute] = start.split(':').map((x) => Number(x) || 0);
    const endMinutes = hour * 60 + minute + span;
    const end = `${String(Math.floor(endMinutes / 60) % 24).padStart(2, '0')}:${String(endMinutes % 60).padStart(2, '0')}`;

    draft.items.push({
      id: genId(),
      startTime: start,
      endTime: isPointKind(kind) ? start : end,
      kind,
    });

    markDirty();
    paintRows();
  }

  const toolbar = h('div.toolbar',
    h('a.btn.btn-sm', { href: '#/layouts' }, t('layoutEditor.back')),
    h('span.toolbar-label', t('layoutEditor.nameLabel')),
    nameInput,
    h('div.spacer'),
    dirtyMark,
    h('button.btn.btn-sm', { type: 'button', onClick: () => addItem('class') },
      t('layoutEditor.addPoint')),
    h('button.btn.btn-sm', { type: 'button', onClick: () => addItem('break') },
      t('layoutEditor.addBreak')),
    h('button.btn.btn-sm.btn-primary', { type: 'button', onClick: save },
      t('profiles.editor.save')));

  container.appendChild(h('div',
    toolbar,
    h('p.card-desc', t('layoutEditor.lead')),
    statsHost,
    listHost));

  paintRows();
}
