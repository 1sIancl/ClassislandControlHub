/**
 * 定时提醒视图。
 * 提醒按账号隔离：这里只会看到、也只能改到自己创建的提醒；到点由服务端调度器推送到教室大屏。
 */

import { api } from '../core/api.js?v=103';
import {
  h, clear, toast, loadingBlock, modal, confirmDialog, field, select,
  emptyState, formatDateTime, relativeTime, hintIcon,
} from '../core/ui.js?v=103';

export const meta = {
  title: '定时提醒',
  subtitle: '按设定时间自动推送到教室大屏，各账号互不干扰',
};

const REPEATS = [
  { value: 'once', label: '仅一次' },
  { value: 'daily', label: '每天' },
  { value: 'weekly', label: '每周' },
  { value: 'monthly', label: '每月' },
];

const WEEKDAYS = [
  { value: 1, label: '一' },
  { value: 2, label: '二' },
  { value: 3, label: '三' },
  { value: 4, label: '四' },
  { value: 5, label: '五' },
  { value: 6, label: '六' },
  { value: 7, label: '日' },
];

/** 到点做什么（与服务端 ReminderActions 一一对应）。 */
const ACTIONS = [
  { value: 'notify', label: '发通知（推送到教室大屏）' },
  { value: 'power.shutdown', label: '定时关机（关闭教室机）' },
  { value: 'power.wake', label: '定时开机（网络唤醒）' },
];

const ACTION_LABELS = { notify: '发通知', 'power.shutdown': '定时关机', 'power.wake': '定时开机' };
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
  if (showToast) toast('ok', '已刷新');
}

// ────────────────────────────── 概览与工具栏 ──────────────────────────────

function renderSummary() {
  const s = cache.summary || { total: 0, enabled: 0, firedLast24Hours: 0, nextFireAt: null };
  return h('div.board-overview',
    h('div.ov-stat', h('b', String(s.total)), h('span', '条提醒')),
    h('div.ov-stat', h('b', String(s.enabled)), h('span', '启用中')),
    h('div.ov-stat', h('b', String(s.firedLast24Hours)), h('span', '近 24 小时触发')),
    h('div.ov-online',
      h('div.ov-online-head',
        h('span', '下次触发'),
        h('b', s.nextFireAt ? formatDateTime(s.nextFireAt) : '暂无')),
      h('div', { style: { fontSize: '11.5px', color: 'var(--text-faint)' } },
        s.nextFireAt ? relativeTime(s.nextFireAt) : '没有启用中的提醒')),
  );
}

function renderToolbar(container) {
  return h('div.toolbar',
    h('div.segmented',
      h('button', {
        class: `segmented-item${tab === 'list' ? ' active' : ''}`,
        type: 'button',
        onClick: () => { tab = 'list'; paint(container); },
      }, '提醒列表'),
      h('button', {
        class: `segmented-item${tab === 'history' ? ' active' : ''}`,
        type: 'button',
        onClick: () => { tab = 'history'; paint(container); },
      }, '触发历史'),
    ),
    h('div.spacer'),
    h('button.btn.btn-sm.btn-primary', {
      type: 'button',
      onClick: () => openEditor(container, null),
    }, '+ 新建提醒'),
    h('button.btn.btn-sm', { type: 'button', onClick: () => reload(container, true) }, '刷新'),
  );
}

// ────────────────────────────── 提醒列表 ──────────────────────────────

function renderList(container) {
  if (cache.reminders.length === 0) {
    return h('div.card',
      emptyState('bell', '还没有提醒',
        '可以建一条「每周一 8:00 提醒各班开晨会」这样的定时提醒，到点会自动推送到教室大屏。',
        h('button.btn.btn-primary', { type: 'button', onClick: () => openEditor(container, null) }, '新建提醒')));
  }

  return h('div.table-wrap',
    h('table.data',
      h('thead', h('tr',
        h('th', '提醒'),
        h('th', '重复规则'),
        h('th', '下次触发'),
        h('th', '推送到'),
        h('th', '状态'),
        h('th', { style: { textAlign: 'right' } }, '操作'),
      )),
      h('tbody', ...cache.reminders.map((r) => h('tr',
        h('td',
          h('div.cell-main', r.title || '（无标题）',
            isPowerAction(r.action)
              ? h('span', { class: `badge ${ACTION_BADGES[r.action]}`, style: { marginLeft: '6px' } },
                ACTION_LABELS[r.action])
              : null),
          h('div.cell-sub', r.content || (isPowerAction(r.action) ? '（动作型提醒，没有内容）' : '—'))),
        h('td',
          h('div.reminder-repeat', repeatText(r)),
          r.speak ? h('div.reminder-target', '含语音播报') : null),
        h('td',
          r.enabled
            ? h('div.reminder-next', r.nextFireAt ? formatDateTime(r.nextFireAt) : '—')
            : h('span', { style: { color: 'var(--text-faint)' } }, '已停用')),
        h('td',
          h('div', r.targetName),
          h('div.reminder-target', `累计触发 ${r.fireCount} 次`)),
        h('td', r.enabled
          ? h('span.badge.badge-ok', '启用中')
          : h('span.badge.badge-neutral', '已停用')),
        h('td.actions',
          h('button.btn.btn-sm', {
            type: 'button',
            title: isPowerAction(r.action) ? '立刻执行一次（会真的动手，用于验证配置）' : '立即推送一次，用于验证配置',
            onClick: () => runNow(container, r),
          }, isPowerAction(r.action) ? '立即执行' : '立即触发'),
          ' ',
          h('button.btn.btn-sm', { type: 'button', onClick: () => openEditor(container, r) }, '编辑'),
          ' ',
          h('button.btn.btn-sm', {
            type: 'button',
            onClick: () => toggleEnabled(container, r),
          }, r.enabled ? '停用' : '启用'),
          ' ',
          h('button.btn.btn-sm.btn-danger', {
            type: 'button',
            onClick: () => removeReminder(container, r),
          }, '删除'),
        ),
      ))),
    ),
  );
}

function repeatText(r) {
  const time = r.fireTime;
  switch (r.repeatKind) {
    case 'daily':
      return r.repeatInterval > 1 ? `每 ${r.repeatInterval} 天 ${time}` : `每天 ${time}`;
    case 'weekly': {
      const days = (r.repeatWeekdays || []).map((d) => `周${WEEKDAYS.find((w) => w.value === d)?.label || d}`).join('、');
      const prefix = r.repeatInterval > 1 ? `每 ${r.repeatInterval} 周` : '每周';
      return `${prefix} ${days || '（未选）'} ${time}`;
    }
    case 'monthly': {
      const day = r.repeatDayOfMonth || Number((r.startDate || '').slice(8, 10)) || 1;
      const prefix = r.repeatInterval > 1 ? `每 ${r.repeatInterval} 个月` : '每月';
      return `${prefix} ${day} 号 ${time}`;
    }
    default:
      return `${r.startDate} ${time} 仅一次`;
  }
}

// ────────────────────────────── 触发历史 ──────────────────────────────

function renderHistory() {
  if (cache.fires.length === 0) {
    return h('div.card', emptyState('list', '还没有触发记录', '提醒到点或被手动触发后，这里会留下记录，方便核对是否准时。'));
  }

  return h('div.table-wrap',
    h('table.data',
      h('thead', h('tr',
        h('th', '触发时间'),
        h('th', '提醒'),
        h('th', '推送结果'),
        h('th', '目标'),
      )),
      h('tbody', ...cache.fires.map((f) => h('tr',
        h('td', h('div', formatDateTime(f.firedAt)), h('div.cell-sub', relativeTime(f.firedAt))),
        h('td',
          h('div', f.reminderTitle || '（提醒已删除）'),
          h('div.cell-sub', ACTION_LABELS[f.action] || '发通知')),
        h('td',
          h('span.badge.badge-ok', `推送 ${f.affected} 台`),
          f.skipped > 0
            ? h('span.badge.badge-warn', { style: { marginLeft: '6px' } }, `跳过离线 ${f.skipped} 台`)
            : null),
        h('td', h('span.reminder-target', f.detail || '—')),
      ))),
    ),
  );
}

// ────────────────────────────── 操作 ──────────────────────────────

async function runNow(container, reminder) {
  const power = isPowerAction(reminder.action);
  const label = ACTION_LABELS[reminder.action] || '发通知';

  // 动作型提醒会真的动手（关机不可撤销），确认框要说清这点并把按钮标红。
  const message = power
    ? `将立刻对「${reminder.targetName}」执行 ${label}——这会真的动手，关机不可撤销。`
      + '这不会改变原有重复计划。确定继续吗？'
    : `将立刻把「${reminder.title}」推送到${reminder.targetName}。这不会改变原有重复计划。确定继续吗？`;

  if (!await confirmDialog(power ? `立即${label}` : '立即触发', message, power ? '执行' : '推送', power)) {
    return;
  }

  try {
    const result = await api(`/admin/reminders/${reminder.id}/run`, { method: 'POST' });
    toast('ok', '已执行', result.detail || `${label}：影响 ${result.affected} 台。`);
    await reload(container);
  } catch (err) {
    toastError(err, '执行失败');
  }
}

async function toggleEnabled(container, reminder) {
  const body = { ...toRequest(reminder), enabled: !reminder.enabled };
  try {
    await api(`/admin/reminders/${reminder.id}`, { method: 'PUT', body });
    toast('ok', reminder.enabled ? '已停用' : '已启用');
    await reload(container);
  } catch (err) {
    toastError(err, '操作失败');
  }
}

async function removeReminder(container, reminder) {
  if (!await confirmDialog('删除提醒',
    `确定删除「${reminder.title}」吗？它的触发历史也会一并删除。`, '删除', true)) {
    return;
  }

  await api(`/admin/reminders/${reminder.id}`, { method: 'DELETE' });
  toast('ok', '已删除');
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

  const titleInput = h('input', { type: 'text', value: reminder?.title || '', placeholder: '例如：开晨会提醒' });
  const contentInput = h('textarea', {
    placeholder: '推送到教室大屏的内容…',
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

  const everyUnit = h('span', { style: { fontSize: '12.5px', color: 'var(--text-faint)' } }, '天');
  const everyField = field('重复间隔', h('div', { style: { display: 'flex', alignItems: 'center', gap: '8px' } }, everyInput, everyUnit));
  const weekdaysField = field('星期几', weekdayPicker, '按周重复时，选中哪几天就在哪几天触发。');
  const monthDayField = field('每月几号', monthDayInput, '选 29~31 时，没有这一天的月份会跳过。');

  const targetTypeSelect = select([
    { value: 'all', label: '全部在线教室' },
    { value: 'group', label: '指定分组（楼栋 / 楼层）' },
    { value: 'device', label: '指定单间教室' },
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
  const contentField = field('内容', contentInput);
  const speakField = h('label.checkbox-field', speakChk, h('span', '同时语音播报提醒内容'));

  function repaintAction() {
    const power = isPowerAction(draft.action);

    // 关机 / 开机不是「消息」：内容与语音播报没有意义，直接收起，免得管理员以为要填点什么。
    contentField.hidden = power;
    speakField.hidden = power;

    clear(actionHint);
    if (draft.action === 'power.shutdown') {
      actionHint.appendChild(h('div.notice.notice-warn',
        h('span.notice-icon', '!'),
        h('div', '到点会真的关掉教室机（下发关机指令，不可撤销）。只发给在线设备：'
          + '离线设备不排队——否则它第二天早上刚被打开时会被关掉。'
          + '上课期间关机意味着大屏黑屏，请把时间排在放学之后。')));
      return;
    }

    if (draft.action === 'power.wake') {
      actionHint.appendChild(h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', '到点直接发 Wake-on-LAN 魔术包，关机状态也能开机（因此不按在线过滤）。'
          + '前提：教室机在 BIOS / 网卡里启用了「网络唤醒」，且 A 端与教室在同一广播域'
          + '（跨 VLAN 需要交换机开中继）。没上报过网卡地址的设备会被跳过，原因写在触发历史里。')));
      return;
    }

    actionHint.appendChild(h('div.notice.notice-info',
      h('span.notice-icon', 'i'),
      h('div', '到点把标题与内容推送到教室大屏；只发给在线设备——提醒讲时效，离线教室不做补发。')));
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
    everyUnit.textContent = kind === 'weekly' ? '周' : kind === 'monthly' ? '个月' : '天';
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
        h('div', '推送给全部教室；离线教室会被跳过（提醒讲时效，不做排队补发）。')));
      return;
    }

    if (draft.targetType === 'group') {
      const options = cache.groups.map((g) => {
        const parent = g.parentId ? cache.groups.find((x) => x.id === g.parentId) : null;
        return { value: g.id, label: parent ? `${parent.name} / ${g.name}` : `${g.name}（整栋）` };
      });
      targetHost.appendChild(field('目标分组',
        select(options.length ? options : [{ value: '', label: '（还没有分组）' }],
          reminder?.targetId || options[0]?.value || '', (v) => { draft.targetId = v; }),
        '给楼栋或楼层都可以，楼栋会覆盖其下所有楼层。'));
      draft.targetId = reminder?.targetId || options[0]?.value || '';
      return;
    }

    const options = cache.devices.map((d) => ({ value: d.id, label: `${d.name}${d.online ? '（在线）' : '（离线）'}` }));
    targetHost.appendChild(field('目标教室',
      select(options.length ? options : [{ value: '', label: '（还没有设备）' }],
        reminder?.targetId || options[0]?.value || '', (v) => { draft.targetId = v; })));
    draft.targetId = reminder?.targetId || options[0]?.value || '';
  }

  repaintRepeat();
  repaintTarget();
  repaintAction();

  modal({
    title: isEdit ? `编辑提醒 · ${reminder.title}` : '新建定时提醒',
    width: 'wide',
    body: h('div',
      field(h('span', '到时做什么', hintIcon(
      '排程（什么时候）与动作（做什么）是两件事：同一条提醒可以只发通知，也可以在到点关机 / 唤醒教室机。'
      + '关机走系统电源；唤醒走网卡魔术包，需要那台教室机在 BIOS / 网卡里开了网络唤醒。')), actionSelect),
      actionHint,
      field('标题', titleInput, '教室大屏上会以醒目方式显示标题；关机 / 开机类提醒留空会自动填「定时关机 / 定时开机」。'),
      contentField,
      h('div.form-row',
        field('重复规则', repeatSelect),
        everyField,
      ),
      h('div.form-row',
        field('日期', dateInput, '「仅一次」时这是触发日期；重复规则下这是生效起始日期。'),
        field('时间', timeInput, '服务器本地时间，精确到分钟。'),
      ),
      weekdaysField,
      monthDayField,
      field('结束日期', endDateInput, '留空表示长期有效。'),
      field('推送到', targetTypeSelect),
      targetHost,
      speakField,
      h('label.checkbox-field', enabledChk, h('span', '创建后立即启用')),
      h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', '提醒由服务端按校正后的时间调度，误差通常在 15 秒内；每条提醒只属于你自己的账号，其它账号看不到。'
          + '服务停机期间错过的时刻会补一次：通知补发无害，关机 / 开机这类动作迟到超过 10 分钟就只推进计划、不执行。')),
    ),
    confirmText: isEdit ? '保存' : '创建',
    onConfirm: async () => {
      const body = {
        title: titleInput.value.trim(),
        content: contentInput.value.trim(),
        action: draft.action,
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
        toast('warn', '请填写标题或内容');
        return false;
      }

      try {
        if (isEdit) {
          await api(`/admin/reminders/${reminder.id}`, { method: 'PUT', body });
          toast('ok', '已保存');
        } else {
          await api('/admin/reminders', { method: 'POST', body });
          toast('ok', '提醒已创建', body.enabled
            ? (isPowerAction(body.action)
              ? `到点会自动执行「${ACTION_LABELS[body.action]}」。`
              : '到点会自动推送到教室大屏。')
            : '当前为停用状态。');
        }
      } catch (err) {
        toastError(err, '保存失败');
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
