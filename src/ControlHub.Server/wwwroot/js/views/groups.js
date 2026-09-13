/**
 * 分组管理视图：按年级/楼层等维度组织设备，并统一下发默认配置档案。
 */

import { api } from '../core/api.js?v=12';
import {
  h, clear, formatDateTime, toast, loadingBlock, modal, confirmDialog,
  emptyState, field, select,
} from '../core/ui.js?v=12';

export const meta = {
  title: '分组管理',
  subtitle: '按年级或楼栋组织设备，统一绑定默认配置档案',
};

export async function render(container) {
  clear(container);
  container.appendChild(loadingBlock());

  const [groups, profiles, devices] = await Promise.all([
    api('/admin/groups'),
    api('/admin/profiles'),
    api('/admin/devices'),
  ]);

  clear(container);
  container.appendChild(h('div',
    h('div.card',
      h('div.card-head',
        h('div',
          h('h3', `分组（${groups.length}）`),
          h('p.card-desc', '分组内设备会继承该组的默认配置档案；也可为单台设备单独指定档案以覆盖分组设置。'),
        ),
        h('button.btn.btn-primary.btn-sm', { type: 'button', onClick: () => openDialog(null) }, '+ 新建分组'),
      ),
      groups.length === 0
        ? emptyState('🗂️', '还没有分组',
          '可以先创建一个「高一年级」之类的分组，再批量把设备加进去。',
          h('button.btn.btn-primary', { type: 'button', onClick: () => openDialog(null) }, '新建分组'))
        : h('div.table-wrap',
          h('table.data',
            h('thead', h('tr',
              h('th', '分组名称'),
              h('th', '默认配置档案'),
              h('th', '设备数'),
              h('th', '创建时间'),
              h('th', { style: { textAlign: 'right' } }, '操作'),
            )),
            h('tbody', ...groups.map((g) => renderRow(g, devices))),
          ),
        ),
    ),
  ));

  function renderRow(group, allDevices) {
    const members = allDevices.filter((d) => d.groupId === group.id);
    const online = members.filter((d) => d.online).length;
    const profile = profiles.find((p) => p.id === group.defaultProfileId);

    return h('tr',
      h('td',
        h('div.cell-main', group.name),
        group.description ? h('div.cell-sub', group.description) : null,
      ),
      h('td', profile
        ? h('span.badge.badge-accent', `${profile.name} · v${profile.revision}`)
        : h('span', { style: { color: 'var(--text-faint)' } }, '未绑定（使用全局默认档案）')),
      h('td',
        members.length === 0
          ? h('span', { style: { color: 'var(--text-faint)' } }, '0')
          : h('span', `${members.length} 台`, h('span', {
            style: { color: 'var(--text-faint)', fontSize: '11.5px', marginLeft: '6px' },
          }, `在线 ${online}`)),
      ),
      h('td', { style: { fontSize: '12px', color: 'var(--text-faint)' } }, formatDateTime(group.createdAt)),
      h('td.actions',
        h('button.btn.btn-sm', { type: 'button', onClick: () => openDialog(group) }, '编辑'),
        ' ',
        h('button.btn.btn-sm', {
          type: 'button',
          onClick: () => window.location.hash = `#/devices?group=${group.id}`,
        }, '查看设备'),
        ' ',
        h('button.btn.btn-sm.btn-danger', {
          type: 'button',
          onClick: () => remove(group, members.length),
        }, '删除'),
      ),
    );
  }

  function openDialog(group) {
    const nameInput = h('input', { type: 'text', value: group?.name || '', placeholder: '例如：高一年级' });
    const descInput = h('input', { type: 'text', value: group?.description || '', placeholder: '可选' });
    const profileSelect = select(
      [
        { value: '', label: '（不指定，使用全局默认档案）' },
        ...profiles.map((p) => ({ value: p.id, label: `${p.name}（内容版本 ${p.revision}）` })),
      ],
      group?.defaultProfileId || '',
    );

    modal({
      title: group ? `编辑分组 · ${group.name}` : '新建分组',
      width: 'wide',
      body: h('div',
        field('分组名称', nameInput),
        field('备注', descInput, '仅用于管理端展示。'),
        field('默认配置档案', profileSelect,
          '组内设备若未单独指定档案，就会使用这里选定的档案。'),
      ),
      confirmText: group ? '保存' : '创建',
      onConfirm: async () => {
        const body = {
          name: nameInput.value.trim(),
          description: descInput.value.trim(),
          defaultProfileId: profileSelect.value,
        };
        if (!body.name) {
          toast('warn', '请填写分组名称');
          return false;
        }

        if (group) {
          await api(`/admin/groups/${group.id}`, { method: 'PUT', body });
          toast('ok', '已保存', '组内设备会立即重新拉取配置。');
        } else {
          await api('/admin/groups', { method: 'POST', body });
          toast('ok', '分组已创建');
        }

        await render(document.getElementById('content'));
        return true;
      },
    });
  }

  async function remove(group, memberCount) {
    const message = memberCount > 0
      ? `删除分组「${group.name}」后，组内 ${memberCount} 台设备会变为未分组（并回退到全局默认档案）。确定继续吗？`
      : `确定删除分组「${group.name}」吗？`;

    if (!await confirmDialog('删除分组', message, '删除', true)) return;

    await api(`/admin/groups/${group.id}`, { method: 'DELETE' });
    toast('ok', '已删除');
    await render(document.getElementById('content'));
  }
}
