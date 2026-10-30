/**
 * 配置下发视图：查看绑定关系、按范围推送，并跟踪客户端同步进度。
 */

import { api, session } from '../core/api.js?v=161';
import { toastError } from '../core/errors.js?v=161';
import {
  h, clear, relativeTime, toast, loadingBlock, modal, field, select,
  emptyState, confirmDialog, deviceStateBadge, syncBadge,
} from '../core/ui.js?v=161';
import { t } from '../core/i18n.js?v=161';

export const meta = {
  title: t('deploy.title'),
  subtitle: t('deploy.subtitle'),
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
        h('h3', t('deploy.progress')),
        h('p.card-desc', t('deploy.revisionLine', { revision: info.revision })),
      ),
      h('span.badge.badge-accent', t('deploy.onlineRatio', { online, active })),
    ),
    h('div', { style: { marginBottom: '10px', display: 'flex', justifyContent: 'space-between', fontSize: '13px' } },
      h('span', t('deploy.syncedCount', { count: synced })),
      h('span', { style: { color: 'var(--text-faint)' } }, t('deploy.pendingCount', { count: pending.length, percent })),
    ),
    h('div.progress', h('span', { style: { width: `${percent}%` } })),
  );
}

function renderAssignments(assignments) {
  return h('div.card',
    h('div.card-head',
      h('div',
        h('h3', t('deploy.bindingsTitle', { count: assignments.length })),
        h('p.card-desc', t('deploy.bindingsDesc')),
      ),
      h('a', { href: '#/devices', class: 'btn btn-sm', style: { textDecoration: 'none' } }, t('deploy.adjustBindings')),
    ),
    assignments.length === 0
      ? emptyState('link', t('deploy.noBindings'),
        t('deploy.noBindingsDesc'))
      : h('div.table-wrap',
        h('table.data',
          h('thead', h('tr',
            h('th', t('deploy.colTarget')),
            h('th', t('deploy.colKind')),
            h('th', t('deploy.colProfile')),
          )),
          h('tbody', ...assignments.map((a) => h('tr',
            h('td', h('div.cell-main', a.targetName || a.targetId)),
            h('td', a.targetType === 'group'
              ? h('span.badge.badge-info', t('deploy.kindGroup'))
              : h('span.badge.badge-neutral', t('deploy.kindDevice'))),
            h('td', a.profileName || h('span', { style: { color: 'var(--danger)' } }, t('deploy.profileDeleted'))),
          ))),
        ),
      ),
  );
}

function renderPushPanel(groups, profiles, devices) {
  const scopeSelect = select([
    { value: 'all', label: t('deploy.scopeAll') },
    { value: 'group', label: t('deploy.scopeGroup') },
    { value: 'device', label: t('deploy.scopeDevices') },
  ], 'all', () => repaintTarget());

  const targetHost = h('div');
  const messageInput = h('input', { type: 'text', placeholder: t('deploy.messagePlaceholder') });

  function repaintTarget() {
    clear(targetHost);
    const scope = scopeSelect.value;

    if (scope === 'group') {
      if (groups.length === 0) {
        targetHost.appendChild(h('div', { style: { fontSize: '12.5px', color: 'var(--text-faint)' } }, t('deploy.noGroups')));
        return;
      }
      targetHost.appendChild(field(t('deploy.targetGroup'),
        h('div', { style: { display: 'grid', gap: '6px' } },
          ...groups.map((g) => {
            const box = h('input', { type: 'checkbox', dataset: { targetId: g.id } });
            return h('label.checkbox-field', box, t('deploy.groupOption', { name: g.name, count: g.deviceCount }));
          }),
        )),
      );
    } else if (scope === 'device') {
      const active = devices.filter((d) => !d.revoked);
      if (active.length === 0) {
        targetHost.appendChild(h('div', { style: { fontSize: '12.5px', color: 'var(--text-faint)' } }, t('deploy.noDevices')));
        return;
      }
      targetHost.appendChild(field(t('deploy.targetDevice'),
        h('div', {
          style: { display: 'grid', gap: '6px', maxHeight: '200px', overflowY: 'auto', padding: '4px 0' },
        },
          ...active.map((d) => {
            const box = h('input', { type: 'checkbox', dataset: { targetId: d.id } });
            return h('label.checkbox-field', box, d.online ? d.name : t('deploy.deviceOption', { name: d.name }));
          }),
        )),
      );
    }
  }

  repaintTarget();

  const pushButton = h('button.btn.btn-primary', { type: 'button' }, t('deploy.pushNow'));
  pushButton.addEventListener('click', async () => {
    const scope = scopeSelect.value;
    let targetIds = [];

    if (scope !== 'all') {
      targetIds = Array.from(targetHost.querySelectorAll('input[type="checkbox"]'))
        .filter((box) => box.checked)
        .map((box) => box.dataset.targetId)
        .filter(Boolean);

      if (targetIds.length === 0) {
        toast('warn', t('deploy.needTarget'));
        return;
      }
    }

    const scopeText = scope === 'all' ? t('deploy.scopeAll') : t('deploy.scopeSummary', { count: targetIds.length, scope: scope === 'group' ? t('deploy.wordGroup') : t('deploy.wordDevice') });

    const push = async () => {
      pushButton.disabled = true;
      try {
        const result = await api('/admin/push', {
          method: 'POST',
          body: { scope, targetIds, force: false, message: messageInput.value.trim() },
        });
        toast('ok', t('deploy.pushed'), t('deploy.pushedToast', { count: result.affected, revision: result.revision }));
        await render(document.getElementById('content'));
      } catch (err) {
        toastError(err, t('deploy.pushFailed'));
      } finally {
        pushButton.disabled = false;
      }
    };

    // 先对比「设备当前实际应用的档案」与「即将下发的档案」：有变化时用差异清单替代一句确认，
    // 避免管理员在不知情的情况下把整间教室的课表换掉。
    pushButton.disabled = true;
    const diffs = await loadApplyDiffs(scope, targetIds, devices);
    pushButton.disabled = false;

    if (diffs.some((d) => d.changes > 0)) {
      openDiffDialog(diffs, scopeText, push);
      return;
    }

    const ok = await confirmDialog(t('deploy.confirmTitle'),
      t('deploy.confirmMessage', { scope: scopeText }), t('deploy.confirmButton'));
    if (!ok) return;
    await push();
  });

  return h('div.card',
    h('div.card-head',
      h('div',
        h('h3', t('deploy.pushNow')),
        h('p.card-desc', t('deploy.pushHint')),
      ),
    ),
    field(t('deploy.scope'), scopeSelect),
    targetHost,
    field(t('deploy.message'), messageInput, t('deploy.messageHint')),
    h('div.card-actions', pushButton),
  );
}

/**
 * 拉取推送目标的差异预览。
 * <para>最多对比 20 台：预览是为了「点下去之前心里有数」，而不是审计，没必要为几百台各打一次请求。</para>
 */
async function loadApplyDiffs(scope, targetIds, devices) {
  let ids = targetIds;
  if (scope === 'all') {
    ids = devices.filter((d) => d.online && !d.revoked).map((d) => d.id);
  } else if (scope === 'group') {
    ids = devices.filter((d) => !d.revoked && targetIds.includes(d.groupId)).map((d) => d.id);
  }

  const result = [];
  for (const id of ids.slice(0, 20)) {
    try {
      const diff = await api(`/admin/devices/${id}/apply-diff`);
      const device = devices.find((d) => d.id === id);
      const changes = diff.sections.reduce(
        (sum, s) => sum + s.added.length + s.removed.length + s.changed.length, 0);
      result.push({ deviceName: device ? device.name : id, changes, ...diff });
    } catch {
      // 单台对比失败不影响整体预览。
    }
  }

  return result;
}

/** 差异预览对话框：列出每台设备的「新增 / 修改 / 删除」，确认后才真正推送。 */
function openDiffDialog(diffs, scopeText, onConfirm) {
  const changed = diffs.filter((d) => d.changes > 0).length;

  return modal({
    title: t('deploy.diffTitle'),
    width: 'wide',
    confirmText: t('deploy.confirmTitle'),
    body: h('div',
      h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', t('deploy.previewIntro', { scope: scopeText, compared: diffs.length, changed })
          + t('deploy.diffHint'))),
      h('div.cmd-list', { style: { marginTop: '12px' } }, ...diffs.map(diffBlock)),
    ),
    onConfirm,
  });
}

/** 单台设备的差异条目。 */
function diffBlock(entry) {
  const sections = entry.sections.filter(
    (s) => s.added.length + s.removed.length + s.changed.length > 0);

  return h('div.cmd-row',
    h('div.cmd-head',
      h('span.cmd-kind', entry.deviceName),
      h('span.log-level.' + (entry.hasBaseline ? 'warn' : 'info'), entry.hasBaseline ? t('deploy.diffBaseline') : t('deploy.diffFirst')),
      h('span.log-time', `${entry.appliedProfileName || '—'} → ${entry.targetProfileName || '—'}`),
    ),
    entry.note ? h('div.shot-note', entry.note) : null,
    ...sections.flatMap((s) => [
      h('div.shot-note', { style: { color: 'var(--text)', fontWeight: '600' } },
        t('deploy.sectionChanges', { section: s.section, count: s.added.length + s.removed.length + s.changed.length })),
      ...s.added.map((x) => h('div.shot-note', { style: { color: 'var(--ok, #1a7f37)' } }, t('deploy.diffAdded', { text: x }))),
      ...s.changed.map((x) => h('div.shot-note', { style: { color: 'var(--warn, #b26a00)' } }, t('deploy.diffChanged', { text: x }))),
      ...s.removed.map((x) => h('div.shot-note', { style: { color: 'var(--danger)' } }, t('deploy.diffRemoved', { text: x }))),
    ]),
  );
}

function renderPending(pending) {
  if (pending.length === 0) {
    return h('div.card', { style: { marginTop: '16px' } },
      h('div.notice.notice-ok', { style: { margin: 0 } },
        h('span.notice-icon', '✓'),
        h('div', t('deploy.allSynced')),
      ),
    );
  }

  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', t('deploy.pendingTitle', { count: pending.length })),
        h('p.card-desc', t('deploy.pendingHint')),
      ),
      h('button.btn.btn-sm.btn-primary', {
        type: 'button',
        onClick: async () => {
          const offlineOnly = pending.every((d) => !d.online);
          if (offlineOnly) {
            toast('warn', t('deploy.deviceOffline'), t('deploy.deviceOfflineDesc'));
            return;
          }
          const result = await api('/admin/push', {
            method: 'POST',
            body: { scope: 'device', targetIds: pending.map((d) => d.id), force: true, message: '' },
          });
          toast('ok', t('deploy.repushed'), t('deploy.repushedToast', { count: result.affected }));
          await render(document.getElementById('content'));
        },
      }, t('deploy.repush')),
    ),
    h('div.table-wrap',
      h('table.data',
        h('thead', h('tr',
          h('th', t('deploy.wordDevice')),
          h('th', t('deploy.wordGroup')),
          h('th', t('deploy.colState')),
          h('th', t('deploy.colApplied')),
          h('th', t('deploy.colLastSeen')),
        )),
        h('tbody', ...pending.map((d) => h('tr',
          h('td',
            h('div.cell-main', d.name),
            h('div.cell-sub', d.machineName || ''),
          ),
          h('td', d.groupName || t('deploy.ungrouped')),
          h('td', deviceStateBadge(d)),
          h('td', syncBadge(d)),
          h('td', { style: { fontSize: '12px', color: 'var(--text-faint)' } }, relativeTime(d.lastSeenAt)),
        ))),
      ),
    ),
  );
}
