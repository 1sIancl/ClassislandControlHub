/**
 * 走班制时段规则（#25）：第 N 节时，这台教室用哪个档案。
 *
 * 规则存的是**节次**而不是钟点：作息表会变（冬令时、临时调整），存钟点的话每次改作息都要重配。
 * 后台每 30 秒对一次点，只在「该换」时才改档案并下发；这里提供配置入口与「立即对齐」。
 */

import { api } from '../core/api.js?v=128';
import { toastError } from '../core/errors.js?v=128';
import { h, clear, toast, loadingBlock, field, select, modal, formatDateTime } from '../core/ui.js?v=128';

export const meta = {
  title: '走班制',
  subtitle: '按节次自动切换教室档案（同一教室不同节次是不同班级）',
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
    toastError(err, '走班规则加载失败');
  }
}

/** 顶部：说明 + 立即对齐。 */
function renderHead(container, rules) {
  const applyBtn = h('button.btn', { type: 'button' }, '立即按当前时段对齐');
  applyBtn.addEventListener('click', async () => {
    applyBtn.disabled = true;
    try {
      const report = await api('/admin/profile-slots/apply-now', { method: 'POST' });
      const changed = report?.changed ?? 0;
      toast('ok', `已检查 ${report?.deviceCount ?? 0} 台，切换 ${changed} 台`,
        (report?.items ?? []).slice(0, 3).map((i) => `${i.deviceName}：${i.message}`).join('；'));
      await render(container);
    } catch (err) {
      toastError(err, '对齐失败');
    } finally {
      applyBtn.disabled = false;
    }
  });

  return h('div.card',
    h('div', { style: { fontWeight: '600', marginBottom: '6px' } }, '走班制时段规则'),
    h('div.cell-sub', '按节次配置（第 1 节 → A 班档案、第 2 节 → B 班档案……）。'),
    h('div.notice.notice-info', { style: { marginTop: '10px' } },
      h('span.notice-icon', 'i'),
      h('div', '后台每 30 秒检查一次，只在需要换的时候才改档案并下发；课间沿用刚上完那一节，'
        + '放学后保持最后一节。考试模式进行中这些规则不生效。')),
    h('div', { style: { marginTop: '12px', display: 'flex', gap: '10px', alignItems: 'center' } },
      applyBtn,
      h('span.cell-sub', `${rules.length} 台教室已配置`)));
}

/** 给还没配规则的教室开一个入口。 */
function renderPicker(container, devices, profiles) {
  const withRules = new Set();   // 表格里已有的教室不必再选一遍
  const candidates = devices.filter((d) => !d.revoked);
  const deviceSelect = select(
    candidates.map((d) => ({ value: d.id, label: d.name })), candidates[0]?.id ?? '');
  const openBtn = h('button.btn', { type: 'button' }, '配置这台教室的规则');
  openBtn.addEventListener('click', () => {
    if (!deviceSelect.value) {
      toast('warn', '没有可配置的教室');
      return;
    }

    openEditor(container, deviceSelect.value, profiles);
  });

  return h('div.card',
    h('div', { style: { marginBottom: '10px', fontWeight: '600' } }, '配置一台教室'),
    h('div', { style: { display: 'flex', gap: '10px', alignItems: 'flex-end', flexWrap: 'wrap' } },
      field('教室', deviceSelect),
      openBtn),
    withRules.size === 0 ? null : null);
}

/** 已配置规则的教室一览。 */
function renderTable(container, rules, profiles) {
  if (rules.length === 0) {
    return h('div.card', '还没有任何教室配置走班规则。用上面的「配置这台教室的规则」开始。');
  }

  const rows = rules.map((r) => h('tr',
    h('td', r.deviceName),
    h('td', r.periodCount > 0 ? `${r.periodCount} 节` : '（档案里没有作息时间表）'),
    h('td', r.currentPeriodIndex === undefined || r.currentPeriodIndex === null
      ? '不在上课时段'
      : `第 ${r.currentPeriodIndex + 1} 节`),
    h('td', r.slots.map((s) => `第 ${s.periodIndex + 1} 节→${s.profileName}`).join('，')),
    h('td',
      h('button.btn.btn-sm', {
        type: 'button', onClick: () => openEditor(container, r.deviceId, profiles),
      }, '编辑'))));

  return h('div.card',
    h('div', { style: { marginBottom: '10px', fontWeight: '600' } }, '已配置的教室'),
    h('div.table-wrap',
      h('table.data',
        h('thead', h('tr',
          h('th', '教室'), h('th', '作息节数'), h('th', '当前'), h('th', '规则'), h('th', ''))),
        h('tbody', ...rows))));
}

/** 编辑器：一节一行（节次 + 档案），保存时整体替换。 */
async function openEditor(container, deviceId, profiles) {
  let current;
  try {
    current = await api(`/admin/devices/${deviceId}/profile-slots`);
  } catch (err) {
    toastError(err, '读取规则失败');
    return;
  }

  const periodCount = current.periodCount ?? 0;
  const slots = (current.slots ?? []).map((s) => ({ ...s }));
  const rows = h('div');

  const periodOptions = () => {
    const list = [];
    const max = periodCount > 0 ? periodCount : 12;
    for (let i = 0; i < max; i++) {
      list.push({ value: String(i), label: `第 ${i + 1} 节` });
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
        field('节次', periodSel),
        field('档案', profileSel),
        h('button.btn.btn-sm', {
          type: 'button',
          onClick: () => {
            slots.splice(index, 1);
            repaint();
          },
        }, '删除')));
    });

    if (slots.length === 0) {
      rows.appendChild(h('div.cell-sub', '还没有规则。点下面「添加一行」开始。'));
    }
  }

  repaint();

  modal({
    title: `走班规则 · ${current.deviceName ?? deviceId}`,
    width: 'wide',
    body: h('div',
      periodCount > 0
        ? h('div.notice.notice-info',
          h('span.notice-icon', 'i'),
          h('div', `这台教室当前生效档案的作息有 ${periodCount} 节`
            + (current.currentPeriodIndex === undefined || current.currentPeriodIndex === null
              ? '，现在不在上课时段。'
              : `，现在第 ${current.currentPeriodIndex + 1} 节。`)))
        : h('div.notice.notice-warn',
          h('span.notice-icon', '!'),
          h('div', '这台教室当前生效的档案里没有作息时间表，规则不会生效——'
            + '请先给该档案配置时间表。')),
      rows,
      h('button.btn.btn-sm', {
        type: 'button',
        onClick: () => {
          slots.push({ periodIndex: slots.length, profileId: profiles[0]?.id ?? '' });
          repaint();
        },
      }, '添加一行')),
    confirmText: '保存',
    onConfirm: async () => {
      const payload = slots.map((s) => ({
        periodIndex: Number(s.periodIndex),
        profileId: s.profileId,
      }));

      await api(`/admin/devices/${deviceId}/profile-slots`, { method: 'PUT', body: { slots: payload } });
      toast('ok', '已保存', payload.length === 0
        ? '这台教室的走班规则已清空。'
        : `${payload.length} 条规则已生效（下一轮检查时自动切换）。`);
      await render(container);
      return true;
    },
  });
}
