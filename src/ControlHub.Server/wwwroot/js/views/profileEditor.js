/**
 * 配置档案编辑器：时间表 / 课表 / 科目 / 自定义设置四个标签页。
 * 修改先落在内存对象上，点「保存并下发」一次性提交。
 */

import { api } from '../core/api.js?v=80';
import { toastError } from '../core/errors.js?v=80';
import {
  h, clear, toast, loadingBlock, modal, confirmDialog, field, icon, select,
  emptyState, formatDateTime,
} from '../core/ui.js?v=80';
import { SCHED_SCALES, getSchedScale, setSchedScale } from '../core/prefs.js?v=80';

export const meta = {
  title: '编辑配置档案',
  subtitle: '',
};

const TABS = [
  { key: 'timeLayouts', label: '时间表' },
  { key: 'classPlans', label: '课表' },
  { key: 'subjects', label: '科目' },
  { key: 'settings', label: '自定义设置' },
];

/** 时间点类型：点击单元格上的类型标签可循环切换，避免使用下拉框。 */
const TIME_KIND_ORDER = ['class', 'break', 'separator', 'action'];
const TIME_KIND_LABEL = { class: '上课', break: '课间', separator: '分割线', action: '行动' };

/** 生成唯一 ID：优先 crypto.randomUUID，非安全上下文（HTTP）回退到手写 UUID v4。 */
function genId() {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0;
    const v = c === 'x' ? r : (r & 0x3) | 0x8;
    return v.toString(16);
  });
}

/** 分段选择器：一排扁平按钮，替代下拉框。 */
function segmented(items, currentId, onPick, emptyLabel) {
  if (!items || items.length === 0) {
    return h('span.segmented-empty', emptyLabel || '（暂无）');
  }
  return h('div.segmented',
    ...items.map((item) => h('button', {
      class: `segmented-item${item.id === currentId ? ' active' : ''}`,
      type: 'button',
      onClick: () => onPick(item.id),
    }, item.label)));
}

/** 拖拽中的载荷：`{kind:'subject',subjectId}` 或 `{kind:'slot',day,index}`。 */
let dragPayload = null;

/** 编辑器状态。 */
let state = {
  profileId: null,
  profile: null,
  content: null,
  activeTab: 'timeLayouts',
  selectedLayoutId: null,
  selectedPlanId: null,
  classPlanLayoutId: null,
  scheduleSelection: null,
  armedSubjectId: null,
  dirty: false,
};

export async function render(container, params) {
  clear(container);
  container.appendChild(loadingBlock('正在读取档案内容…'));

  const profile = await api(`/admin/profiles/${params.id}`);
  state = {
    profileId: params.id,
    profile,
    content: normalizeContent(profile.content),
    activeTab: state.activeTab,
    selectedLayoutId: profile.content?.timeLayouts?.[0]?.id || null,
    selectedPlanId: profile.content?.classPlans?.[0]?.id || null,
    classPlanLayoutId: state.classPlanLayoutId || profile.content?.timeLayouts?.[0]?.id || null,
    scheduleSelection: null,
    armedSubjectId: null,
    dirty: false,
  };

  paint(container);
}

function normalizeContent(content) {
  const c = content || {};
  return {
    timeLayouts: c.timeLayouts || [],
    classPlans: c.classPlans || [],
    subjects: c.subjects || [],
    settings: {
      values: c.settings?.values || {},
      lockLocalEditing: !!c.settings?.lockLocalEditing,
      announcement: c.settings?.announcement || '',
    },
  };
}

function paint(container) {
  clear(container);

  // 页面标题由路由统一处理，这里只渲染内容。
  container.appendChild(h('div',
    renderHeader(),
    h('div.card',
      h('div.editor-toolbar',
        h('div.tabs', ...TABS.map((tab) => h('button', {
          class: `tab${state.activeTab === tab.key ? ' active' : ''}`,
          type: 'button',
          onClick: () => {
            state.activeTab = tab.key;
            paint(container);
          },
        }, tab.label,
          h('span.tab-count', String(countOf(tab.key))),
        ))),
        // 课表视图缩放（#54）：只切换 body 上的类名让 CSS 生效，**不重建 DOM**——
        // 编辑器里可能正有未保存的输入，重建会打断它。
        h('div.scale-switch', ...SCHED_SCALES.map((s) => h(
          'button.segmented-item' + (getSchedScale() === s.key ? '.active' : ''),
          {
            type: 'button',
            title: s.hint,
            dataset: { scale: s.key },
            onClick: () => {
              setSchedScale(s.key);
              for (const btn of document.querySelectorAll('.scale-switch .segmented-item')) {
                btn.classList.toggle('active', btn.dataset.scale === s.key);
              }
            },
          },
          s.label,
        ))),
      ),
      h('div#tabBody', renderTab()),
    ),
  ));

  syncDirtyFlag();
}

/** 同步「有未保存修改」标记（不重建 DOM）。 */
function syncDirtyFlag() {
  const flag = document.getElementById('dirtyFlag');
  if (!flag) return;
  clear(flag);
  if (state.dirty) flag.appendChild(h('span.badge.badge-warn', '有未保存修改'));
}

/** 输入类编辑用：只刷新未保存标记，不重建 DOM，避免打断输入。 */
function markDirtyLight() {
  if (state.dirty) return;
  state.dirty = true;
  syncDirtyFlag();
}

function countOf(key) {
  switch (key) {
    case 'timeLayouts': return state.content.timeLayouts.length;
    case 'classPlans': return state.content.classPlans.length;
    case 'subjects': return state.content.subjects.length;
    case 'settings': return Object.keys(state.content.settings.values).length;
    default: return 0;
  }
}

function renderHeader() {
  return h('div', {
    style: {
      display: 'flex', gap: '14px', alignItems: 'center', flexWrap: 'wrap',
      marginBottom: '16px', padding: '14px 16px',
      background: 'var(--bg-panel)', border: '1px solid var(--border)', borderRadius: 'var(--radius)',
    },
  },
    h('div', { style: { flex: '1 1 260px' } },
      h('div', { style: { display: 'flex', alignItems: 'center', gap: '9px', flexWrap: 'wrap' } },
        h('strong', { style: { fontSize: '15px' } }, state.profile.name),
        state.profile.isDefault ? h('span.badge.badge-accent', '全局默认') : null,
        h('span.badge.badge-neutral', `内容版本 ${state.profile.revision}`),
        h('span#dirtyFlag'),
      ),
      h('div', { style: { fontSize: '12px', color: 'var(--text-faint)', marginTop: '3px' } },
        `最后更新 ${formatDateTime(state.profile.updatedAt)}`),
    ),
    h('div.card-actions',
      h('button.btn.btn-sm', {
        type: 'button',
        onClick: () => window.location.hash = '#/profiles',
      }, '返回列表'),
      h('button.btn.btn-sm', {
        type: 'button',
        disabled: !state.dirty,
        onClick: () => discard(),
      }, '放弃修改'),
      h('button.btn.btn-sm.btn-primary', {
        type: 'button',
        onClick: () => save(),
      }, '保存并下发'),
    ),
  );
}

function discard() {
  confirmDialog('放弃修改', '当前未保存的修改将会丢失，确定继续吗？', '放弃', true).then((ok) => {
    if (ok) render(document.getElementById('content'), { id: state.profileId });
  });
}

function markDirty() {
  if (!state.dirty) {
    state.dirty = true;
    paint(document.getElementById('content'));
  }
}

async function save() {
  try {
    const result = await api(`/admin/profiles/${state.profileId}`, {
      method: 'PUT',
      body: {
        name: state.profile.name,
        description: state.profile.description || '',
        content: state.content,
      },
    });

    state.profile = result.profile;
    state.content = normalizeContent(result.profile.content);
    state.dirty = false;

    const notes = result.notes || [];
    toast('ok', '保存成功', notes.length > 0
      ? `服务器自动处理了 ${notes.length} 项内容问题，配置版本已更新为 ${result.revision}。`
      : `全局配置版本已更新为 ${result.revision}，客户端将在数秒内自动同步。`);

    if (notes.length > 0) {
      modal({
        title: '内容自动处理说明',
        width: 'wide',
        hideFooter: true,
        body: h('div',
          h('p', { style: { marginTop: 0, color: 'var(--text-dim)' } },
            '保存时发现以下内容需要修正，服务器已自动处理：'),
          h('ul', { style: { margin: 0, paddingLeft: '20px', fontSize: '12.5px', lineHeight: '1.9' } },
            ...notes.map((n) => h('li', n))),
        ),
      });
    }

    paint(document.getElementById('content'));
  } catch (err) {
    toastError(err, '保存失败');
  }
}

function renderTab() {
  switch (state.activeTab) {
    case 'timeLayouts': return renderTimeLayouts();
    case 'classPlans': return renderClassPlans();
    case 'subjects': return renderSubjects();
    case 'settings': return renderSettings();
    default: return h('div');
  }
}

function repaintTab() {
  paint(document.getElementById('content'));
}

// ────────────────────────────── 时间表 ──────────────────────────────

function currentLayout() {
  return state.content.timeLayouts.find((l) => l.id === state.selectedLayoutId) || null;
}

function renderTimeLayouts() {
  const layouts = state.content.timeLayouts;
  const layout = currentLayout();

  return h('div',
    h('div.toolbar',
      h('span.toolbar-label', '时间表'),
      segmented(
        layouts.map((l) => ({ id: l.id, label: `${l.name}（${classCount(l)} 节）` })),
        state.selectedLayoutId,
        (v) => {
          state.selectedLayoutId = v;
          repaintTab();
        },
        '（还没有时间表）',
      ),
      h('div.spacer'),
      h('button.btn.btn-sm.btn-primary', { type: 'button', onClick: addLayout }, '+ 新建时间表'),
      layout ? h('button.btn.btn-sm', { type: 'button', onClick: () => renameLayout(layout) }, '重命名') : null,
      layout ? h('button.btn.btn-sm', { type: 'button', onClick: () => generateSchedule(layout) }, '快速生成作息') : null,
      layout ? h('button.btn.btn-sm', { type: 'button', onClick: () => addItem(layout) }, '+ 添加时间点') : null,
      layout ? h('button.btn.btn-sm.btn-danger', { type: 'button', onClick: () => removeLayout(layout) }, '删除') : null,
    ),
    !layout
      ? emptyState('clock', '还没有时间表',
        '时间表定义了一天的作息时间点，课表以它的「上课」时间点为基准。',
        h('button.btn.btn-primary', { type: 'button', onClick: addLayout }, '新建时间表'))
      : h('div.grid-scroll',
        h('div.period-grid', ...layout.items.map((item, index) => periodCell(layout, item, index))),
      ),
    layout && layout.items.length === 0
      ? h('div.notice.notice-warn', { style: { marginTop: '14px' } },
        h('span.notice-icon', '!'),
        h('div', '该时间表还没有任何时间点。点击「快速生成作息」可一次生成完整的一天作息。'))
      : null,
  );
}

function classCount(layout) {
  return (layout.items || []).filter((i) => i.kind === 'class').length;
}

/** 时间点单元格：节次 / 类型 / 起止时间 / 名称。 */
function periodCell(layout, item, index) {
  const isPoint = item.kind === 'separator' || item.kind === 'action';

  const kindButton = h('button.pcell-kind', {
    type: 'button',
    title: '点击切换类型：上课 → 课间 → 分割线 → 行动',
    onClick: () => {
      const next = TIME_KIND_ORDER[(TIME_KIND_ORDER.indexOf(item.kind) + 1) % TIME_KIND_ORDER.length];
      item.kind = next;
      if (next === 'separator' || next === 'action') item.endTime = item.startTime;
      markDirtyValues();
    },
  }, TIME_KIND_LABEL[item.kind] || '上课');

  const flagButton = h('button.pcell-flag', {
    type: 'button',
    class: item.isHideDefault ? 'on' : '',
    title: '切换该时间点是否默认隐藏',
    onClick: () => {
      item.isHideDefault = !item.isHideDefault;
      markDirtyValues();
    },
  }, item.isHideDefault ? '已隐藏' : '默认显示');

  const startInput = timeInput(item.startTime, (v) => {
    item.startTime = v;
    if (isPoint) item.endTime = v;
    markDirtyLight();
  });
  startInput.classList.add('pcell-time');

  const endInput = timeInput(item.endTime, (v) => {
    item.endTime = v;
    markDirtyLight();
  });
  endInput.classList.add('pcell-time');
  if (isPoint) {
    endInput.disabled = true;
    endInput.classList.add('muted');
  }

  const nameInput = h('input.pcell-name', {
    type: 'text',
    value: item.breakName || '',
    placeholder: item.kind === 'break' ? '课间名称（留空显示「课间休息」）' : '备注（可选）',
    onInput: (e) => {
      item.breakName = e.target.value.trim() || null;
      markDirtyLight();
    },
  });

  return h('div', { class: `pcell kind-${item.kind}${item.isHideDefault ? ' hidden-point' : ''}` },
    h('div.pcell-top',
      // 非上课时间点的类型已由类型标签表达，不再重复文字，保持单元格清爽。
      item.kind === 'class' ? h('span.pcell-no', `第 ${countClassBefore(layout, index) + 1} 节`) : null,
      kindButton,
      h('div.spacer'),
      flagButton,
      h('button.pcell-del', {
        type: 'button',
        title: '删除该时间点',
        onClick: () => {
          layout.items.splice(index, 1);
          markDirtyValues();
        },
      }, icon('close', 14)),
    ),
    h('div.pcell-times', startInput, h('span.pcell-dash', '–'), endInput),
    nameInput,
  );
}

/** 统计某个索引之前有多少个「上课」时间点，用于显示节次。 */
function countClassBefore(layout, index) {
  let count = 0;
  for (let i = 0; i < index; i += 1) {
    if (layout.items[i].kind === 'class') count += 1;
  }
  return count;
}

function markDirtyValues() {
  state.dirty = true;
  repaintTab();
}

function timeInput(value, onChange) {
  const input = h('input', { type: 'time', step: '1', value: toTimeInputValue(value) });
  input.addEventListener('change', () => onChange(normalizeTimeText(input.value)));
  return input;
}

function toTimeInputValue(text) {
  const t = normalizeTimeText(text);
  return t ? t.slice(0, 5) : '';
}

/** 把 `HH:mm` 补成 `HH:mm:ss`。 */
function normalizeTimeText(text) {
  if (!text) return '00:00:00';
  const parts = String(text).split(':');
  const hh = (parts[0] || '00').padStart(2, '0');
  const mm = (parts[1] || '00').padStart(2, '0');
  const ss = (parts[2] || '00').padStart(2, '0');
  return `${hh}:${mm}:${ss}`;
}

function addItem(layout) {
  const last = layout.items[layout.items.length - 1];
  const start = last ? last.endTime : '08:00:00';
  layout.items.push({
    startTime: start,
    endTime: addMinutes(start, 45),
    kind: 'class',
    breakName: null,
    isHideDefault: false,
    defaultSubjectId: null,
  });
  markDirtyValues();
}

/** 给 `HH:mm:ss` 增加分钟数。 */
function addMinutes(text, minutes) {
  const [hh, mm, ss] = normalizeTimeText(text).split(':').map(Number);
  const total = (hh * 3600 + mm * 60 + ss + minutes * 60) % 86400;
  const h2 = Math.floor(total / 3600);
  const m2 = Math.floor((total % 3600) / 60);
  const s2 = total % 60;
  return [h2, m2, s2].map((n) => String(n).padStart(2, '0')).join(':');
}

function addLayout() {
  const id = genId();
  state.content.timeLayouts.push({
    id,
    name: `新时间表 ${state.content.timeLayouts.length + 1}`,
    items: [],
  });
  state.selectedLayoutId = id;
  markDirtyValues();
}

function renameLayout(layout) {
  const input = h('input', { type: 'text', value: layout.name });
  modal({
    title: '重命名时间表',
    width: 'wide',
    body: field('名称', input),
    confirmText: '保存',
    onConfirm: () => {
      layout.name = input.value.trim() || layout.name;
      markDirtyValues();
      return true;
    },
  });
}

function removeLayout(layout) {
  const used = state.content.classPlans.filter((p) => p.timeLayoutId === layout.id);
  const message = used.length > 0
    ? `「${layout.name}」正在被 ${used.length} 张课表使用。删除后这些课表需要重新指定时间表。确定继续吗？`
    : `确定删除时间表「${layout.name}」吗？`;

  confirmDialog('删除时间表', message, '删除', true).then((ok) => {
    if (!ok) return;
    state.content.timeLayouts = state.content.timeLayouts.filter((l) => l.id !== layout.id);
    state.selectedLayoutId = state.content.timeLayouts[0]?.id || null;
    markDirtyValues();
  });
}

/** 快速生成作息：按「第 1 节开始时间 + 每节时长 + 课间时长」批量生成一整天。 */
function generateSchedule(layout) {
  const startInput = h('input', { type: 'time', value: '08:00' });
  const periodInput = h('input', { type: 'number', value: '45', min: '5', max: '180' });
  const breakInput = h('input', { type: 'number', value: '10', min: '0', max: '120' });
  const countInput = h('input', { type: 'number', value: '8', min: '1', max: '20' });
  const replaceCheckbox = h('input', { type: 'checkbox' });
  replaceCheckbox.checked = true;

  modal({
    title: '快速生成作息',
    width: 'wide',
    body: h('div',
      h('div.form-row',
        field('第 1 节开始时间', startInput),
        field('每节时长（分钟）', periodInput),
        field('课间时长（分钟）', breakInput),
        field('每天节数', countInput),
      ),
      h('label.checkbox-field', replaceCheckbox, '清空现有时间点后重新生成'),
      h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', '生成规则：每节课后插入一个课间，最后一节后不再追加课间。生成后可继续手动微调。'),
      ),
    ),
    confirmText: '生成',
    onConfirm: () => {
      const period = Number(periodInput.value) || 45;
      const gap = Number(breakInput.value) || 0;
      const count = Number(countInput.value) || 8;
      let cursor = normalizeTimeText(startInput.value);

      const items = [];
      for (let i = 0; i < count; i += 1) {
        const end = addMinutes(cursor, period);
        items.push({ startTime: cursor, endTime: end, kind: 'class', breakName: null, isHideDefault: false, defaultSubjectId: null });
        cursor = end;
        if (i < count - 1 && gap > 0) {
          const breakEnd = addMinutes(cursor, gap);
          items.push({ startTime: cursor, endTime: breakEnd, kind: 'break', breakName: null, isHideDefault: false, defaultSubjectId: null });
          cursor = breakEnd;
        }
      }

      layout.items = replaceCheckbox.checked ? items : [...layout.items, ...items];
      markDirtyValues();
      return true;
    },
  });
}

// ────────────────────────────── 课表 ──────────────────────────────

const GRID_DAYS = [
  { value: 1, label: '周一' },
  { value: 2, label: '周二' },
  { value: 3, label: '周三' },
  { value: 4, label: '周四' },
  { value: 5, label: '周五' },
  { value: 6, label: '周六' },
  { value: 0, label: '周日' },
];

function classPlanLayout() {
  return state.content.timeLayouts.find((l) => l.id === state.classPlanLayoutId) || null;
}

/** 课表页：整周「节次 × 星期」网格，点选或拖拽即可排课、调课。 */
function renderClassPlans() {
  const layouts = state.content.timeLayouts;
  const layout = classPlanLayout();

  const layoutBar = segmented(
    layouts.map((l) => ({ id: l.id, label: `${l.name}（${classCount(l)} 节）` })),
    state.classPlanLayoutId,
    (v) => {
      state.classPlanLayoutId = v;
      state.scheduleSelection = null;
      repaintTab();
    },
    layouts.length === 0 ? '（请先创建时间表）' : null,
  );

  if (!layout) {
    return h('div',
      h('div.toolbar', h('span.toolbar-label', '作息时间表'), layoutBar),
      emptyState('clock', '还没有时间表',
        '课程表以时间表的「上课」时间点为基准，横向为周一到周日、纵向为节次。请先创建时间表（作息）。',
        h('button.btn.btn-sm.btn-primary', {
          type: 'button',
          onClick: () => { state.activeTab = 'timeLayouts'; repaintTab(); },
        }, '先创建时间表')),
    );
  }

  const periods = layout.items.filter((i) => i.kind === 'class');
  if (periods.length === 0) {
    return h('div',
      h('div.toolbar', h('span.toolbar-label', '作息时间表'), layoutBar),
      emptyState('clock', '时间表还没有「上课」时间点',
        '请先在「时间表」中添加至少一节课（上课类型），再回来排课。'),
    );
  }

  // 选中格子索引越界时复位。
  if (state.scheduleSelection && state.scheduleSelection.index >= periods.length) {
    state.scheduleSelection = null;
  }

  return h('div',
    h('div.toolbar',
      h('span.toolbar-label', '作息时间表'),
      layoutBar,
      h('div.spacer'),
      h('span.toolbar-hint', '点格子选中后点科目，或把科目拖进格子；拖动已排课程可调课；双击格子清空'),
      h('button.btn.btn-sm', {
        type: 'button',
        title: '按节复制 / 批量清空 / 替换科目 / 课表模板',
        onClick: () => openBulkOps(layout, periods.length),
      }, '批量操作'),
    ),
    renderPalette(periods.length),
    h('div.grid-scroll',
      h('div.sched-grid',
        h('div.sched-head.corner', '节次 / 时间'),
        ...GRID_DAYS.map((d) => h('div.sched-head', d.label)),
        ...periods.flatMap((period, index) => [
          h('div.sched-period',
            h('span.no', `第 ${index + 1} 节`),
            h('span.tm', `${period.startTime.slice(0, 5)}–${period.endTime.slice(0, 5)}`)),
          ...GRID_DAYS.map((d) => schedCell(layout, d.value, index, periods.length)),
        ]),
      ),
    ),
  );
}

// ── 课表批量操作（#9）─────────────────────────────────────────────────

/**
 * 批量操作弹窗：在「天 × 节次」的矩形区域上做复制 / 清空 / 替换科目，以及课表模板的存取。
 *
 * 为什么用表单而不是鼠标框选：框选只能表达「连续的一块」，而实际需求常是
 * 「周一~周三的第 1~4 节 → 周四周五的第 5~8 节」这种跨区搬移；
 * 表单能精确表达，也更容易在执行前核对一遍。
 */
function openBulkOps(layout, periodCount) {
  const dayOptions = GRID_DAYS.map((d, i) => ({ value: String(i), label: d.label }));
  const periodOptions = Array.from({ length: periodCount }, (_, i) => ({
    value: String(i), label: `第 ${i + 1} 节`,
  }));
  const subjectOptions = [
    { value: '', label: '（请选择科目）' },
    ...state.content.subjects.map((s) => ({ value: s.id, label: s.name || '（未命名科目）' })),
  ];

  const cfg = {
    action: 'copy',
    mode: 'replace',
    srcDayFrom: '0', srcDayTo: '4', srcPeriodFrom: '0', srcPeriodTo: String(periodCount - 1),
    dstDayFrom: '0', dstDayTo: '4', dstPeriodFrom: '0', dstPeriodTo: String(periodCount - 1),
    replaceFrom: '', replaceTo: '',
    templateId: '',
  };
  const body = h('div');
  const runtime = { templates: [], templateName: '' };

  /** 把「起 / 止」两个下标配置展开成连续下标数组。 */
  const expand = (fromKey, toKey) => {
    const lo = Math.min(Number(cfg[fromKey]), Number(cfg[toKey]));
    const hi = Math.max(Number(cfg[fromKey]), Number(cfg[toKey]));
    return Array.from({ length: hi - lo + 1 }, (_, i) => lo + i);
  };

  /** 「起 / 止」两栏并排。 */
  const pair = (options, fromKey, toKey) => h('div.form-row',
    select(options, cfg[fromKey], (v) => { cfg[fromKey] = v; }),
    select(options, cfg[toKey], (v) => { cfg[toKey] = v; }));

  // ── 动作实现 ──

  function applyCopy() {
    const srcDays = expand('srcDayFrom', 'srcDayTo');
    const srcPeriods = expand('srcPeriodFrom', 'srcPeriodTo');
    const dstDays = expand('dstDayFrom', 'dstDayTo');
    const dstPeriods = expand('dstPeriodFrom', 'dstPeriodTo');

    // **先把源整块读出来再写**：源与目标重叠很常见（如第 1~4 节 → 第 3~6 节），
    // 边读边写会让后半段读到已被覆盖的值。
    const block = srcPeriods.map((p) => srcDays.map((d) => readSlot(layout, GRID_DAYS[d].value, p)));

    let written = 0;
    let skipped = 0;
    dstPeriods.forEach((period, row) => {
      const srcRow = block[row % block.length];
      dstDays.forEach((dayIndex, col) => {
        const value = srcRow[col % srcRow.length];
        const day = GRID_DAYS[dayIndex].value;
        if (cfg.mode === 'merge' && readSlot(layout, day, period)) {
          skipped++;
          return;
        }

        writeSlot(layout, day, period, value);
        written++;
      });
    });

    return { written, skipped };
  }

  function applyClear() {
    const days = expand('srcDayFrom', 'srcDayTo');
    const periods = expand('srcPeriodFrom', 'srcPeriodTo');
    let cleared = 0;
    for (const period of periods) {
      for (const dayIndex of days) {
        const day = GRID_DAYS[dayIndex].value;
        if (readSlot(layout, day, period)) {
          writeSlot(layout, day, period, null);
          cleared++;
        }
      }
    }

    return { cleared };
  }

  function applyReplace() {
    if (!cfg.replaceFrom || !cfg.replaceTo || cfg.replaceFrom === cfg.replaceTo) {
      return { invalid: true };
    }

    let count = 0;
    for (const day of GRID_DAYS) {
      for (let i = 0; i < periodCount; i++) {
        const current = readSlot(layout, day.value, i);
        if (current && current.subjectId === cfg.replaceFrom) {
          writeSlot(layout, day.value, i, { subjectId: cfg.replaceTo, isEnabled: current.isEnabled });
          count++;
        }
      }
    }

    return { count };
  }

  /** 把当前课表收成「天 → 科目名」网格（存模板用）。 */
  function collectGrid() {
    const grid = {};
    for (const day of GRID_DAYS) {
      const row = [];
      for (let i = 0; i < periodCount; i++) {
        const current = readSlot(layout, day.value, i);
        const subject = current
          ? state.content.subjects.find((s) => s.id === current.subjectId)
          : null;
        row.push(subject ? subject.name : null);
      }

      grid[String(day.value)] = row;
    }

    return grid;
  }

  /**
   * 套用模板：**只填空格、不清空已有内容**。
   * 套用的语义是「参考别人排好的」，把目标已有的课直接抹掉风险太大；
   * 想完全按模板来，先「批量清空」再套用即可。
   */
  function applyTemplate(template) {
    let applied = 0;
    const missing = [];
    for (const day of GRID_DAYS) {
      const row = template.grid?.[String(day.value)];
      if (!Array.isArray(row)) {
        continue;
      }

      row.forEach((name, index) => {
        if (index >= periodCount || !name || readSlot(layout, day.value, index)) {
          return; // 超出目标节数、空格、或已有内容 —— 都不动
        }

        const subject = state.content.subjects.find((s) => s.name === name);
        if (subject) {
          writeSlot(layout, day.value, index, { subjectId: subject.id, isEnabled: true });
          applied++;
        } else if (!missing.includes(name)) {
          // 记下来告诉用户：模板里的科目这套档案里没有，套用后那几格会是空的。
          missing.push(name);
        }
      });
    }

    return { applied, missing };
  }

  // ── 渲染 ──

  function paint() {
    clear(body);

    body.appendChild(h('div.card-actions', { style: { gap: '6px', marginBottom: '12px' } },
      ...[['copy', '按节复制'], ['clear', '批量清空'], ['replace', '替换科目'], ['template', '课表模板']]
        .map(([key, label]) => h('button', {
          type: 'button',
          class: `btn btn-sm${cfg.action === key ? ' btn-primary' : ''}`,
          onClick: () => { cfg.action = key; paint(); },
        }, label))));

    if (cfg.action === 'copy') {
      body.appendChild(field('源 · 天', pair(dayOptions, 'srcDayFrom', 'srcDayTo')));
      body.appendChild(field('源 · 节次', pair(periodOptions, 'srcPeriodFrom', 'srcPeriodTo')));
      body.appendChild(field('目标 · 天', pair(dayOptions, 'dstDayFrom', 'dstDayTo')));
      body.appendChild(field('目标 · 节次', pair(periodOptions, 'dstPeriodFrom', 'dstPeriodTo')));
      body.appendChild(field('方式', select([
        { value: 'replace', label: '覆盖目标（源里的空格也会照搬）' },
        { value: 'merge', label: '只填空格（目标已有课的不动）' },
      ], cfg.mode, (v) => { cfg.mode = v; }),
      '源与目标重叠也没问题：会先把源整块读出来再写。'));
    } else if (cfg.action === 'clear') {
      body.appendChild(field('范围 · 天', pair(dayOptions, 'srcDayFrom', 'srcDayTo')));
      body.appendChild(field('范围 · 节次', pair(periodOptions, 'srcPeriodFrom', 'srcPeriodTo')));
      body.appendChild(h('p.card-desc', { style: { margin: 0 } },
        '把这一块里所有已排的课清掉（只影响本档案内存中的内容，点「保存并下发」才生效）。'));
    } else if (cfg.action === 'replace') {
      body.appendChild(field('把', select(subjectOptions, cfg.replaceFrom,
        (v) => { cfg.replaceFrom = v; })));
      body.appendChild(field('换成', select(subjectOptions, cfg.replaceTo,
        (v) => { cfg.replaceTo = v; }),
      '全周范围内所有这个科目都会被换掉，包括禁用的节点。'));
    } else {
      // 模板：存 / 套用
      const nameInput = h('input', {
        type: 'text', placeholder: '模板名，例如「夏季作息课表」', maxlength: '32',
      });
      body.appendChild(field('存为模板',
        h('div.form-row', nameInput, h('button.btn.btn-sm', {
          type: 'button',
          onClick: async () => {
            try {
              const created = await api('/admin/timetable-templates', {
                method: 'POST',
                body: {
                  name: nameInput.value.trim(),
                  grid: collectGrid(),
                  periodCount,
                },
              });
              toast('ok', '已存为模板', `「${created.name}」之后可以在别的档案里套用。`);
              nameInput.value = '';
              await loadTemplates();
              paint();
            } catch (err) {
              toastError(err, '存为模板失败');
            }
          },
        }, '保存')),
      '模板按**科目名称**记录（不是 ID），因此可以套用到别的档案；'
      + '那边没有同名科目时会跳过并提示。'));

      if (runtime.templates.length === 0) {
        body.appendChild(h('p.card-desc', { style: { margin: 0 } }, '还没有任何课表模板。'));
      } else {
        body.appendChild(field('套用模板', select([
          { value: '', label: '（请选择模板）' },
          ...runtime.templates.map((t) => ({ value: t.id, label: `${t.name}（${t.periodCount} 节）` })),
        ], cfg.templateId, (v) => { cfg.templateId = v; }),
        '**只填空格，不覆盖已有课程**；想完全照搬，先「批量清空」再套用。'));
      }
    }
  }

  async function loadTemplates() {
    try {
      runtime.templates = await api('/admin/timetable-templates');
    } catch {
      runtime.templates = [];
    }
  }

  paint();

  modal({
    title: '课表批量操作',
    width: 'wide',
    body,
    confirmText: '执行',
    onConfirm: async () => {
      const code = () => {
        markDirty();
        repaintTab();
      };

      if (cfg.action === 'copy') {
        const result = applyCopy();
        code();
        toast('ok', '已复制', `写入 ${result.written} 格`
          + `${result.skipped ? `，跳过 ${result.skipped} 格（已有课）` : ''}。`
          + '记得点右上角「保存并下发」才会生效。');
        return true;
      }

      if (cfg.action === 'clear') {
        const result = applyClear();
        code();
        toast('ok', '已清空', `清掉 ${result.cleared} 格。记得点右上角「保存并下发」才会生效。`);
        return true;
      }

      if (cfg.action === 'replace') {
        const result = applyReplace();
        if (result.invalid) {
          toast('warn', '请选择两个不同的科目');
          return false;
        }

        code();
        toast('ok', '已替换', `换了 ${result.count} 格。记得点右上角「保存并下发」才会生效。`);
        return true;
      }

      // 模板：执行 = 套用选中的那个
      const template = runtime.templates.find((t) => t.id === cfg.templateId);
      if (!template) {
        toast('warn', '请先选择一个模板');
        return false;
      }

      const result = applyTemplate(template);
      code();
      toast('ok', '已套用模板', `填入 ${result.applied} 格`
        + `${result.missing.length ? `；模板里的「${result.missing.join('、')}」在这套档案里没有同名科目，已跳过` : ''}。`
        + '记得点右上角「保存并下发」才会生效。');
      return true;
    },
  });

  loadTemplates().then(paint);
}

/** 取「某一天」的课表；不存在返回 null（空天保持空，不自动创建）。 */
function getDayPlan(layout, day) {
  return state.content.classPlans.find((p) => p.timeLayoutId === layout.id
    && (p.daysOfWeek || []).length === 1 && p.daysOfWeek[0] === day) || null;
}

/** 确保某天的课表存在（用户实际填课时才创建），返回课表对象。 */
function ensureDayPlan(layout, day) {
  let plan = getDayPlan(layout, day);
  if (!plan) {
    const label = GRID_DAYS.find((d) => d.value === day)?.label || String(day);
    plan = {
      id: genId(),
      name: `${label}课表`,
      timeLayoutId: layout.id,
      isEnabled: true,
      daysOfWeek: [day],
      weekInterval: 0,
      weekOffset: 0,
      slots: [],
    };
    state.content.classPlans.push(plan);
  }
  return plan;
}

/** 科目简称：优先 initial，其次 name 首字，最后「未命名」。 */
function subjectShort(subject) {
  if (subject.initial) return subject.initial;
  if (subject.name) return subject.name.slice(0, 1);
  return '未命名';
}

/** 读取某一格的课程；空格返回 null。 */
function readSlot(layout, day, index) {
  const plan = getDayPlan(layout, day);
  const slot = plan ? (plan.slots || []).find((s) => s.index === index) : null;
  return slot && slot.subjectId
    ? { subjectId: slot.subjectId, isEnabled: slot.isEnabled !== false }
    : null;
}

/** 写入某一格的课程；`value` 为 null 表示清空该格。空天只有真正排课时才会创建课表。 */
function writeSlot(layout, day, index, value) {
  if (!value) {
    const plan = getDayPlan(layout, day);
    const slot = plan ? (plan.slots || []).find((s) => s.index === index) : null;
    if (slot) slot.subjectId = null;
    return;
  }

  const plan = ensureDayPlan(layout, day);
  let slot = (plan.slots || []).find((s) => s.index === index);
  if (!slot) {
    slot = { index, subjectId: value.subjectId, isEnabled: value.isEnabled !== false };
    plan.slots.push(slot);
    plan.slots.sort((a, b) => a.index - b.index);
  } else {
    slot.subjectId = value.subjectId;
    slot.isEnabled = value.isEnabled !== false;
  }
}

/** 选中格并自动前进到下一节（同一列内；已是最后一节则停在原地）。 */
function advanceSelection(day, index, periodCount) {
  state.scheduleSelection = index < periodCount - 1
    ? { day, index: index + 1 }
    : { day, index };
}

/**
 * 单个课表单元格：整格呈现「科目简称 + 全称 + 教师」，可通过点击或拖拽排课、调课。
 */
function schedCell(layout, day, index, periodCount) {
  const current = readSlot(layout, day, index);
  const subject = current ? state.content.subjects.find((s) => s.id === current.subjectId) : null;
  const selected = !!state.scheduleSelection
    && state.scheduleSelection.day === day
    && state.scheduleSelection.index === index;

  const classes = ['sched-cell'];
  if (subject) classes.push('filled');
  if (current && !current.isEnabled) classes.push('off');
  if (selected) classes.push('selected');

  const cell = h('div', {
    class: classes.join(' '),
    role: 'button',
    tabindex: '0',
    title: subject ? '拖动可调到其他格子；双击清空该格' : '点击选中后点科目，或把科目拖进来',
  },
    subject
      ? h('span.sched-initial', subjectShort(subject))
      : h('span.sched-plus', '＋'),
    subject ? h('span.sched-name', subject.name || '未命名') : null,
    subject && subject.teacherName ? h('span.sched-teacher', subject.teacherName) : null,
  );

  if (subject) cell.draggable = true;

  cell.addEventListener('click', () => {
    if (state.armedSubjectId) {
      writeSlot(layout, day, index, { subjectId: state.armedSubjectId, isEnabled: true });
      state.dirty = true;
      advanceSelection(day, index, periodCount);
    } else {
      state.scheduleSelection = { day, index };
    }
    repaintTab();
  });

  cell.addEventListener('dblclick', () => {
    writeSlot(layout, day, index, null);
    state.dirty = true;
    state.scheduleSelection = { day, index };
    repaintTab();
  });

  cell.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      cell.click();
    }
  });

  cell.addEventListener('dragstart', (e) => {
    if (!subject) {
      e.preventDefault();
      return;
    }
    dragPayload = { kind: 'slot', day, index };
    e.dataTransfer.effectAllowed = 'move';
    try {
      e.dataTransfer.setData('text/plain', `slot:${day}:${index}`);
    } catch { /* 非安全上下文下可能不可用，忽略即可 */ }
    cell.classList.add('dragging');
  });

  cell.addEventListener('dragend', () => {
    cell.classList.remove('dragging');
    dragPayload = null;
  });

  cell.addEventListener('dragover', (e) => {
    if (!dragPayload) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = dragPayload.kind === 'slot' ? 'move' : 'copy';
    cell.classList.add('drop-target');
  });

  cell.addEventListener('dragleave', () => cell.classList.remove('drop-target'));

  cell.addEventListener('drop', (e) => {
    e.preventDefault();
    cell.classList.remove('drop-target');
    applyCellDrop(layout, day, index, periodCount);
  });

  return cell;
}

/** 处理放置：科目 → 排课；已有课程 → 移动（目标已有课则互换）。 */
function applyCellDrop(layout, day, index, periodCount) {
  const payload = dragPayload;
  dragPayload = null;
  if (!payload) return;

  if (payload.kind === 'subject') {
    writeSlot(layout, day, index, { subjectId: payload.subjectId, isEnabled: true });
    state.dirty = true;
    advanceSelection(day, index, periodCount);
    repaintTab();
    return;
  }

  if (payload.kind !== 'slot') return;
  if (payload.day === day && payload.index === index) return;

  const source = readSlot(layout, payload.day, payload.index);
  if (!source) return;
  const target = readSlot(layout, day, index);

  writeSlot(layout, payload.day, payload.index, target);
  writeSlot(layout, day, index, source);
  state.dirty = true;
  state.scheduleSelection = { day, index };
  repaintTab();
}

/** 科目调色板：点选后点格子排课，或直接拖进格子。 */
function renderPalette(periodCount) {
  if (state.content.subjects.length === 0) {
    return h('div.sched-palette',
      h('span.palette-empty', '还没有科目。请先在「科目」标签页添加科目，再回到这里排课。'));
  }

  return h('div.sched-palette',
    ...state.content.subjects.map((subject) => {
      const armed = state.armedSubjectId === subject.id;
      const chip = h('div', {
        class: `sched-chip${armed ? ' armed' : ''}`,
        role: 'button',
        tabindex: '0',
        title: armed ? '再次点击可取消选中' : '点击选中后点格子排课；也可直接拖到格子',
      },
        h('b.sched-chip-initial', subjectShort(subject)),
        h('span.sched-chip-name', subject.name || '未命名'),
      );

      chip.draggable = true;

      chip.addEventListener('click', () => {
        if (armed) {
          state.armedSubjectId = null;
          repaintTab();
          return;
        }

        state.armedSubjectId = subject.id;
        const sel = state.scheduleSelection;
        const layout = classPlanLayout();
        if (sel && layout && sel.index < periodCount) {
          writeSlot(layout, sel.day, sel.index, { subjectId: subject.id, isEnabled: true });
          state.dirty = true;
          advanceSelection(sel.day, sel.index, periodCount);
        }
        repaintTab();
      });

      chip.addEventListener('keydown', (e) => {
        if (e.key === 'Enter' || e.key === ' ') {
          e.preventDefault();
          chip.click();
        }
      });

      chip.addEventListener('dragstart', (e) => {
        dragPayload = { kind: 'subject', subjectId: subject.id };
        e.dataTransfer.effectAllowed = 'copy';
        try {
          e.dataTransfer.setData('text/plain', `subject:${subject.id}`);
        } catch { /* 忽略 */ }
        chip.classList.add('dragging');
      });

      chip.addEventListener('dragend', () => {
        chip.classList.remove('dragging');
        dragPayload = null;
      });

      return chip;
    }),
  );
}

// ────────────────────────────── 科目 ──────────────────────────────

function renderSubjects() {
  const subjects = state.content.subjects;

  const rows = subjects.map((subject, index) => h('tr',
    h('td', textInput(subject.name, (v) => {
      subject.name = v;
      if (!subject.initial) subject.initial = v.slice(0, 1);
      markDirtyLight();
    }, '例如：语文')),
    h('td', textInput(subject.initial, (v) => {
      subject.initial = v;
      markDirtyLight();
    }, '语')),
    h('td', textInput(subject.teacherName, (v) => {
      subject.teacherName = v;
      markDirtyLight();
    }, '可选')),
    h('td', { style: { textAlign: 'center' } }, checkbox(subject.isOutDoor, (v) => {
      subject.isOutDoor = v;
      markDirtyLight();
    })),
    h('td', h('button.btn.btn-ghost.btn-sm', {
      type: 'button',
      onClick: () => {
        state.content.subjects.splice(index, 1);
        markDirtyValues();
      },
    }, icon('close', 14))),
  ));

  return h('div',
    h('div.toolbar',
      h('span', { style: { fontSize: '12.5px', color: 'var(--text-dim)' } },
        `共 ${subjects.length} 个科目。回到「课表」即可点选或拖拽科目进行排课。`),
      h('div.spacer'),
      h('button.btn.btn-sm.btn-primary', {
        type: 'button',
        onClick: () => {
          state.content.subjects.push({
            id: genId(),
            name: '',
            initial: '',
            teacherName: '',
            isOutDoor: false,
          });
          markDirtyValues();
        },
      }, '+ 添加科目'),
    ),
    subjects.length === 0
      ? emptyState('book', '还没有科目', '先添加科目，才能在课表中安排课程。')
      : h('div.table-wrap', { style: { maxHeight: '60vh', overflowY: 'auto' } },
        h('table.data',
          h('thead', h('tr',
            h('th', '科目名称'),
            h('th', { style: { width: '120px' } }, '简称'),
            h('th', { style: { width: '170px' } }, '任课教师'),
            h('th', { style: { width: '92px', textAlign: 'center' } }, '户外'),
            h('th', { style: { width: '60px' } }, ''),
          )),
          h('tbody', ...rows),
        ),
      ),
  );
}

function textInput(value, onChange, placeholder) {
  const input = h('input', {
    type: 'text',
    value: value || '',
    placeholder: placeholder || '',
  });
  input.addEventListener('change', () => onChange(input.value.trim()));
  return input;
}

function checkbox(checked, onChange) {
  const input = h('input', { type: 'checkbox', onChange: (e) => onChange(e.target.checked) });
  input.checked = !!checked;
  return input;
}

// ────────────────────────────── 自定义设置 ──────────────────────────────

function renderSettings() {
  const settings = state.content.settings;

  const announcementInput = h('textarea', {
    placeholder: '例如：本周起执行夏季作息，请注意作息变化。',
    onInput: (e) => {
      settings.announcement = e.target.value;
      markDirtyLight();
    },
  });
  announcementInput.value = settings.announcement || '';

  const lockCheckbox = checkbox(settings.lockLocalEditing, (v) => {
    settings.lockLocalEditing = v;
    markDirtyLight();
  });

  const entries = Object.entries(settings.values || {});
  const kvRows = entries.map(([key, value], index) => h('div.kv-row',
    h('input', {
      type: 'text',
      value: key,
      placeholder: '键名，例如 播报.启用',
      onChange: (e) => {
        const newKey = e.target.value.trim();
        if (!newKey) return;
        const rebuilt = {};
        entries.forEach(([k, v], i) => {
          rebuilt[i === index ? newKey : k] = v;
        });
        settings.values = rebuilt;
        markDirtyValues();
      },
    }),
    h('input', {
      type: 'text',
      value: value,
      placeholder: '值',
      onChange: (e) => {
        settings.values[key] = e.target.value;
        markDirtyLight();
      },
    }),
    h('button.btn.btn-ghost.btn-sm', {
      type: 'button',
      onClick: () => {
        delete settings.values[key];
        markDirtyValues();
      },
    }, icon('close', 14)),
  ));

  return h('div',
    h('div.notice.notice-info',
      h('span.notice-icon', 'i'),
      h('div', '自定义配置项会随配置一起下发给客户端插件，插件可按需读取。键名建议使用「命名空间.键名」的形式，避免冲突。'),
    ),
    field('下发公告', announcementInput, '客户端同步后会读取该文本，可在插件界面中向使用者展示。'),
    h('label.checkbox-field', lockCheckbox, '锁定客户端本地编辑（插件将提示配置由集控统一管理）'),
    h('div', { style: { display: 'flex', alignItems: 'center', gap: '10px', margin: '18px 0 10px' } },
      h('div', { style: { fontSize: '13px', fontWeight: '600' } }, `自定义配置项（${entries.length}）`),
      h('div', { style: { flex: '1' } }),
      h('button.btn.btn-sm', {
        type: 'button',
        onClick: () => {
          settings.values[`自定义.键${entries.length + 1}`] = '';
          markDirtyValues();
        },
      }, '+ 添加配置项'),
    ),
    entries.length === 0
      ? h('div', { style: { fontSize: '12.5px', color: 'var(--text-faint)', padding: '10px 0' } },
        '暂无自定义配置项。')
      : h('div', ...kvRows),
  );
}
