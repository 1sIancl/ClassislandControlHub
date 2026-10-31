/**
 * 定时提醒视图。
 * 提醒按账号隔离：这里只会看到、也只能改到自己创建的提醒；到点由服务端调度器推送到教室大屏。
 */

import { api } from '../core/api.js?v=163';
import {
  h, clear, toast, loadingBlock, modal, confirmDialog, field, select,
  emptyState, formatDateTime, relativeTime, hintIcon,
} from '../core/ui.js?v=163';
import { t } from './../core/i18n.js?v=163';
// 富内容预览（#11）：与插件详情页共用同一个安全渲染器（先转义再套结构）。
import { createRichPreview } from '../core/markdown.js?v=163';

export const meta = {
  title: t('reminders.title'),
  subtitle: t('reminders.subtitle'),
};

const REPEATS = [
  { value: 'once', label: t('reminders.repeatOnceOnly') },
  { value: 'daily', label: t('reminders.repeatDaily') },
  { value: 'weekly', label: t('reminders.repeatWeekly') },
  { value: 'monthly', label: t('reminders.repeatMonthly') },
];

const WEEKDAYS = [
  { value: 1, label: t('reminders.wdShortMon') },
  { value: 2, label: t('reminders.wdShortTue') },
  { value: 3, label: t('reminders.wdShortWed') },
  { value: 4, label: t('reminders.wdShortThu') },
  { value: 5, label: t('reminders.wdShortFri') },
  { value: 6, label: t('reminders.wdShortSat') },
  { value: 7, label: t('reminders.wdShortSun') },
];

/** 到点做什么（与服务端 ReminderActions 一一对应）。 */
const ACTIONS = [
  { value: 'notify', label: t('reminders.actionNotice') },
  { value: 'power.shutdown', label: t('reminders.actionShutdown') },
  { value: 'power.wake', label: t('reminders.actionWake') },
];

const ACTION_LABELS = { notify: t('reminders.actionNoticeShort'), 'power.shutdown': t('reminders.actionShutdownShort'), 'power.wake': t('reminders.actionWakeShort') };

/** 触发方式（与服务端 ReminderTriggers 一一对应，#12）。 */
const TRIGGERS = [
  { value: 'time', label: t('reminders.triggerTime') },
  { value: 'device_online', label: t('reminders.triggerOnline') },
  { value: 'sync.completed', label: t('reminders.triggerSynced') },
  { value: 'automation.fired', label: t('reminders.triggerAutomation') },
];

/** 事件触发的说明文案（选「按时间」时不显示）。 */
const TRIGGER_NOTES = {
  device_online: t('reminders.triggerOnlineLead')
    + t('reminders.triggerOnlineBody')
    + t('reminders.triggerOnlineTail'),
  'sync.completed': t('reminders.triggerSyncedLead')
    + t('reminders.triggerSyncedBody')
    + t('reminders.triggerDedupe'),
  'automation.fired': t('reminders.triggerAutomationLead')
    + t('reminders.triggerAutomationBody')
    + t('reminders.triggerAutomationTail')
    + t('reminders.triggerAutomationTail2'),
};
const ACTION_BADGES = { 'power.shutdown': 'badge-warn', 'power.wake': 'badge-info' };
const isPowerAction = (action) => action === 'power.shutdown' || action === 'power.wake';

let cache = { reminders: [], summary: null, fires: [], devices: [], groups: [] };
let tab = 'list';

export async function render(container) {
  clear(container);
  container.appendChild(loadingBlock());
  await loadAll();
  paint(container);
}

async function loadAll() {
  const [reminders, summary, devices, groups] = await Promise.all([
    api('/admin/reminders'),
    api('/admin/reminders/summary'),
    api('/admin/devices'),
    api('/admin/groups'),
  ]);

  cache.reminders = reminders;
  cache.summary = summary;
  cache.devices = devices;
  cache.groups = groups;
  cache.fires = await api('/admin/reminders/fires', { query: { limit: 100 } });
}

function paint(container) {
  clear(container);
  container.appendChild(h('div',
    renderSummary(),
    renderToolbar(container),
    tab === 'list' ? renderList(container) : renderHistory(),
  ));
}

async function reload(container, showToast = false) {
  await loadAll();
  paint(container);
  if (showToast) toast('ok', t('reminders.refreshed'));
}

// ────────────────────────────── 概览与工具栏 ──────────────────────────────

function renderSummary() {
  const s = cache.summary || { total: 0, enabled: 0, firedLast24Hours: 0, nextFireAt: null };
  return h('div.board-overview',
    h('div.ov-stat', h('b', String(s.total)), h('span', t('reminders.countUnit'))),
    h('div.ov-stat', h('b', String(s.enabled)), h('span', t('reminders.active'))),
    h('div.ov-stat', h('b', String(s.firedLast24Hours)), h('span', t('reminders.fired24h'))),
    h('div.ov-online',
      h('div.ov-online-head',
        h('span', t('reminders.nextFire')),
        h('b', s.nextFireAt ? formatDateTime(s.nextFireAt) : t('reminders.none'))),
      h('div', { style: { fontSize: '11.5px', color: 'var(--text-faint)' } },
        s.nextFireAt ? relativeTime(s.nextFireAt) : t('reminders.noneActive'))),
  );
}

function renderToolbar(container) {
  return h('div.toolbar',
    h('div.segmented',
      h('button', {
        class: `segmented-item${tab === 'list' ? ' active' : ''}`,
        type: 'button',
        onClick: () => { tab = 'list'; paint(container); },
      }, t('reminders.listTitle')),
      h('button', {
        class: `segmented-item${tab === 'history' ? ' active' : ''}`,
        type: 'button',
        onClick: () => { tab = 'history'; paint(container); },
      }, t('reminders.history')),
    ),
    h('div.spacer'),
    h('button.btn.btn-sm.btn-primary', {
      type: 'button',
      onClick: () => openEditor(container, null),
    }, t('reminders.new')),
    h('button.btn.btn-sm', { type: 'button', onClick: () => reload(container, true) }, t('reminders.refresh')),
  );
}

// ────────────────────────────── 提醒列表 ──────────────────────────────

function renderList(container) {
  if (cache.reminders.length === 0) {
    return h('div.card',
      emptyState('bell', t('reminders.empty'),
        t('reminders.emptyHint'),
        h('button.btn.btn-primary', { type: 'button', onClick: () => openEditor(container, null) }, t('reminders.create'))));
  }

  return h('div.table-wrap',
    h('table.data',
      h('thead', h('tr',
        h('th', t('reminders.column')),
        h('th', t('reminders.repeatRule')),
        h('th', t('reminders.nextFire')),
        h('th', t('reminders.pushTo')),
        h('th', t('reminders.state')),
        h('th', { style: { textAlign: 'right' } }, t('reminders.actions')),
      )),
      h('tbody', ...cache.reminders.map((r) => h('tr',
        h('td',
          h('div.cell-main', r.title || t('reminders.untitled'),
            isPowerAction(r.action)
              ? h('span', { class: `badge ${ACTION_BADGES[r.action]}`, style: { marginLeft: '6px' } },
                ACTION_LABELS[r.action])
              : null),
          h('div.cell-sub', r.content || (isPowerAction(r.action) ? t('reminders.actionNoContent') : '—'))),
        h('td',
          h('div.reminder-repeat', repeatText(r)),
          r.speak ? h('div.reminder-target', t('reminders.speaks')) : null),
        h('td',
          r.enabled
            ? h('div.reminder-next', r.nextFireAt ? formatDateTime(r.nextFireAt) : '—')
            : h('span', { style: { color: 'var(--text-faint)' } }, t('reminders.disabled'))),
        h('td',
          h('div', r.targetName),
          h('div.reminder-target', t('reminders.fireCount', { count: r.fireCount }))),
        h('td', r.enabled
          ? h('span.badge.badge-ok', t('reminders.active'))
          : h('span.badge.badge-neutral', t('reminders.disabled'))),
        h('td.actions',
          h('button.btn.btn-sm', {
            type: 'button',
            title: isPowerAction(r.action) ? t('reminders.runNowPowerHint') : t('reminders.runNowHint'),
            onClick: () => runNow(container, r),
          }, isPowerAction(r.action) ? t('reminders.runNowPower') : t('reminders.runNow')),
          ' ',
          h('button.btn.btn-sm', { type: 'button', onClick: () => openEditor(container, r) }, t('reminders.edit')),
          ' ',
          h('button.btn.btn-sm', {
            type: 'button',
            onClick: () => toggleEnabled(container, r),
          }, r.enabled ? t('reminders.disable') : t('reminders.enable')),
          ' ',
          h('button.btn.btn-sm.btn-danger', {
            type: 'button',
            onClick: () => removeReminder(container, r),
          }, t('reminders.delete')),
        ),
      ))),
    ),
  );
}

function repeatText(r) {
  const time = r.fireTime;
  switch (r.repeatKind) {
    case 'daily':
      return r.repeatInterval > 1
        ? t('reminders.repeatDailyN', { n: r.repeatInterval, time })
        : t('reminders.repeatDaily', { time });
    case 'weekly': {
      const days = (r.repeatWeekdays || []).map((d) => t('reminders.weekdayLabel', {
        label: WEEKDAYS.find((w) => w.value === d)?.label || d,
      })).join(t('reminders.listSep'));
      const prefix = r.repeatInterval > 1
        ? t('reminders.repeatWeeklyN', { n: r.repeatInterval })
        : t('reminders.repeatWeekly');
      return t('reminders.repeatWeeklyFull', {
        prefix,
        days: days || t('reminders.noneSelected'),
        time,
      });
    }
    case 'monthly': {
      const day = r.repeatDayOfMonth || Number((r.startDate || '').slice(8, 10)) || 1;
      const prefix = r.repeatInterval > 1
        ? t('reminders.repeatMonthlyN', { n: r.repeatInterval })
        : t('reminders.repeatMonthly');
      return t('reminders.repeatMonthlyFull', { prefix, day, time });
    }
    default:
      return t('reminders.repeatOnce', { date: r.startDate, time });
  }
}

// ────────────────────────────── 触发历史 ──────────────────────────────

function renderHistory() {
  if (cache.fires.length === 0) {
    return h('div.card', emptyState('list', t('reminders.noHistory'), t('reminders.noHistoryHint')));
  }

  return h('div.table-wrap',
    h('table.data',
      h('thead', h('tr',
        h('th', t('reminders.firedAt')),
        h('th', t('reminders.column')),
        h('th', t('reminders.pushResult')),
        h('th', t('reminders.target')),
      )),
      h('tbody', ...cache.fires.map((f) => h('tr',
        h('td', h('div', formatDateTime(f.firedAt)), h('div.cell-sub', relativeTime(f.firedAt))),
        h('td',
          h('div', f.reminderTitle || t('reminders.deletedReminder')),
          h('div.cell-sub', ACTION_LABELS[f.action] || t('reminders.actionNoticeShort'))),
        h('td',
          h('span.badge.badge-ok', t('reminders.pushedCount', { count: f.affected })),
          f.skipped > 0
            ? h('span.badge.badge-warn', { style: { marginLeft: '6px' } }, t('reminders.skippedCount', { count: f.skipped }))
            : null),
        h('td', h('span.reminder-target', f.detail || '—')),
      ))),
    ),
  );
}

// ────────────────────────────── 操作 ──────────────────────────────

async function runNow(container, reminder) {
  const power = isPowerAction(reminder.action);
  const label = ACTION_LABELS[reminder.action] || t('reminders.actionNoticeShort');

  // 动作型提醒会真的动手（关机不可撤销），确认框要说清这点并把按钮标红。
  const message = power
    ? t('reminders.runPowerConfirm', { target: reminder.targetName, action: label })
    : t('reminders.runNoticeConfirm', {
      title: reminder.title,
      target: reminder.targetName,
    });

  if (!await confirmDialog(
    power ? t('reminders.runPowerTitle', { action: label }) : t('reminders.runTriggerTitle'),
    message,
    power ? t('reminders.runPowerButton') : t('reminders.runButton'),
    power,
  )) {
    return;
  }

  try {
    const result = await api(`/admin/reminders/${reminder.id}/run`, { method: 'POST' });
    toast('ok', t('reminders.ran'), result.detail || result.detail || t('reminders.runResult', { action: label, count: result.affected }));
    await reload(container);
  } catch (err) {
    toastError(err, t('reminders.runFailed'));
  }
}

async function toggleEnabled(container, reminder) {
  const body = { ...toRequest(reminder), enabled: !reminder.enabled };
  try {
    await api(`/admin/reminders/${reminder.id}`, { method: 'PUT', body });
    toast('ok', reminder.enabled ? t('reminders.disabled') : t('reminders.enabled'));
    await reload(container);
  } catch (err) {
    toastError(err, t('reminders.actionFailed'));
  }
}

async function removeReminder(container, reminder) {
  if (!await confirmDialog(t('reminders.deleteTitle'),
    t('reminders.deleteConfirm', { title: reminder.title }), t('reminders.delete'), true)) {
    return;
  }

  await api(`/admin/reminders/${reminder.id}`, { method: 'DELETE' });
  toast('ok', t('reminders.deleted'));
  await reload(container);
}

/** 把已有提醒转成请求体，便于「只改一个字段」时复用。 */
function toRequest(reminder) {
  return {
    title: reminder.title,
    content: reminder.content,
    targetType: reminder.targetType,
    targetId: reminder.targetId || '',
    speak: reminder.speak,
    enabled: reminder.enabled,
    repeatKind: reminder.repeatKind,
    repeatInterval: reminder.repeatInterval,
    repeatWeekdays: reminder.repeatWeekdays || [],
    repeatDayOfMonth: reminder.repeatDayOfMonth || 0,
    fireTime: reminder.fireTime,
    startDate: reminder.startDate,
    endDate: reminder.endDate || '',
    action: reminder.action || 'notify',
  };
}

// ────────────────────────────── 新建 / 编辑 ──────────────────────────────

function openEditor(container, reminder) {
  const isEdit = Boolean(reminder);
  const now = new Date();
  const today = toDateInput(now);
  const nextHour = `${String(Math.min(23, now.getHours() + 1)).padStart(2, '0')}:00`;

  /** 当前编辑中的重复配置，跟着界面控件走。 */
  const draft = {
    repeatKind: reminder?.repeatKind || 'once',
    weekdays: [...(reminder?.repeatWeekdays || [])],
    targetType: reminder?.targetType || 'all',
    action: reminder?.action || 'notify',
  };

  const titleInput = h('input', { type: 'text', value: reminder?.title || '', placeholder: t('reminders.titlePlaceholder') });
  const contentInput = h('textarea', {
    placeholder: t('reminders.contentPlaceholder'),
    style: { minHeight: '80px' },
  });
  contentInput.value = reminder?.content || '';

  const dateInput = h('input', { type: 'date', value: reminder?.startDate || today });
  const timeInput = h('input', { type: 'time', value: reminder?.fireTime || nextHour });
  const endDateInput = h('input', { type: 'date', value: reminder?.endDate || '' });
  const everyInput = h('input', { type: 'number', min: '1', max: '365', value: String(reminder?.repeatInterval || 1) });

  const repeatSelect = select(REPEATS, draft.repeatKind, (v) => {
    draft.repeatKind = v;
    repaintRepeat();
  });

  const weekdayPicker = h('div.weekday-picker');
  const monthDayInput = h('input', {
    type: 'number', min: '1', max: '31',
    value: String(reminder?.repeatDayOfMonth || Number(today.slice(8, 10)) || 1),
  });

  const everyUnit = h('span', { style: { fontSize: '12.5px', color: 'var(--text-faint)' } }, t('reminders.unitDay'));
  const everyField = field(t('reminders.repeatInterval'), h('div', { style: { display: 'flex', alignItems: 'center', gap: '8px' } }, everyInput, everyUnit));
  const weekdaysField = field(t('reminders.weekdays'), weekdayPicker, t('reminders.weekdaysHint'));
  const monthDayField = field(t('reminders.dayOfMonth'), monthDayInput, t('reminders.dayOfMonthHint'));

  const targetTypeSelect = select([
    { value: 'all', label: t('reminders.targetAll') },
    { value: 'group', label: t('reminders.targetGroups') },
    { value: 'device', label: t('reminders.targetDevice') },
  ], draft.targetType, (v) => {
    draft.targetType = v;
    repaintTarget();
  });

  const targetHost = h('div');
  const speakChk = h('input', { type: 'checkbox' });
  speakChk.checked = reminder?.speak ?? true;
  const enabledChk = h('input', { type: 'checkbox' });
  enabledChk.checked = reminder?.enabled ?? true;

  // ── 「到时做什么」：排程（什么时候）与动作（做什么）正交，所以同一套时间控件配三种动作 ──
  const actionSelect = select(ACTIONS, draft.action, (v) => {
    draft.action = v;
    repaintAction();
  });
  const actionHint = h('div');
  // 富内容预览（#11）：边写边看，省得发出去才发现排版不对。
  const preview = createRichPreview(() => contentInput.value);
  contentInput.addEventListener('input', preview.refresh);
  // 模板变量（#11 的一部分）：提示写在这儿，比写进文档更容易被看到。
  const contentField = field(t('reminders.content'), h('div', contentInput, preview.element),
    t('reminders.variablesLead')
    + t('reminders.variablesTail')
    + t('reminders.richHint')
    + t('reminders.htmlHint'));

  // 触发方式（#12）：按时间 / 设备上线 / 配置同步完成。
  draft.triggerKind = reminder?.triggerKind || 'time';
  const triggerSelect = select(TRIGGERS, draft.triggerKind, (v) => {
    draft.triggerKind = v;
    repaintTrigger();
  });
  const triggerHint = h('div');

  function repaintTrigger() {
    // 除「按时间」之外都是事件触发：排程字段对它们没有意义，整块收起
    // （留在界面上只会让人以为两种触发同时在用）。
    const note = TRIGGER_NOTES[draft.triggerKind];
    scheduleBlock.hidden = !!note;
    clear(triggerHint);
    triggerHint.appendChild(note
      ? h('div.notice.notice-info', h('span.notice-icon', 'i'), h('div', note))
      : h('div'));
  }
  const speakField = h('label.checkbox-field', speakChk, h('span', t('reminders.speakLabel')));

  function repaintAction() {
    const power = isPowerAction(draft.action);

    // 关机 / 开机不是「消息」：内容与语音播报没有意义，直接收起，免得管理员以为要填点什么。
    contentField.hidden = power;
    speakField.hidden = power;

    clear(actionHint);
    if (draft.action === 'power.shutdown') {
      actionHint.appendChild(h('div.notice.notice-warn',
        h('span.notice-icon', '!'),
        h('div', t('reminders.shutdownLead')
          + t('reminders.shutdownBody')
          + t('reminders.shutdownTail'))));
      return;
    }

    if (draft.action === 'power.wake') {
      actionHint.appendChild(h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', t('reminders.wakeLead')
          + t('reminders.wakeBody')
          + t('reminders.wakeTail'))));
      return;
    }

    actionHint.appendChild(h('div.notice.notice-info',
      h('span.notice-icon', 'i'),
      h('div', t('reminders.noticeLead'))));
  }

  function repaintWeekdays() {
    weekdayPicker.replaceChildren(...WEEKDAYS.map((day) => h('button', {
      class: `weekday-chip${draft.weekdays.includes(day.value) ? ' active' : ''}`,
      type: 'button',
      onClick: () => {
        const index = draft.weekdays.indexOf(day.value);
        if (index >= 0) draft.weekdays.splice(index, 1);
        else draft.weekdays.push(day.value);
        repaintWeekdays();
      },
    }, day.label)));
  }

  function repaintRepeat() {
    const kind = draft.repeatKind;
    everyField.hidden = kind === 'once';
    weekdaysField.hidden = kind !== 'weekly';
    monthDayField.hidden = kind !== 'monthly';
    everyUnit.textContent = kind === 'weekly' ? t('reminders.everyWeekPrefix') : kind === 'monthly' ? t('reminders.everyMonthSuffix') : t('reminders.unitDay');
    repaintWeekdays();

    // 「仅一次」时日期就是触发日，默认填今天。
    if (kind === 'once' && !dateInput.value) {
      dateInput.value = today;
    }
  }

  function repaintTarget() {
    clear(targetHost);
    if (draft.targetType === 'all') {
      targetHost.appendChild(h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', t('reminders.targetAllHint'))));
      return;
    }

    if (draft.targetType === 'group') {
      const options = cache.groups.map((g) => {
        const parent = g.parentId ? cache.groups.find((x) => x.id === g.parentId) : null;
        return { value: g.id, label: parent ? `${parent.name} / ${g.name}` : t('reminders.groupWhole', { name: g.name }) };
      });
      targetHost.appendChild(field(t('reminders.targetGroupsLabel'),
        select(options.length ? options : [{ value: '', label: t('reminders.noGroups') }],
          reminder?.targetId || options[0]?.value || '', (v) => { draft.targetId = v; }),
        t('reminders.groupsHint')));
      draft.targetId = reminder?.targetId || options[0]?.value || '';
      return;
    }

    const options = cache.devices.map((d) => ({
      value: d.id,
      label: t('reminders.deviceOption', {
        name: d.name,
        state: d.online ? t('reminders.onlineSuffix') : t('reminders.offlineSuffix'),
      }),
    }));
    targetHost.appendChild(field(t('reminders.targetDevices'),
      select(options.length ? options : [{ value: '', label: t('reminders.noDevices') }],
        reminder?.targetId || options[0]?.value || '', (v) => { draft.targetId = v; })));
    draft.targetId = reminder?.targetId || options[0]?.value || '';
  }

  // 排程相关的字段打包成一个块：触发方式选「设备上线」时整块收起。
  const scheduleBlock = h('div',
    h('div.form-row',
      field(t('reminders.repeatRule'), repeatSelect),
      everyField,
    ),
    h('div.form-row',
      field(t('reminders.date'), dateInput, t('reminders.dateHint')),
      field(t('reminders.time'), timeInput, t('reminders.timeHint')),
    ),
    weekdaysField,
    monthDayField,
    field(t('reminders.endDate'), endDateInput, t('reminders.endDateHint')),
  );

  repaintRepeat();
  repaintTarget();
  repaintAction();
  repaintTrigger();

  modal({
    title: isEdit ? t('reminders.editTitle', { title: reminder.title }) : t('reminders.newTitle'),
    width: 'wide',
    body: h('div',
      field(h('span', t('reminders.trigger'), hintIcon(
        t('reminders.triggerLead')
        + t('reminders.triggerBody')
        + t('reminders.triggerTail'))), triggerSelect),
      scheduleBlock,
      field(h('span', t('reminders.whatToDo'), hintIcon(
      t('reminders.actionLead')
      + t('reminders.actionBody'))), actionSelect),
      actionHint,
      field(t('reminders.titleLabel'), titleInput, t('reminders.titleHint')),
      contentField,
      field(t('reminders.pushTo'), targetTypeSelect),
      targetHost,
      speakField,
      h('label.checkbox-field', enabledChk, h('span', t('reminders.enableNow'))),
      triggerHint,
      h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', t('reminders.schedLead')
          + t('reminders.schedTail'))),
    ),
    confirmText: isEdit ? t('reminders.save') : t('reminders.createButton'),
    onConfirm: async () => {
      const body = {
        title: titleInput.value.trim(),
        content: contentInput.value.trim(),
        action: draft.action,
        triggerKind: draft.triggerKind,
        targetType: draft.targetType,
        targetId: draft.targetId || '',
        speak: speakChk.checked,
        enabled: enabledChk.checked,
        repeatKind: draft.repeatKind,
        repeatInterval: Number(everyInput.value) || 1,
        repeatWeekdays: draft.weekdays,
        repeatDayOfMonth: Number(monthDayInput.value) || 0,
        fireTime: timeInput.value,
        startDate: dateInput.value,
        endDate: endDateInput.value || '',
      };

      // 「标题与内容不能同时为空」只对通知成立：关机 / 开机不是消息（服务端也会再校验一次）。
      if (draft.action === 'notify' && !body.title && !body.content) {
        toast('warn', t('reminders.needTitleOrContent'));
        return false;
      }

      try {
        if (isEdit) {
          await api(`/admin/reminders/${reminder.id}`, { method: 'PUT', body });
          toast('ok', t('reminders.saved'));
        } else {
          await api('/admin/reminders', { method: 'POST', body });
          toast('ok', t('reminders.created'), body.enabled
            ? (isPowerAction(body.action)
              ? t('reminders.createdAction', { action: ACTION_LABELS[body.action] })
              : t('reminders.createdNotice'))
            : t('reminders.createdDisabled'));
        }
      } catch (err) {
        toastError(err, t('reminders.saveFailed'));
        return false;
      }

      await reload(container);
      return true;
    },
  });
}

function toDateInput(date) {
  const pad = (n) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}
