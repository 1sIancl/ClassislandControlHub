/**
 * 远程管理视图：远程命令行（统一/单台）、插件管理、外观下发、提醒、备份。
 * 数据来自 A 端 /admin/devices、/admin/backups 等接口。
 */

import { api, fetchBlob } from '../core/api.js?v=37';
import {
  h, clear, toast, loadingBlock, confirmDialog, field, select, emptyState, formatDateTime, modal,
} from '../core/ui.js?v=37';

export const meta = {
  title: '远程管理',
  subtitle: '远程命令行、插件、外观、提醒与备份',
};

const TABS = [
  { key: 'command', label: '远程命令行' },
  { key: 'diagnostic', label: '远程诊断' },
  { key: 'plugins', label: '插件管理' },
  { key: 'appearance', label: '外观下发' },
  { key: 'notify', label: '发送提醒' },
  { key: 'automation', label: '自动化' },
  { key: 'backup', label: '备份' },
];

/** 指令状态 → 展示文案 / 日志级别。 */
const STATUS_LABEL = {
  pending: '排队中',
  dispatched: '已派发',
  done: '已完成',
  failed: '失败',
  expired: '已过期',
};

const STATUS_LEVEL = {
  done: 'info',
  failed: 'error',
  expired: 'error',
  dispatched: 'warn',
  pending: 'warn',
};

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
      h('div#tabBody', renderTab(container)),
    ),
  ));
}

function renderTab(container) {
  switch (state.tab) {
    case 'command': return renderCommand(container);
    case 'diagnostic': return renderDiagnostic(container);
    case 'plugins': return renderPlugins(container);
    case 'appearance': return renderAppearance(container);
    case 'notify': return renderNotify(container);
    case 'automation': return renderAutomation(container);
    case 'backup': return renderBackup();
    default: return h('div');
  }
}

function deviceOptions() {
  return state.devices.map((d) => ({ value: d.id, label: `${d.name}${d.online ? '（在线）' : '（离线）'}` }));
}

/** 重新拉取设备列表（在线状态会随心跳变化）。 */
async function reloadDevices(container) {
  state.devices = await api('/admin/devices');
  paint(container);
}

function deviceRefreshButton(container) {
  return h('button.btn.btn-sm', {
    type: 'button',
    title: '重新读取设备在线状态',
    onClick: async () => {
      await reloadDevices(container);
      toast('ok', '已刷新设备状态');
    },
  }, '刷新设备');
}

/** 「含离线设备」开关：默认只发给在线设备，避免对离线设备堆积陈旧指令。 */
function offlineOption() {
  const input = h('input', { type: 'checkbox' });
  return {
    input,
    el: h('label.checkbox-field', input,
      h('span', '含离线设备（命令会排队，等设备上线后执行，2 小时后自动作废）')),
  };
}

function broadcastResult(result) {
  const skipped = result?.skipped ?? 0;
  return `已下发 ${result?.affected ?? 0} 台在线设备`
    + (skipped > 0 ? `；${skipped} 台离线未下发（如需排队，请勾选「含离线设备」）` : '');
}

/**
 * 待执行指令队列视图：显示「还没被设备取走」的指令（离线排队 / 定时等待），带到期倒计时与逐条取消。
 * <para>与指令历史的区别：历史回答「过去发生了什么」，队列回答「还有什么没执行、什么时候作废」。</para>
 * @param {HTMLSelectElement} deviceSelect 目标设备下拉框（值为设备 ID；`__all__` 表示全部在线设备）。
 * @returns {{ el: HTMLElement, refresh: Function }} 视图元素与手动刷新函数。
 */
function createQueueView(deviceSelect) {
  const box = h('div', { style: { marginTop: '16px' } });

  const refresh = async () => {
    const deviceId = deviceSelect.value;
    if (!deviceId || deviceId === '__all__') { clear(box); return; }

    let list = [];
    try {
      list = await api(`/admin/devices/${deviceId}/commands/queue`);
    } catch (err) {
      clear(box);
      box.appendChild(h('div.notice.notice-danger', h('span.notice-icon', '!'), h('div', err.message)));
      return;
    }

    clear(box);
    const device = state.devices.find((d) => d.id === deviceId);

    box.appendChild(h('div.toolbar',
      h('span.toolbar-label', `待执行指令队列（${list.length}）`),
      h('div.spacer'),
      h('button.btn.btn-sm', { type: 'button', onClick: refresh }, '刷新队列'),
    ));

    if (list.length === 0) {
      box.appendChild(h('div', { style: { padding: '6px 0', color: 'var(--text-faint)', fontSize: '12.5px' } },
        '队列为空。设备在线时指令会立即执行；要让离线设备排队执行，请勾选「含离线设备」。'));
      return;
    }

    box.appendChild(h('div.cmd-list', ...list.map((c) => {
      const inflight = Boolean(c.dispatchedAt);
      const remaining = formatRemaining(c.expiresAt);
      return h('div.cmd-row',
        h('div.cmd-head',
          h('span.log-time', formatDateTime(c.issuedAt)),
          h('span.log-level.warn', inflight ? '已派发' : '排队中'),
          h('span.cmd-kind', c.kind),
          remaining ? h('span.cmd-kind', remaining) : null,
          h('div.spacer'),
          inflight
            ? null
            : h('button.btn.btn-sm', {
              type: 'button',
              onClick: async () => {
                if (!await confirmDialog('取消指令',
                  `确定取消这条「${c.kind}」指令吗？取消后设备不会执行它。`, '取消指令')) return;
                try {
                  await api(`/admin/devices/commands/${c.id}`, { method: 'DELETE' });
                  toast('ok', '已取消指令');
                } catch (err) {
                  toast('error', '取消失败', err.message);
                }
                await refresh();
              },
            }, '取消'),
        ),
        h('div.shot-note', queueStateText(c, device, inflight)),
      );
    })));
  };

  deviceSelect.addEventListener('change', () => { refresh(); });
  refresh();

  return { el: box, refresh };
}

/** 队列条目「为什么还没执行」的说明文案。 */
function queueStateText(command, device, inflight) {
  if (inflight) return '设备已取走指令，正在执行，等待回报结果。';

  const notBefore = command.notBefore ? new Date(command.notBefore).getTime() : 0;
  if (notBefore > Date.now()) {
    return `定时指令：${formatDateTime(command.notBefore)} 生效后执行。`;
  }

  if (device && device.online === false) {
    return '设备当前离线，将在下次上线心跳时立即执行。';
  }

  return '设备在线，将在下一次心跳时执行。';
}

/** 到期倒计时文案（队列条目右侧）。 */
function formatRemaining(expiresAt) {
  if (!expiresAt) return '';
  const left = new Date(expiresAt).getTime() - Date.now();
  if (!Number.isFinite(left)) return '';
  if (left <= 0) return '已过期';

  const minutes = Math.round(left / 60000);
  if (minutes < 60) return `${minutes} 分钟后作废`;

  const hours = Math.floor(minutes / 60);
  return `${hours} 小时 ${minutes % 60} 分钟后作废`;
}

// ────────────────────── 远程命令行 ──────────────────────

function renderCommand(container) {
  const targetSelect = select(
    [{ value: '__all__', label: '全部在线设备（统一执行）' }, ...deviceOptions()],
    '__all__',
  );
  const cmdInput = h('input', { type: 'text', placeholder: '例如：ipconfig /all 或 uname -a' });
  const offline = offlineOption();
  const out = h('div', { style: { display: 'none' } });
  const queue = createQueueView(targetSelect);

  // 「含离线设备」只对「全部在线设备」有意义：单台下发时服务端本来就会把离线设备的指令排队，
  // 因此单台模式下隐藏该勾选，避免让人以为勾了才排队。
  const syncOfflineOption = () => {
    offline.el.style.display = targetSelect.value === '__all__' ? '' : 'none';
  };
  targetSelect.addEventListener('change', syncOfflineOption);
  syncOfflineOption();

  const runBtn = h('button.btn.btn-primary', {
    type: 'button',
    onClick: async () => {
      const command = cmdInput.value.trim();
      if (!command) { toast('warn', '请输入命令'); return; }
      const isAll = targetSelect.value === '__all__';
      const ok = await confirmDialog('执行远程命令',
        `将在${isAll ? '所选范围内的设备' : '所选设备'}上执行：\n${command}\n\n确定继续吗？`, '执行');
      if (!ok) return;

      const body = { kind: 'shell', payload: JSON.stringify({ command }) };
      try {
        if (isAll) {
          const r = await api('/admin/devices/command', {
            method: 'POST',
            body: { ...body, includeOffline: offline.input.checked },
          });
          toast('ok', '已下发', broadcastResult(r));
          if (Array.isArray(r.items) && r.items.length > 0) {
            openBroadcastPanel(r.items, command);
          }
        } else {
          await api(`/admin/devices/${targetSelect.value}/command`, { method: 'POST', body });
          const target = state.devices.find((d) => d.id === targetSelect.value);
          toast('ok', '已下发', target && target.online === false
            ? '设备当前离线，指令已进入队列，上线后自动执行（可在下方队列里取消）。'
            : '请在下方查看执行结果。');
          await loadCommandHistory(targetSelect.value, out);
          pollHistory(targetSelect.value, out);
          await queue.refresh();
        }
      } catch (e) {
        toast('error', '下发失败', e.message);
      }
    },
  }, '执行');

  return h('div',
    h('div.toolbar',
      h('span.toolbar-label', '目标设备'),
      targetSelect,
      deviceRefreshButton(container),
      h('div.spacer'),
      h('button.btn.btn-sm', { type: 'button', onClick: () => loadCommandHistory(targetSelect.value, out) }, '查看历史'),
    ),
    field('命令', cmdInput, '在 Windows 上以 cmd /c 执行，其它平台以 bash -c 执行。'),
    offline.el,
    h('div', { style: { display: 'flex', gap: '8px', marginTop: '12px' } }, runBtn),
    queue.el,
    h('div', { style: { marginTop: '16px' } }, out),
  );
}

async function loadCommandHistory(deviceId, outEl) {
  if (deviceId === '__all__') { toast('warn', '请选择单台设备查看历史'); return; }
  const list = await api(`/admin/devices/${deviceId}/commands`);
  outEl.style.display = 'block';
  clear(outEl);
  if (list.length === 0) {
    outEl.appendChild(h('div', { style: { padding: '12px', color: 'var(--text-faint)', fontSize: '12.5px' } }, '暂无指令记录。'));
    return;
  }

  outEl.appendChild(h('div.cmd-list', ...list.map((c) => h('div.cmd-row',
    h('div.cmd-head',
      h('span.log-time', formatDateTime(c.issuedAt)),
      h('span.log-level.' + (STATUS_LEVEL[c.status] || 'warn'), STATUS_LABEL[c.status] || c.status),
      h('span.cmd-kind', c.kind),
      c.exitCode ? h('span.cmd-kind', `退出码 ${c.exitCode}`) : null,
    ),
    h('pre.cmd-output', c.output && c.output.length > 0 ? c.output : '等待执行…'),
  ))));
}

/** 下发后自动查看数次结果，省掉手动点「查看历史」。 */
function pollHistory(deviceId, outEl, times = 4) {
  let left = times;
  const tick = async () => {
    if (left-- <= 0 || deviceId === '__all__') return;
    try {
      await loadCommandHistory(deviceId, outEl);
    } catch { /* 轮询失败忽略 */ }
    setTimeout(tick, 1500);
  };
  setTimeout(tick, 1500);
}

/**
 * 带鉴权下载文件。
 * 管理端接口都需要 Authorization 头，直接用 <a href> 打开新标签不会带令牌（必然 401），
 * 因此先取回 Blob 再触发保存。
 */
async function downloadFile(path, fileName) {
  const blob = await fetchBlob(path);
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

// ────────────────────── 插件管理 ──────────────────────

function renderPlugins(container) {
  const targetSelect = select(deviceOptions(), state.devices[0]?.id || '');
  const listBox = h('div', { style: { marginTop: '14px' } });

  const refreshList = async () => {
    if (!targetSelect.value) return;
    await api(`/admin/devices/${targetSelect.value}/plugins/refresh`, { method: 'POST' });
  };

  /** 发一条插件指令；随后自动请求刷新清单，让状态列反映最新结果。 */
  const sendPluginCommand = async (kind, payload, message) => {
    if (!targetSelect.value) { toast('warn', '请先选择设备'); return; }
    try {
      await api(`/admin/devices/${targetSelect.value}/command`, {
        method: 'POST',
        body: { kind, payload: JSON.stringify(payload) },
      });
      toast('ok', '指令已下发', message);

      // 启停/卸载要等 B 端执行完再刷新清单，否则拿到的是旧状态。
      setTimeout(() => refreshList().catch(() => {}), 800);
      setTimeout(() => load().catch(() => {}), 5000);
    } catch (e) {
      toast('error', '下发失败', e.message);
    }
  };

  const load = async () => {
    if (!targetSelect.value) {
      clear(listBox);
      listBox.appendChild(h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', '还没有设备接入。请先到「设备管理」生成注册码并接入教室终端。')));
      return;
    }

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
          h('thead', h('tr',
            h('th', '插件'), h('th', '版本'), h('th', '作者'), h('th', '状态'),
            h('th', { style: { textAlign: 'right' } }, '操作'),
          )),
          h('tbody', ...plugins.map((p) => h('tr',
            h('td', h('div.cell-main', p.name), h('div.cell-sub', p.id)),
            h('td', p.version || '—'),
            h('td', p.author || '—'),
            h('td', p.isEnabled ? h('span.badge.badge-ok', '已启用') : h('span.badge.badge-neutral', '已禁用')),
            h('td.actions',
              h('button.btn.btn-sm', {
                type: 'button',
                onClick: () => sendPluginCommand('plugin.toggle', { pluginId: p.id, enabled: !p.isEnabled },
                  `已请求${p.isEnabled ? '禁用' : '启用'}「${p.name}」，ClassIsland 重启后生效。`),
              }, p.isEnabled ? '禁用' : '启用'),
              ' ',
              isHubPlugin(p)
                ? null
                : h('button.btn.btn-sm.btn-danger', {
                  type: 'button',
                  onClick: async () => {
                    const ok = await confirmDialog('卸载插件',
                      `将从该设备删除插件「${p.name}」，ClassIsland 重启后生效。确定继续吗？`, '卸载', true);
                    if (!ok) return;
                    await sendPluginCommand('plugin.uninstall', { pluginId: p.id },
                      `已请求卸载「${p.name}」，ClassIsland 重启后生效。`);
                  },
                }, '卸载'),
            ),
          ))),
        ),
      ));

      listBox.appendChild(h('div.notice.notice-info', { style: { marginTop: '12px' } },
        h('span.notice-icon', 'i'),
        h('div', '插件的启用 / 禁用 / 卸载都通过「插件目录下的 .disabled 标记」实现，需重启 ClassIsland 才会真正生效。')));
    } catch (e) {
      clear(listBox);
      listBox.appendChild(h('div.notice.notice-danger', h('span.notice-icon', '!'), h('div', e.message)));
    }
  };

  targetSelect.addEventListener('change', load);
  if (targetSelect.value) load();

  return h('div',
    h('div.toolbar',
      h('span.toolbar-label', '目标设备'),
      targetSelect,
      deviceRefreshButton(container),
      h('button.btn.btn-sm', {
        type: 'button',
        onClick: async () => {
          await refreshList();
          toast('ok', '已请求刷新', 'B 端将在下一次心跳后上报。');
          setTimeout(() => load().catch(() => {}), 5000);
        },
      }, '刷新插件列表'),
      h('button.btn.btn-sm', { type: 'button', onClick: load }, '查看'),
    ),
    listBox,
  );
}

/** 集控接收端插件自身不允许被远程卸载（否则会失联）。 */
function isHubPlugin(plugin) {
  const id = String(plugin.id || '');
  const name = String(plugin.name || '');
  return id.toLowerCase().includes('controlhub') || name.includes('集控');
}

// ────────────────────── 外观下发 ──────────────────────

function renderAppearance(container) {
  const targetSelect = select(
    [{ value: '__all__', label: '全部在线设备（统一）' }, ...deviceOptions()],
    '__all__',
  );
  const themeSelect = select([
    { value: '', label: '不修改' },
    { value: 'light', label: '浅色' },
    { value: 'dark', label: '深色' },
  ], '');
  const accentInput = h('input', { type: 'text', placeholder: '例如 #1E90FF（留空不修改）' });
  const offline = offlineOption();

  const applyBtn = h('button.btn.btn-primary', {
    type: 'button',
    onClick: async () => {
      const appearance = {
        theme: themeSelect.value || null,
        accentColor: accentInput.value.trim() || null,
      };
      if (!appearance.theme && !appearance.accentColor) {
        toast('warn', '请至少选择主题或填写强调色');
        return;
      }

      try {
        if (targetSelect.value === '__all__') {
          const r = await api('/admin/devices/appearance', {
            method: 'POST',
            body: { appearance, includeOffline: offline.input.checked },
          });
          toast('ok', '已下发', broadcastResult(r));
        } else {
          await api(`/admin/devices/${targetSelect.value}/command`, {
            method: 'POST',
            body: { kind: 'appearance.apply', payload: JSON.stringify(appearance) },
          });
          toast('ok', '已下发', '外观配置已发送，客户端会立即应用。');
        }
      } catch (e) {
        toast('error', '下发失败', e.message);
      }
    },
  }, '统一下发外观');

  return h('div',
    h('div.notice.notice-info',
      h('span.notice-icon', 'i'),
      h('div', '统一管理所有教室大屏的 ClassIsland 外观。下发生效后立即应用，无需重启。')),
    h('div.form-row',
      field('目标设备', targetSelect),
      field('主题', themeSelect),
    ),
    field('强调色', accentInput, '形如 #1E90FF；填错格式会提示「没有可应用的项」。'),
    offline.el,
    h('div', { style: { marginTop: '12px' } }, applyBtn),
  );
}

// ────────────────────── 发送提醒 ──────────────────────

function renderNotify(container) {
  const targetSelect = select(
    [
      { value: '__all__', label: '全部在线设备（统一）' },
      ...deviceOptions(),
      { value: '__pick__', label: '多选设备…' },
    ],
    '__all__',
  );
  const titleInput = h('input', { type: 'text', placeholder: '例如：紧急通知' });
  const msgInput = h('textarea', { placeholder: '提醒内容…', style: { minHeight: '90px' } });
  const speakChk = h('input', { type: 'checkbox' });
  const offline = offlineOption();

  // 多选目标：选「多选设备…」后弹窗勾选，确定后显示已选台数
  let picked = [];
  const pickInfo = h('span.pick-info', { hidden: true });

  // 通知模板：常用广播内容一键套用
  const templateSelect = select([{ value: '', label: '（不使用模板）' }], '');
  let templates = [];
  const loadTemplates = async () => {
    try {
      templates = await api('/admin/notice-templates');
      clear(templateSelect);
      templateSelect.appendChild(h('option', { value: '' }, '（不使用模板）'));
      for (const t of templates) {
        templateSelect.appendChild(h('option', { value: t.id }, t.name));
      }
    } catch { /* 没有 remote.read 权限时静默降级为「无模板」 */ }
  };

  templateSelect.addEventListener('change', () => {
    const t = templates.find((x) => x.id === templateSelect.value);
    if (!t) return;
    titleInput.value = t.title || '';
    msgInput.value = t.content || '';
    speakChk.checked = Boolean(t.speak);
    toast('ok', '已套用模板', t.name);
  });

  targetSelect.addEventListener('change', () => {
    if (targetSelect.value === '__pick__') {
      openDevicePicker();
      return;
    }

    picked = [];
    pickInfo.hidden = true;
  });

  function openDevicePicker() {
    const selected = new Set(picked);
    const list = h('div.picker-list');

    if (state.devices.length === 0) {
      list.appendChild(h('div', { style: { color: 'var(--text-faint)', fontSize: '12.5px' } }, '还没有设备接入。'));
    }

    for (const d of state.devices) {
      const box = h('input', { type: 'checkbox' });
      box.checked = selected.has(d.id);
      box.addEventListener('change', () => {
        if (box.checked) {
          selected.add(d.id);
        } else {
          selected.delete(d.id);
        }
      });

      list.appendChild(h('label.picker-item', box,
        h('span', ` ${d.name}`),
        h('span.picker-meta', `${d.online ? '在线' : '离线'}${d.remark ? ` · ${d.remark}` : ''}`)));
    }

    modal({
      title: '选择目标设备',
      width: 'wide',
      body: h('div',
        h('p', { style: { marginTop: 0, color: 'var(--text-dim)', fontSize: '12.5px' } },
          '勾选要发送通知的教室。即时通知只发给在线设备，离线设备不会补发。'),
        list),
      confirmText: '确定',
      onConfirm: () => {
        if (selected.size === 0) {
          toast('warn', '请至少选择一台设备');
          return false;
        }

        picked = [...selected];
        pickInfo.textContent = `已选 ${picked.length} 台设备`;
        pickInfo.hidden = false;
        return true;
      },
    });
  }

  const saveTemplateBtn = h('button.btn.btn-sm', {
    type: 'button',
    onClick: () => {
      const nameInput = h('input', { type: 'text', placeholder: '例如：广播站通知' });
      modal({
        title: '存为通知模板',
        body: h('div', field('模板名称', nameInput, '仅管理端使用，下次发同类通知可一键套用。')),
        confirmText: '保存',
        onConfirm: async () => {
          const name = nameInput.value.trim();
          if (!name) {
            toast('warn', '请填写模板名称');
            return false;
          }

          await api('/admin/notice-templates', {
            method: 'POST',
            body: {
              name,
              title: titleInput.value.trim(),
              content: msgInput.value.trim(),
              speak: speakChk.checked,
            },
          });
          toast('ok', '模板已保存', '下次可直接从模板下拉里选择。');
          await loadTemplates();
          return true;
        },
      });
    },
  }, '存为模板');

  const sendBtn = h('button.btn.btn-primary', {
    type: 'button',
    onClick: async () => {
      const title = titleInput.value.trim();
      const message = msgInput.value.trim();
      if (!title && !message) { toast('warn', '请填写提醒内容'); return; }

      const payload = JSON.stringify({ title, message, speak: speakChk.checked });
      try {
        if (targetSelect.value === '__pick__') {
          if (picked.length === 0) { toast('warn', '请先选择目标设备'); return; }
          const r = await api('/admin/devices/command', {
            method: 'POST',
            body: { kind: 'notify', payload, deviceIds: picked, includeOffline: false },
          });
          toast('ok', '已发送', broadcastResult(r));
        } else if (targetSelect.value === '__all__') {
          const r = await api('/admin/devices/command', {
            method: 'POST',
            body: { kind: 'notify', payload, includeOffline: offline.input.checked },
          });
          toast('ok', '已广播', broadcastResult(r));
        } else {
          await api(`/admin/devices/${targetSelect.value}/notify`, {
            method: 'POST',
            body: { title, message, speak: speakChk.checked },
          });
          toast('ok', '已发送', `提醒已下发${speakChk.checked ? '，并会语音播报' : ''}。`);
        }
      } catch (e) {
        toast('error', '发送失败', e.message);
      }
    },
  }, '发送提醒');

  loadTemplates();

  return h('div',
    h('div.form-row',
      field('目标设备', targetSelect),
      field('通知模板', h('div.template-row', templateSelect, saveTemplateBtn),
        '选择模板会填入标题、内容与播报设置；也可以把当前内容存成新模板。'),
    ),
    pickInfo,
    field('标题', titleInput),
    field('内容', msgInput),
    h('label.checkbox-field', speakChk, h('span', '语音播报提醒内容')),
    offline.el,
    h('div', { style: { marginTop: '12px' } }, sendBtn),
  );
}

// ────────────────────── 自动化 ──────────────────────

/**
 * 远程触发 ClassIsland 的自动化规则：B 端把「信号触发器」的信号名上报上来，
 * A 端按信号名触发，效果等同于在教室里手动点一次那条自动化。
 */
function renderAutomation(container) {
  const deviceSelect = select(deviceOptions(), state.devices[0]?.id || '');
  const datalist = h('datalist#automationSignals');
  const signalInput = h('input', {
    type: 'text',
    placeholder: '例如：放学（与 ClassIsland「信号触发器」里填写的名称一致）',
  });
  signalInput.setAttribute('list', 'automationSignals');

  const historyBox = h('div', { style: { marginTop: '14px' } });

  /** 从历史指令里挑出 automation.list 的回报，解析出可用信号名填入输入建议。 */
  async function harvestSignals() {
    if (!deviceSelect.value) return;
    try {
      const list = await api(`/admin/devices/${deviceSelect.value}/commands`);
      const found = new Set();
      for (const cmd of list) {
        if (cmd.kind !== 'automation.list' || !cmd.output) continue;
        try {
          for (const s of (JSON.parse(cmd.output).signals || [])) found.add(s);
        } catch { /* 单条解析失败就跳过 */ }
      }

      clear(datalist);
      for (const s of found) datalist.appendChild(h('option', { value: s }));
      if (found.size > 0) {
        toast('ok', '已获取可用信号', [...found].join('、'));
      }
    } catch { /* 拿不到就退回手动输入 */ }
  }

  const pullBtn = h('button.btn.btn-sm', {
    type: 'button',
    onClick: async () => {
      if (!deviceSelect.value) { toast('warn', '请先选择设备'); return; }
      try {
        await api(`/admin/devices/${deviceSelect.value}/command`, {
          method: 'POST',
          body: { kind: 'automation.list' },
        });
        toast('ok', '已请求', '设备回报后会把可用信号填进输入建议。');
        await loadCommandHistory(deviceSelect.value, historyBox);
        pollHistory(deviceSelect.value, historyBox, 3);
        setTimeout(() => harvestSignals().catch(() => {}), 3000);
      } catch (e) {
        toast('error', '请求失败', e.message);
      }
    },
  }, '拉取可用信号');

  const triggerBtn = h('button.btn.btn-primary', {
    type: 'button',
    onClick: async () => {
      const signal = signalInput.value.trim();
      if (!deviceSelect.value) { toast('warn', '请先选择设备'); return; }
      if (!signal) { toast('warn', '请填写信号名'); return; }
      if (!await confirmDialog('触发自动化',
        `将在所选设备上触发自动化信号「${signal}」。确定继续吗？`, '触发')) {
        return;
      }

      try {
        await api(`/admin/devices/${deviceSelect.value}/command`, {
          method: 'POST',
          body: { kind: 'automation.trigger', payload: JSON.stringify({ signal }) },
        });
        toast('ok', '已触发', '执行结果可在下方命令历史里查看。');
        await loadCommandHistory(deviceSelect.value, historyBox);
        pollHistory(deviceSelect.value, historyBox, 3);
      } catch (e) {
        toast('error', '触发失败', e.message);
      }
    },
  }, '触发自动化');

  deviceSelect.addEventListener('change', () => {
    clear(datalist);
    clear(historyBox);
  });

  return h('div',
    h('div.form-row', field('目标设备', deviceSelect)),
    field('自动化信号', h('div.template-row', signalInput, pullBtn),
      'B 端会列出该设备上配置了「信号触发器」的自动化名称；触发效果等于在教室里手动点一次那条自动化。'),
    h('div', { style: { marginTop: '12px' } }, triggerBtn),
    datalist,
    historyBox,
  );
}

/**
 * 广播结果面板：逐台显示「排队中 / 已派发 / 成功 / 失败 / 过期」，并自动刷新几次。
 * <para>逐台结果按 <c>commandId</c> 精确匹配（服务端签发时保证顺序一致），不靠时间猜，
 * 因此同一台设备上并发下发的其它指令不会互相串台。</para>
 */
function openBroadcastPanel(items, commandText) {
  const cells = new Map();
  const list = h('div.cmd-list', { style: { marginTop: '12px' } });

  items.forEach((item) => {
    const status = h('span.log-level.warn', '排队中');
    const output = h('pre.cmd-output', '等待执行…');
    cells.set(item.commandId, { status, output, done: false });
    list.appendChild(h('div.cmd-row',
      h('div.cmd-head',
        h('span.cmd-kind', item.deviceName || item.deviceId),
        status,
      ),
      output,
    ));
  });

  const dialog = modal({
    title: '批量执行结果',
    width: 'wide',
    hideFooter: true,
    body: h('div',
      h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', `命令：${commandText}　共 ${items.length} 台。结果会自动刷新；关掉这个窗口也可以稍后在设备的指令历史里查看。`)),
      list,
    ),
  });

  let left = 12;
  const tick = async () => {
    if (left-- <= 0 || dialog.closed?.()) return;

    await Promise.all(items.map(async (item) => {
      const cell = cells.get(item.commandId);
      if (!cell || cell.done) return;
      try {
        const history = await api(`/admin/devices/${item.deviceId}/commands`);
        const found = history.find((c) => c.id === item.commandId);
        if (found) applyCommandState(cell, found);
      } catch { /* 单台失败忽略，下一轮再试 */ }
    }));

    if (left > 0) setTimeout(tick, 3000);
  };
  setTimeout(tick, 2000);

  return dialog;
}

/** 把某条指令的当前状态刷到面板单元格上。 */
function applyCommandState(cell, command) {
  cell.status.textContent = STATUS_LABEL[command.status] || command.status;
  cell.status.className = 'log-level ' + (STATUS_LEVEL[command.status] || 'warn');
  if (command.output) cell.output.textContent = command.output;

  if (command.status !== 'pending' && command.status !== 'dispatched') {
    cell.done = true;
  }
}

// ────────────────────── 远程诊断 ──────────────────────

function renderDiagnostic(container) {
  const deviceSelect = select(deviceOptions(), state.devices[0]?.id || '');
  const offline = offlineOption();
  const out = h('div', { style: { display: 'none' } });
  const gallery = h('div', { style: { marginTop: '18px' } });
  const queue = createQueueView(deviceSelect);

  const run = (kind, label) => async () => {
    const deviceId = deviceSelect.value;
    if (!deviceId) { toast('warn', '请先选择设备'); return; }

    try {
      await api(`/admin/devices/${deviceId}/command`, { method: 'POST', body: { kind, payload: '{}' } });
      toast('ok', `已请求${label}`, '结果稍后出现在下方；离线设备会排队等上线后执行。');
      await loadCommandHistory(deviceId, out);
      pollHistory(deviceId, out, 6);
      await queue.refresh();
      if (kind === 'diagnostic.screenshot') {
        pollScreenshots(deviceId, gallery, 4);
      }
    } catch (e) {
      toast('error', '下发失败', e.message);
    }
  };

  deviceSelect.addEventListener('change', () => {
    clear(out);
    out.style.display = 'none';
    loadScreenshots(deviceSelect.value, gallery);
  });

  const view = h('div',
    h('div.toolbar',
      h('span.toolbar-label', '目标设备'),
      deviceSelect,
      deviceRefreshButton(container),
    ),
    h('div.notice.notice-info', { style: { marginTop: '12px' } },
      h('span.notice-icon', 'i'),
      h('div', '诊断指令由教室端执行：截图会上传保存（每台设备保留最近 5 张），'
        + '前台进程与诊断数据包以文本形式出现在下方结果里；诊断数据包还会顺带把教室端日志推上来，'
        + '可在设备列表的「运行日志」中查看。')),
    h('div', { style: { display: 'flex', gap: '8px', flexWrap: 'wrap', marginTop: '12px' } },
      h('button.btn.btn-primary', { type: 'button', onClick: run('diagnostic.screenshot', '屏幕截图') }, '抓取屏幕截图'),
      h('button.btn', { type: 'button', onClick: run('diagnostic.processes', '进程快照') }, '请求前台进程'),
      h('button.btn', { type: 'button', onClick: run('diagnostic.bundle', '诊断数据包') }, '请求诊断数据包'),
    ),
    offline.el,
    queue.el,
    h('div', { style: { marginTop: '16px' } }, out),
    gallery,
  );

  // 首次进入直接把该设备已有截图拉出来。
  loadScreenshots(deviceSelect.value, gallery);
  return view;
}

/** 截图列表：缩略图 + 点击查看大图（图片需带鉴权读取，因此先取 Blob 再用 objectURL 展示）。 */
async function loadScreenshots(deviceId, box) {
  if (!deviceId) return;
  clear(box);
  box.appendChild(loadingBlock('正在读取截图…'));

  let list = [];
  try {
    list = await api(`/admin/devices/${deviceId}/diagnostics`);
  } catch (err) {
    clear(box);
    box.appendChild(h('div.notice.notice-danger', h('span.notice-icon', '!'), h('div', err.message)));
    return;
  }

  clear(box);
  box.appendChild(h('div.toolbar',
    h('span.toolbar-label', `屏幕截图（${list.length}）`),
    h('div.spacer'),
    list.length > 0
      ? h('button.btn.btn-sm', {
        type: 'button',
        onClick: async () => {
          if (!await confirmDialog('清空截图', '确定清空该设备已上传的全部截图吗？', '清空', true)) return;
          await api(`/admin/devices/${deviceId}/diagnostics`, { method: 'DELETE' });
          toast('ok', '已清空截图');
          await loadScreenshots(deviceId, box);
        },
      }, '清空截图')
      : null,
  ));

  if (list.length === 0) {
    box.appendChild(emptyState('devices', '暂无截图', '点上方「抓取屏幕截图」，教室端在线时会立即回传。'));
    return;
  }

  box.appendChild(h('div.shot-grid', ...list.map(screenshotCard)));
}

function screenshotCard(item) {
  const wrap = h('div.shot-thumb-wrap', h('div.shot-loading', '加载中…'));

  fetchBlob(`/admin/devices/diagnostics/${item.id}/content`)
    .then((blob) => {
      const url = URL.createObjectURL(blob);
      const img = h('img.shot-thumb', { src: url, alt: '设备截图' });
      img.addEventListener('load', () => URL.revokeObjectURL(url), { once: true });
      clear(wrap);
      wrap.appendChild(img);
    })
    .catch((err) => {
      clear(wrap);
      wrap.appendChild(h('div.shot-failed', err.message));
    });

  wrap.addEventListener('click', () => openScreenshot(item));

  return h('div.shot-card',
    h('div.shot-head',
      h('span', formatDateTime(item.capturedAt)),
      h('span.shot-size', `${Math.max(1, Math.round(item.sizeBytes / 1024))} KB`),
    ),
    wrap,
    h('div.shot-note', item.note || '—'),
  );
}

async function openScreenshot(item) {
  const dialog = modal({
    title: `截图 · ${formatDateTime(item.capturedAt)}`,
    width: 'xwide',
    hideFooter: true,
    body: loadingBlock('正在读取截图…'),
  });

  try {
    const blob = await fetchBlob(`/admin/devices/diagnostics/${item.id}/content`);
    const url = URL.createObjectURL(blob);
    clear(dialog.bodyEl);
    dialog.bodyEl.appendChild(h('div',
      h('img', {
        src: url,
        alt: '设备截图',
        style: { width: '100%', borderRadius: '10px', border: '1px solid var(--border)' },
      }),
      h('div', { style: { marginTop: '10px', fontSize: '12.5px', color: 'var(--text-dim)' } }, item.note || ''),
      h('div', { style: { marginTop: '10px' } },
        h('a.btn.btn-sm', { href: url, download: `screenshot-${item.id}.png` }, '下载原图')),
    ));
  } catch (err) {
    clear(dialog.bodyEl);
    dialog.bodyEl.appendChild(h('div.notice.notice-danger', h('span.notice-icon', '!'), h('div', err.message)));
  }
}

/** 下发截图指令后自动刷新几次，省掉手动点「刷新」。 */
function pollScreenshots(deviceId, box, times = 4) {
  let left = times;
  const tick = async () => {
    if (left-- <= 0 || !deviceId) return;
    try {
      await loadScreenshots(deviceId, box);
    } catch { /* 轮询失败忽略 */ }
    setTimeout(tick, 2500);
  };
  setTimeout(tick, 2500);
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
        listBox.appendChild(emptyState('database', '还没有备份', '点击「立即备份」创建第一个备份。'));
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
