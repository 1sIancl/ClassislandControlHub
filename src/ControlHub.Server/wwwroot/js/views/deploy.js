/**
 * 配置下发视图：查看绑定关系、按范围推送，并跟踪客户端同步进度。
 */

import { api, session } from '../core/api.js?v=10';
import {
  h, clear, relativeTime, toast, loadingBlock, modal, field, select,
  emptyState, confirmDialog, deviceStateBadge, syncBadge,
} from '../core/ui.js?v=10';

export const meta = {
  title: '配置下发',
  subtitle: '定向推送新配置并跟踪客户端同步进度',
};

export async function render(container) {
  clear(container);
  container.appendChild(loadingBlock());

  const [assignments, devices, groups, profiles, info] = await Promise.all([
    api('/admin/assignments'),
    api('/admin/devices'),
    api('/admin/groups'),
    api('/admin/profiles'),
    api('/server/info', { auth: false }),
  ]);

  session.revision = info.revision;

  const pending = devices.filter((d) => !d.revoked && !d.upToDate);

  clear(container);
  container.appendChild(h('div',
    renderProgress(info, devices, pending),
    h('div.grid-2',
      renderAssignments(assignments),
      renderPushPanel(groups, profiles, devices),
    ),
    renderPending(pending),
  ));
}

function renderProgress(info, devices, pending) {
  const active = devices.filter((d) => !d.revoked).length;
  const online = devices.filter((d) => d.online && !d.revoked).length;
  const synced = active - pending.length;
  const percent = active === 0 ? 100 : Math.round((synced / active) * 100);

  return h('div.card',
    h('div.card-head',
      h('div',
        h('h3', '同步进度'),
        h('p.card-desc', `当前全局配置版本 #${info.revision}，客户端会在收到通知后数秒内自动拉取。`),
      ),
      h('span.badge.badge-accent', `在线 ${online} / ${active}`),
    ),
    h('div', { style: { marginBottom: '10px', display: 'flex', justifyContent: 'space-between', fontSize: '13px' } },
      h('span', `已同步 ${synced} 台`),
      h('span', { style: { color: 'var(--text-faint)' } }, `待同步 ${pending.length} 台 · ${percent}%`),
    ),
    h('div.progress', h('span', { style: { width: `${percent}%` } })),
  );
}

function renderAssignments(assignments) {
  return h('div.card',
    h('div.card-head',
      h('div',
        h('h3', `绑定关系（${assignments.length}）`),
        h('p.card-desc', '设备与分组的档案绑定。设备单独绑定优先于所属分组。'),
      ),
      h('a', { href: '#/devices', class: 'btn btn-sm', style: { textDecoration: 'none' } }, '调整绑定'),
    ),
    assignments.length === 0
      ? emptyState('🔗', '还没有绑定关系',
        '所有设备当前都使用全局默认档案。可在「设备管理」或「分组管理」中指定具体档案。')
      : h('div.table-wrap',
        h('table.data',
          h('thead', h('tr',
            h('th', '目标'),
            h('th', '类型'),
            h('th', '配置档案'),
          )),
          h('tbody', ...assignments.map((a) => h('tr',
            h('td', h('div.cell-main', a.targetName || a.targetId)),
            h('td', a.targetType === 'group'
              ? h('span.badge.badge-info', '分组默认')
              : h('span.badge.badge-neutral', '设备指定')),
            h('td', a.profileName || h('span', { style: { color: 'var(--danger)' } }, '档案已删除')),
          ))),
        ),
      ),
  );
}

function renderPushPanel(groups, profiles, devices) {
  const scopeSelect = select([
    { value: 'all', label: '全部在线设备' },
    { value: 'group', label: '指定分组' },
    { value: 'device', label: '指定设备' },
  ], 'all', () => repaintTarget());

  const targetHost = h('div');
  const messageInput = h('input', { type: 'text', placeholder: '可选，客户端会在界面上提示该消息' });

  function repaintTarget() {
    clear(targetHost);
    const scope = scopeSelect.value;

    if (scope === 'group') {
      if (groups.length === 0) {
        targetHost.appendChild(h('div', { style: { fontSize: '12.5px', color: 'var(--text-faint)' } }, '尚未创建任何分组。'));
        return;
      }
      targetHost.appendChild(field('目标分组',
        h('div', { style: { display: 'grid', gap: '6px' } },
          ...groups.map((g) => {
            const box = h('input', { type: 'checkbox', dataset: { targetId: g.id } });
            return h('label.checkbox-field', box, `${g.name}（${g.deviceCount} 台设备）`);
          }),
        )),
      );
    } else if (scope === 'device') {
      const active = devices.filter((d) => !d.revoked);
      if (active.length === 0) {
        targetHost.appendChild(h('div', { style: { fontSize: '12.5px', color: 'var(--text-faint)' } }, '尚未接入任何设备。'));
        return;
      }
      targetHost.appendChild(field('目标设备',
        h('div', {
          style: { display: 'grid', gap: '6px', maxHeight: '200px', overflowY: 'auto', padding: '4px 0' },
        },
          ...active.map((d) => {
            const box = h('input', { type: 'checkbox', dataset: { targetId: d.id } });
            return h('label.checkbox-field', box, `${d.name}${d.online ? '' : '（离线）'}`);
          }),
        )),
      );
    }
  }

  repaintTarget();

  const pushButton = h('button.btn.btn-primary', { type: 'button' }, '立即推送');
  pushButton.addEventListener('click', async () => {
    const scope = scopeSelect.value;
    let targetIds = [];

    if (scope !== 'all') {
      targetIds = Array.from(targetHost.querySelectorAll('input[type="checkbox"]'))
        .filter((box) => box.checked)
        .map((box) => box.dataset.targetId)
        .filter(Boolean);

      if (targetIds.length === 0) {
        toast('warn', '请选择推送目标');
        return;
      }
    }

    const scopeText = scope === 'all' ? '全部在线设备' : `${scope === 'group' ? '分组' : '设备'}（${targetIds.length} 个）`;
    const ok = await confirmDialog('确认推送',
      `将立即通知 ${scopeText} 重新拉取当前配置。确定继续吗？`, '推送');
    if (!ok) return;

    pushButton.disabled = true;
    try {
      const result = await api('/admin/push', {
        method: 'POST',
        body: { scope, targetIds, force: false, message: messageInput.value.trim() },
      });
      toast('ok', '推送已发出', `影响 ${result.affected} 台设备，当前版本 #${result.revision}。`);
      await render(document.getElementById('content'));
    } catch (err) {
      toast('error', '推送失败', err.message);
    } finally {
      pushButton.disabled = false;
    }
  });

  return h('div.card',
    h('div.card-head',
      h('div',
        h('h3', '立即推送'),
        h('p.card-desc', '推送只改变生效时机，不修改内容。内容变更（保存档案）本身也会自动触发同步。'),
      ),
    ),
    field('推送范围', scopeSelect),
    targetHost,
    field('附带消息', messageInput, '客户端同步成功后可在插件界面看到该提示，10 分钟内有效。'),
    h('div.card-actions', pushButton),
  );
}

function renderPending(pending) {
  if (pending.length === 0) {
    return h('div.card', { style: { marginTop: '16px' } },
      h('div.notice.notice-ok', { style: { margin: 0 } },
        h('span.notice-icon', '✓'),
        h('div', '所有设备均已同步到最新配置。'),
      ),
    );
  }

  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', `待同步设备（${pending.length}）`),
        h('p.card-desc', '离线设备会在下次上线后自动补齐，无需人工干预。'),
      ),
      h('button.btn.btn-sm.btn-primary', {
        type: 'button',
        onClick: async () => {
          const offlineOnly = pending.every((d) => !d.online);
          if (offlineOnly) {
            toast('warn', '设备当前离线', '离线设备将在重新上线后自动同步，请稍后再查看。');
            return;
          }
          const result = await api('/admin/push', {
            method: 'POST',
            body: { scope: 'device', targetIds: pending.map((d) => d.id), force: true, message: '' },
          });
          toast('ok', '已重新推送', `影响 ${result.affected} 台设备。`);
          await render(document.getElementById('content'));
        },
      }, '对未同步设备重新推送'),
    ),
    h('div.table-wrap',
      h('table.data',
        h('thead', h('tr',
          h('th', '设备'),
          h('th', '分组'),
          h('th', '状态'),
          h('th', '已应用版本'),
          h('th', '最近心跳'),
        )),
        h('tbody', ...pending.map((d) => h('tr',
          h('td',
            h('div.cell-main', d.name),
            h('div.cell-sub', d.machineName || ''),
          ),
          h('td', d.groupName || '未分组'),
          h('td', deviceStateBadge(d)),
          h('td', syncBadge(d)),
          h('td', { style: { fontSize: '12px', color: 'var(--text-faint)' } }, relativeTime(d.lastSeenAt)),
        ))),
      ),
    ),
  );
}
