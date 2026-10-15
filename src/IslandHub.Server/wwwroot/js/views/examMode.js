/**
 * 考试模式（#21）：把选定教室的档案**临时**换成考试档案，考完一键还原。
 *
 * 做成独立页面而不是塞进「配置下发」页：它是一段**有状态**的操作（进行中 / 已结束），
 * 需要一直看得见「切的是哪个档案、哪些教室、各自原来是什么」——塞在推送表单旁边
 * 最容易让人以为它只是一次普通推送。
 */

import { api } from '../core/api.js?v=117';
import { toastError } from '../core/errors.js?v=117';
import {
  h, clear, toast, loadingBlock, field, select, confirmDialog, formatDateTime,
} from '../core/ui.js?v=117';

export const meta = {
  title: '考试模式',
  subtitle: '临时把选定教室切到考试档案，考完一键还原',
};

export async function render(container) {
  clear(container);
  container.appendChild(loadingBlock());

  try {
    const [state, profiles, groups] = await Promise.all([
      api('/admin/exam-mode'),
      api('/admin/profiles'),
      api('/admin/groups'),
    ]);

    clear(container);
    container.appendChild(renderStatusCard(state));
    container.appendChild(state.active
      ? renderEndCard(container)
      : renderStartCard(container, profiles, groups));
  } catch (err) {
    clear(container);
    toastError(err, '考试模式加载失败');
  }
}

/** 当前状态：进行中就列出切到了哪个档案、涉及哪些教室、各自原来是什么。 */
function renderStatusCard(state) {
  if (!state.active) {
    return h('div.card',
      h('div', { style: { fontWeight: '600', marginBottom: '6px' } }, '当前没有进行中的考试模式'),
      h('div.cell-sub', '教室按各自（或所属分组）的档案正常运行。'),
      h('div.notice.notice-info', { style: { marginTop: '10px' } },
        h('span.notice-icon', 'i'),
        h('div', '开启后：选定教室会被切到考试档案并立即下发；',
          h('strong', '结束时会还原成这些教室各自原来的档案'),
          '（原本没单独指定档案的会还原成「跟随分组」）。')));
  }

  const rows = (state.targets || []).map((t) => h('tr',
    h('td', t.deviceName || t.deviceId),
    h('td', t.previousProfileName || '跟随分组（无单独档案）')));

  return h('div.card',
    h('div', { style: { display: 'flex', alignItems: 'center', gap: '10px', flexWrap: 'wrap' } },
      h('span', { style: { fontWeight: '600' } }, '考试模式进行中'),
      h('span', { style: { fontSize: '12px', color: 'var(--text-dim)' } },
        `由 ${state.startedBy || '未知'} 于 ${formatDateTime(state.startedAt)} 开启`)),
    h('div', { style: { marginTop: '8px' } },
      h('div', `考试档案：${state.profileName || state.profileId}`),
      h('div', `通知文案：${state.message || '（无）'}`),
      h('div', `涉及教室：${state.targetCount} 台`)),
    rows.length
      ? h('div.table-wrap', { style: { marginTop: '10px' } },
        h('table.data',
          h('thead', h('tr', h('th', '教室'), h('th', '结束后还原为'))),
          h('tbody', ...rows)))
      : null,
    h('div.notice.notice-warn', { style: { marginTop: '10px' } },
      h('span.notice-icon', '!'),
      h('div', '考试期间请勿在「配置下发 / 设备管理」里改动这些教室的档案——',
        '否则结束后它们会按上表还原，你的改动会被覆盖。')));
}

/** 开始表单。 */
function renderStartCard(container, profiles, groups) {
  if (profiles.length === 0) {
    return h('div.card', '还没有配置档案。请先到「配置档案」里建一个考试用的档案。');
  }

  const profileSelect = select(profiles.map((p) => ({ value: p.id, label: p.name })), profiles[0].id);
  const scopeSelect = select([
    { value: 'all', label: '全部教室' },
    { value: 'group', label: '指定分组' },
  ], 'all');
  const groupSelect = select(groups.map((g) => ({ value: g.id, label: g.name })), groups[0]?.id ?? '');
  const groupField = field('分组', groupSelect);
  groupField.hidden = true;
  scopeSelect.addEventListener('change', () => {
    groupField.hidden = scopeSelect.value !== 'group';
  });

  const messageInput = h('input', {
    type: 'text', value: '考试进行中，请勿操作教室设备。', style: { width: '320px' },
  });
  const notifyChk = h('input', { type: 'checkbox' });
  notifyChk.checked = true;

  const startBtn = h('button.btn.btn-primary', { type: 'button' }, '开启考试模式');
  startBtn.addEventListener('click', async () => {
    const scope = scopeSelect.value;
    if (scope === 'group' && !groupSelect.value) {
      toast('warn', '请选择分组');
      return;
    }

    const targetIds = scope === 'group' ? [groupSelect.value] : [];
    const scopeText = scope === 'group'
      ? `分组「${groupSelect.options[groupSelect.selectedIndex]?.text ?? ''}」`
      : '全部教室';
    if (!await confirmDialog({
      title: '开启考试模式',
      message: `将把${scopeText}切到档案「${profileSelect.options[profileSelect.selectedIndex]?.text ?? ''}」`
        + '并立即下发。结束后可以一键还原成各自原来的档案。',
      confirmText: '开启',
    })) {
      return;
    }

    startBtn.disabled = true;
    try {
      const result = await api('/admin/exam-mode/start', {
        method: 'POST',
        body: {
          profileId: profileSelect.value,
          scope,
          targetIds,
          message: messageInput.value.trim(),
          notify: notifyChk.checked,
        },
      });
      toast('ok', '考试模式已开启', `已切换 ${result.targetCount} 台教室并下发。`);
      await render(container);
    } catch (err) {
      toastError(err, '开启失败');
      startBtn.disabled = false;
    }
  });

  return h('div.card',
    h('div', { style: { fontWeight: '600', marginBottom: '10px' } }, '开启考试模式'),
    h('div', field('考试档案', profileSelect)),
    h('div', field('范围', scopeSelect)),
    h('div', groupField),
    h('div', field('通知文案', messageInput, '会作为一条提醒发给这些教室（可关掉下方勾选）。')),
    h('label.checkbox-field', notifyChk, h('span', '同时给这些教室推一条屏幕提醒')),
    h('div', { style: { marginTop: '12px' } }, startBtn));
}

/** 结束按钮（带确认：还原是不可撤销的档案改写）。 */
function renderEndCard(container) {
  const endBtn = h('button.btn', { type: 'button' }, '结束考试模式并还原');
  endBtn.addEventListener('click', async () => {
    if (!await confirmDialog({
      title: '结束考试模式',
      message: '将把上表里的教室还原成各自原来的档案并下发。',
      confirmText: '结束并还原',
    })) {
      return;
    }

    endBtn.disabled = true;
    try {
      await api('/admin/exam-mode/end', { method: 'POST' });
      toast('ok', '考试模式已结束', '已按各自原档案发起下发。');
      await render(container);
    } catch (err) {
      toastError(err, '结束失败');
      endBtn.disabled = false;
    }
  });

  return h('div.card', endBtn);
}
