/**
 * 时间表编辑器（**独立页面**，不依附任何档案）。
 *
 * <p>
 * 时间表是全局实体（`/admin/time-layouts/{id}`）：一份作息只有一条记录，
 * 多个档案的课表引用它。所以编辑它不需要先选档案，也不该藏在档案编辑器里——
 * 那正是以前「同一份作息存三份、改一处漏两处」的根源。
 * </p>
 *
 * <p>
 * 这里是**唯一**的时间点编辑入口；档案编辑器里不再有时间表页签，
 * 只有「使用哪些时间表」的选择（可随时更换）。
 * </p>
 */

import { api } from '../core/api.js?v=169';
import { toastError, errorBlock } from '../core/errors.js?v=169';
import {
  h, clear, toast, loadingBlock, modal, confirmDialog, field, select,
} from '../core/ui.js?v=169';
import { t } from './../core/i18n.js?v=169';

export const meta = {
  title: t('layoutEditor.title'),
  subtitle: t('layoutEditor.subtitle'),
};

/** 时间点类型：与宿主 TimeType 对应。 */
const KIND_ORDER = ['class', 'break', 'separator', 'action'];

function kindLabels() {
  return {
    class: t('profiles.editor.kindClass'),
    break: t('profiles.editor.kindBreak'),
    separator: t('profiles.editor.kindSeparator'),
    action: t('profiles.editor.kindAction'),
  };
}

/** 生成唯一 ID（与档案编辑器同一套规则）。 */
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

/** 分割线与行动是零时长点：只该有一个时间。 */
const isPointKind = (kind) => kind === 'separator' || kind === 'action';

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
  const dirtyMark = h('span.cell-sub');

  const markDirty = () => {
    dirtyMark.textContent = t('layoutEditor.unsaved');
  };

  /** 开始 / 结束时间输入框。分割线与行动只有一个时间，结束框跟着开始走且不可编辑。 */
  function timeCell(item, which) {
    const point = isPointKind(item.kind);
    const input = h('input', {
      type: 'text',
      value: (which === 'start' || point ? item.startTime : item.endTime) ?? '',
      placeholder: '08:00',
      style: { width: '86px' },
    });

    if (which === 'end' && point) {
      // 不是"少一个框"，是从根上杜绝负时长与几小时的异常时间点。
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
    };

    return input;
  }

  /** 类型下拉：切到零时长类型时把结束时间对齐。 */
  function kindSelect(item) {
    const labels = kindLabels();
    const node = select(
      KIND_ORDER.map((key) => ({ value: key, label: labels[key] })),
      item.kind ?? 'class',
    );

    node.onchange = () => {
      item.kind = node.value;
      if (isPointKind(item.kind)) {
        item.endTime = item.startTime;
      }

      markDirty();
      paintRows();
    };

    return node;
  }

  /** 名称输入：课间同步写 breakName（宿主只认它）。 */
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

  function paintRows() {
    clear(listHost);

    if (draft.items.length === 0) {
      listHost.appendChild(h('p.card-desc', t('layoutEditor.empty')));
      return;
    }

    const rows = draft.items.map((item, index) => h('tr',
      h('td', timeCell(item, 'start')),
      h('td', timeCell(item, 'end')),
      h('td', kindSelect(item)),
      h('td', nameCell(item)),
      h('td', removeCell(index))));

    const head = h('tr',
      h('th', { style: { width: '110px' } }, t('layoutEditor.start')),
      h('th', { style: { width: '110px' } }, t('layoutEditor.end')),
      h('th', { style: { width: '120px' } }, t('layoutEditor.kind')),
      h('th', t('layoutEditor.name')),
      h('th', { style: { width: '90px' } }, ''));

    const table = h('table.data.data-compact',
      h('thead', head),
      h('tbody', ...rows));

    listHost.appendChild(h('div.table-wrap', table));
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

  function addItem() {
    // 新时间点接在最后一个之后：默认 45 分钟，比"从 00:00 开始"少改两次。
    const last = draft.items[draft.items.length - 1];
    const start = last?.endTime || '08:00';
    const [hour, minute] = start.split(':').map((x) => Number(x) || 0);
    const endMinutes = hour * 60 + minute + 45;
    const end = `${String(Math.floor(endMinutes / 60) % 24).padStart(2, '0')}:${String(endMinutes % 60).padStart(2, '0')}`;

    draft.items.push({ id: genId(), startTime: start, endTime: end, kind: 'class' });
    markDirty();
    paintRows();
  }

  const toolbar = h('div.toolbar',
    h('a.btn.btn-sm', { href: '#/layouts' }, t('layoutEditor.back')),
    h('span.toolbar-label', t('layoutEditor.nameLabel')),
    nameInput,
    h('div.spacer'),
    dirtyMark,
    h('button.btn.btn-sm', { type: 'button', onClick: addItem }, t('layoutEditor.addPoint')),
    h('button.btn.btn-sm.btn-primary', { type: 'button', onClick: save }, t('profiles.editor.save')));

  container.appendChild(h('div',
    toolbar,
    h('p.card-desc', t('layoutEditor.lead')),
    listHost));

  paintRows();
}
