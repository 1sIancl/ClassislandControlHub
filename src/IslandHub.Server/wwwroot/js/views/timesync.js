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

import { api, hasPermission } from '../core/api.js?v=117';
import {
  h, clear, formatDateTime, toast, loadingBlock, field, kvRow,
} from '../core/ui.js?v=117';

export const meta = {
  title: '时间偏移',
  subtitle: '服务器授时校准，可按校区（分组）分别叠加后下发给对应教室终端',
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
      h('div', '当前账号没有「系统设置」权限，看不到授时配置。')));
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
    toast('ok', '已保存', message);
    await refresh();
  };

  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', '时间偏移（授时）'),
        h('p.card-desc', '设定后，服务器会在 NTP 授时基础上叠加这些偏移，再下发给所有教室终端。'),
      ),
    ),
    h('div', { style: { display: 'grid', gap: '10px', fontSize: '13px', marginBottom: '14px' } },
      kvRow('固定偏移', `${timeOffset.offsetSeconds ?? 0} 秒`),
      kvRow('每日漂移', `${timeOffset.dailyDriftSeconds ?? 0} 秒/天`),
      kvRow('已累计', `${timeOffset.driftDays ?? 0} 天 → ${(timeOffset.driftCompensationSeconds ?? 0).toFixed(1)} 秒`),
      kvRow('NTP 校正偏移', `${timeOffset.ntpOffsetSeconds ?? 0} 秒`),
      kvRow('当前授时时间', formatDateTime(timeOffset.serverTime)),
      kvRow('授时状态', timeOffset.lastSyncStatus || '—'),
    ),

    field('固定偏移（秒，正值 = 整体提前）', offsetInput,
      '一次性校正。例如填 180 让所有终端快 3 分钟；填 -60 慢 1 分钟。范围 ±86400 秒。'),

    field('每日漂移（秒/天，正值 = 每天变快）', driftInput,
      '用于补偿「设备时钟每天稳定快/慢几秒」——这种偏差用固定偏移修不好，'
      + '今天校准了明天又偏。填了之后，系统按天数累计补偿（只算整天，'
      + '避免一天之内时间慢慢爬）。范围 ±600 秒/天。改动会把累计起点重置为今天。'),

    h('div.card-actions',
      h('button.btn.btn-primary.btn-sm', {
        type: 'button',
        onClick: async () => {
          const seconds = Number(offsetInput.value);
          if (Number.isNaN(seconds)) {
            toast('warn', '请输入有效数字');
            return;
          }

          await save({ offsetSeconds: seconds }, '固定偏移已生效，将叠加到后续授时中。');
        },
      }, '保存固定偏移'),
      h('button.btn.btn-sm', {
        type: 'button',
        onClick: async () => {
          const drift = Number(driftInput.value);
          if (Number.isNaN(drift)) {
            toast('warn', '请输入有效数字');
            return;
          }

          await save({ offsetSeconds: Number(offsetInput.value) || 0, dailyDriftSeconds: drift },
            `每日漂移 ${drift} 秒/天已生效，累计起点重置为今天。`);
        },
      }, '保存每日漂移'),
      h('button.btn.btn-sm.btn-ghost', {
        type: 'button',
        onClick: async () => {
          await save({ offsetSeconds: Number(offsetInput.value) || 0, resetDriftAnchor: true },
            '累计起点已重置为今天（漂移值保留）。');
        },
      }, '重置累计起点'),
    ),
    h('p.card-desc', { style: { marginTop: '8px' } },
      '「重置累计起点」的用法：给教室设备做过一次人工校准之后点它，'
      + '让累计从今天重新开始——否则之前累计出来的量会继续叠加上去。'),
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
        h('h3', '分组（校区）时间偏移'),
        h('p.card-desc',
          '这里的值叠加在上面那张卡的全局偏移之上：某间校区的教室整体慢 2 分钟，'
          + '就给它的分组填 120。设备取自己所属分组配的值；分组没配就沿上级分组就近继承；'
          + '一路都没配则只叠加全局偏移。'),
        h('p.card-desc',
          '教室端是从服务器内置 NTP 取时间的（UDP 123），报文里没有设备身份，'
          + '因此这里按设备的来源地址判定归属——设备要先接入 IslandHub、上报过地址才会生效。'),
      ),
    ),

    groups.length === 0
      ? h('div.notice.notice-info',
          h('span.notice-icon', 'i'),
          h('div', '还没有任何分组，所以只能设全局偏移。'
            + '先在「设备」页建好校区 / 楼栋分组、把教室放进去，这里才会出现对应的一行。'))
      : h('table.data',
          h('thead', h('tr',
            h('th', '分组'),
            h('th', '设备'),
            h('th', '本组偏移（秒）'),
            h('th', '当前生效'),
            h('th', '操作'))),
          h('tbody', groups.map((group) => renderGroupRow(group, refresh)))),
  );
}

function renderGroupRow(group, refresh) {
  const input = h('input', {
    type: 'number', step: '1', style: { width: '110px' },
    value: group.offsetSeconds ?? '',
    placeholder: '继承上级',
  });

  const effective = group.effectiveSeconds ?? 0;
  const stateText = group.offsetSeconds === null
    ? (effective === 0 ? '未配 · 只叠加全局偏移' : `未配 · 继承出 ${effective} 秒`)
    : '本组已单独配置';

  const save = async (body, message) => {
    await api('/admin/time-offset', { method: 'PUT', body });
    toast('ok', '已保存', message);
    await refresh();
  };

  return h('tr', { dataset: { groupId: group.id } },
    h('td',
      h('div', { style: { fontWeight: '600' } }, group.name),
      h('div.cell-sub', group.parentName ? `上级：${group.parentName}` : '顶层分组')),
    h('td', `${group.deviceCount ?? 0} 台`),
    h('td', h('div', { style: { display: 'flex', gap: '6px', alignItems: 'center' } },
      input, h('span.cell-sub', '秒'))),
    h('td',
      h('div', effective === 0 ? '—' : `${effective} 秒`),
      h('div.cell-sub', stateText)),
    h('td',
      h('div', { style: { display: 'flex', gap: '6px' } },
        h('button.btn.btn-primary.btn-sm', {
          type: 'button',
          onClick: async () => {
            const raw = input.value.trim();
            if (raw === '') {
              toast('warn', '请输入秒数', '要取消这一组的偏移，请点右边的「清除」。');
              return;
            }

            const seconds = Number(raw);
            if (Number.isNaN(seconds)) {
              toast('warn', '请输入有效数字');
              return;
            }

            await save({ groupId: group.id, groupOffsetSeconds: seconds },
              `分组「${group.name}」偏移 ${seconds} 秒已生效。`);
          },
        }, '保存'),
        h('button.btn.btn-sm.btn-ghost', {
          type: 'button',
          onClick: async () => {
            await save({ groupId: group.id, groupOffsetSeconds: null },
              `分组「${group.name}」的偏移已清除，改为继承上级。`);
          },
        }, '清除'))),
  );
}
