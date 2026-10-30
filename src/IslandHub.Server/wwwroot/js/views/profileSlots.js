/**
 * 走班制时段规则（#25）：第 N 节时，这台教室用哪个档案。
 *
 * 规则存的是**节次**而不是钟点：作息表会变（冬令时、临时调整），存钟点的话每次改作息都要重配。
 * 后台每 30 秒对一次点，只在「该换」时才改档案并下发；这里提供配置入口与「立即对齐」。
 */

import { api } from '../core/api.js?v=161';
import { toastError } from '../core/errors.js?v=161';
import { h, clear, toast, loadingBlock, field, select, modal, formatDateTime } from '../core/ui.js?v=161';
import { t } from '../core/i18n.js?v=161';

export const meta = {
  title: t('slots.title'),
  subtitle: t('slots.subtitle'),
};

export async function render(container) {
  clear(container);
  container.appendChild(loadingBlock());

  try {
    const [devices, profiles, rules] = await Promise.all([
      api('/admin/devices'),
      api('/admin/profiles'),
      api('/admin/profile-slots'),
    ]);

    clear(container);
    container.appendChild(renderHead(container, rules));
    container.appendChild(renderPicker(container, devices, profiles));
    container.appendChild(renderTable(container, rules, profiles));
  } catch (err) {
    clear(container);
    toastError(err, t('slots.loadFailed'));
  }
}

/** 顶部：说明 + 立即对齐。 */
function renderHead(container, rules) {
  const applyBtn = h('button.btn', { type: 'button' }, t('slots.alignNow'));
  applyBtn.addEventListener('click', async () => {
    applyBtn.disabled = true;
    try {
      const report = await api('/admin/profile-slots/apply-now', { method: 'POST' });
      const changed = report?.changed ?? 0;
      toast('ok', t('slots.alignResult', { checked: report?.deviceCount ?? 0, changed }),
        (report?.items ?? []).slice(0, 3).map((i) => `${i.deviceName}：${i.message}`).join('；'));
      await render(container);
    } catch (err) {
      toastError(err, t('slots.alignFailed'));
    } finally {
      applyBtn.disabled = false;
    }
  });

  return h('div.card',
    h('div', { style: { fontWeight: '600', marginBottom: '6px' } }, t('slots.rulesTitle')),
    h('div.cell-sub', t('slots.rulesDesc1')),
    h('div.notice.notice-info', { style: { marginTop: '10px' } },
      h('span.notice-icon', 'i'),
      h('div', t('slots.rulesDesc2')
        + t('slots.rulesDesc3'))),
    h('div', { style: { marginTop: '12px', display: 'flex', gap: '10px', alignItems: 'center' } },
      applyBtn,
      h('span.cell-sub', t('slots.configuredCount', { count: rules.length }))));
}

/** 给还没配规则的教室开一个入口。 */
function renderPicker(container, devices, profiles) {
  const withRules = new Set();   // 表格里已有的教室不必再选一遍
  const candidates = devices.filter((d) => !d.revoked);
  const deviceSelect = select(
    candidates.map((d) => ({ value: d.id, label: d.name })), candidates[0]?.id ?? '');
  const openBtn = h('button.btn', { type: 'button' }, t('slots.configureRoom'));
  openBtn.addEventListener('click', () => {
    if (!deviceSelect.value) {
      toast('warn', t('slots.noRooms'));
      return;
    }

    openEditor(container, deviceSelect.value, profiles);
  });

  return h('div.card',
    h('div', { style: { marginBottom: '10px', fontWeight: '600' } }, t('slots.configureOne')),
    h('div', { style: { display: 'flex', gap: '10px', alignItems: 'flex-end', flexWrap: 'wrap' } },
      field(t('slots.colRoom'), deviceSelect),
      openBtn),
    withRules.size === 0 ? null : null);
}

/** 已配置规则的教室一览。 */
function renderTable(container, rules, profiles) {
  if (rules.length === 0) {
    return h('div.card', t('slots.empty'));
  }

  const rows = rules.map((r) => h('tr',
    h('td', r.deviceName),
    h('td', r.periodCount > 0 ? t('slots.periodCount', { count: r.periodCount }) : t('slots.noSchedule')),
    h('td', r.currentPeriodIndex === undefined || r.currentPeriodIndex === null
      ? t('slots.notInPeriod')
      : t('slots.currentPeriod', { index: r.currentPeriodIndex + 1 })),
    h('td', r.slots.map((s) => t('slots.ruleSummary', { index: s.periodIndex + 1, profile: s.profileName })).join('，')),
    h('td',
      h('button.btn.btn-sm', {
        type: 'button', onClick: () => openEditor(container, r.deviceId, profiles),
      }, t('slots.edit')))));

  return h('div.card',
    h('div', { style: { marginBottom: '10px', fontWeight: '600' } }, t('slots.configuredRooms')),
    h('div.table-wrap',
      h('table.data',
        h('thead', h('tr',
          h('th', t('slots.colRoom')), h('th', t('slots.colPeriods')), h('th', t('slots.colCurrent')), h('th', t('slots.colRules')), h('th', ''))),
        h('tbody', ...rows))));
}

/** 编辑器：一节一行（节次 + 档案），保存时整体替换。 */
async function openEditor(container, deviceId, profiles) {
  let current;
  try {
    current = await api(`/admin/devices/${deviceId}/profile-slots`);
  } catch (err) {
    toastError(err, t('slots.readFailed'));
    return;
  }

  const periodCount = current.periodCount ?? 0;
  const slots = (current.slots ?? []).map((s) => ({ ...s }));
  const rows = h('div');

  const periodOptions = () => {
    const list = [];
    const max = periodCount > 0 ? periodCount : 12;
    for (let i = 0; i < max; i++) {
      list.push({ value: String(i), label: t('slots.periodLabel', { index: i + 1 }) });
    }

    return list;
  };

  const profileOptions = profiles.map((p) => ({ value: p.id, label: p.name }));

  function repaint() {
    clear(rows);
    slots.forEach((slot, index) => {
      const periodSel = select(periodOptions(), String(slot.periodIndex), (v) => {
        slots[index].periodIndex = Number(v);
      });
      const profileSel = select(profileOptions,
        slot.profileId || (profiles[0]?.id ?? ''), (v) => {
          slots[index].profileId = v;
        });

      rows.appendChild(h('div',
        { style: { display: 'flex', gap: '10px', alignItems: 'flex-end', marginBottom: '8px' } },
        field(t('slots.colPeriod'), periodSel),
        field(t('slots.colProfile'), profileSel),
        h('button.btn.btn-sm', {
          type: 'button',
          onClick: () => {
            slots.splice(index, 1);
            repaint();
          },
        }, t('slots.delete'))));
    });

    if (slots.length === 0) {
      rows.appendChild(h('div.cell-sub', t('slots.noRules')));
    }
  }

  repaint();

  modal({
    title: t('slots.dialogTitle', { name: current.deviceName ?? deviceId }),
    width: 'wide',
    body: h('div',
      periodCount > 0
        ? h('div.notice.notice-info',
          h('span.notice-icon', 'i'),
          h('div', t('slots.periodInfo', { count: periodCount })
            + (current.currentPeriodIndex === undefined || current.currentPeriodIndex === null
              ? t('slots.hintNotInPeriod')
              : t('slots.hintInPeriod', { index: current.currentPeriodIndex + 1 }))))
        : h('div.notice.notice-warn',
          h('span.notice-icon', '!'),
          h('div', t('slots.noScheduleWarn1')
            + t('slots.noScheduleWarn2'))),
      rows,
      h('button.btn.btn-sm', {
        type: 'button',
        onClick: () => {
          slots.push({ periodIndex: slots.length, profileId: profiles[0]?.id ?? '' });
          repaint();
        },
      }, t('slots.addRow'))),
    confirmText: t('slots.save'),
    onConfirm: async () => {
      const payload = slots.map((s) => ({
        periodIndex: Number(s.periodIndex),
        profileId: s.profileId,
      }));

      await api(`/admin/devices/${deviceId}/profile-slots`, { method: 'PUT', body: { slots: payload } });
      toast('ok', t('slots.saved'), payload.length === 0
        ? t('slots.cleared')
        : t('slots.savedCount', { count: payload.length }));
      await render(container);
      return true;
    },
  });
}
