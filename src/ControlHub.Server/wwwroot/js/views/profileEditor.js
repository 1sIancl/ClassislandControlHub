/**
 * 配置档案编辑器。
 * 左侧为分类（时间表 / 课表 / 科目 / 自定义设置），右侧为具体内容编辑区。
 * 所有修改先落在内存对象上，点击「保存并下发」后一次性提交，由服务端做规范化与版本递增。
 */

import { api } from '../core/api.js';
import {
  h, clear, toast, loadingBlock, modal, confirmDialog, field, select,
  emptyState, formatDateTime, copyText,
} from '../core/ui.js';

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

const TIME_KINDS = [
  { value: 'class', label: '上课' },
  { value: 'break', label: '课间' },
  { value: 'separator', label: '分割线' },
  { value: 'action', label: '行动' },
];

const WEEKDAYS = [
  { value: 1, label: '周一' },
  { value: 2, label: '周二' },
  { value: 3, label: '周三' },
  { value: 4, label: '周四' },
  { value: 5, label: '周五' },
  { value: 6, label: '周六' },
  { value: 0, label: '周日' },
];

/** 编辑器状态。 */
let state = {
  profileId: null,
  profile: null,
  content: null,
  activeTab: 'timeLayouts',
  selectedLayoutId: null,
  selectedPlanId: null,
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
      h('div#tabBody', renderTab()),
    ),
  ));
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
        state.dirty ? h('span.badge.badge-warn', '有未保存修改') : null,
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
    toast('error', '保存失败', err.message);
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
  repaint();
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
      select(
        layouts.length === 0
          ? [{ value: '', label: '（还没有时间表）' }]
          : layouts.map((l) => ({ value: l.id, label: `${l.name}（${classCount(l)} 节）` })),
        state.selectedLayoutId,
        (v) => {
          state.selectedLayoutId = v;
          repaintTab();
        },
      ),
      h('button.btn.btn-sm.btn-primary', { type: 'button', onClick: addLayout }, '+ 新建时间表'),
      layout ? h('button.btn.btn-sm', { type: 'button', onClick: () => renameLayout(layout) }, '重命名') : null,
      layout ? h('button.btn.btn-sm', { type: 'button', onClick: () => generateSchedule(layout) }, '⚡ 快速生成作息') : null,
      layout ? h('button.btn.btn-sm.btn-danger', { type: 'button', onClick: () => removeLayout(layout) }, '删除') : null,
      h('div.spacer'),
      layout ? h('button.btn.btn-sm', { type: 'button', onClick: () => addItem(layout) }, '+ 添加时间点') : null,
    ),
    !layout
      ? emptyState('🕐', '还没有时间表',
        '时间表定义了一天的作息时间点，课表以它的「上课」时间点为基准。',
        h('button.btn.btn-primary', { type: 'button', onClick: addLayout }, '新建时间表'))
      : h('div.table-wrap', { style: { maxHeight: '58vh', overflowY: 'auto' } },
        h('table.data',
          h('thead', h('tr',
            h('th', { style: { width: '64px' } }, '序号'),
            h('th', { style: { width: '150px' } }, '开始时间'),
            h('th', { style: { width: '150px' } }, '结束时间'),
            h('th', { style: { width: '130px' } }, '类型'),
            h('th', '名称 / 备注'),
            h('th', { style: { width: '82px' } }, '默认隐藏'),
            h('th', { style: { width: '60px' } }, ''),
          )),
          h('tbody', ...layout.items.map((item, index) => renderItemRow(layout, item, index))),
        ),
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

function renderItemRow(layout, item, index) {
  const startInput = timeInput(item.startTime, (v) => {
    item.startTime = v;
    if (item.kind === 'separator' || item.kind === 'action') item.endTime = v;
    markDirtyValues();
  });

  const endInput = timeInput(item.endTime, (v) => {
    item.endTime = v;
    markDirtyValues();
  });
  const isPoint = item.kind === 'separator' || item.kind === 'action';
  if (isPoint) endInput.disabled = true;

  const kindSelect = select(TIME_KINDS, item.kind, (v) => {
    item.kind = v;
    if (v === 'separator' || v === 'action') item.endTime = item.startTime;
    markDirtyValues();
  });

  const nameInput = h('input', {
    type: 'text',
    value: item.breakName || '',
    placeholder: item.kind === 'break' ? '课间名称，留空显示「课间休息」' : '备注（可选）',
    onInput: (e) => {
      item.breakName = e.target.value.trim() || null;
      markDirtyValues();
    },
  });

  const hideCheckbox = h('input', {
    type: 'checkbox',
    onChange: (e) => {
      item.isHideDefault = e.target.checked;
      markDirtyValues();
    },
  });
  hideCheckbox.checked = !!item.isHideDefault;

  return h('tr',
    h('td.period-index', item.kind === 'class' ? `第 ${countClassBefore(layout, index) + 1} 节` : '—'),
    h('td', startInput),
    h('td', endInput),
    h('td', kindSelect),
    h('td', nameInput),
    h('td', { style: { textAlign: 'center' } }, hideCheckbox),
    h('td',
      h('button.btn.btn-ghost.btn-sm', {
        type: 'button',
        title: '删除该时间点',
        onClick: () => {
          layout.items.splice(index, 1);
          markDirtyValues();
        },
      }, '✕'),
    ),
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
  const id = crypto.randomUUID();
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

function currentPlan() {
  return state.content.classPlans.find((p) => p.id === state.selectedPlanId) || null;
}

function layoutOf(plan) {
  return state.content.timeLayouts.find((l) => l.id === plan?.timeLayoutId) || null;
}

function renderClassPlans() {
  const plans = state.content.classPlans;
  const plan = currentPlan();

  return h('div',
    h('div.toolbar',
      select(
        plans.length === 0
          ? [{ value: '', label: '（还没有课表）' }]
          : plans.map((p) => ({ value: p.id, label: `${p.name}（${(p.slots || []).length} 节）` })),
        state.selectedPlanId,
        (v) => {
          state.selectedPlanId = v;
          repaintTab();
        },
      ),
      h('button.btn.btn-sm.btn-primary', { type: 'button', onClick: addPlan }, '+ 新建课表'),
      plan ? h('button.btn.btn-sm', { type: 'button', onClick: () => duplicatePlan(plan) }, '复制') : null,
      plan ? h('button.btn.btn-sm.btn-danger', { type: 'button', onClick: () => removePlan(plan) }, '删除') : null,
    ),
    !plan
      ? emptyState('📅', '还没有课表',
        '课表以时间表的「上课」时间点为基准排列课程。',
        h('button.btn.btn-primary', { type: 'button', onClick: addPlan }, '新建课表'))
      : renderPlanEditor(plan),
  );
}

function renderPlanEditor(plan) {
  const layout = layoutOf(plan);

  // 基本信息
  const nameInput = h('input', {
    type: 'text',
    value: plan.name,
    onInput: (e) => {
      plan.name = e.target.value;
      state.dirty = true;
    },
  });

  const layoutSelect = select(
    state.content.timeLayouts.map((l) => ({ value: l.id, label: `${l.name}（${classCount(l)} 节）` })),
    plan.timeLayoutId,
    (v) => {
      plan.timeLayoutId = v;
      markDirtyValues();
    },
    state.content.timeLayouts.length === 0 ? '（请先创建时间表）' : undefined,
  );

  const enabledCheckbox = h('input', {
    type: 'checkbox',
    onChange: (e) => {
      plan.isEnabled = e.target.checked;
      state.dirty = true;
    },
  });
  enabledCheckbox.checked = plan.isEnabled !== false;

  // 触发规则
  const dayBoxes = WEEKDAYS.map((d) => {
    const box = h('input', {
      type: 'checkbox',
      onChange: () => {
        plan.daysOfWeek = dayBoxes
          .filter((entry) => entry.box.checked)
          .map((entry) => entry.day);
        state.dirty = true;
      },
    });
    box.checked = (plan.daysOfWeek || []).includes(d.value);
    return { day: d.value, box, node: h('label', {
      style: {
        display: 'inline-flex', alignItems: 'center', gap: '6px',
        marginRight: '16px', fontSize: '13px', cursor: 'pointer',
      },
    }, box, d.label) };
  });

  const intervalSelect = select([
    { value: '0', label: '每周都生效' },
    { value: '2', label: '每 2 周生效（单双周）' },
    { value: '3', label: '每 3 周生效' },
    { value: '4', label: '每 4 周生效' },
  ], String(plan.weekInterval || 0), (v) => {
    plan.weekInterval = Number(v);
    markDirtyValues();
  });

  const offsetSelect = select([
    { value: '0', label: '第 1 周' },
    { value: '1', label: '第 2 周' },
    { value: '2', label: '第 3 周' },
    { value: '3', label: '第 4 周' },
  ], String(plan.weekOffset || 0), (v) => {
    plan.weekOffset = Number(v);
    state.dirty = true;
  });

  // 节次表
  const periods = layout ? layout.items.filter((i) => i.kind === 'class') : [];
  const slots = plan.slots || (plan.slots = []);

  const periodTable = !layout
    ? h('div.notice.notice-danger',
      h('span.notice-icon', '!'),
      h('div', '该课表关联的时间表不存在，请先在上方选择一个时间表。'))
    : periods.length === 0
      ? h('div.notice.notice-warn',
        h('span.notice-icon', '!'),
        h('div', '所选时间表没有任何「上课」时间点，无法排课。请先在「时间表」中添加上课时间点。'))
      : h('div.table-wrap', { style: { maxHeight: '48vh', overflowY: 'auto' } },
        h('table.period-table',
          h('thead', h('tr',
            h('th', { style: { width: '60px' } }, '节次'),
            h('th', { style: { width: '140px' } }, '时间'),
            h('th', '科目'),
          )),
          h('tbody', ...periods.map((period, index) => {
            const slot = slots.find((s) => s.index === index) || { index, subjectId: null, isEnabled: true };
            if (!slots.includes(slot)) slots.push(slot);

            const subjectSelect = select(
              [
                { value: '', label: '（无课 / 空堂）' },
                ...state.content.subjects.map((s) => ({
                  value: s.id,
                  label: s.initial ? `${s.name}（${s.initial}）` : s.name,
                })),
              ],
              slot.subjectId || '',
              (v) => {
                slot.subjectId = v || null;
                state.dirty = true;
              },
            );

            return h('tr',
              h('td.period-index', `第 ${index + 1} 节`),
              h('td.period-time', `${period.startTime.slice(0, 5)} - ${period.endTime.slice(0, 5)}`),
              h('td', subjectSelect),
            );
          })),
        ),
      );

  return h('div',
    h('div.form-row',
      field('课表名称', nameInput),
      field('关联时间表', layoutSelect),
      field('周次规则', intervalSelect, plan.weekInterval > 0
        ? '用于单双周 / 多周轮换的课表。'
        : '设置为多周生效后可进一步选择生效周次。'),
      plan.weekInterval > 0 ? field('生效周次', offsetSelect) : null,
    ),
    h('div', { style: { marginBottom: '14px' } },
      h('div', { style: { fontSize: '12.5px', color: 'var(--text-dim)', marginBottom: '8px' } },
        '适用星期（不勾选表示每天生效）'),
      h('div', ...dayBoxes.map((entry) => entry.node)),
    ),
    h('label.checkbox-field', enabledCheckbox, '默认启用（无需触发规则即可生效）'),
    h('div', { style: { fontSize: '12.5px', color: 'var(--text-dim)', margin: '10px 0 8px' } },
      `课程安排（共 ${periods.length} 节）`),
    periodTable,
  );
}

function addPlan() {
  const layout = currentLayout() || state.content.timeLayouts[0];
  const id = crypto.randomUUID();
  const plan = {
    id,
    name: `新课表 ${state.content.classPlans.length + 1}`,
    timeLayoutId: layout?.id || '',
    isEnabled: true,
    daysOfWeek: [],
    weekInterval: 0,
    weekOffset: 0,
    slots: [],
  };

  if (layout) {
    const periods = layout.items.filter((i) => i.kind === 'class');
    plan.slots = periods.map((p, index) => ({ index, startTime: p.startTime, subjectId: null, isEnabled: true }));
  }

  state.content.classPlans.push(plan);
  state.selectedPlanId = id;
  markDirtyValues();
}

function duplicatePlan(plan) {
  const copy = JSON.parse(JSON.stringify(plan));
  copy.id = crypto.randomUUID();
  copy.name = `${plan.name} 副本`;
  state.content.classPlans.push(copy);
  state.selectedPlanId = copy.id;
  markDirtyValues();
}

function removePlan(plan) {
  confirmDialog('删除课表', `确定删除课表「${plan.name}」吗？`, '删除', true).then((ok) => {
    if (!ok) return;
    state.content.classPlans = state.content.classPlans.filter((p) => p.id !== plan.id);
    state.selectedPlanId = state.content.classPlans[0]?.id || null;
    markDirtyValues();
  });
}

// ────────────────────────────── 科目 ──────────────────────────────

function renderSubjects() {
  const subjects = state.content.subjects;

  const rows = subjects.map((subject, index) => h('tr',
    h('td', textInput(subject.name, (v) => {
      subject.name = v;
      if (!subject.initial) subject.initial = v.slice(0, 1);
      state.dirty = true;
    }, '例如：语文')),
    h('td', textInput(subject.initial, (v) => {
      subject.initial = v;
      state.dirty = true;
    }, '语')),
    h('td', textInput(subject.teacherName, (v) => {
      subject.teacherName = v;
      state.dirty = true;
    }, '可选')),
    h('td', { style: { textAlign: 'center' } }, checkbox(subject.isOutDoor, (v) => {
      subject.isOutDoor = v;
      state.dirty = true;
    })),
    h('td', h('button.btn.btn-ghost.btn-sm', {
      type: 'button',
      onClick: () => {
        state.content.subjects.splice(index, 1);
        markDirtyValues();
      },
    }, '✕')),
  ));

  return h('div',
    h('div.toolbar',
      h('span', { style: { fontSize: '12.5px', color: 'var(--text-dim)' } },
        `共 ${subjects.length} 个科目，课表排课时会作为下拉选项。`),
      h('div.spacer'),
      h('button.btn.btn-sm.btn-primary', {
        type: 'button',
        onClick: () => {
          state.content.subjects.push({
            id: crypto.randomUUID(),
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
      ? emptyState('📖', '还没有科目', '先添加科目，才能在课表中安排课程。')
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
      state.dirty = true;
    },
  });
  announcementInput.value = settings.announcement || '';

  const lockCheckbox = checkbox(settings.lockLocalEditing, (v) => {
    settings.lockLocalEditing = v;
    state.dirty = true;
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
        state.dirty = true;
      },
    }),
    h('button.btn.btn-ghost.btn-sm', {
      type: 'button',
      onClick: () => {
        delete settings.values[key];
        markDirtyValues();
      },
    }, '✕'),
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
