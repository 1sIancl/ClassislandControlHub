/**
 * 设备管理视图：设备清单、状态监控、分组/档案绑定与注册码管理。
 */

import { api } from '../core/api.js?v=12';
import {
  h, clear, formatDateTime, relativeTime, toast, loadingBlock,
  modal, confirmDialog, deviceStateBadge, syncBadge,
  emptyState, field, select, copyText, append,
} from '../core/ui.js?v=12';
import { getLayout, saveLayout } from '../core/prefs.js?v=12';

export const meta = {
  title: '设备管理',
  subtitle: '查看教室终端状态、绑定分组与配置档案',
};

let cache = { devices: [], groups: [], profiles: [], codes: [] };
let filter = { keyword: '', groupId: '', state: '' };

// ── 设备表格列定义（支持显隐配置，操作列固定） ──
const COLUMN_DEFS = [
  {
    key: 'name', label: '设备',
    cell: (d) => h('td',
      h('div.cell-main', d.name),
      h('div.cell-sub', [d.machineName, d.ipAddress].filter(Boolean).join(' · ') || d.id.slice(0, 8)),
    ),
  },
  {
    key: 'group', label: '分组',
    cell: (d) => h('td', d.groupName
      ? h('span.badge.badge-neutral', d.groupName)
      : h('span', { style: { color: 'var(--text-faint)' } }, '未分组')),
  },
  {
    key: 'state', label: '状态',
    cell: (d) => h('td',
      deviceStateBadge(d),
      d.lastError ? h('div', {
        title: d.lastError,
        style: { fontSize: '11px', color: 'var(--danger)', marginTop: '3px', maxWidth: '190px', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
      }, d.lastError) : null,
    ),
  },
  {
    key: 'sync', label: '配置同步',
    cell: (d) => h('td', syncBadge(d),
      h('div', { style: { fontSize: '11px', color: 'var(--text-faint)', marginTop: '3px' } },
        `档案版本 ${d.serverRevision}`)),
  },
  {
    key: 'plan', label: '当前课表',
    cell: (d) => h('td', d.currentClassPlanName || h('span', { style: { color: 'var(--text-faint)' } }, '—')),
  },
  {
    key: 'version', label: '版本信息',
    cell: (d) => h('td',
      h('div', { style: { fontSize: '12px' } }, `CI ${d.classIslandVersion || '—'}`),
      h('div', { style: { fontSize: '11px', color: 'var(--text-faint)' } }, `插件 ${d.pluginVersion || '—'}`),
    ),
  },
  {
    key: 'heartbeat', label: '最近心跳',
    cell: (d) => h('td',
      h('div', { style: { fontSize: '12.5px' } }, relativeTime(d.lastSeenAt)),
      h('div', { style: { fontSize: '11px', color: 'var(--text-faint)' } },
        d.lastSyncAt ? `同步 ${relativeTime(d.lastSyncAt)}` : '尚未同步'),
    ),
  },
];

const COLUMN_KEYS = () => COLUMN_DEFS.map((c) => c.key);

function visibleColumns() {
  const layout = getLayout('columns.devices', COLUMN_KEYS());
  return layout
    .filter((l) => l.enabled !== false)
    .map((l) => COLUMN_DEFS.find((c) => c.key === l.key))
    .filter(Boolean);
}

export async function render(container, params = {}) {
  // 支持从分组页跳转过来时预先筛选该分组。
  if (params.group !== undefined) {
    filter.groupId = params.group || '';
  }

  clear(container);
  container.appendChild(loadingBlock());

  const [devices, groups, profiles, codes] = await Promise.all([
    api('/admin/devices'),
    api('/admin/groups'),
    api('/admin/profiles'),
    api('/admin/enroll-codes'),
  ]);

  cache = { devices, groups, profiles, codes };

  clear(container);
  container.appendChild(h('div',
    renderToolbar(),
    renderTable(),
    renderEnrollCodes(),
  ));
}

function renderToolbar() {
  const groupOptions = cache.groups.map((g) => ({ value: g.id, label: g.name }));

  return h('div.toolbar',
    h('input', {
      type: 'text',
      placeholder: '搜索设备名 / 机器名 / IP…',
      value: filter.keyword,
      onInput: (e) => {
        filter.keyword = e.target.value.trim();
        refreshTable();
      },
    }),
    select([{ value: '', label: '全部分组' }, ...groupOptions], filter.groupId, (v) => {
      filter.groupId = v;
      refreshTable();
    }),
    select([
      { value: '', label: '全部状态' },
      { value: 'online', label: '在线' },
      { value: 'offline', label: '离线' },
      { value: 'pending', label: '待同步' },
      { value: 'error', label: '异常' },
      { value: 'revoked', label: '已停用' },
    ], filter.state, (v) => {
      filter.state = v;
      refreshTable();
    }),
    h('div.spacer'),
    h('button.btn', { type: 'button', onClick: openColumnCustomize }, '⚙ 列'),
    h('button.btn', { type: 'button', onClick: () => refresh(true) }, '刷新'),
  );
}

function refreshTable() {
  const host = document.getElementById('deviceTableHost');
  if (host) {
    clear(host);
    host.appendChild(renderTable());
  }
}

async function refresh(showToast = false) {
  const [devices, groups, profiles, codes] = await Promise.all([
    api('/admin/devices'),
    api('/admin/groups'),
    api('/admin/profiles'),
    api('/admin/enroll-codes'),
  ]);
  cache = { devices, groups, profiles, codes };
  const container = document.getElementById('content');
  if (container) {
    clear(container);
    container.appendChild(h('div', renderToolbar(), renderTable(), renderEnrollCodes()));
  }
  if (showToast) toast('ok', '已刷新');
}

function filteredDevices() {
  const keyword = filter.keyword.toLowerCase();
  return cache.devices.filter((d) => {
    if (filter.groupId && d.groupId !== filter.groupId) return false;

    if (filter.state) {
      if (filter.state === 'online' && !d.online) return false;
      if (filter.state === 'offline' && (d.online || d.revoked)) return false;
      if (filter.state === 'pending' && (d.upToDate || d.revoked)) return false;
      if (filter.state === 'error' && d.state !== 'error') return false;
      if (filter.state === 'revoked' && !d.revoked) return false;
    }

    if (!keyword) return true;
    return [d.name, d.machineName, d.ipAddress, d.classIslandVersion, d.currentClassPlanName]
      .filter(Boolean)
      .some((v) => String(v).toLowerCase().includes(keyword));
  });
}

function renderTable() {
  const devices = filteredDevices();

  if (cache.devices.length === 0) {
    return h('div#deviceTableHost', h('div.card',
      emptyState('🖥️', '还没有设备接入',
        '生成一个注册码，然后在教室电脑的 ClassIsland 中安装集控插件并填写该注册码。'),
    ));
  }

  if (devices.length === 0) {
    return h('div#deviceTableHost', h('div.card',
      emptyState('🔍', '没有匹配的设备', '尝试调整搜索关键词或筛选条件。'),
    ));
  }

  return h('div#deviceTableHost', h('div.table-wrap',
    h('table.data',
      h('thead', h('tr',
        ...visibleColumns().map((c) => h('th', c.label)),
        h('th', { style: { textAlign: 'right' } }, '操作'),
      )),
      h('tbody', ...devices.map((d) => h('tr',
        ...visibleColumns().map((c) => c.cell(d)),
        h('td.actions',
          h('button.btn.btn-sm', { type: 'button', onClick: () => openEditDialog(d) }, '编辑'),
          ' ',
          h('button.btn.btn-sm', { type: 'button', onClick: () => openLogsDialog(d) }, '日志'),
          ' ',
          h('button.btn.btn-sm', {
            type: 'button',
            onClick: () => toggleRevoke(d),
          }, d.revoked ? '恢复' : '停用'),
        ),
      ))),
    ),
  ));
}

/** 「列」配置面板：勾选设备表格要显示的列。 */
function openColumnCustomize() {
  const container = h('div');

  function toggleColumn(key, enabled) {
    const layout = getLayout('columns.devices', COLUMN_KEYS());
    const item = layout.find((l) => l.key === key);
    if (item) item.enabled = enabled;
    saveLayout('columns.devices', layout);
    rerender();
  }

  function buildPanel() {
    const layout = getLayout('columns.devices', COLUMN_KEYS());
    const panel = h('div.config-panel');
    layout.forEach((item) => {
      const def = COLUMN_DEFS.find((c) => c.key === item.key);
      const label = def ? def.label : item.key;
      panel.appendChild(h('div.config-item' + (item.enabled === false ? '.disabled' : ''),
        h('span.item-label', label),
        h('input', { type: 'checkbox', checked: item.enabled !== false, onChange: (e) => toggleColumn(item.key, e.target.checked) }),
      ));
    });
    return panel;
  }

  function rerender() {
    clear(container);
    append(container, buildPanel());
  }

  rerender();

  modal({
    title: '自定义设备表格列',
    body: container,
    confirmText: '完成',
    onConfirm: () => {
      refreshTable();
      return true;
    },
  });
}

function openEditDialog(device) {
  const nameInput = h('input', { type: 'text', value: device.name });

  const groupSelect = select(
    [{ value: '', label: '（不分组）' }, ...cache.groups.map((g) => ({ value: g.id, label: g.name }))],
    device.groupId || '',
  );

  const profileSelect = select(
    [
      { value: '', label: '（继承分组 / 默认档案）' },
      ...cache.profiles.map((p) => ({ value: p.id, label: `${p.name}（内容版本 ${p.revision}）` })),
    ],
    device.profileId || '',
  );

  modal({
    title: `编辑设备 · ${device.name}`,
    width: 'wide',
    body: h('div',
      field('设备名称', nameInput, '显示在管理界面与客户端中的名称，例如「高一(3)班」。'),
      field('所属分组', groupSelect, '分组可批量指定默认档案与推送范围。'),
      field('指定配置档案', profileSelect,
        '指定后优先级高于分组默认档案。留空则按「分组默认档案 → 全局默认档案」依次回退。'),
      h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', '保存后会立即唤醒该设备重新拉取配置，通常几秒内即可生效。'),
      ),
    ),
    confirmText: '保存',
    onConfirm: async () => {
      await api(`/admin/devices/${device.id}`, {
        method: 'PUT',
        body: {
          name: nameInput.value.trim(),
          groupId: groupSelect.value,
          profileId: profileSelect.value,
        },
      });
      toast('ok', '已保存', `${nameInput.value.trim()} 的配置已更新。`);
      await refresh();
    },
  });
}

async function toggleRevoke(device) {
  const revoking = !device.revoked;
  if (revoking) {
    const ok = await confirmDialog('停用设备',
      `停用后「${device.name}」将无法再连接集控服务器，也不会收到新配置。确定继续吗？`,
      '停用', true);
    if (!ok) return;
  }

  await api(`/admin/devices/${device.id}/revoke`, {
    method: 'POST',
    body: { revoked: revoking },
  });
  toast('ok', revoking ? '已停用' : '已恢复', device.name);
  await refresh();
}

async function openLogsDialog(device) {
  const dialog = modal({
    title: `运行日志 · ${device.name}`,
    width: 'xwide',
    hideFooter: true,
    body: loadingBlock('正在读取日志…'),
  });

  const bodyEl = dialog.bodyEl;
  clear(bodyEl);

  let logs = [];
  try {
    logs = await api(`/admin/devices/${device.id}/logs`, { query: { limit: 300 } });
  } catch (err) {
    clear(bodyEl);
    bodyEl.appendChild(h('div.notice.notice-danger', h('span.notice-icon', '!'), h('div', err.message)));
    return;
  }

  const toolbar = h('div.toolbar',
    h('span', { style: { fontSize: '12.5px', color: 'var(--text-dim)' } }, `共 ${logs.length} 条`),
    h('div.spacer'),
    h('button.btn.btn-sm', {
      type: 'button',
      onClick: async () => {
        if (!await confirmDialog('清空日志', `确定清空「${device.name}」的全部上报日志吗？`, '清空', true)) return;
        await api(`/admin/devices/${device.id}/logs`, { method: 'DELETE' });
        toast('ok', '已清空');
        dialog.close();
      },
    }, '清空日志'),
  );

  const list = logs.length === 0
    ? emptyState('📄', '暂无日志', '客户端会在同步或异常时上报日志。')
    : h('div.log-list', ...logs.map((l) => h('div.log-line',
      h('span.log-time', formatDateTime(l.timestamp)),
      h(`span.log-level.${l.level}`, l.level.toUpperCase()),
      h('span.log-msg', l.message),
    )));

  bodyEl.appendChild(h('div', toolbar, list));
}

// ── 注册码 ──────────────────────────────────────────────────────────────

function renderEnrollCodes() {
  const codes = cache.codes;

  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', '设备注册码'),
        h('p.card-desc', '把注册码填入客户端的集控插件设置中即可完成接入；注册码可限制使用次数与有效期。'),
      ),
      h('button.btn.btn-primary.btn-sm', { type: 'button', onClick: openCreateCodeDialog }, '+ 生成注册码'),
    ),
    codes.length === 0
      ? emptyState('🔑', '还没有注册码', '生成一个注册码用于新设备接入。',
        h('button.btn.btn-primary', { type: 'button', onClick: openCreateCodeDialog }, '生成注册码'))
      : h('div.table-wrap',
        h('table.data',
          h('thead', h('tr',
            h('th', '注册码'),
            h('th', '备注'),
            h('th', '使用情况'),
            h('th', '有效期'),
            h('th', '创建时间'),
            h('th', { style: { textAlign: 'right' } }, '操作'),
          )),
          h('tbody', ...codes.map(renderCodeRow)),
        ),
      ),
  );
}

function renderCodeRow(code) {
  const remaining = code.remainingUses < 0 ? '不限' : code.remainingUses;
  const expired = code.expiresAt && new Date(code.expiresAt) < new Date();
  const exhausted = code.remainingUses === 0;

  return h('tr',
    h('td',
      h('div', { style: { display: 'flex', alignItems: 'center', gap: '8px' } },
        h('code', { style: { fontSize: '14px', letterSpacing: '1.5px', fontWeight: '700' } }, code.code),
        h('button.btn.btn-ghost.btn-sm', {
          type: 'button',
          title: '复制注册码',
          onClick: () => copyText(code.code, '注册码已复制'),
        }, '复制'),
      ),
    ),
    h('td', code.note || h('span', { style: { color: 'var(--text-faint)' } }, '—')),
    h('td',
      h('span', `已用 ${code.usedCount} / 剩余 ${remaining}`),
    ),
    h('td',
      expired ? h('span.badge.badge-danger', '已过期')
        : exhausted ? h('span.badge.badge-danger', '次数已用尽')
          : code.expiresAt
            ? h('span', { style: { fontSize: '12px' } }, formatDateTime(code.expiresAt))
            : h('span.badge.badge-ok', '长期有效'),
    ),
    h('td', { style: { fontSize: '12px', color: 'var(--text-faint)' } }, formatDateTime(code.createdAt)),
    h('td.actions',
      h('button.btn.btn-sm', { type: 'button', onClick: () => openEditCodeDialog(code) }, '编辑'),
      ' ',
      h('button.btn.btn-sm.btn-danger', {
        type: 'button',
        onClick: async () => {
          if (!await confirmDialog('删除注册码', `确定删除注册码 ${code.code} 吗？已注册的设备不受影响。`, '删除', true)) return;
          await api(`/admin/enroll-codes/${encodeURIComponent(code.code)}`, { method: 'DELETE' });
          toast('ok', '已删除');
          await refresh();
        },
      }, '删除'),
    ),
  );
}

function openCreateCodeDialog() {
  const noteInput = h('input', { type: 'text', placeholder: '例如：高一（3）班' });
  const maxUsesInput = h('input', { type: 'number', value: '1', min: '0' });
  const hoursInput = h('input', { type: 'number', value: '72', min: '0' });

  modal({
    title: '生成注册码',
    width: 'wide',
    body: h('div',
      field('备注', noteInput, '仅用于管理端识别，不会下发给客户端。'),
      h('div.form-row',
        field('可注册次数', maxUsesInput, '填 0 表示不限次数（适合整班批量部署）。'),
        field('有效小时数', hoursInput, '填 0 表示长期有效。'),
      ),
    ),
    confirmText: '生成',
    onConfirm: async () => {
      const result = await api('/admin/enroll-codes', {
        method: 'POST',
        body: {
          note: noteInput.value.trim(),
          maxUses: Number(maxUsesInput.value) || 0,
          validHours: Number(hoursInput.value) || 0,
        },
      });

      await refresh();
      modal({
        title: '注册码已生成',
        width: 'wide',
        hideFooter: true,
        body: h('div',
          h('p', { style: { marginTop: '0', color: 'var(--text-dim)' } },
            '请把下面的注册码填入客户端的集控插件设置中：'),
          h('div.copy-row',
            h('div.code-block', result.code),
            h('button.btn', { type: 'button', onClick: () => copyText(result.code, '注册码已复制') }, '复制'),
          ),
          h('div.notice.notice-info', { style: { marginTop: '14px' } },
            h('span.notice-icon', 'i'),
            h('div', `可用次数：${result.maxUses === 0 ? '不限' : result.maxUses}；`
              + `有效期：${result.expiresAt ? formatDateTime(result.expiresAt) : '长期有效'}`),
          ),
        ),
      });
    },
  });
}

function openEditCodeDialog(code) {
  const noteInput = h('input', { type: 'text', value: code.note || '' });
  const maxUsesInput = h('input', { type: 'number', value: String(code.maxUses), min: '0' });
  const hoursInput = h('input', { type: 'number', value: '72', min: '0' });

  modal({
    title: `编辑注册码 · ${code.code}`,
    width: 'wide',
    body: h('div',
      field('备注', noteInput),
      h('div.form-row',
        field('可注册次数', maxUsesInput, '填 0 表示不限。'),
        field('重置有效期（小时）', hoursInput, '从当前时间起重新计算，填 0 表示长期有效。'),
      ),
      h('div.notice.notice-warn',
        h('span.notice-icon', '!'),
        h('div', '保存会重置有效期，已使用次数保持不变。'),
      ),
    ),
    confirmText: '保存',
    onConfirm: async () => {
      await api(`/admin/enroll-codes/${encodeURIComponent(code.code)}`, {
        method: 'PUT',
        body: {
          note: noteInput.value.trim(),
          maxUses: Number(maxUsesInput.value) || 0,
          validHours: Number(hoursInput.value) || 0,
        },
      });
      toast('ok', '已保存');
      await refresh();
    },
  });
}
