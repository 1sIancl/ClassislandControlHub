/**
 * 时间偏移（授时）视图。
 *
 * <para>从「系统维护」挪到「教学配置」：它调的是**教室大屏上的时钟**——
 * 「设备时钟每天快 3 秒」是教学场景里的问题，改它的人通常正在配课表与作息，
 * 而不是在排查服务器。放在「系统维护」里，找它的路径太绕。</para>
 *
 * <para>权限仍是 settings.read（接口 /admin/time-offset 的校验在服务端，
 * 不是随导航走的），所以这一页只对有系统设置权限的账号出现。</para>
 */

import { api, hasPermission } from '../core/api.js?v=144';
import {
  h, clear, formatDateTime, toast, loadingBlock, field, kvRow,
} from '../core/ui.js?v=144';
import { t } from '../core/i18n.js?v=144';

export const meta = {
  title: t('tsync.title'),
  subtitle: t('tsync.subtitle'),
};

export async function render(container) {
  clear(container);
  container.appendChild(loadingBlock());

  // 没有 settings.read 的账号拿不到这个接口（403），那就别请求——
  // 直接说明「没权限」比抛一句英文错误好得多。
  if (!hasPermission('settings.read')) {
    clear(container);
    container.appendChild(h('div.notice.notice-warn',
      h('span.notice-icon', '!'),
      h('div', t('tsync.noPermission'))));
    return;
  }

  const state = await api('/admin/time-offset');

  // 保存后重新拉一次「读数」再整页重绘：分组那张表还带「设备台数 / 按层级继承出的生效值」，
  // 重绘比在本地拼状态更不容易出现「界面显示的和真正生效的不一致」。
  const refresh = async () => {
    const next = await api('/admin/time-offset');
    clear(container);
    container.appendChild(h('div', renderTimeCard(next, refresh)));
    container.appendChild(h('div', renderGroupCard(next, refresh)));
  };

  clear(container);
  container.appendChild(h('div', renderTimeCard(state, refresh)));
  container.appendChild(h('div', renderGroupCard(state, refresh)));
}

function renderTimeCard(timeOffset, refresh) {
  const offsetInput = h('input', {
    type: 'number', step: '1', value: timeOffset.offsetSeconds ?? 0, placeholder: '0',
  });
  const driftInput = h('input', {
    type: 'number',
    step: '0.5',
    value: timeOffset.dailyDriftSeconds ?? 0,
    placeholder: '0',
  });

  const save = async (body, message) => {
    await api('/admin/time-offset', { method: 'PUT', body });
    toast('ok', t('tsync.saved'), message);
    await refresh();
  };

  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', t('tsync.cardGlobal')),
        h('p.card-desc', t('tsync.cardGlobalDesc')),
      ),
    ),
    h('div', { style: { display: 'grid', gap: '10px', fontSize: '13px', marginBottom: '14px' } },
      kvRow(t('tsync.fixedOffset'), t('tsync.secondsValue', { value: timeOffset.offsetSeconds ?? 0 })),
      kvRow(t('tsync.dailyDrift'), t('tsync.secondsPerDay', { value: timeOffset.dailyDriftSeconds ?? 0 })),
      kvRow(t('tsync.accumulated'), t('tsync.driftAccumulated', { days: timeOffset.driftDays ?? 0, seconds: (timeOffset.driftCompensationSeconds ?? 0).toFixed(1) })),
      kvRow(t('tsync.ntpOffset'), t('tsync.secondsValue', { value: timeOffset.ntpOffsetSeconds ?? 0 })),
      kvRow(t('tsync.currentTime'), formatDateTime(timeOffset.serverTime)),
      kvRow(t('tsync.status'), timeOffset.lastSyncStatus || '—'),
    ),

    field(t('tsync.fixedOffsetLabel'), offsetInput,
      t('tsync.fixedOffsetHint')),

    field(t('tsync.driftLabel'), driftInput,
      t('tsync.driftHint1')
      + t('tsync.driftHint2')
      + t('tsync.driftHint3')),

    h('div.card-actions',
      h('button.btn.btn-primary.btn-sm', {
        type: 'button',
        onClick: async () => {
          const seconds = Number(offsetInput.value);
          if (Number.isNaN(seconds)) {
            toast('warn', t('tsync.invalidNumber'));
            return;
          }

          await save({ offsetSeconds: seconds }, t('tsync.fixedSaved'));
        },
      }, t('tsync.saveFixed')),
      h('button.btn.btn-sm', {
        type: 'button',
        onClick: async () => {
          const drift = Number(driftInput.value);
          if (Number.isNaN(drift)) {
            toast('warn', t('tsync.invalidNumber'));
            return;
          }

          await save({ offsetSeconds: Number(offsetInput.value) || 0, dailyDriftSeconds: drift },
            t('tsync.driftSaved', { drift }));
        },
      }, t('tsync.saveDrift')),
      h('button.btn.btn-sm.btn-ghost', {
        type: 'button',
        onClick: async () => {
          await save({ offsetSeconds: Number(offsetInput.value) || 0, resetDriftAnchor: true },
            t('tsync.driftReset'));
        },
      }, t('tsync.resetStart')),
    ),
    h('p.card-desc', { style: { marginTop: '8px' } },
      t('tsync.resetHint1')
      + t('tsync.resetHint2')),
  );
}

/**
 * 分组（校区）时间偏移（#26）。
 *
 * <para>为什么单独一张卡：它是「每个分组一行」的表，和上面那几个全局标量混在一起，
 * 就看不出「全局 → 分组」这两层是叠加关系了。</para>
 *
 * <para>为什么按来源地址判定：教室端是从服务器内置 NTP（UDP 123）取时间的，
 * NTP 报文里没有设备身份，只能用来源地址对应到设备、再由设备所属分组决定叠加多少。</para>
 */
function renderGroupCard(state, refresh) {
  const groups = state.groups ?? [];

  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', t('tsync.groupTitle')),
        h('p.card-desc',
          t('tsync.groupHint1')
          + t('tsync.groupHint2')
          + t('tsync.groupHint3')),
        h('p.card-desc',
          t('tsync.ntpHint1')
          + t('tsync.ntpHint2')),
      ),
    ),

    groups.length === 0
      ? h('div.notice.notice-info',
          h('span.notice-icon', 'i'),
          h('div', t('tsync.noGroups')
            + t('tsync.noGroupsHint')))
      : h('table.data',
          h('thead', h('tr',
            h('th', t('tsync.colGroup')),
            h('th', t('tsync.colDevices')),
            h('th', t('tsync.colOffset')),
            h('th', t('tsync.colEffective')),
            h('th', t('tsync.colActions')))),
          h('tbody', groups.map((group) => renderGroupRow(group, refresh)))),
  );
}

function renderGroupRow(group, refresh) {
  const input = h('input', {
    type: 'number', step: '1', style: { width: '110px' },
    value: group.offsetSeconds ?? '',
    placeholder: t('tsync.inherit'),
  });

  const effective = group.effectiveSeconds ?? 0;
  const stateText = group.offsetSeconds === null
    ? (effective === 0 ? t('tsync.notSetGlobalOnly') : t('tsync.inheritedValue', { value: effective }))
    : t('tsync.configured');

  const save = async (body, message) => {
    await api('/admin/time-offset', { method: 'PUT', body });
    toast('ok', t('tsync.saved'), message);
    await refresh();
  };

  return h('tr', { dataset: { groupId: group.id } },
    h('td',
      h('div', { style: { fontWeight: '600' } }, group.name),
      h('div.cell-sub', group.parentName ? t('tsync.parentName', { name: group.parentName }) : t('tsync.topLevel'))),
    h('td', t('tsync.deviceCount', { count: group.deviceCount ?? 0 })),
    h('td', h('div', { style: { display: 'flex', gap: '6px', alignItems: 'center' } },
      input, h('span.cell-sub', t('tsync.unitSeconds')))),
    h('td',
      h('div', effective === 0 ? '—' : t('tsync.secondsValue', { value: effective })),
      h('div.cell-sub', stateText)),
    h('td',
      h('div', { style: { display: 'flex', gap: '6px' } },
        h('button.btn.btn-primary.btn-sm', {
          type: 'button',
          onClick: async () => {
            const raw = input.value.trim();
            if (raw === '') {
              toast('warn', t('tsync.enterSeconds'), t('tsync.clearHint'));
              return;
            }

            const seconds = Number(raw);
            if (Number.isNaN(seconds)) {
              toast('warn', t('tsync.invalidNumber'));
              return;
            }

            await save({ groupId: group.id, groupOffsetSeconds: seconds },
              t('tsync.groupSaved', { name: group.name, seconds }));
          },
        }, t('tsync.save')),
        h('button.btn.btn-sm.btn-ghost', {
          type: 'button',
          onClick: async () => {
            await save({ groupId: group.id, groupOffsetSeconds: null },
              t('tsync.groupCleared', { name: group.name }));
          },
        }, t('tsync.clear')))),
  );
}
