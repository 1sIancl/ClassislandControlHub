/**
 * 远程管理视图：远程命令行（统一/单台）、插件管理、外观下发、提醒、备份。
 * 数据来自 A 端 /admin/devices、/admin/backups 等接口。
 */

import { api } from '../core/api.js?v=12';
import {
  h, clear, toast, loadingBlock, confirmDialog, field, select, emptyState, formatDateTime,
} from '../core/ui.js?v=12';

export const meta = {
  title: '远程管理',
  subtitle: '远程命令行、插件、外观、提醒与备份',
};

const TABS = [
  { key: 'command', label: '远程命令行' },
  { key: 'plugins', label: '插件管理' },
  { key: 'appearance', label: '外观下发' },
  { key: 'notify', label: '发送提醒' },
  { key: 'backup', label: '备份' },
];

let state = { tab: 'command', devices: [], backupList: [] };

export async function render(container) {
  clear(container);
  container.appendChild(loadingBlock());
  state.devices = await api('/admin/devices');
  clear(container);
  paint(container);
}

function paint(container) {
  clear(container);
  container.appendChild(h('div',
    h('div.card',
      h('div.tabs', ...TABS.map((t) => h('button', {
        class: `tab${state.tab === t.key ? ' active' : ''}`,
        type: 'button',
        onClick: () => { state.tab = t.key; paint(container); },
      }, t.label))),
      h('div#tabBody', renderTab()),
    ),
  ));
}

function renderTab() {
  switch (state.tab) {
    case 'command': return renderCommand();
    case 'plugins': return renderPlugins();
    case 'appearance': return renderAppearance();
    case 'notify': return renderNotify();
    case 'backup': return renderBackup();
    default: return h('div');
  }
}

function deviceOptions() {
  return state.devices.map((d) => ({ value: d.id, label: `${d.name}${d.online ? '（在线）' : '（离线）'}` }));
}

// ────────────────────── 远程命令行 ──────────────────────

function renderCommand() {
  const targetSelect = select(
    [{ value: '__all__', label: '★ 全部设备（统一执行）' }, ...deviceOptions()],
    '__all__',
  );
  const cmdInput = h('input', { type: 'text', placeholder: '例如：ipconfig /all 或 uname -a' });
  const out = h('div.log-list', { style: { display: 'none' } });

  const runBtn = h('button.btn.btn-primary', {
    type: 'button',
    onClick: async () => {
      const command = cmdInput.value.trim();
      if (!command) { toast('warn', '请输入命令'); return; }
      const isAll = targetSelect.value === '__all__';
      const ok = await confirmDialog('执行远程命令',
        `将在${isAll ? '全部在线设备' : '所选设备'}上执行：\n${command}\n\n确定继续吗？`, '执行');
      if (!ok) return;

      try {
        if (isAll) {
          const r = await api('/admin/devices/command', {
            method: 'POST',
            body: { kind: 'shell', payload: JSON.stringify({ command }) },
          });
          toast('ok', '已下发', `影响 ${r.affected} 台设备，稍后可在设备详情查看结果。`);
        } else {
          await api(`/admin/devices/${targetSelect.value}/command`, {
            method: 'POST',
            body: { kind: 'shell', payload: JSON.stringify({ command }) },
          });
          toast('ok', '已下发', '请在下方查看执行结果。');
          await loadCommandHistory(targetSelect.value, out);
        }
      } catch (e) {
        toast('error', '下发失败', e.message);
      }
    },
  }, '执行');

  return h('div',
    h('div.form-row', field('目标设备', targetSelect)),
    field('命令', cmdInput),
    h('div', { style: { display: 'flex', gap: '8px', marginTop: '4px' } },
      runBtn,
      h('button.btn', { type: 'button', onClick: () => loadCommandHistory(targetSelect.value, out) }, '查看历史'),
    ),
    h('div', { style: { marginTop: '16px' } }, out),
  );
}

async function loadCommandHistory(deviceId, outEl) {
  if (deviceId === '__all__') { toast('warn', '请选择单台设备查看历史'); return; }
  const list = await api(`/admin/devices/${deviceId}/commands`);
  outEl.style.display = 'block';
  clear(outEl);
  if (list.length === 0) {
    outEl.appendChild(h('div', { style: { padding: '12px', color: 'var(--text-faint)', fontSize: '12.5px' } }, '暂无指令记录'));
    return;
  }

  for (const c of list) {
    outEl.appendChild(h('div.log-line',
      h('span.log-time', formatDateTime(c.issuedAt)),
      h('span.log-level.' + (c.status === 'done' ? 'info' : c.status === 'failed' ? 'error' : 'warn'), c.status),
      h('span.log-msg', `[${c.kind}] ${c.output || '等待执行…'}`),
    ));
  }
}

// ────────────────────── 插件管理 ──────────────────────

function renderPlugins() {
  const targetSelect = select(deviceOptions(), state.devices[0]?.id || '');
  const listBox = h('div', { style: { marginTop: '14px' } });

  const load = async () => {
    if (!targetSelect.value) return;
    clear(listBox);
    listBox.appendChild(loadingBlock());
    try {
      const plugins = await api(`/admin/devices/${targetSelect.value}/plugins`);
      clear(listBox);
      if (plugins.length === 0) {
        listBox.appendChild(h('div.notice.notice-info',
          h('span.notice-icon', 'i'),
          h('div', '该设备尚未上报插件列表。点击「刷新插件列表」让 B 端上报。')));
        return;
      }

      listBox.appendChild(h('div.table-wrap',
        h('table.data',
          h('thead', h('tr', h('th', '插件'), h('th', '版本'), h('th', '作者'), h('th', '状态'))),
          h('tbody', ...plugins.map((p) => h('tr',
            h('td', h('div.cell-main', p.name), h('div.cell-sub', p.id)),
            h('td', p.version),
            h('td', p.author || '—'),
            h('td', p.isEnabled ? h('span.badge.badge-ok', '已启用') : h('span.badge.badge-neutral', '已禁用')),
          ))),
        ),
      ));
    } catch (e) {
      clear(listBox);
      listBox.appendChild(h('div.notice.notice-danger', h('span.notice-icon', '!'), h('div', e.message)));
    }
  };

  const refreshBtn = h('button.btn.btn-primary.btn-sm', {
    type: 'button',
    onClick: async () => {
      if (!targetSelect.value) return;
      await api(`/admin/devices/${targetSelect.value}/plugins/refresh`, { method: 'POST' });
      toast('ok', '已请求刷新', 'B 端将在下一次心跳后上报。');
    },
  }, '刷新插件列表');

  targetSelect.addEventListener('change', load);

  return h('div',
    h('div.toolbar',
      h('span', { style: { fontSize: '12.5px', color: 'var(--text-dim)' } }, '目标设备'),
      targetSelect,
      refreshBtn,
      h('button.btn.btn-sm', { type: 'button', onClick: load }, '查看'),
    ),
    listBox,
  );
}

// ────────────────────── 外观下发 ──────────────────────

function renderAppearance() {
  const targetSelect = select(
    [{ value: '__all__', label: '★ 全部设备（统一）' }, ...deviceOptions()],
    '__all__',
  );
  const themeSelect = select([
    { value: '', label: '不修改' },
    { value: 'light', label: '浅色' },
    { value: 'dark', label: '深色' },
  ], '');
  const accentInput = h('input', { type: 'text', placeholder: '例如 #1E90FF（留空不修改）' });
  const fontInput = h('input', { type: 'text', placeholder: '例如 Microsoft YaHei（留空不修改）' });

  const applyBtn = h('button.btn.btn-primary', {
    type: 'button',
    onClick: async () => {
      const appearance = {
        theme: themeSelect.value || null,
        accentColor: accentInput.value.trim() || null,
        fontFamily: fontInput.value.trim() || null,
      };
      try {
        if (targetSelect.value === '__all__') {
          const r = await api('/admin/devices/appearance', { method: 'POST', body: { appearance } });
          toast('ok', '已下发', `影响 ${r.affected} 台设备。`);
        } else {
          await api(`/admin/devices/${targetSelect.value}/command`, {
            method: 'POST',
            body: { kind: 'appearance.apply', payload: JSON.stringify(appearance) },
          });
          toast('ok', '已下发', '外观配置已发送。');
        }
      } catch (e) {
        toast('error', '下发失败', e.message);
      }
    },
  }, '统一下发外观');

  return h('div',
    h('div.notice.notice-info',
      h('span.notice-icon', 'i'),
      h('div', '统一管理所有教室大屏的 ClassIsland 外观（主题 / 强调色 / 字体）。应用后需重启 ClassIsland 生效。')),
    h('div.form-row',
      field('目标设备', targetSelect),
      field('主题', themeSelect),
    ),
    h('div.form-row',
      field('强调色', accentInput),
      field('字体', fontInput),
    ),
    applyBtn,
  );
}

// ────────────────────── 发送提醒 ──────────────────────

function renderNotify() {
  const targetSelect = select(
    [{ value: '__all__', label: '★ 全部设备（统一）' }, ...deviceOptions()],
    '__all__',
  );
  const titleInput = h('input', { type: 'text', placeholder: '例如：紧急通知' });
  const msgInput = h('textarea', { placeholder: '提醒内容…', style: { minHeight: '90px' } });
  const speakChk = h('input', { type: 'checkbox' });

  const sendBtn = h('button.btn.btn-primary', {
    type: 'button',
    onClick: async () => {
      const title = titleInput.value.trim();
      const message = msgInput.value.trim();
      if (!title && !message) { toast('warn', '请填写提醒内容'); return; }

      const payload = JSON.stringify({ title, message, speak: speakChk.checked });
      try {
        if (targetSelect.value === '__all__') {
          await api('/admin/devices/command', {
            method: 'POST',
            body: { kind: 'notify', payload },
          });
          toast('ok', '已广播', '提醒已下发到全部在线设备。');
        } else {
          await api(`/admin/devices/${targetSelect.value}/notify`, {
            method: 'POST',
            body: { title, message, speak: speakChk.checked },
          });
          toast('ok', '已发送', '提醒已下发。');
        }
      } catch (e) {
        toast('error', '发送失败', e.message);
      }
    },
  }, '发送提醒');

  return h('div',
    h('div.form-row', field('目标设备', targetSelect)),
    field('标题', titleInput),
    field('内容', msgInput),
    h('label.checkbox-field', speakChk, h('span', '语音播报提醒内容')),
    sendBtn,
  );
}

// ────────────────────── 备份 ──────────────────────

function renderBackup() {
  const listBox = h('div', { style: { marginTop: '14px' } });

  const load = async () => {
    clear(listBox);
    listBox.appendChild(loadingBlock());
    try {
      state.backupList = await api('/admin/backups');
      clear(listBox);
      if (state.backupList.length === 0) {
        listBox.appendChild(emptyState('💾', '还没有备份', '点击「立即备份」创建第一个备份。'));
        return;
      }

      listBox.appendChild(h('div.table-wrap',
        h('table.data',
          h('thead', h('tr',
            h('th', '备份'), h('th', '类型'), h('th', '时间'), h('th', '大小'), h('th', '操作'),
          )),
          h('tbody', ...state.backupList.map((b) => h('tr',
            h('td', h('div.cell-main', b.id), h('div.cell-sub', b.note || '')),
            h('td', h('span.badge.badge-neutral', b.type)),
            h('td', formatDateTime(b.createdAt)),
            h('td', `${(b.sizeBytes / 1024).toFixed(1)} KB`),
            h('td.actions',
              h('a.btn.btn-sm', { href: `/api/v1/admin/backups/${b.id}/download`, style: { textDecoration: 'none' } }, '下载'),
              ' ',
              h('button.btn.btn-sm', {
                type: 'button',
                onClick: async () => {
                  const ok = await confirmDialog('恢复备份',
                    `将用备份「${b.id}」覆盖当前数据库，需重启服务生效。当前数据会先自动备份一次。确定继续吗？`,
                    '恢复');
                  if (!ok) return;
                  await api(`/admin/backups/${b.id}/restore`, { method: 'POST' });
                  toast('ok', '已恢复', '请重启集控服务以生效。');
                },
              }, '恢复'),
              ' ',
              h('button.btn.btn-sm.btn-danger', {
                type: 'button',
                onClick: async () => {
                  const ok = await confirmDialog('删除备份', `确定删除备份「${b.id}」吗？`, '删除', true);
                  if (!ok) return;
                  await api(`/admin/backups/${b.id}`, { method: 'DELETE' });
                  toast('ok', '已删除');
                  await load();
                },
              }, '删除'),
            ),
          ))),
        ),
      ));
    } catch (e) {
      clear(listBox);
      listBox.appendChild(h('div.notice.notice-danger', h('span.notice-icon', '!'), h('div', e.message)));
    }
  };

  load();

  return h('div',
    h('div.toolbar',
      h('button.btn.btn-primary.btn-sm', {
        type: 'button',
        onClick: async () => {
          await api('/admin/backups', { method: 'POST', body: { note: '手动备份' } });
          toast('ok', '备份已创建');
          await load();
        },
      }, '立即备份'),
      h('button.btn.btn-sm', { type: 'button', onClick: load }, '刷新'),
      h('div.spacer'),
      h('span', { style: { fontSize: '12px', color: 'var(--text-faint)' } }, '自动备份默认每 7 天一次，保留最近 16 份'),
    ),
    listBox,
  );
}
