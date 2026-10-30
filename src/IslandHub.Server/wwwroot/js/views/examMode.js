/**
 * 考试模式（#21）：把选定教室的档案**临时**换成考试档案，考完一键还原。
 *
 * 做成独立页面而不是塞进「配置下发」页：它是一段**有状态**的操作（进行中 / 已结束），
 * 需要一直看得见「切的是哪个档案、哪些教室、各自原来是什么」——塞在推送表单旁边
 * 最容易让人以为它只是一次普通推送。
 */

import { api } from '../core/api.js?v=160';
import { toastError } from '../core/errors.js?v=160';
import {
  h, clear, toast, loadingBlock, field, select, confirmDialog, formatDateTime,
} from '../core/ui.js?v=160';
import { t } from '../core/i18n.js?v=160';

export const meta = {
  title: t('exam.title'),
  subtitle: t('exam.subtitle'),
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
    toastError(err, t('exam.loadFailed'));
  }
}

/** 当前状态：进行中就列出切到了哪个档案、涉及哪些教室、各自原来是什么。 */
function renderStatusCard(state) {
  if (!state.active) {
    return h('div.card',
      h('div', { style: { fontWeight: '600', marginBottom: '6px' } }, t('exam.idleTitle')),
      h('div.cell-sub', t('exam.idleDesc')),
      h('div.notice.notice-info', { style: { marginTop: '10px' } },
        h('span.notice-icon', 'i'),
        h('div', t('exam.intro1'),
          h('strong', t('exam.intro2')),
          t('exam.intro3'))));
  }

  const rows = (state.targets || []).map((t) => h('tr',
    h('td', t.deviceName || t.deviceId),
    h('td', t.previousProfileName || t('exam.followGroup'))));

  return h('div.card',
    h('div', { style: { display: 'flex', alignItems: 'center', gap: '10px', flexWrap: 'wrap' } },
      h('span', { style: { fontWeight: '600' } }, t('exam.running')),
      h('span', { style: { fontSize: '12px', color: 'var(--text-dim)' } },
        t('exam.startedBy', {
          by: state.startedBy || t('exam.unknown'),
          at: formatDateTime(state.startedAt),
        }))),
    h('div', { style: { marginTop: '8px' } },
      h('div', t('exam.profileLine', { name: state.profileName || state.profileId })),
      h('div', t('exam.messageLine', { text: state.message || t('exam.none') })),
      h('div', t('exam.targetLine', { count: state.targetCount }))),
    rows.length
      ? h('div.table-wrap', { style: { marginTop: '10px' } },
        h('table.data',
          h('thead', h('tr', h('th', t('exam.colRoom')), h('th', t('exam.colRestore')))),
          h('tbody', ...rows)))
      : null,
    h('div.notice.notice-warn', { style: { marginTop: '10px' } },
      h('span.notice-icon', '!'),
      h('div', t('exam.warn1'),
        t('exam.warn2'))));
}

/** 开始表单。 */
function renderStartCard(container, profiles, groups) {
  if (profiles.length === 0) {
    return h('div.card', t('exam.noProfiles'));
  }

  const profileSelect = select(profiles.map((p) => ({ value: p.id, label: p.name })), profiles[0].id);
  const scopeSelect = select([
    { value: 'all', label: t('exam.allRooms') },
    { value: 'group', label: t('exam.byGroup') },
  ], 'all');
  const groupSelect = select(groups.map((g) => ({ value: g.id, label: g.name })), groups[0]?.id ?? '');
  const groupField = field(t('exam.group'), groupSelect);
  groupField.hidden = true;
  scopeSelect.addEventListener('change', () => {
    groupField.hidden = scopeSelect.value !== 'group';
  });

  const messageInput = h('input', {
    type: 'text', value: t('exam.busy'), style: { width: '320px' },
  });
  const notifyChk = h('input', { type: 'checkbox' });
  notifyChk.checked = true;

  const startBtn = h('button.btn.btn-primary', { type: 'button' }, t('exam.start'));
  startBtn.addEventListener('click', async () => {
    const scope = scopeSelect.value;
    if (scope === 'group' && !groupSelect.value) {
      toast('warn', t('exam.selectGroup'));
      return;
    }

    const targetIds = scope === 'group' ? [groupSelect.value] : [];
    const scopeText = scope === 'group'
      ? t('exam.groupQuoted', { name: groupSelect.options[groupSelect.selectedIndex]?.text ?? '' })
      : t('exam.allRooms');
    if (!await confirmDialog({
      title: t('exam.start'),
      message: t('exam.startConfirm1', {
        scope: scopeText,
        profile: profileSelect.options[profileSelect.selectedIndex]?.text ?? '',
      }) + t('exam.startConfirm2'),
      confirmText: t('exam.confirmStart'),
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
      toast('ok', t('exam.started'), t('exam.startedDesc', { count: result.targetCount }));
      await render(container);
    } catch (err) {
      toastError(err, t('exam.startFailed'));
      startBtn.disabled = false;
    }
  });

  return h('div.card',
    h('div', { style: { fontWeight: '600', marginBottom: '10px' } }, t('exam.start')),
    h('div', field(t('exam.profile'), profileSelect)),
    h('div', field(t('exam.scope'), scopeSelect)),
    h('div', groupField),
    h('div', field(t('exam.message'), messageInput, t('exam.messageHint'))),
    h('label.checkbox-field', notifyChk, h('span', t('exam.pushNotice'))),
    h('div', { style: { marginTop: '12px' } }, startBtn));
}

/** 结束按钮（带确认：还原是不可撤销的档案改写）。 */
function renderEndCard(container) {
  const endBtn = h('button.btn', { type: 'button' }, t('exam.end'));
  endBtn.addEventListener('click', async () => {
    if (!await confirmDialog({
      title: t('exam.endTitle'),
      message: t('exam.endConfirm'),
      confirmText: t('exam.endConfirmBtn'),
    })) {
      return;
    }

    endBtn.disabled = true;
    try {
      await api('/admin/exam-mode/end', { method: 'POST' });
      toast('ok', t('exam.ended'), t('exam.endedDesc'));
      await render(container);
    } catch (err) {
      toastError(err, t('exam.endFailed'));
      endBtn.disabled = false;
    }
  });

  return h('div.card', endBtn);
}
