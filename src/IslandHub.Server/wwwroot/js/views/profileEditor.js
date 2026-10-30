/**
 * 配置档案编辑器：时间表 / 课表 / 科目 / 自定义设置四个标签页。
 * 修改先落在内存对象上，点「保存并下发」一次性提交。
 */

import { api } from '../core/api.js?v=160';
import { toastError } from '../core/errors.js?v=160';
import {
  h, clear, toast, loadingBlock, modal, confirmDialog, field, icon, select,
  emptyState, formatDateTime,
} from '../core/ui.js?v=160';
import { SCHED_SCALES, getSchedScale, setSchedScale } from '../core/prefs.js?v=160';
import { t } from './../core/i18n.js?v=160';

export const meta = {
  title: t('profiles.editor.title'),
  subtitle: '',
};

const TABS = [
  { key: 'timeLayouts', label: t('profiles.editor.tabLayouts') },
  { key: 'classPlans', label: t('profiles.editor.tabTimetable') },
  { key: 'subjects', label: t('profiles.editor.tabSubjects') },
  { key: 'settings', label: t('profiles.editor.tabCustom') },
];

/** 时间点类型：点击单元格上的类型标签可循环切换，避免使用下拉框。 */
const TIME_KIND_ORDER = ['class', 'break', 'separator', 'action'];
const TIME_KIND_LABEL = { class: t('profiles.editor.kindClass'), break: t('profiles.editor.kindBreak'), separator: t('profiles.editor.kindSeparator'), action: t('profiles.editor.kindAction') };

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
    return h('span.segmented-empty', emptyLabel || t('profiles.editor.none'));
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
  container.appendChild(loadingBlock(t('profiles.editor.loading')));

  const profile = await api(`/admin/profiles/${params.id}`);
  state = {
    profileId: params.id,
    profile,
    content: normalizeContent(profile.content),
    activeTab: state.activeTab,
    selectedLayoutId: profile.content?.timeLayouts?.[0]?.id || null,
    selectedPlanId: profile.content?.classPlans?.[0]?.id || null,
    classPlanLayoutId: state.classPlanLayoutId || profile.content?.timeLayouts?.[0]?.id || null,
    rotationWeek: 1,
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
    rotation: {
      weeks: normalizeWeeks(c.rotation?.weeks),
      semesterStartDate: c.rotation?.semesterStartDate || '',
      offset: Number(c.rotation?.offset) || 0,
    },
    settings: {
      values: c.settings?.values || {},
      lockLocalEditing: !!c.settings?.lockLocalEditing,
      announcement: c.settings?.announcement || '',
    },
  };
}

// ── 多周轮换 ──────────────────────────────────────────────────────────
//
// ClassIsland 原生就支持多周轮换（每张课表带「周期 + 周期内第几周」，宿主用「轮换起点 + 偏移」
// 算今天该用哪张）。IslandHub 这边**不重新实现轮换**，只做两件事：让管理员能逐周排课、
// 把这些从课表推不出来的信息（起点 / 偏移）随档案下发。
//
// 界面上「周」的归属只由课表自己的 weekInterval / weekOffset 决定——不另存一份，
// 避免出现「档案说 2 周、课表说第 3 周」这种两边打架的静默失效。

/** 轮换周期上限（周），与 ClassIsland 宿主的「最大轮换周期」一致。 */
const MAX_ROTATION_WEEKS = 9;

/** 周期取值归一：只有 2~9 才算轮换，其余一律视为「不轮换」。 */
function normalizeWeeks(value) {
  const n = Math.floor(Number(value) || 0);
  return n >= 2 && n <= MAX_ROTATION_WEEKS ? n : 1;
}

/** 本档案的轮换设置（保证存在，便于就地改）。 */
function profileRotation() {
  if (!state.content.rotation) {
    state.content.rotation = { weeks: 1, semesterStartDate: '', offset: 0 };
  }

  return state.content.rotation;
}

/** 课表属于第几周（1..N）；「每周生效」返回 0。 */
function planWeek(plan) {
  const interval = Number(plan.weekInterval) || 0;
  return interval < 2 ? 0 : (Number(plan.weekOffset) || 0) + 1;
}

/** 当前正在编辑的轮换周（1..周期）。 */
function currentWeek() {
  const weeks = profileRotation().weeks;
  const week = Number(state.rotationWeek) || 1;
  return week >= 1 && week <= weeks ? week : 1;
}

/** 某一周里已排课的天数（「每周生效」的课表算作第 1 周）。 */
function weekDayCount(layout, week) {
  const days = new Set();
  for (const plan of state.content.classPlans) {
    if (plan.timeLayoutId !== layout.id || (plan.daysOfWeek || []).length !== 1) {
      continue;
    }

    if ((planWeek(plan) || 1) === week) {
      days.add(plan.daysOfWeek[0]);
    }
  }

  return days.size;
}

/** 去掉课表名里的「（第 N 周）」后缀，便于按周复制时重建名字。 */
function stripWeekSuffix(name) {
  return String(name || '').replace(/（第\s*\d+\s*周）$/, '').trim();
}

/** 复制一份课表到指定周（新 id、新名字、深拷贝课程格子）。 */
function clonePlanForWeek(plan, week, weeks) {
  return {
    ...plan,
    id: genId(),
    name: t('profiles.editor.planWeekName', {
      name: stripWeekSuffix(plan.name) || t('profiles.editor.timetableFallback'),
      week,
    }),
    weekInterval: weeks,
    weekOffset: week - 1,
    slots: (plan.slots || []).map((s) => ({ ...s })),
  };
}

/**
 * 调整轮换周期。
 *
 * 两条安全线：
 * ① **新周期里每天都必须有课表**——只把第 1 周标成第 1 周、留着第 2 周空着，
 *    那一周的教室大屏就是空的，而且要等到那周上课才会被发现，所以自动从第 1 周补齐；
 * ② **缩短周期会删课表**（第 3 周的内容没有立足之地），删除前必须确认，并把要删的数量讲清楚。
 */
function setRotationWeeks(weeks) {
  const rotation = profileRotation();
  if (weeks === rotation.weeks) {
    return;
  }

  /** 取某一周里「按天」的课表（每周生效的算作第 1 周）。 */
  const dayPlansOfWeek = (week) => state.content.classPlans.filter((p) => {
    const w = planWeek(p) || 1;
    return w === week && (p.daysOfWeek || []).length === 1;
  });

  if (weeks < 2) {
    const doomed = state.content.classPlans.filter((p) => (planWeek(p) || 1) > 1);
    const run = () => {
      for (const plan of state.content.classPlans) {
        plan.weekInterval = 0;
        plan.weekOffset = 0;
      }

      if (doomed.length > 0) {
        const drop = new Set(doomed);
        state.content.classPlans = state.content.classPlans.filter((p) => !drop.has(p));
      }

      rotation.weeks = 1;
      state.rotationWeek = 1;
      markDirty();
      repaintTab();
      toast('ok', t('profiles.editor.rotationOff'), doomed.length > 0
        ? t('profiles.editor.rotationOffDone', { count: doomed.length })
        : t('profiles.editor.rotationOffAll'));
    };

    if (doomed.length === 0) {
      run();
      return;
    }

    confirmDialog(t('profiles.editor.disableRotation'),
      t('profiles.editor.disableRotationLead', { count: doomed.length })
      + t('profiles.editor.disableRotationTail'),
      t('profiles.editor.deleteAndDisable'), true).then((ok) => { if (ok) run(); });
    return;
  }

  const dropped = state.content.classPlans.filter((p) => (planWeek(p) || 1) > weeks).length;
  const run = () => {
    const template = new Map();
    for (const plan of dayPlansOfWeek(1)) {
      template.set(plan.daysOfWeek[0], plan);
    }

    const keep = [];
    const others = [];
    for (const plan of state.content.classPlans) {
      const week = planWeek(plan) || 1;
      if (week === 1) {
        keep.push(plan);
      } else if (week <= weeks) {
        others.push(plan);
      }
      // week > 新周期：直接丢弃（第 1 周里已有同名同天的课表，删除前已确认过）。
    }

    let copied = 0;
    for (let week = 2; week <= weeks; week++) {
      const existing = new Set(others
        .filter((p) => planWeek(p) === week)
        .map((p) => p.daysOfWeek[0]));
      for (const [day, plan] of template) {
        if (existing.has(day)) {
          continue;
        }

        others.push(clonePlanForWeek(plan, week, weeks));
        copied++;
      }
    }

    for (const plan of keep) {
      plan.weekInterval = weeks;
      plan.weekOffset = 0;
    }

    // 一定要把重建后的清单写回去：`others` 里是本次新造的各周副本，
    // 只 push 到临时数组而不赋值，表现就是「切换周次后新周仍然是空的」。
    state.content.classPlans = [...keep, ...others];

    rotation.weeks = weeks;
    if ((Number(state.rotationWeek) || 1) > weeks) {
      state.rotationWeek = 1;
    }

    markDirty();
    repaintTab();
    toast('ok', t('profiles.editor.rotationOnDone', { weeks }),
      copied > 0
        ? t('profiles.editor.rotationOnHint', { count: copied })
        : t('profiles.editor.rotationOnHint2'));
  };

  if (dropped > 0) {
    confirmDialog(t('profiles.editor.shortenRotation'),
      t('profiles.editor.shortenRotationConfirm', { week: weeks + 1, count: dropped }),
      t('profiles.editor.deleteAndContinue'), true).then((ok) => { if (ok) run(); });
    return;
  }

  run();
}

/** 顶部「多周轮换」设置条。 */
function renderRotationBar() {
  const rotation = profileRotation();
  const weeks = rotation.weeks;

  const weekSelect = select(
    Array.from({ length: MAX_ROTATION_WEEKS }, (_, i) => ({
      value: String(i + 1),
      label: i === 0 ? t('profiles.editor.noRotation') : t('profiles.editor.rotationWeeks', { n: i + 1 }),
    })),
    String(weeks),
    (v) => setRotationWeeks(Number(v)),
  );

  const startInput = h('input', {
    type: 'date',
    value: rotation.semesterStartDate || '',
    disabled: weeks < 2,
  });
  startInput.addEventListener('change', () => {
    rotation.semesterStartDate = startInput.value || '';
    markDirtyLight();
  });

  const offsetInput = h('input', {
    type: 'number',
    min: -9,
    max: 9,
    step: 1,
    value: String(rotation.offset || 0),
    disabled: weeks < 2,
    style: { width: '84px' },
  });
  offsetInput.addEventListener('change', () => {
    const n = Math.max(-9, Math.min(9, Math.floor(Number(offsetInput.value) || 0)));
    rotation.offset = n;
    offsetInput.value = String(n);
    markDirtyLight();
  });

  return h('div.card', { style: { marginBottom: '14px' } },
    h('div.card-head',
      h('div',
        h('h3', t('profiles.editor.rotation')),
        h('p.card-desc', t('profiles.editor.rotationLead')
          + t('profiles.editor.rotationBody')
          + t('profiles.editor.rotationOffsetHint'))),
      h('div.card-actions',
        h('span.badge' + (weeks < 2 ? '.badge-neutral' : '.badge-accent'),
          weeks < 2 ? t('profiles.editor.notEnabled') : t('profiles.editor.rotationSummary', { weeks }))),
    ),
    h('div.form-row', { style: { flexWrap: 'wrap' } },
      field(t('profiles.editor.rotationCycle'), weekSelect),
      field(t('profiles.editor.rotationStart'), startInput,
        t('profiles.editor.rotationStartHint')),
      field(t('profiles.editor.rotationOffset'), offsetInput,
        t('profiles.editor.rotationOffsetHint2')),
    ),
  );
}

function paint(container) {
  clear(container);

  // 页面标题由路由统一处理，这里只渲染内容。
  container.appendChild(h('div',
    renderHeader(),
    renderRotationBar(),
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
  if (state.dirty) flag.appendChild(h('span.badge.badge-warn', t('profiles.editor.unsaved')));
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
        state.profile.isDefault ? h('span.badge.badge-accent', t('profiles.editor.globalDefault')) : null,
        h('span.badge.badge-neutral', t('profiles.editor.contentRevision', { revision: state.profile.revision })),
        h('span#dirtyFlag'),
      ),
      h('div', { style: { fontSize: '12px', color: 'var(--text-faint)', marginTop: '3px' } },
        t('profiles.editor.lastUpdated', { time: formatDateTime(state.profile.updatedAt) })),
    ),
    h('div.card-actions',
      h('button.btn.btn-sm', {
        type: 'button',
        onClick: () => window.location.hash = '#/profiles',
      }, t('profiles.editor.back')),
      h('button.btn.btn-sm', {
        type: 'button',
        disabled: !state.dirty,
        onClick: () => discard(),
      }, t('profiles.editor.discard')),
      h('button.btn.btn-sm.btn-primary', {
        type: 'button',
        onClick: () => save(),
      }, t('profiles.editor.saveAndDeploy')),
    ),
  );
}

function discard() {
  confirmDialog(t('profiles.editor.discard'), t('profiles.editor.discardConfirm'), t('profiles.editor.discardYes'), true).then((ok) => {
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
    toast('ok', t('profiles.editor.saveOk'), notes.length > 0
      ? t('profiles.editor.saveOkNotes', { count: notes.length, revision: result.revision })
      : t('profiles.editor.saveOkRevision', { revision: result.revision }));

    if (notes.length > 0) {
      modal({
        title: t('profiles.editor.autoFixTitle'),
        width: 'wide',
        hideFooter: true,
        body: h('div',
          h('p', { style: { marginTop: 0, color: 'var(--text-dim)' } },
            t('profiles.editor.autoFixLead')),
          h('ul', { style: { margin: 0, paddingLeft: '20px', fontSize: '12.5px', lineHeight: '1.9' } },
            ...notes.map((n) => h('li', n))),
        ),
      });
    }

    paint(document.getElementById('content'));
  } catch (err) {
    toastError(err, t('profiles.editor.saveFailed'));
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
      h('span.toolbar-label', t('profiles.editor.tabLayouts')),
      segmented(
        layouts.map((l) => ({ id: l.id, label: t('profiles.editor.layoutOption', { name: l.name, count: classCount(l) }) })),
        state.selectedLayoutId,
        (v) => {
          state.selectedLayoutId = v;
          repaintTab();
        },
        t('profiles.editor.noLayouts'),
      ),
      h('div.spacer'),
      h('button.btn.btn-sm.btn-primary', { type: 'button', onClick: addLayout }, t('profiles.editor.newLayout')),
      layout ? h('button.btn.btn-sm', { type: 'button', onClick: () => renameLayout(layout) }, t('profiles.editor.rename')) : null,
      layout ? h('button.btn.btn-sm', { type: 'button', onClick: () => generateSchedule(layout) }, t('profiles.editor.quickGenerate')) : null,
      layout ? h('button.btn.btn-sm', { type: 'button', onClick: () => addItem(layout) }, t('profiles.editor.addPoint')) : null,
      layout ? h('button.btn.btn-sm.btn-danger', { type: 'button', onClick: () => removeLayout(layout) }, t('profiles.editor.delete')) : null,
    ),
    !layout
      ? emptyState('clock', t('profiles.editor.layoutsEmpty'),
        t('profiles.editor.layoutsLead'),
        h('button.btn.btn-primary', { type: 'button', onClick: addLayout }, t('profiles.editor.createLayout')))
      : h('div',
        // 引用提示（阶段六）：时间表是被课表共享引用的，改一次会波及所有引用它的周/天。
        // 不说清楚，管理员会以为只改了当前这一张课表（轮换周次里尤其容易误会）。
        referencedPlanCount(layout) > 0
          ? h('div.notice.notice-info', { style: { marginTop: '10px' } },
            h('span.notice-icon', 'i'),
            h('div', t('profiles.editor.layoutRefs', { count: referencedPlanCount(layout) })))
          : null,
        // 可视化时间轴放在表格**上方**（两个视图同时可见，不需要切来切去）：
        // 拖动色块平移整段时间、拖下边缘改时长，分钟吸附到 5 分钟并自动避让相邻时间点。
        renderLayoutTimeline(layout),
        h('div.grid-scroll', { style: { marginTop: '14px' } },
          h('div.period-grid', ...layout.items.map((item, index) => periodCell(layout, item, index))),
        ),
      ),
    layout && layout.items.length === 0
      ? h('div.notice.notice-warn', { style: { marginTop: '14px' } },
        h('span.notice-icon', '!'),
        h('div', t('profiles.editor.layoutEmpty')))
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
    title: t('profiles.editor.kindCycleHint'),
    onClick: () => {
      const next = TIME_KIND_ORDER[(TIME_KIND_ORDER.indexOf(item.kind) + 1) % TIME_KIND_ORDER.length];
      item.kind = next;
      if (next === 'separator' || next === 'action') item.endTime = item.startTime;
      markDirtyValues();
    },
  }, TIME_KIND_LABEL[item.kind] || t('profiles.editor.kindClass'));

  const flagButton = h('button.pcell-flag', {
    type: 'button',
    class: item.isHideDefault ? 'on' : '',
    title: t('profiles.editor.hideToggleHint'),
    onClick: () => {
      item.isHideDefault = !item.isHideDefault;
      markDirtyValues();
    },
  }, item.isHideDefault ? t('profiles.editor.hidden') : t('profiles.editor.shown'));

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

  // 显示名：一个输入框写两处——`displayName` 是通用显示名，课间还要同步写 `breakName`
  // （宿主只认 breakName，不同步的话教室里显示的仍是「课间休息」）。
  const nameInput = h('input.pcell-name', {
    type: 'text',
    value: item.displayName || item.breakName || '',
    placeholder: item.kind === 'break' ? t('profiles.editor.breakNamePlaceholder') : t('profiles.editor.noteOptional'),
    onInput: (e) => {
      const text = e.target.value.trim() || null;
      item.displayName = text;
      if (item.kind === 'break') {
        item.breakName = text;
      }
      markDirtyLight();
    },
  });

  // 附加设置（插件挂在时间点上的数据）：只读展示，点开看原始 JSON。
  // 这些值 A 端不解释语义，编辑它们等于让管理员手改插件数据，风险大于收益。
  // 行动（kind === 'action'）：类型与原始配置都能改——不然"能解析"只停在看得见的层面。
  const actionButton = item.kind === 'action'
    ? h('button.pcell-attach', {
      type: 'button',
      title: t('profiles.editor.actionHint'),
      onClick: () => openActionDialog(item),
    }, t('profiles.editor.actionBadge', { type: item.actionType || t('profiles.editor.actionNone') }))
    : null;

  const attachedCount = Object.keys(item.attachedSettings || {}).length;
  const attachBadge = attachedCount > 0
    ? h('button.pcell-attach', {
      type: 'button',
      title: t('profiles.editor.attachedHint', { count: attachedCount }),
      onClick: () => openAttachedDialog(item, attachedCount),
    }, t('profiles.editor.attachedBadge', { count: attachedCount }))
    : null;

  return h('div', { class: `pcell kind-${item.kind}${item.isHideDefault ? ' hidden-point' : ''}` },
    h('div.pcell-top',
      // 非上课时间点的类型已由类型标签表达，不再重复文字，保持单元格清爽。
      item.kind === 'class' ? h('span.pcell-no', t('profiles.editor.periodN', { n: countClassBefore(layout, index) + 1 })) : null,
      kindButton,
      h('div.spacer'),
      flagButton,
      h('button.pcell-del', {
        type: 'button',
        title: t('profiles.editor.deletePoint'),
        'aria-label': t('profiles.editor.deletePoint'),
        onClick: () => {
          layout.items.splice(index, 1);
          markDirtyValues();
        },
      }, icon('close', 14)),
    ),
    h('div.pcell-times', startInput, h('span.pcell-dash', '–'), endInput),
    nameInput,
    actionButton,
    attachBadge,
  );
}

/**
 * 附加设置的常见字段名 → 中文。
 *
 * <p>**这只是词表对照，不是语义解释**：键名由插件作者自己起，任何一项都可能被用成别的意思。
 * 所以界面上把原始键名摆在前面、中文只作提示，避免"看着像颜色就以为是颜色"。</p>
 */
const ATTACHED_FIELD_LABELS = {
  format: '格式',
  countdownformat: '倒计时格式',
  color: '颜色',
  colour: '颜色',
  foreground: '前景色',
  background: '背景色',
  enabled: '启用',
  isenabled: '启用',
  visible: '显示',
  isvisible: '显示',
  hidden: '隐藏',
  ishidden: '隐藏',
  duration: '时长',
  seconds: '秒',
  opacity: '不透明度',
  size: '大小',
  fontsize: '字号',
  text: '文本',
  content: '内容',
  title: '标题',
  position: '位置',
  align: '对齐',
  width: '宽度',
  height: '高度',
};

/** `#RGB` / `#RRGGBB` / `#RRGGBBAA`——只有这几种才当颜色渲染色块。 */
const ATTACHED_COLOR = /^#([0-9a-f]{3}|[0-9a-f]{6}|[0-9a-f]{8})$/i;

/**
 * 已知的附加设置类型（GUID → 名称 + 字段中文）。
 *
 * <p>
 * 依据：对一份真实的 ClassIsland 2.1.1.1 `.cidata` 统计后发现 **59 个时间点全部带附加设置、
 * 只有 3 个 GUID**，且每个 GUID 在 53~59 个时间点上字段完全一致——说明这些 GUID 是**固定常量**
 * （不是每台机器随机生成），可以安全地按类型命名。
 * </p>
 *
 * <p>
 * 两条克制：① 只翻**字段名**，不解释**枚举值**（`ExtraInfoType: 2` 到底是"倒计时"还是"课程表"
 * 没有公开契约，猜错会误导）；② GUID 对不上时（换了宿主版本/别的插件）自动退回通用结构解析，
 * 不会把未知数据硬套成已知类型。
 * </p>
 */
const KNOWN_ATTACHED = {
  // 天气预警 / 预报在时间点上的显示方式
  '7625de96-38aa-4b71-b478-3f156dd9458d': {
    nameKey: 'attached.known.weather.name',
    fields: {
      AlertShowMode: 'attached.known.weather.alertShowMode',
      ForecastShowMode: 'attached.known.weather.forecastShowMode',
      IsAttachSettingsEnabled: 'attached.known.common.enabled',
      IsActive: 'attached.known.common.active',
    },
  },
  // 上下课提醒（提示文本、遮罩文本、提前量）
  '08f0d9c3-c770-4093-a3d0-02f3d90c24bc': {
    nameKey: 'attached.known.classNotice.name',
    fields: {
      IsClassOnNotificationEnabled: 'attached.known.classNotice.on',
      IsClassOnPreparingNotificationEnabled: 'attached.known.classNotice.preparing',
      IsClassOffNotificationEnabled: 'attached.known.classNotice.off',
      ClassPreparingDeltaTime: 'attached.known.classNotice.deltaSeconds',
      ClassOnPreparingText: 'attached.known.classNotice.preparingText',
      OutdoorClassOnPreparingText: 'attached.known.classNotice.outdoorPreparingText',
      ClassOnPreparingMaskText: 'attached.known.classNotice.preparingMask',
      OutdoorClassOnPreparingMaskText: 'attached.known.classNotice.outdoorPreparingMask',
      ClassOnMaskText: 'attached.known.classNotice.onMask',
      ClassOffMaskText: 'attached.known.classNotice.offMask',
      ClassOffOverlayText: 'attached.known.classNotice.offOverlay',
      IsAttachSettingsEnabled: 'attached.known.common.enabled',
      IsActive: 'attached.known.common.active',
    },
  },
  // 时间点附加信息 / 倒计时
  '58e5b69a-764a-472b-bcf7-003b6a8c7fdf': {
    nameKey: 'attached.known.extraInfo.name',
    fields: {
      ShowExtraInfoOnTimePoint: 'attached.known.extraInfo.show',
      ExtraInfoType: 'attached.known.extraInfo.type',
      IsCountdownEnabled: 'attached.known.extraInfo.countdown',
      CountdownSeconds: 'attached.known.extraInfo.countdownSeconds',
      ExtraInfo4ShowSecondsSeconds: 'attached.known.extraInfo.showSecondsSeconds',
      ScheduleSpacing: 'attached.known.extraInfo.spacing',
      ShowCurrentLessonOnlyOnClass: 'attached.known.extraInfo.currentLessonOnly',
      IsNonExactCountdownEnabled: 'attached.known.extraInfo.nonExactCountdown',
      IsAttachSettingsEnabled: 'attached.known.common.enabled',
      IsActive: 'attached.known.common.active',
    },
  },
};

/**
 * 把一个附加设置的值解析成可读的一行。
 *
 * <p>能解析到什么程度：类型（空 / 开 / 关 / 数字 / 文本 / 颜色 / 列表 / 对象）全解析，
 * 对象再往下展开一层字段。**不做语义转换**——值怎么存就怎么显示，免得"看起来解析了、其实猜错了"。</p>

/**
 * 附加设置弹窗：把每一项都解析成可读结构，原始 JSON 收在折叠区里兜底。
 *
 * <p>为什么不做成可编辑：这些键值由插件读，写坏一个字段比不显示更糟；
 * 而且 A 端不知道每个字段的取值范围（比如格式串支持哪些占位符）。</p>
 */
function openAttachedDialog(item, count) {
  // 深拷贝：弹窗里改的是草稿，点「保存」才写回 item（再靠「保存并下发」落到服务端）。
  // 直接改原对象的话，用户点取消也回不去了。
  const draft = JSON.parse(JSON.stringify(item.attachedSettings || {}));
  const groupsHost = h('div');

  /** 单个字段的编辑控件：按值的类型选控件，对象/数组给 JSON 文本框。 */
  const fieldEditor = (value, onChange) => {
    if (typeof value === 'boolean') {
      const box = h('input', { type: 'checkbox' });
      box.checked = value;
      box.onchange = () => onChange(box.checked);
      return h('label.checkbox-field', box,
        h('span', value ? t('profiles.editor.attachedOn') : t('profiles.editor.attachedOff')));
    }

    if (typeof value === 'number') {
      const input = h('input', { type: 'number', step: 'any', value: String(value) });
      input.oninput = () => onChange(input.value === '' ? 0 : Number(input.value));
      return input;
    }

    if (typeof value === 'string') {
      const input = h('input', { type: 'text', value });
      input.oninput = () => onChange(input.value);
      return input;
    }

    // 对象 / 数组 / null：JSON 文本框（清空 = null）。
    const area = h('textarea', { rows: 3, style: { width: '100%' } });
    area.value = value === null ? '' : JSON.stringify(value, null, 2);
    area.oninput = () => {
      const text = area.value.trim();
      if (text === '') {
        onChange(null);
        area.style.borderColor = '';
        return;
      }

      try {
        onChange(JSON.parse(text));
        area.style.borderColor = '';
      } catch {
        // 只标红、不打断输入：边打字边弹错会让人没法编辑。
        area.style.borderColor = 'var(--danger)';
      }
    };
    return area;
  };

  const paintGroups = () => {
    clear(groupsHost);

    const guids = Object.keys(draft);
    if (guids.length === 0) {
      groupsHost.appendChild(h('p.card-desc', t('profiles.editor.attachedEmpty')));
      return;
    }

    groupsHost.appendChild(h('div.attach-groups', ...guids.map((guid, index) => {
      const value = draft[guid];
      const known = KNOWN_ATTACHED[guid.trim().toLowerCase()] ?? KNOWN_ATTACHED[guid.trim()];
      const isObject = value && typeof value === 'object' && !Array.isArray(value);

      const body = isObject
        ? h('div.attach-fields', ...Object.entries(value).map(([key, inner]) => {
          const label = known?.fields?.[key] ? t(known.fields[key]) : ATTACHED_FIELD_LABELS[key.toLowerCase()];
          const row = h('div.attach-row',
            h('span.attach-key', key, label ? h('span.attach-key-hint', `（${label}）`) : null));
          row.appendChild(fieldEditor(inner, (next) => {
            draft[guid][key] = next;
          }));
          return row;
        }))
        : h('div.attach-row', fieldEditor(value, (next) => {
          draft[guid] = next;
        }));

      return h('div.attach-group',
        h('div.attach-head',
          h('span.attach-index', t('profiles.editor.attachedGroup', { n: index + 1 })),
          known ? h('span.attach-known', t(known.nameKey)) : null,
          h('code.attach-guid', { title: guid }, guid),
          h('div.spacer'),
          h('button.btn.btn-sm.btn-danger', {
            type: 'button',
            title: t('profiles.editor.attachedRemoveHint'),
            onClick: async () => {
              const ok = await confirmDialog(
                t('profiles.editor.attachedRemove'),
                t('profiles.editor.attachedRemoveConfirm', { guid }),
                t('profiles.editor.delete'),
                true,
              );
              if (ok) {
                delete draft[guid];
                paintGroups();
              }
            },
          }, t('profiles.editor.delete'))),
        body,
        isObject
          ? h('button.btn.btn-sm', {
            type: 'button',
            style: { marginTop: '6px' },
            onClick: () => {
              const name = prompt(t('profiles.editor.attachedNewField'));
              if (name) {
                // 新字段默认 null：不凭空造类型，写什么由管理员决定。
                draft[guid][name.trim()] = null;
                paintGroups();
              }
            },
          }, t('profiles.editor.attachedNewField'))
          : null,
      );
    })));
  };

  paintGroups();

  modal({
    title: t('profiles.editor.attachedTitle', { count }),
    width: 'wide',
    body: h('div',
      h('p.card-desc', t('profiles.editor.attachedEditNote')),
      groupsHost,
      h('button.btn.btn-sm', {
        type: 'button',
        style: { marginTop: '10px' },
        onClick: () => {
          const guid = prompt(t('profiles.editor.attachedNewGuid'));
          if (guid) {
            draft[guid.trim()] = {};
            paintGroups();
          }
        },
      }, t('profiles.editor.attachedNew')),
      h('details.attach-raw',
        h('summary', t('profiles.editor.attachedRaw')),
        h('pre.attach-json', JSON.stringify(draft, null, 2)))),
    confirmText: t('profiles.editor.save'),
    onConfirm: () => {
      // 空对象视同"没有附加设置"：留一个 {} 只会让徽章显示「附加 1」却点开是空的。
      const cleaned = {};
      for (const [guid, value] of Object.entries(draft)) {
        if (value && typeof value === 'object' && !Array.isArray(value)
          && Object.keys(value).length === 0) {
          continue;
        }

        cleaned[guid] = value;
      }

      item.attachedSettings = Object.keys(cleaned).length > 0 ? cleaned : null;
      markDirtyValues();
      return true;
    },
  });
}

/**
 * 行动时间点的动作内容（`Action.Type` + 原始配置）。
 *
 * <p>
 * 为什么是「类型 + 原始 JSON」而不是按类型渲染表单：`Action.Type` 是开放集合
 * （`Notification` / `Automation` / `Run` / 插件自定义），A 端没有这些类型的 schema；
 * 硬造表单只会让管理员填出教室端读不懂的配置。给原始 JSON 至少能**改**、
 * 能对照插件文档填，且不会因为缺少某个字段被静默截断。
 * </p>
 */
function openActionDialog(item) {
  const typeInput = h('input', {
    type: 'text',
    value: item.actionType || '',
    placeholder: 'Notification / Automation / Run / …',
  });

  const settingsArea = h('textarea', { rows: 6, style: { width: '100%' } });
  settingsArea.value = item.actionSettings || '';

  const error = h('div.attach-dim', { hidden: true, style: { color: 'var(--danger)' } });

  settingsArea.oninput = () => {
    const text = settingsArea.value.trim();
    if (text === '') {
      settingsArea.style.borderColor = '';
      error.hidden = true;
      return;
    }

    try {
      JSON.parse(text);
      settingsArea.style.borderColor = '';
      error.hidden = true;
    } catch {
      settingsArea.style.borderColor = 'var(--danger)';
      error.hidden = false;
    }
  };

  modal({
    title: t('profiles.editor.actionTitle'),
    width: 'wide',
    body: h('div',
      h('p.card-desc', t('profiles.editor.actionNote')),
      field(t('profiles.editor.actionTypeLabel'), typeInput, t('profiles.editor.actionTypeHint')),
      field(t('profiles.editor.actionSettingsLabel'), settingsArea, t('profiles.editor.actionSettingsHint')),
      error),
    confirmText: t('profiles.editor.save'),
    onConfirm: () => {
      const text = settingsArea.value.trim();
      if (text !== '') {
        try {
          JSON.parse(text);
        } catch {
          error.hidden = false;
          error.textContent = t('profiles.editor.actionBadJson');
          return false;
        }
      }

      item.actionType = typeInput.value.trim() || null;
      item.actionSettings = text || null;
      markDirtyValues();
      return true;
    },
  });
}

/** 有多少张课表引用了这份时间表（轮换周次时同一个时间表往往被多张课表共用）。 */
function referencedPlanCount(layout) {
  if (!layout) {
    return 0;
  }

  return (state.content.classPlans || [])
    .filter((plan) => plan.timeLayoutId === layout.id)
    .length;
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

// ────────────────────────── 时间表可视化编辑（#56）──────────────────────────
//
// 为什么要有：表格适合「精确填时间」，但调作息在现实里是「第 2 节整体往后挪 5 分钟」这类动作——
// 填表要同时改开始与结束、还得自己避免和上/下一个时间点重叠。时间轴把一天画成一条竖条：
// 拖动色块平移、拖下边缘改时长，且**自动避让相邻时间点**（不允许重叠）。
//
// 关于刻度：1 分钟 = 1.2px（5 分钟 = 6px，够precision又不会让一天长得离谱），
// 时间轴自身定高可滚动，因此不需要为了「塞进一屏」而牺牲拖动精度；
// 分辨率固定为 5 分钟（作息表本来就是 5 分钟的倍数，吸附之后数字好看且可复现）。

const TL_PX_PER_MIN = 1.2;

/** 拖动吸附粒度（分钟）。 */
const TL_SNAP_MIN = 5;

/** 上课 / 课间的最短时长（分钟）：拖到更短会夹住，避免出现「0 分钟的一节课」。 */
const TL_MIN_DURATION = 5;

/** `HH:mm:ss` → 分钟；认不出来返回 null。 */
function tlMinutes(time) {
  const m = /^(\d{1,2}):(\d{2})(?::(\d{2}))?$/.exec(String(time || '').trim());
  if (!m) return null;
  const total = Number(m[1]) * 60 + Number(m[2]);
  return total >= 0 && total <= 1439 ? total : null;
}

/** 分钟 → `HH:mm:ss`（夹在一天之内）。 */
function tlTime(minutes) {
  const clamped = Math.max(0, Math.min(1439, Math.round(minutes)));
  const hh = String(Math.floor(clamped / 60)).padStart(2, '0');
  const mm = String(clamped % 60).padStart(2, '0');
  return `${hh}:${mm}:00`;
}

/** 吸附到 5 分钟。 */
function tlSnap(minutes) {
  return Math.round(minutes / TL_SNAP_MIN) * TL_SNAP_MIN;
}

/** 时间轴可见范围：至少覆盖 06:00–22:00，超出则按小时向外扩。 */
function tlSpan(layout) {
  const items = layout.items || [];
  const starts = items.map((i) => tlMinutes(i.startTime)).filter((v) => v !== null);
  const ends = items.map((i) => {
    const start = tlMinutes(i.startTime);
    const end = tlMinutes(i.endTime);
    return end === null || start === null ? start : Math.max(start, end);
  }).filter((v) => v !== null);
  const from = Math.max(0, Math.floor(Math.min(360, ...starts) / 60) * 60);
  const to = Math.min(1440, Math.ceil(Math.max(1320, ...ends) / 60) * 60);
  return { from, to: to > from ? to : from + 60 };
}

/** 某个时间点（按数组下标）可平移的上下界（分钟）；边界不存在时为整天。 */
function tlMoveBounds(layout, index) {
  const items = layout.items || [];
  const item = items[index];
  const start = tlMinutes(item.startTime) ?? 0;
  const end = tlMinutes(item.endTime) ?? start;
  const duration = ['separator', 'action'].includes(item.kind) ? 0 : Math.max(0, end - start);

  const prevEnd = index > 0 ? tlMinutes(items[index - 1].endTime) : 0;
  const nextStart = index < items.length - 1 ? tlMinutes(items[index + 1].startTime) : 1440;
  let lower = prevEnd === null ? 0 : prevEnd;
  let upper = (nextStart === null ? 1440 : nextStart) - duration;

  // 数据本身乱序 / 重叠时不要把用户卡死：退化成「整天」范围，让他能拖出来。
  if (lower > upper) {
    lower = 0;
    upper = 1440 - duration;
  }
  return { lower: Math.max(0, lower), upper: Math.min(1440 - duration, upper), duration };
}

/** 平移一个时间点（结束时间同步平移，保持时长）；返回实际落地的新开始时间。 */
function tlApplyMove(layout, index, minutes) {
  const item = layout.items[index];
  const { lower, upper, duration } = tlMoveBounds(layout, index);
  const target = Math.max(lower, Math.min(upper, tlSnap(minutes)));
  item.startTime = tlTime(target);
  item.endTime = tlTime(target + duration);
  return target;
}

/** 改一个时间点的结束时间（只对上课 / 课间有效）；返回实际落地的新结束时间。 */
function tlApplyResize(layout, index, minutes) {
  const items = layout.items || [];
  const item = items[index];
  const start = tlMinutes(item.startTime) ?? 0;
  const nextStart = index < items.length - 1 ? tlMinutes(items[index + 1].startTime) : 1440;
  const upper = Math.min(1440, nextStart === null ? 1440 : nextStart);
  const lower = Math.min(start + TL_MIN_DURATION, upper);
  const target = Math.max(lower, Math.min(upper, tlSnap(minutes)));
  item.endTime = tlTime(target);
  return target;
}

/** 时间点的时长（分钟）；分割线 / 行动算 0。 */
function tlDuration(item) {
  if (['separator', 'action'].includes(item.kind)) return 0;
  const start = tlMinutes(item.startTime) ?? 0;
  const end = tlMinutes(item.endTime) ?? start;
  return Math.max(0, end - start);
}

/** 把一天画成一条可拖动的时间轴。 */
function renderLayoutTimeline(layout) {
  const items = layout.items || [];
  if (items.length === 0) return null;

  const span = tlSpan(layout);
  const height = Math.round((span.to - span.from) * TL_PX_PER_MIN);
  const toY = (minutes) => (minutes - span.from) * TL_PX_PER_MIN;

  const ticks = [];
  for (let hour = span.from; hour <= span.to; hour += 60) {
    ticks.push({ minutes: hour, label: `${String(Math.floor(hour / 60)).padStart(2, '0')}:00` });
  }

  const canvas = h('div.tl-canvas', { style: { height: `${height}px` } },
    ...ticks.flatMap((tick) => [
      h('div.tl-line', { style: { top: `${toY(tick.minutes)}px` } }),
      h('div.tl-mark', { style: { top: `${toY(tick.minutes)}px` } }, tick.label),
    ]),
    ...items.map((item, index) => timelineBlock(layout, item, index, toY)),
  );

  return h('div.tl-wrap',
    h('div.tl-title',
      h('span', t('profiles.editor.timeline')),
      h('span.tl-hint', t('profiles.editor.timelineHint'))),
    h('div.tl-scroll', canvas),
  );
}

/** 时间轴上的一个色块（含拖动交互）。 */
function timelineBlock(layout, item, index, toY) {
  const start = tlMinutes(item.startTime) ?? 0;
  const duration = tlDuration(item);
  const isPoint = item.kind === 'separator' || item.kind === 'action';
  const blockHeight = isPoint ? 3 : Math.max(6, duration * TL_PX_PER_MIN);

  const label = isPoint
    ? t('profiles.editor.tlPointLabel', {
      kind: TIME_KIND_LABEL[item.kind] || '',
      time: tlTime(start).slice(0, 5),
    }).trim()
    : t('profiles.editor.tlBlockLabel', {
      head: item.kind === 'class'
        ? t('profiles.editor.periodHead', { n: countClassBefore(layout, index) + 1 })
        : t('profiles.editor.breakHead'),
      from: tlTime(start).slice(0, 5),
      to: tlTime(start + duration).slice(0, 5),
      minutes: duration,
      name: item.breakName && item.kind === 'break'
        ? t('profiles.editor.breakNameSuffix', { name: item.breakName })
        : '',
    });

  const block = h('div', {
    class: `tl-block kind-${item.kind}${item.isHideDefault ? ' hidden-point' : ''}`,
    style: { top: `${toY(start)}px`, height: `${blockHeight}px` },
    title: label,
    'data-index': String(index),
    'data-kind': item.kind,
  }, h('span.tl-block-label', label));

  if (!isPoint) {
    block.appendChild(h('div.tl-grip', { title: t('profiles.editor.dragDuration') }));
  }

  block.addEventListener('pointerdown', (event) => {
    // 抓下边缘 = 改时长；抓块体 = 平移（分割线 / 行动没有时长，只平移）。
    const onGrip = event.target instanceof Element && event.target.classList.contains('tl-grip');
    startTimelineDrag(event, layout, index, block, onGrip && !isPoint ? 'resize' : 'move', toY);
  });

  return block;
}

/** 拖动处理：实时更新色块位置与读数，松手后重绘（让下方的表格同步）。 */
function startTimelineDrag(event, layout, index, block, mode, toY) {
  if (event.button !== 0) return;
  event.preventDefault();
  event.stopPropagation();

  const item = layout.items[index];
  const originStart = tlMinutes(item.startTime) ?? 0;
  const originEnd = tlMinutes(item.endTime) ?? originStart;
  const startY = event.clientY;

  block.classList.add('dragging');
  const tip = h('div.tl-tip', '');
  block.appendChild(tip);

  const isPointKind = ['separator', 'action'].includes(item.kind);
  const paint = () => {
    const start = tlMinutes(item.startTime) ?? 0;
    const duration = tlDuration(item);
    block.style.top = `${toY(start)}px`;
    block.style.height = isPointKind ? '3px' : `${Math.max(6, duration * TL_PX_PER_MIN)}px`;
    const text = t('profiles.editor.tlRange', { from: tlTime(start).slice(0, 5), to: tlTime(start + duration).slice(0, 5), minutes: duration });
    tip.textContent = text;
    block.querySelector('.tl-block-label').textContent = text;
  };

  const onMove = (moveEvent) => {
    const deltaMinutes = (moveEvent.clientY - startY) / TL_PX_PER_MIN;
    if (mode === 'resize') {
      tlApplyResize(layout, index, originEnd + deltaMinutes);
    } else {
      tlApplyMove(layout, index, originStart + deltaMinutes);
    }
    paint();
    markDirtyLight();
  };

  const onUp = () => {
    window.removeEventListener('pointermove', onMove);
    window.removeEventListener('pointerup', onUp);
    window.removeEventListener('pointercancel', onUp);
    block.classList.remove('dragging');
    // 松手才重绘：拖动过程中重绘会把手里的元素换掉，拖到一半就断了。
    markDirtyValues();
  };

  window.addEventListener('pointermove', onMove);
  window.addEventListener('pointerup', onUp);
  window.addEventListener('pointercancel', onUp);
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
    name: t('profiles.editor.newLayoutName', { n: state.content.timeLayouts.length + 1 }),
    items: [],
  });
  state.selectedLayoutId = id;
  markDirtyValues();
}

function renameLayout(layout) {
  const input = h('input', { type: 'text', value: layout.name });
  modal({
    title: t('profiles.editor.renameLayout'),
    width: 'wide',
    body: field(t('profiles.editor.name'), input),
    confirmText: t('profiles.editor.save'),
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
    ? t('profiles.editor.deleteLayoutUsed', { name: layout.name, count: used.length })
    : t('profiles.editor.deleteLayoutConfirm', { name: layout.name });

  confirmDialog(t('profiles.editor.deleteLayout'), message, t('profiles.editor.delete'), true).then((ok) => {
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
    title: t('profiles.editor.quickGenerate'),
    width: 'wide',
    body: h('div',
      h('div.form-row',
        field(t('profiles.editor.firstStart'), startInput),
        field(t('profiles.editor.periodMinutes'), periodInput),
        field(t('profiles.editor.breakMinutes'), breakInput),
        field(t('profiles.editor.periodsPerDay'), countInput),
      ),
      h('label.checkbox-field', replaceCheckbox, t('profiles.editor.clearBeforeGenerate')),
      h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', t('profiles.editor.generateHint')),
      ),
    ),
    confirmText: t('profiles.editor.generate'),
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
  { value: 1, label: t('profiles.editor.wdMon') },
  { value: 2, label: t('profiles.editor.wdTue') },
  { value: 3, label: t('profiles.editor.wdWed') },
  { value: 4, label: t('profiles.editor.wdThu') },
  { value: 5, label: t('profiles.editor.wdFri') },
  { value: 6, label: t('profiles.editor.wdSat') },
  { value: 0, label: t('profiles.editor.wdSun') },
];

function classPlanLayout() {
  return state.content.timeLayouts.find((l) => l.id === state.classPlanLayoutId) || null;
}

/** 课表页：整周「节次 × 星期」网格，点选或拖拽即可排课、调课。 */
function renderClassPlans() {
  const layouts = state.content.timeLayouts;
  const layout = classPlanLayout();

  const layoutBar = segmented(
    layouts.map((l) => ({ id: l.id, label: t('profiles.editor.layoutOption', { name: l.name, count: classCount(l) }) })),
    state.classPlanLayoutId,
    (v) => {
      state.classPlanLayoutId = v;
      state.scheduleSelection = null;
      repaintTab();
    },
    layouts.length === 0 ? t('profiles.editor.needLayout') : null,
  );

  if (!layout) {
    return h('div',
      h('div.toolbar', h('span.toolbar-label', t('profiles.editor.dailyLayout')), layoutBar),
      emptyState('clock', t('profiles.editor.layoutsEmpty'),
        t('profiles.editor.gridLead'),
        h('button.btn.btn-sm.btn-primary', {
          type: 'button',
          onClick: () => { state.activeTab = 'timeLayouts'; repaintTab(); },
        }, t('profiles.editor.createLayoutFirst'))),
    );
  }

  const periods = layout.items.filter((i) => i.kind === 'class');
  if (periods.length === 0) {
    return h('div',
      h('div.toolbar', h('span.toolbar-label', t('profiles.editor.dailyLayout')), layoutBar),
      emptyState('clock', t('profiles.editor.layoutNoLessons'),
        t('profiles.editor.layoutNoLessonsHint')),
    );
  }

  // 选中格子索引越界时复位。
  if (state.scheduleSelection && state.scheduleSelection.index >= periods.length) {
    state.scheduleSelection = null;
  }

  // 多周轮换：周次切换器。一格 = 某一周的整周课表；标「（空）」的那一周教室大屏什么都不会显示，
  // 这类空周只靠肉眼翻周次才看得出来，所以直接在标签上提示。
  const weeks = profileRotation().weeks;
  const week = currentWeek();
  const weekBar = weeks < 2 ? null : h('div.toolbar',
    h('span.toolbar-label', t('profiles.editor.rotationWeek')),
    segmented(
      Array.from({ length: weeks }, (_, i) => ({
        id: i + 1,
        label: t('profiles.editor.weekOption', {
          n: i + 1,
          empty: weekDayCount(layout, i + 1) === 0 ? t('profiles.editor.emptySuffix') : '',
        }),
      })),
      week,
      (v) => {
        state.rotationWeek = Number(v);
        state.scheduleSelection = null;
        repaintTab();
      },
    ),
    h('div.spacer'),
    h('span.toolbar-hint', t('profiles.editor.editingWeek', { week })),
  );

  return h('div',
    weekBar,
    h('div.toolbar',
      h('span.toolbar-label', t('profiles.editor.dailyLayout')),
      layoutBar,
      h('div.spacer'),
      h('span.toolbar-hint', t('profiles.editor.gridHint')),
      h('button.btn.btn-sm', {
        type: 'button',
        title: t('profiles.editor.batchHint'),
        onClick: () => openBulkOps(layout, periods.length),
      }, t('profiles.editor.batch')),
    ),
    renderPalette(periods.length),
    h('div.grid-scroll',
      h('div.sched-grid',
        h('div.sched-head.corner', t('profiles.editor.periodTime')),
        ...GRID_DAYS.map((d) => h('div.sched-head', d.label)),
        ...periods.flatMap((period, index) => [
          h('div.sched-period',
            h('span.no', t('profiles.editor.periodN', { n: index + 1 })),
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
    value: String(i), label: t('profiles.editor.periodN', { n: i + 1 }),
  }));
  const subjectOptions = [
    { value: '', label: t('profiles.editor.pickSubject') },
    ...state.content.subjects.map((s) => ({ value: s.id, label: s.name || t('profiles.editor.unnamedSubject') })),
  ];

  const cfg = {
    action: 'copy',
    mode: 'replace',
    srcDayFrom: '0', srcDayTo: '4', srcPeriodFrom: '0', srcPeriodTo: String(periodCount - 1),
    dstDayFrom: '0', dstDayTo: '4', dstPeriodFrom: '0', dstPeriodTo: String(periodCount - 1),
    // 「从别的周复制」的源周次：默认选「不是当前周」的那一周（1↔2），省得每次都要改。
    srcWeek: String(currentWeek() === 1 ? Math.min(2, profileRotation().weeks) : 1),
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

  /**
   * 从另一周复制到**当前正在编辑的那一周**。
   *
   * 与「按节复制」的区别：那个是在同一周内搬移，这个是跨周抄整周。
   * 轮换课表通常是「整周基本一样、只有少数几节不同」，先抄一整周再改那几节最省事，
   * 也避免新起的一周留着空格——某个半天没课，教室大屏那半天就什么都不显示。
   */
  function applyCopyFromWeek() {
    const sourceWeek = Number(cfg.srcWeek) || 1;
    const days = expand('srcDayFrom', 'srcDayTo');
    const periods = expand('srcPeriodFrom', 'srcPeriodTo');

    const sourceByDay = new Map();
    for (const plan of state.content.classPlans) {
      if (plan.timeLayoutId !== layout.id || (plan.daysOfWeek || []).length !== 1) {
        continue;
      }

      if ((planWeek(plan) || 1) === sourceWeek) {
        sourceByDay.set(plan.daysOfWeek[0], plan);
      }
    }

    // 先把源周整块读出来再写（与「按节复制」同一条原则：边读边写会读到已覆盖的值）。
    const block = periods.map((period) => days.map((dayIndex) => {
      const slot = (sourceByDay.get(GRID_DAYS[dayIndex].value)?.slots || [])
        .find((s) => s.index === period);
      return slot && slot.subjectId
        ? { subjectId: slot.subjectId, isEnabled: slot.isEnabled !== false }
        : null;
    }));

    let written = 0;
    let skipped = 0;
    periods.forEach((period, row) => {
      days.forEach((dayIndex, col) => {
        const day = GRID_DAYS[dayIndex].value;
        if (cfg.mode === 'merge' && readSlot(layout, day, period)) {
          skipped++;
          return;
        }

        writeSlot(layout, day, period, block[row][col]);
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

    // 「从别的周复制」只在多周轮换档案里出现——单周档案里它没有意义。
    const actionKeys = [['copy', t('profiles.editor.copyByPeriod')], ['clear', t('profiles.editor.bulkClear')], ['replace', t('profiles.editor.replaceSubject')], ['template', t('profiles.editor.template')]];
    if (profileRotation().weeks >= 2) {
      actionKeys.splice(1, 0, ['fromweek', t('profiles.editor.copyFromWeek')]);
    }

    body.appendChild(h('div.card-actions', { style: { gap: '6px', marginBottom: '12px' } },
      ...actionKeys.map(([key, label]) => h('button', {
          type: 'button',
          class: `btn btn-sm${cfg.action === key ? ' btn-primary' : ''}`,
          onClick: () => { cfg.action = key; paint(); },
        }, label))));

    if (cfg.action === 'copy') {
      body.appendChild(field(t('profiles.editor.sourceDay'), pair(dayOptions, 'srcDayFrom', 'srcDayTo')));
      body.appendChild(field(t('profiles.editor.sourcePeriod'), pair(periodOptions, 'srcPeriodFrom', 'srcPeriodTo')));
      body.appendChild(field(t('profiles.editor.targetDay'), pair(dayOptions, 'dstDayFrom', 'dstDayTo')));
      body.appendChild(field(t('profiles.editor.targetPeriod'), pair(periodOptions, 'dstPeriodFrom', 'dstPeriodTo')));
      body.appendChild(field(t('profiles.editor.mode'), select([
        { value: 'replace', label: t('profiles.editor.modeOverwrite') },
        { value: 'merge', label: t('profiles.editor.modeFillEmpty') },
      ], cfg.mode, (v) => { cfg.mode = v; }),
      t('profiles.editor.overlapHint')));
    } else if (cfg.action === 'fromweek') {
      const weeks = profileRotation().weeks;
      const here = currentWeek();
      body.appendChild(field(t('profiles.editor.sourceWeek'),
        select(
          Array.from({ length: weeks }, (_, i) => ({
            value: String(i + 1),
            label: t('profiles.editor.weekOptionCurrent', {
              n: i + 1,
              current: i + 1 === here ? t('profiles.editor.currentSuffix') : '',
            }),
          })),
          cfg.srcWeek,
          (v) => { cfg.srcWeek = v; },
        ),
        t('profiles.editor.copyTargetWeekHint')));
      body.appendChild(field(t('profiles.editor.sourceDay'), pair(dayOptions, 'srcDayFrom', 'srcDayTo')));
      body.appendChild(field(t('profiles.editor.sourcePeriod'), pair(periodOptions, 'srcPeriodFrom', 'srcPeriodTo')));
      body.appendChild(field(t('profiles.editor.mode'), select([
        { value: 'replace', label: t('profiles.editor.modeOverwriteWeek') },
        { value: 'merge', label: t('profiles.editor.modeFillEmptyWeek') },
      ], cfg.mode, (v) => { cfg.mode = v; }),
      t('profiles.editor.copyWeekHint')));
    } else if (cfg.action === 'clear') {
      body.appendChild(field(t('profiles.editor.rangeDay'), pair(dayOptions, 'srcDayFrom', 'srcDayTo')));
      body.appendChild(field(t('profiles.editor.rangePeriod'), pair(periodOptions, 'srcPeriodFrom', 'srcPeriodTo')));
      body.appendChild(h('p.card-desc', { style: { margin: 0 } },
        t('profiles.editor.clearHint')));
    } else if (cfg.action === 'replace') {
      body.appendChild(field(t('profiles.editor.replaceFrom'), select(subjectOptions, cfg.replaceFrom,
        (v) => { cfg.replaceFrom = v; })));
      body.appendChild(field(t('profiles.editor.replaceTo'), select(subjectOptions, cfg.replaceTo,
        (v) => { cfg.replaceTo = v; }),
      t('profiles.editor.replaceHint')));
    } else {
      // 模板：存 / 套用
      const nameInput = h('input', {
        type: 'text', placeholder: t('profiles.editor.templateNamePlaceholder'), maxlength: '32',
      });
      body.appendChild(field(t('profiles.editor.saveTemplate'),
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
              toast('ok', t('profiles.editor.templateSaved'), t('profiles.editor.templateSavedHint', { name: created.name }));
              nameInput.value = '';
              await loadTemplates();
              paint();
            } catch (err) {
              toastError(err, t('profiles.editor.templateSaveFailed'));
            }
          },
        }, t('profiles.editor.save'))),
      t('profiles.editor.templateLead')
      + t('profiles.editor.templateTail')));

      if (runtime.templates.length === 0) {
        body.appendChild(h('p.card-desc', { style: { margin: 0 } }, t('profiles.editor.noTemplates')));
      } else {
        body.appendChild(field(t('profiles.editor.applyTemplate'), select([
          { value: '', label: t('profiles.editor.pickTemplate') },
          ...runtime.templates.map((t) => ({ value: t.id, label: t('profiles.editor.templateOption', { name: t.name, count: t.periodCount }) })),
        ], cfg.templateId, (v) => { cfg.templateId = v; }),
        t('profiles.editor.applyTemplateHint')));
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
    title: t('profiles.editor.batchTitle'),
    width: 'wide',
    body,
    confirmText: t('profiles.editor.run'),
    onConfirm: async () => {
      const code = () => {
        markDirty();
        repaintTab();
      };

      if (cfg.action === 'fromweek') {
        const sourceWeek = Number(cfg.srcWeek) || 1;
        const targetWeek = currentWeek();
        if (sourceWeek === targetWeek) {
          toast('warn', t('profiles.editor.sourceIsCurrent'),
            t('profiles.editor.sourceIsCurrentHint'));
          return false;
        }

        const copied = applyCopyFromWeek();
        code();
        toast('ok', t('profiles.editor.copied'),
          t('profiles.editor.copiedDetail', { source: sourceWeek, target: targetWeek, count: copied.written })
          + `${copied.skipped ? `，跳过 ${copied.skipped} 格（当前周已有课）` : ''}。`
          + t('profiles.editor.rememberSave'));
        return true;
      }

      if (cfg.action === 'copy') {
        const result = applyCopy();
        code();
        toast('ok', t('profiles.editor.copied'), t('profiles.editor.writtenCells', { count: result.written })
          + `${result.skipped ? `，跳过 ${result.skipped} 格（已有课）` : ''}。`
          + t('profiles.editor.rememberSave'));
        return true;
      }

      if (cfg.action === 'clear') {
        const result = applyClear();
        code();
        toast('ok', t('profiles.editor.cleared'), t('profiles.editor.clearedDetail', { count: result.cleared }));
        return true;
      }

      if (cfg.action === 'replace') {
        const result = applyReplace();
        if (result.invalid) {
          toast('warn', t('profiles.editor.needTwoSubjects'));
          return false;
        }

        code();
        toast('ok', t('profiles.editor.replaced'), t('profiles.editor.replacedDetail', { count: result.count }));
        return true;
      }

      // 模板：执行 = 套用选中的那个
      const template = runtime.templates.find((t) => t.id === cfg.templateId);
      if (!template) {
        toast('warn', t('profiles.editor.needTemplate'));
        return false;
      }

      const result = applyTemplate(template);
      code();
      toast('ok', t('profiles.editor.templateApplied'), t('profiles.editor.appliedCells', { count: result.applied })
        + `${result.missing.length ? `；模板里的「${result.missing.join('、')}」在这套档案里没有同名科目，已跳过` : ''}。`
        + t('profiles.editor.rememberSave'));
      return true;
    },
  });

  loadTemplates().then(paint);
}

/** 取「某一天」的课表；不存在返回 null（空天保持空，不自动创建）。 */
function getDayPlan(layout, day) {
  const candidates = state.content.classPlans.filter((p) => p.timeLayoutId === layout.id
    && (p.daysOfWeek || []).length === 1 && p.daysOfWeek[0] === day);

  if (profileRotation().weeks < 2) {
    return candidates[0] || null;
  }

  // 轮换档案里同一天会有多张课表（每周一张）：优先当前正在编辑的那一周，
  // 其次才是「每周生效」的那张（用它兜底，免得在别的周显示成空）。
  const week = currentWeek();
  return candidates.find((p) => planWeek(p) === week)
    || candidates.find((p) => planWeek(p) === 0)
    || null;
}

/** 确保某天的课表存在（用户实际填课时才创建），返回课表对象。 */
function ensureDayPlan(layout, day) {
  let plan = getDayPlan(layout, day);
  if (plan) {
    return plan;
  }

  const weeks = profileRotation().weeks;
  const week = currentWeek();
  const label = GRID_DAYS.find((d) => d.value === day)?.label || String(day);
  plan = {
    id: genId(),
    name: weeks >= 2
      ? t('profiles.editor.planNameWeek', { label, week })
      : t('profiles.editor.planName', { label }),
    timeLayoutId: layout.id,
    isEnabled: true,
    daysOfWeek: [day],
    weekInterval: weeks >= 2 ? weeks : 0,
    weekOffset: weeks >= 2 ? week - 1 : 0,
    slots: [],
  };
  state.content.classPlans.push(plan);
  return plan;
}

/** 科目简称：优先 initial，其次 name 首字，最后「未命名」。 */
function subjectShort(subject) {
  if (subject.initial) return subject.initial;
  if (subject.name) return subject.name.slice(0, 1);
  return t('profiles.editor.untitled');
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
    title: subject ? t('profiles.editor.cellHint') : t('profiles.editor.cellHintEmpty'),
  },
    subject
      ? h('span.sched-initial', subjectShort(subject))
      : h('span.sched-plus', '＋'),
    subject ? h('span.sched-name', subject.name || t('profiles.editor.untitled')) : null,
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
      h('span.palette-empty', t('profiles.editor.noSubjectsHint')));
  }

  return h('div.sched-palette',
    ...state.content.subjects.map((subject) => {
      const armed = state.armedSubjectId === subject.id;
      const chip = h('div', {
        class: `sched-chip${armed ? ' armed' : ''}`,
        role: 'button',
        tabindex: '0',
        title: armed ? t('profiles.editor.subjectToggleHint') : t('profiles.editor.subjectPickHint'),
      },
        h('b.sched-chip-initial', subjectShort(subject)),
        h('span.sched-chip-name', subject.name || t('profiles.editor.untitled')),
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
    }, t('profiles.editor.subjectPlaceholder'))),
    h('td', textInput(subject.initial, (v) => {
      subject.initial = v;
      markDirtyLight();
    }, t('profiles.editor.abbrPlaceholder'))),
    h('td', textInput(subject.teacherName, (v) => {
      subject.teacherName = v;
      markDirtyLight();
    }, t('profiles.editor.optional'))),
    h('td', { style: { textAlign: 'center' } }, checkbox(subject.isOutDoor, (v) => {
      subject.isOutDoor = v;
      markDirtyLight();
    })),
    h('td', h('button.btn.btn-ghost.btn-sm', {
      type: 'button',
      // 纯图标按钮必须有无障碍名（#49）：屏幕阅读器念的是 aria-label，不是那个 ✕。
      title: t('profiles.editor.deleteSubject'),
      'aria-label': t('profiles.editor.deleteSubject'),
      onClick: () => {
        state.content.subjects.splice(index, 1);
        markDirtyValues();
      },
    }, icon('close', 14))),
  ));

  return h('div',
    h('div.toolbar',
      h('span', { style: { fontSize: '12.5px', color: 'var(--text-dim)' } },
        t('profiles.editor.subjectsCount', { count: subjects.length })),
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
      }, t('profiles.editor.addSubject')),
    ),
    subjects.length === 0
      ? emptyState('book', t('profiles.editor.subjectsEmpty'), t('profiles.editor.subjectsEmptyHint'))
      : h('div.table-wrap', { style: { maxHeight: '60vh', overflowY: 'auto' } },
        h('table.data',
          h('thead', h('tr',
            h('th', t('profiles.editor.subjectName')),
            h('th', { style: { width: '120px' } }, t('profiles.editor.abbr')),
            h('th', { style: { width: '170px' } }, t('profiles.editor.teacher')),
            h('th', { style: { width: '92px', textAlign: 'center' } }, t('profiles.editor.outdoor')),
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
    placeholder: t('profiles.editor.noticePlaceholder'),
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
      placeholder: t('profiles.editor.customKeyPlaceholder'),
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
      placeholder: t('profiles.editor.customValue'),
      onChange: (e) => {
        settings.values[key] = e.target.value;
        markDirtyLight();
      },
    }),
    h('button.btn.btn-ghost.btn-sm', {
      type: 'button',
      title: t('profiles.editor.deleteEntry'),
      'aria-label': t('profiles.editor.deleteEntry'),
      onClick: () => {
        delete settings.values[key];
        markDirtyValues();
      },
    }, icon('close', 14)),
  ));

  return h('div',
    h('div.notice.notice-info',
      h('span.notice-icon', 'i'),
      h('div', t('profiles.editor.customLead')),
    ),
    field(t('profiles.editor.notice'), announcementInput, t('profiles.editor.noticeHint')),
    h('label.checkbox-field', lockCheckbox, t('profiles.editor.lockLocal')),
    h('div', { style: { display: 'flex', alignItems: 'center', gap: '10px', margin: '18px 0 10px' } },
      h('div', { style: { fontSize: '13px', fontWeight: '600' } }, t('profiles.editor.customTitle', { count: entries.length })),
      h('div', { style: { flex: '1' } }),
      h('button.btn.btn-sm', {
        type: 'button',
        onClick: () => {
          settings.values[t('profiles.editor.customKeyDefault', { n: entries.length + 1 })] = '';
          markDirtyValues();
        },
      }, t('profiles.editor.addEntry')),
    ),
    entries.length === 0
      ? h('div', { style: { fontSize: '12.5px', color: 'var(--text-faint)', padding: '10px 0' } },
        t('profiles.editor.customEmpty'))
      : h('div', ...kvRows),
  );
}
