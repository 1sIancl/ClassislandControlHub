/**
 * 远程管理视图：远程命令行（统一/单台）、插件管理、外观下发、提醒、备份。
 * 数据来自 A 端 /admin/devices、/admin/backups 等接口。
 */

import { api, fetchBlob } from '../core/api.js?v=157';
import { toastError } from '../core/errors.js?v=157';
import { hintForCode } from '../core/error-hints.js?v=157';
import {
  h, clear, toast, loadingBlock, confirmDialog, field, select, emptyState, formatDateTime, modal,
  hintIcon,
} from '../core/ui.js?v=157';
import { t } from './../core/i18n.js?v=157';

export const meta = {
  title: t('remote.title'),
  subtitle: t('remote.subtitle'),
};

const TABS = [
  { key: 'command', label: t('remote.tabCommand') },
  { key: 'diagnostic', label: t('remote.tabDiagnostics') },
  { key: 'plugins', label: t('remote.tabPlugins') },
  { key: 'appearance', label: t('remote.tabAppearance') },
  { key: 'device', label: t('remote.tabVolume') },
  { key: 'notify', label: t('remote.tabNotice') },
  { key: 'automation', label: t('remote.tabAutomation') },
  { key: 'backup', label: t('remote.tabBackup') },
];

/** 指令状态 → 展示文案 / 日志级别。 */
const STATUS_LABEL = {
  pending: t('remote.stateQueued'),
  dispatched: t('remote.stateDispatched'),
  done: t('remote.stateDone'),
  failed: t('remote.stateFailed'),
  expired: t('remote.stateExpired'),
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
    case 'device': return renderDeviceControl(container);
    case 'notify': return renderNotify(container);
    case 'automation': return renderAutomation(container);
    case 'backup': return renderBackup();
    default: return h('div');
  }
}

function deviceOptions() {
  return state.devices.map((d) => ({
    value: d.id,
    label: t('remote.deviceOption', {
      name: d.name,
      state: d.online ? t('remote.onlineSuffix') : t('remote.offlineSuffix'),
    }),
  }));
}

/** 重新拉取设备列表（在线状态会随心跳变化）。 */
async function reloadDevices(container) {
  state.devices = await api('/admin/devices');
  paint(container);
}

function deviceRefreshButton(container) {
  return h('button.btn.btn-sm', {
    type: 'button',
    title: t('remote.refreshDevicesHint'),
    onClick: async () => {
      await reloadDevices(container);
      toast('ok', t('remote.refreshed'));
    },
  }, t('remote.refreshDevices'));
}

/** 「含离线设备」开关：默认只发给在线设备，避免对离线设备堆积陈旧指令。 */
function offlineOption() {
  const input = h('input', { type: 'checkbox' });
  return {
    input,
    el: h('label.checkbox-field', input,
      h('span', t('remote.includeOffline'))),
  };
}

function broadcastResult(result) {
  const skipped = result?.skipped ?? 0;
  return t('remote.sentOnline', { count: result?.affected ?? 0 })
    + (skipped > 0 ? t('remote.skippedOffline', { count: skipped }) : '');
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
      h('span.toolbar-label', t('remote.queueTitle', { count: list.length })),
      h('div.spacer'),
      h('button.btn.btn-sm', { type: 'button', onClick: refresh }, t('remote.refreshQueue')),
    ));

    if (list.length === 0) {
      box.appendChild(h('div', { style: { padding: '6px 0', color: 'var(--text-faint)', fontSize: '12.5px' } },
        t('remote.queueEmpty')));
      return;
    }

    box.appendChild(h('div.cmd-list', ...list.map((c) => {
      const inflight = Boolean(c.dispatchedAt);
      const remaining = formatRemaining(c.expiresAt);
      return h('div.cmd-row',
        h('div.cmd-head',
          h('span.log-time', formatDateTime(c.issuedAt)),
          h('span.log-level.warn', inflight ? t('remote.stateDispatched') : t('remote.stateQueued')),
          h('span.cmd-kind', c.kind),
          remaining ? h('span.cmd-kind', remaining) : null,
          h('div.spacer'),
          inflight
            ? null
            : h('button.btn.btn-sm', {
              type: 'button',
              onClick: async () => {
                if (!await confirmDialog(t('remote.cancelCommand'),
                  t('remote.cancelConfirm', { kind: c.kind }), t('remote.cancelCommand'))) return;
                try {
                  await api(`/admin/devices/commands/${c.id}`, { method: 'DELETE' });
                  toast('ok', t('remote.cancelDone'));
                } catch (err) {
                  toastError(err, t('remote.cancelFailed'));
                }
                await refresh();
              },
            }, t('remote.cancel')),
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
  if (inflight) return t('remote.takenByDevice');

  const notBefore = command.notBefore ? new Date(command.notBefore).getTime() : 0;
  if (notBefore > Date.now()) {
    return t('remote.scheduledAt', { time: formatDateTime(command.notBefore) });
  }

  if (device && device.online === false) {
    return t('remote.offlineWillRun');
  }

  return t('remote.onlineWillRun');
}

/** 到期倒计时文案（队列条目右侧）。 */
function formatRemaining(expiresAt) {
  if (!expiresAt) return '';
  const left = new Date(expiresAt).getTime() - Date.now();
  if (!Number.isFinite(left)) return '';
  if (left <= 0) return t('remote.stateExpired');

  const minutes = Math.round(left / 60000);
  if (minutes < 60) return t('remote.expiresMinutes', { minutes });

  const hours = Math.floor(minutes / 60);
  return t('remote.expiresHours', { hours, minutes: minutes % 60 });
}

// ────────────────────── 远程命令行 ──────────────────────

function renderCommand(container) {
  const targetSelect = select(
    [{ value: '__all__', label: t('remote.scopeOnline') }, ...deviceOptions()],
    '__all__',
  );
  const cmdInput = h('input', { type: 'text', placeholder: t('remote.commandPlaceholder') });
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
      if (!command) { toast('warn', t('remote.needCommand')); return; }
      const isAll = targetSelect.value === '__all__';
      const ok = await confirmDialog(t('remote.runCommand'),
        `将在${isAll ? t('remote.targetsInScope') : t('remote.targetsPicked')}上执行：\n${command}\n\n确定继续吗？`, t('remote.run'));
      if (!ok) return;

      const body = { kind: 'shell', payload: JSON.stringify({ command }) };
      try {
        if (isAll) {
          const r = await api('/admin/devices/command', {
            method: 'POST',
            body: { ...body, includeOffline: offline.input.checked },
          });
          toast('ok', t('remote.sent'), broadcastResult(r));
          if (Array.isArray(r.items) && r.items.length > 0) {
            openBroadcastPanel(r.items, command);
          }
        } else {
          await api(`/admin/devices/${targetSelect.value}/command`, { method: 'POST', body });
          const target = state.devices.find((d) => d.id === targetSelect.value);
          toast('ok', t('remote.sent'), target && target.online === false
            ? t('remote.queuedForOffline')
            : t('remote.checkBelow'));
          await loadCommandHistory(targetSelect.value, out);
          pollHistory(targetSelect.value, out);
          await queue.refresh();
        }
      } catch (e) {
        toastError(e, t('remote.sendFailed'));
      }
    },
  }, t('remote.run'));

  return h('div',
    h('div.toolbar',
      h('span.toolbar-label', t('remote.targetDevice')),
      targetSelect,
      deviceRefreshButton(container),
      h('div.spacer'),
      h('button.btn.btn-sm', { type: 'button', onClick: () => loadCommandHistory(targetSelect.value, out) }, t('remote.viewHistory')),
    ),
    field(t('remote.command'), cmdInput, t('remote.commandHint')),
    offline.el,
    h('div', { style: { display: 'flex', gap: '8px', marginTop: '12px' } }, runBtn),
    queue.el,
    h('div', { style: { marginTop: '16px' } }, out),
  );
}

async function loadCommandHistory(deviceId, outEl) {
  if (deviceId === '__all__') { toast('warn', t('remote.pickOneForHistory')); return; }
  const list = await api(`/admin/devices/${deviceId}/commands`);
  outEl.style.display = 'block';
  clear(outEl);
  if (list.length === 0) {
    outEl.appendChild(h('div', { style: { padding: '12px', color: 'var(--text-faint)', fontSize: '12.5px' } }, t('remote.noCommands')));
    return;
  }

  outEl.appendChild(h('div.cmd-list', ...list.map((c) => h('div.cmd-row',
    h('div.cmd-head',
      h('span.log-time', formatDateTime(c.issuedAt)),
      h('span.log-level.' + (STATUS_LEVEL[c.status] || 'warn'), STATUS_LABEL[c.status] || c.status),
      h('span.cmd-kind', c.kind),
      c.exitCode ? h('span.cmd-kind', t('remote.exitCode', { code: c.exitCode })) : null,
    ),
    h('pre.cmd-output', c.output && c.output.length > 0 ? c.output : t('remote.waiting')),
    failureHint(c),
  ))));
}

/**
 * 失败指令的「怎么办」（#81）。
 * <para>教室端上报了结构化错误码时才有建议；老版本插件不带这个字段 → 返回 null，
 * 界面上就只剩原文（与改之前一样），不会硬编一句可能不对的建议。</para>
 */
function failureHint(command) {
  const hint = hintForCode(command.errorCode);
  if (!hint) {
    return null;
  }

  return h('div.cmd-hint',
    t('remote.howToFix', { hint }),
    // 码本身也留一份：截图给别人排查时，光凭中文描述很难定位。
    h('span.cmd-code', command.errorCode));
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
    if (!targetSelect.value) { toast('warn', t('remote.needDevice')); return; }
    try {
      await api(`/admin/devices/${targetSelect.value}/command`, {
        method: 'POST',
        body: { kind, payload: JSON.stringify(payload) },
      });
      toast('ok', t('remote.commandSent'), message);

      // 启停/卸载要等 B 端执行完再刷新清单，否则拿到的是旧状态。
      setTimeout(() => refreshList().catch(() => {}), 800);
      setTimeout(() => load().catch(() => {}), 5000);
    } catch (e) {
      toastError(e, t('remote.sendFailed'));
    }
  };

  const load = async () => {
    if (!targetSelect.value) {
      clear(listBox);
      listBox.appendChild(h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', t('remote.noDevices'))));
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
          h('div', t('remote.noPluginList'))));
        return;
      }

      listBox.appendChild(h('div.table-wrap',
        h('table.data',
          h('thead', h('tr',
            h('th', t('remote.plugin')), h('th', t('remote.version')), h('th', t('remote.author')), h('th', t('remote.status')),
            h('th', { style: { textAlign: 'right' } }, t('remote.actions')),
          )),
          h('tbody', ...plugins.map((p) => h('tr',
            h('td', h('div.cell-main', p.name), h('div.cell-sub', p.id)),
            h('td', p.version || '—'),
            h('td', p.author || '—'),
            h('td', p.isEnabled ? h('span.badge.badge-ok', t('remote.enabled')) : h('span.badge.badge-neutral', t('remote.disabled'))),
            h('td.actions',
              h('button.btn.btn-sm', {
                type: 'button',
                onClick: () => sendPluginCommand('plugin.toggle', { pluginId: p.id, enabled: !p.isEnabled },
                  t('remote.pluginToggleRequested', {
                    action: p.isEnabled ? t('remote.disable') : t('remote.enable'),
                    name: p.name,
                  })),
              }, p.isEnabled ? t('remote.disable') : t('remote.enable')),
              ' ',
              isHubPlugin(p)
                ? null
                : h('button.btn.btn-sm.btn-danger', {
                  type: 'button',
                  onClick: async () => {
                    const ok = await confirmDialog(t('remote.uninstallPlugin'),
                      t('remote.uninstallConfirm', { name: p.name }), t('remote.uninstall'), true);
                    if (!ok) return;
                    await sendPluginCommand('plugin.uninstall', { pluginId: p.id },
                      t('remote.uninstallRequested', { name: p.name }));
                  },
                }, t('remote.uninstall')),
            ),
          ))),
        ),
      ));

      listBox.appendChild(h('div.notice.notice-info', { style: { marginTop: '12px' } },
        h('span.notice-icon', 'i'),
        h('div', t('remote.pluginToggleHint'))));
    } catch (e) {
      clear(listBox);
      listBox.appendChild(h('div.notice.notice-danger', h('span.notice-icon', '!'), h('div', e.message)));
    }
  };

  targetSelect.addEventListener('change', load);
  if (targetSelect.value) load();

  return h('div',
    h('div.toolbar',
      h('span.toolbar-label', t('remote.targetDevice')),
      targetSelect,
      deviceRefreshButton(container),
      h('button.btn.btn-sm', {
        type: 'button',
        onClick: async () => {
          await refreshList();
          toast('ok', t('remote.refreshRequested'), t('remote.refreshAfterHeartbeat'));
          setTimeout(() => load().catch(() => {}), 5000);
        },
      }, t('remote.refreshPlugins')),
      h('button.btn.btn-sm', { type: 'button', onClick: load }, t('remote.view')),
    ),
    listBox,
  );
}

/** IslandHub 接收端插件自身不允许被远程卸载（否则会失联）。 */
function isHubPlugin(plugin) {
  const id = String(plugin.id || '');
  const name = String(plugin.name || '');
  return id.toLowerCase().includes('controlhub') || name.includes('IslandHub');
}

// ────────────────────── 外观下发 ──────────────────────

/**
 * 教室端设备控制：系统音量（#23）与屏幕亮度（#24）。
 *
 * <para>为什么单独一页：这两项是教室机**本机**的状态，和课表、外观都不搭，
 * 而最常用的场景恰恰是「整栋楼统一静音」——所以目标设备默认给「全部在线设备」。</para>
 */
function renderDeviceControl(container) {
  const targetSelect = select(
    [{ value: '__all__', label: t('remote.scopeOnlineShort') }, ...deviceOptions()],
    '__all__',
  );
  const offline = offlineOption();

  const slider = (initial) => {
    const range = h('input', { type: 'range', min: '0', max: '100', step: '5', value: String(initial) });
    const label = h('span.cmd-kind', `${initial}%`);
    range.addEventListener('input', () => { label.textContent = `${range.value}%`; });
    return { range, label, value: () => Number(range.value) };
  };

  const volume = slider(60);
  const brightness = slider(60);
  const muteSelect = select([
    { value: '', label: t('remote.muteKeep') },
    { value: 'on', label: t('remote.mute') },
    { value: 'off', label: t('remote.unmute') },
  ], '');
  const out = h('div');
  // 网络探测（#107）用到的两个控件：目标地址与 Ping 次数（刻意用普通 input，
  // 次数这种「填个数字」的输入不需要滑块那套外观）。
  const hostInput = h('input', {
    type: 'text', placeholder: t('remote.targetHost'), style: { width: '200px' },
  });
  const probeCount = h('input', {
    type: 'number', min: '1', max: '10', value: '4', style: { width: '64px' },
  });

  /** 下发一条指令；只发给单台时把执行结果拉回来（复用命令行页那套历史）。 */
  const dispatch = async (kind, payload, title) => {
    try {
      if (targetSelect.value === '__all__') {
        const r = await api('/admin/devices/command', {
          method: 'POST',
          body: { kind, payload: JSON.stringify(payload), includeOffline: offline.input.checked },
        });
        toast('ok', title, broadcastResult(r));
        return;
      }

      await api(`/admin/devices/${targetSelect.value}/command`, {
        method: 'POST',
        body: { kind, payload: JSON.stringify(payload) },
      });
      toast('ok', title, t('remote.sentSeeBelow'));
      await loadCommandHistory(targetSelect.value, out);
      pollHistory(targetSelect.value, out);
    } catch (e) {
      toastError(e, t('remote.sendFailed'));
    }
  };

  const volumeBtn = h('button.btn.btn-primary', {
    type: 'button',
    onClick: () => {
      // 只把「真正要改的项」放进载荷：没选静音就不带 mute，
      // 否则会把教室端原本的静音状态一并覆盖掉。
      const payload = { percent: volume.value() };
      if (muteSelect.value) payload.mute = muteSelect.value === 'on';
      dispatch('volume.set', payload, t('remote.volumeSent'));
    },
  }, t('remote.setVolume'));

  const brightnessBtn = h('button.btn.btn-primary', {
    type: 'button',
    onClick: () => dispatch('brightness.set', { percent: brightness.value() }, t('remote.brightnessSent')),
  }, t('remote.setBrightness'));

  return h('div',
    h('div.notice.notice-info',
      h('span.notice-icon', 'i'),
      h('div', t('remote.avLead')
        + t('remote.avBody')
        + t('remote.avTail'))),
    h('div.form-row',
      field(t('remote.targetDevice'), targetSelect, t('remote.avScopeHint')),
      field(t('remote.mute'), muteSelect, t('remote.avKeepHint')),
    ),
    h('div.form-row',
      field(t('remote.volume'), h('div.slider-row', volume.range, volume.label), '0~100。'),
      field(t('remote.brightness'), h('div.slider-row', brightness.range, brightness.label),
        t('remote.brightnessHint')),
    ),
    h('div.form-row',
      field(h('span', t('remote.mainWindow'),
        hintIcon(t('remote.mainWindowLead')
          + t('remote.mainWindowBody')
          + t('remote.fullscreenCaveat')
          + t('remote.fullscreenCaveat2'))),
        h('div', { style: { display: 'flex', gap: '8px', flexWrap: 'wrap' } },
          h('button.btn', {
            type: 'button',
            onClick: () => dispatch('window.set', { visible: true }, t('remote.showWindowSent')),
          }, t('remote.showWindow')),
          h('button.btn.btn-danger', {
            type: 'button',
            onClick: () => dispatch('window.set', { visible: false }, t('remote.hideWindowSent')),
          }, t('remote.hideWindow')),
          h('button.btn', {
            type: 'button',
            onClick: () => dispatch('window.set', { fullscreen: true }, t('remote.fullscreenSent')),
          }, t('remote.fullscreen')),
          h('button.btn', {
            type: 'button',
            onClick: () => dispatch('window.set', { fullscreen: false }, t('remote.exitFullscreenSent')),
          }, t('remote.exitFullscreen')))),
    ),
    h('div.form-row',
      field(h('span', t('remote.netTools'),
        hintIcon(t('remote.netLead')
          + t('remote.netBody')
          + t('remote.netTail')
          + t('remote.tracerouteCaveat'))),
        h('div', { style: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'center' } },
          hostInput,
          h('button.btn', {
            type: 'button',
            onClick: () => {
              const host = hostInput.value.trim();
              if (!host) {
                toast('warn', t('remote.needTarget'));
                return;
              }

              dispatch('diagnostic.ping', { host, count: Number(probeCount.value) || 4 }, t('remote.pingSent'));
            },
          }, 'Ping'),
          h('button.btn', {
            type: 'button',
            onClick: () => {
              const host = hostInput.value.trim();
              if (!host) {
                toast('warn', t('remote.needTarget'));
                return;
              }

              dispatch('diagnostic.traceroute', { host }, t('remote.tracerouteSent'));
            },
          }, t('remote.traceroute')),
          h('button.btn', {
            type: 'button',
            onClick: () => dispatch('diagnostic.disks', {}, t('remote.diskSent')),
          }, t('remote.diskSpace'))))),   // 依次关掉：磁盘空间按钮 → 按钮组 → field() → form-row
    h('div', { style: { display: 'flex', gap: '8px', marginTop: '12px' } }, volumeBtn, brightnessBtn),
    offline.el,
    h('div', { style: { marginTop: '16px' } }, out),
  );
}

function renderAppearance(container) {
  const unchanged = { value: '', label: t('remote.keep') };
  const numberInput = (placeholder) => h('input', { type: 'number', placeholder });
  const onOff = [
    unchanged,
    { value: 'on', label: t('remote.enable') },
    { value: 'off', label: t('remote.off') },
  ];

  const targetSelect = select(
    [{ value: '__all__', label: t('remote.scopeOnlineShort') }, ...deviceOptions()],
    '__all__',
  );
  const themeSelect = select([
    unchanged,
    { value: 'system', label: t('remote.themeSystem') },
    { value: 'light', label: t('remote.themeLight') },
    { value: 'dark', label: t('remote.themeDark') },
  ], '');
  const accentInput = h('input', { type: 'text', placeholder: t('remote.accentPlaceholder') });
  const secondaryInput = h('input', { type: 'text', placeholder: t('remote.secondaryPlaceholder') });
  const fontInput = h('input', { type: 'text', placeholder: t('remote.fontPlaceholder') });
  const sizeSecondary = numberInput(t('remote.sizeExample1'));
  const sizeBody = numberInput(t('remote.sizeExample2'));
  const sizeEmphasized = numberInput(t('remote.sizeExample3'));
  const sizeLarge = numberInput(t('remote.sizeExample4'));
  const radiusInput = numberInput(t('remote.sizeExample5'));
  const opacityInput = numberInput(t('remote.ratioExample1'));
  const scaleInput = numberInput(t('remote.ratioExample2'));
  const materialSelect = select([
    unchanged,
    { value: 'acrylic', label: t('remote.materialAcrylic') },
    { value: 'liquidglass', label: 'Liquid Glass' },
    { value: 'mica', label: 'Mica' },
    { value: 'off', label: t('remote.materialOff') },
  ], '');
  const separatedSelect = select(onOff, '');
  const foregroundInput = h('input', { type: 'text', placeholder: t('remote.foregroundPlaceholder') });
  const offline = offlineOption();

  /** 只把「填了值的项」放进载荷：留空 = 不改动教室机现有设置。 */
  const buildAppearance = () => {
    const appearance = {};
    if (themeSelect.value) appearance.theme = themeSelect.value;
    if (accentInput.value.trim()) appearance.accentColor = accentInput.value.trim();
    if (secondaryInput.value.trim()) appearance.secondaryColor = secondaryInput.value.trim();
    if (fontInput.value.trim()) appearance.fontFamily = fontInput.value.trim();

    const fontSizes = {};
    for (const [key, input] of [
      ['secondary', sizeSecondary],
      ['body', sizeBody],
      ['emphasized', sizeEmphasized],
      ['large', sizeLarge],
    ]) {
      const value = Number(input.value);
      if (input.value.trim() && Number.isFinite(value) && value > 0) fontSizes[key] = value;
    }
    if (Object.keys(fontSizes).length > 0) appearance.fontSizes = fontSizes;

    if (radiusInput.value.trim()) appearance.radius = Number(radiusInput.value);
    if (opacityInput.value.trim()) appearance.opacity = Number(opacityInput.value);
    if (scaleInput.value.trim()) appearance.scale = Number(scaleInput.value);
    if (materialSelect.value) appearance.backgroundMaterial = materialSelect.value;
    if (separatedSelect.value) appearance.separatedIsland = separatedSelect.value === 'on';
    if (foregroundInput.value.trim()) appearance.foregroundColor = foregroundInput.value.trim();
    return appearance;
  };

  const applyBtn = h('button.btn.btn-primary', {
    type: 'button',
    onClick: async () => {
      const appearance = buildAppearance();
      if (Object.keys(appearance).length === 0) {
        toast('warn', t('remote.needOneField'), t('remote.needOneFieldHint'));
        return;
      }

      try {
        if (targetSelect.value === '__all__') {
          const r = await api('/admin/devices/appearance', {
            method: 'POST',
            body: { appearance, includeOffline: offline.input.checked },
          });
          toast('ok', t('remote.sent'), broadcastResult(r));
        } else {
          await api(`/admin/devices/${targetSelect.value}/command`, {
            method: 'POST',
            body: { kind: 'appearance.apply', payload: JSON.stringify(appearance) },
          });
          toast('ok', t('remote.sent'), t('remote.appearanceSent'));
        }
      } catch (e) {
        toastError(e, t('remote.sendFailed'));
      }
    },
  }, t('remote.pushAppearance'));

  return h('div',
    h('div.notice.notice-info',
      h('span.notice-icon', 'i'),
      h('div', t('remote.appearanceLead')
        + t('remote.appearanceBody'))),
    h('div.form-row',
      field(t('remote.targetDevice'), targetSelect),
      field(t('remote.theme'), themeSelect),
    ),
    h('div.form-row',
      field(t('remote.accent'), accentInput),
      field(t('remote.secondary'), secondaryInput),
    ),
    field(t('remote.font'), fontInput, t('remote.fontHint')),
    h('div.form-row',
      field(t('remote.sizeSecondary'), sizeSecondary),
      field(t('remote.sizeBody'), sizeBody),
    ),
    h('div.form-row',
      field(t('remote.sizeEmphasis'), sizeEmphasized),
      field(t('remote.sizeLarge'), sizeLarge),
    ),
    h('div.form-row',
      field(t('remote.radius'), radiusInput, t('remote.radiusHint')),
      field(t('remote.opacity'), opacityInput, '0.1 ~ 1'),
    ),
    h('div.form-row',
      field(t('remote.scale'), scaleInput, '0.5 ~ 3'),
      field(t('remote.material'), materialSelect),
    ),
    h('div.form-row',
      field(t('remote.splitWindow'), separatedSelect),
      field(t('remote.foreground'), foregroundInput, t('remote.foregroundHint')),
    ),
    offline.el,
    h('div', { style: { marginTop: '12px' } }, applyBtn),
  );
}

// ────────────────────── 发送提醒 ──────────────────────

function renderNotify(container) {
  const targetSelect = select(
    [
      { value: '__all__', label: t('remote.scopeOnlineShort') },
      ...deviceOptions(),
      { value: '__pick__', label: t('remote.pickDevices') },
    ],
    '__all__',
  );
  const titleInput = h('input', { type: 'text', placeholder: t('remote.noticeTitlePlaceholder') });
  const msgInput = h('textarea', { placeholder: t('remote.noticeBodyPlaceholder'), style: { minHeight: '90px' } });
  const speakChk = h('input', { type: 'checkbox' });
  // #22：默认「全屏强调」——与这个勾选框出现之前的行为一致（以前一律全屏）。
  // 取消勾选则是「温和通知」：只出角落滚动条，不挡讲台。
  const emphasizeChk = h('input', { type: 'checkbox' });
  emphasizeChk.checked = true;
  const offline = offlineOption();

  // 多选目标：选「多选设备…」后弹窗勾选，确定后显示已选台数
  let picked = [];
  const pickInfo = h('span.pick-info', { hidden: true });

  // 通知模板：常用广播内容一键套用
  const templateSelect = select([{ value: '', label: t('remote.noTemplate') }], '');
  let templates = [];
  const loadTemplates = async () => {
    try {
      templates = await api('/admin/notice-templates');
      clear(templateSelect);
      templateSelect.appendChild(h('option', { value: '' }, t('remote.noTemplate')));
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
    toast('ok', t('remote.templateApplied'), t.name);
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
      list.appendChild(h('div', { style: { color: 'var(--text-faint)', fontSize: '12.5px' } }, t('remote.noDevicesShort')));
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
        h('span.picker-meta', t('remote.pickerMeta', {
          state: d.online ? t('remote.online') : t('remote.offline'),
          remark: d.remark ? t('remote.pickerRemark', { remark: d.remark }) : '',
        }))));
    }

    modal({
      title: t('remote.pickTargets'),
      width: 'wide',
      body: h('div',
        h('p', { style: { marginTop: 0, color: 'var(--text-dim)', fontSize: '12.5px' } },
          t('remote.pickTargetsHint')),
        list),
      confirmText: t('remote.ok'),
      onConfirm: () => {
        if (selected.size === 0) {
          toast('warn', t('remote.needOneDevice'));
          return false;
        }

        picked = [...selected];
        pickInfo.textContent = t('remote.pickedCount', { count: picked.length });
        pickInfo.hidden = false;
        return true;
      },
    });
  }

  const saveTemplateBtn = h('button.btn.btn-sm', {
    type: 'button',
    onClick: () => {
      const nameInput = h('input', { type: 'text', placeholder: t('remote.templateNamePlaceholder') });
      modal({
        title: t('remote.saveAsTemplate'),
        body: h('div', field(t('remote.templateName'), nameInput, t('remote.templateNameHint'))),
        confirmText: t('remote.save'),
        onConfirm: async () => {
          const name = nameInput.value.trim();
          if (!name) {
            toast('warn', t('remote.needTemplateName'));
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
          toast('ok', t('remote.templateSaved'), t('remote.templateSavedHint'));
          await loadTemplates();
          return true;
        },
      });
    },
  }, t('remote.saveTemplate'));

  const sendBtn = h('button.btn.btn-primary', {
    type: 'button',
    onClick: async () => {
      const title = titleInput.value.trim();
      const message = msgInput.value.trim();
      if (!title && !message) { toast('warn', t('remote.needNoticeBody')); return; }

      const payload = JSON.stringify({
        title, message, speak: speakChk.checked, emphasize: emphasizeChk.checked,
      });
      try {
        if (targetSelect.value === '__pick__') {
          if (picked.length === 0) { toast('warn', t('remote.needTargets')); return; }
          const r = await api('/admin/devices/command', {
            method: 'POST',
            body: { kind: 'notify', payload, deviceIds: picked, includeOffline: false },
          });
          toast('ok', t('remote.noticeSentShort'), broadcastResult(r));
        } else if (targetSelect.value === '__all__') {
          const r = await api('/admin/devices/command', {
            method: 'POST',
            body: { kind: 'notify', payload, includeOffline: offline.input.checked },
          });
          toast('ok', t('remote.broadcastSent'), broadcastResult(r));
        } else {
          await api(`/admin/devices/${targetSelect.value}/notify`, {
            method: 'POST',
            body: { title, message, speak: speakChk.checked, emphasize: emphasizeChk.checked },
            });
            toast('ok', t('remote.noticeSentShort'),
            t('remote.noticeSent', {
              style: emphasizeChk.checked ? t('remote.noticeFullscreen') : t('remote.noticeCorner'),
              speak: speakChk.checked ? t('remote.noticeSpeakSuffix') : '',
            }));
        }
      } catch (e) {
        toastError(e, t('remote.noticeFailed'));
      }
    },
  }, t('remote.tabNotice'));

  loadTemplates();

  return h('div',
    h('div.form-row',
      field(t('remote.targetDevice'), targetSelect),
      field(t('remote.noticeTemplate'), h('div.template-row', templateSelect, saveTemplateBtn),
        t('remote.noticeTemplateHint')),
    ),
    pickInfo,
    field(t('remote.noticeTitle'), titleInput),
    field(t('remote.noticeBody'), msgInput),
    h('label.checkbox-field', speakChk, h('span', t('remote.noticeSpeak'))),
    h('label.checkbox-field', emphasizeChk,
      h('span', t('remote.noticeEmphasize'),
        hintIcon(t('remote.emphasizeHint')
          + t('remote.emphasizeHint2')
          + t('remote.emphasizeHint3')))),
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
    placeholder: t('remote.signalPlaceholder'),
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
        toast('ok', t('remote.signalsFetched'), [...found].join('、'));
      }
    } catch { /* 拿不到就退回手动输入 */ }
  }

  const pullBtn = h('button.btn.btn-sm', {
    type: 'button',
    onClick: async () => {
      if (!deviceSelect.value) { toast('warn', t('remote.needDevice')); return; }
      try {
        await api(`/admin/devices/${deviceSelect.value}/command`, {
          method: 'POST',
          body: { kind: 'automation.list' },
        });
        toast('ok', t('remote.requested'), t('remote.signalsHint'));
        await loadCommandHistory(deviceSelect.value, historyBox);
        pollHistory(deviceSelect.value, historyBox, 3);
        setTimeout(() => harvestSignals().catch(() => {}), 3000);
      } catch (e) {
        toastError(e, t('remote.requestFailed'));
      }
    },
  }, t('remote.fetchSignals'));

  const triggerBtn = h('button.btn.btn-primary', {
    type: 'button',
    onClick: async () => {
      const signal = signalInput.value.trim();
      if (!deviceSelect.value) { toast('warn', t('remote.needDevice')); return; }
      if (!signal) { toast('warn', t('remote.needSignal')); return; }
      if (!await confirmDialog(t('remote.triggerAutomation'),
        t('remote.triggerConfirm', { signal }), t('remote.trigger'))) {
        return;
      }

      try {
        await api(`/admin/devices/${deviceSelect.value}/command`, {
          method: 'POST',
          body: { kind: 'automation.trigger', payload: JSON.stringify({ signal }) },
        });
        toast('ok', t('remote.triggered'), t('remote.triggeredHint'));
        await loadCommandHistory(deviceSelect.value, historyBox);
        pollHistory(deviceSelect.value, historyBox, 3);
      } catch (e) {
        toastError(e, t('remote.triggerFailed'));
      }
    },
  }, t('remote.triggerAutomation'));

  deviceSelect.addEventListener('change', () => {
    clear(datalist);
    clear(historyBox);
  });

  return h('div',
    h('div.form-row', field(t('remote.targetDevice'), deviceSelect)),
    field(t('remote.signal'), h('div.template-row', signalInput, pullBtn),
      t('remote.signalHint')),
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
    const status = h('span.log-level.warn', t('remote.stateQueued'));
    const output = h('pre.cmd-output', t('remote.waiting'));
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
    title: t('remote.batchResult'),
    width: 'wide',
    hideFooter: true,
    body: h('div',
      h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', t('remote.batchCaption', { command: commandText, count: items.length }))),
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
    if (!deviceId) { toast('warn', t('remote.needDevice')); return; }

    try {
      await api(`/admin/devices/${deviceId}/command`, { method: 'POST', body: { kind, payload: '{}' } });
      toast('ok', t('remote.requestedAction', { action: label }), t('remote.resultLater'));
      await loadCommandHistory(deviceId, out);
      pollHistory(deviceId, out, 6);
      await queue.refresh();
      if (kind === 'diagnostic.screenshot') {
        pollScreenshots(deviceId, gallery, 4);
      }
    } catch (e) {
      toastError(e, t('remote.sendFailed'));
    }
  };

  deviceSelect.addEventListener('change', () => {
    clear(out);
    out.style.display = 'none';
    loadScreenshots(deviceSelect.value, gallery);
  });

  const view = h('div',
    h('div.toolbar',
      h('span.toolbar-label', t('remote.targetDevice')),
      deviceSelect,
      deviceRefreshButton(container),
    ),
    h('div.notice.notice-info', { style: { marginTop: '12px' } },
      h('span.notice-icon', 'i'),
      h('div', t('remote.diagLead')
        + t('remote.diagBody')
        + t('remote.diagTail'))),
    h('div', { style: { display: 'flex', gap: '8px', flexWrap: 'wrap', marginTop: '12px' } },
      h('button.btn.btn-primary', { type: 'button', onClick: run('diagnostic.screenshot', t('remote.screenshot')) }, t('remote.takeScreenshot')),
      h('button.btn', { type: 'button', onClick: run('diagnostic.processes', t('remote.processes')) }, t('remote.requestProcesses')),
      h('button.btn', { type: 'button', onClick: run('diagnostic.bundle', t('remote.bundle')) }, t('remote.requestBundle')),
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
  box.appendChild(loadingBlock(t('remote.loadingShots')));

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
    h('span.toolbar-label', t('remote.shotsTitle', { count: list.length })),
    h('div.spacer'),
    list.length > 0
      ? h('button.btn.btn-sm', {
        type: 'button',
        onClick: async () => {
          if (!await confirmDialog(t('remote.clearShots'), t('remote.clearShotsConfirm'), t('remote.clear'), true)) return;
          await api(`/admin/devices/${deviceId}/diagnostics`, { method: 'DELETE' });
          toast('ok', t('remote.shotsCleared'));
          await loadScreenshots(deviceId, box);
        },
      }, t('remote.clearShots'))
      : null,
  ));

  if (list.length === 0) {
    box.appendChild(emptyState('devices', t('remote.noShots'), t('remote.noShotsHint')));
    return;
  }

  box.appendChild(h('div.shot-grid', ...list.map(screenshotCard)));
}

function screenshotCard(item) {
  const wrap = h('div.shot-thumb-wrap', h('div.shot-loading', t('remote.loading')));

  fetchBlob(`/admin/devices/diagnostics/${item.id}/content`)
    .then((blob) => {
      const url = URL.createObjectURL(blob);
      const img = h('img.shot-thumb', { src: url, alt: t('remote.deviceShots') });
      img.addEventListener('load', () => URL.revokeObjectURL(url), { once: true });
      clear(wrap);
      wrap.appendChild(img);
    })
    .catch((err) => {
      clear(wrap);
      wrap.appendChild(h('div.shot-failed', err.message));
    });

  wrap.addEventListener('click', () => openScreenshot(item));

  // 无障碍（#49）：这张可点缩略图要能 Tab 到、回车/空格打开——
  // 纯 div 上挂 click，键盘用户根本够不着。
  wrap.tabIndex = 0;
  wrap.setAttribute('role', 'button');
  wrap.setAttribute('aria-label', t('remote.viewShotAt', { time: formatDateTime(item.capturedAt) }));
  wrap.addEventListener('keydown', (event) => {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      openScreenshot(item);
    }
  });

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
    title: t('remote.shotAt', { time: formatDateTime(item.capturedAt) }),
    width: 'xwide',
    hideFooter: true,
    body: loadingBlock(t('remote.loadingShots')),
  });

  try {
    const blob = await fetchBlob(`/admin/devices/diagnostics/${item.id}/content`);
    const url = URL.createObjectURL(blob);
    clear(dialog.bodyEl);
    dialog.bodyEl.appendChild(h('div',
      h('img', {
        src: url,
        alt: t('remote.deviceShots'),
        style: { width: '100%', borderRadius: '10px', border: '1px solid var(--border)' },
      }),
      h('div', { style: { marginTop: '10px', fontSize: '12.5px', color: 'var(--text-dim)' } }, item.note || ''),
      h('div', { style: { marginTop: '10px' } },
        h('a.btn.btn-sm', { href: url, download: `screenshot-${item.id}.png` }, t('remote.downloadOriginal'))),
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
        listBox.appendChild(emptyState('database', t('remote.noBackups'), t('remote.noBackupsHint')));
        return;
      }

      listBox.appendChild(h('div.table-wrap',
        h('table.data',
          h('thead', h('tr',
            h('th', t('remote.tabBackup')), h('th', t('remote.backupType')), h('th', t('remote.backupTime')), h('th', t('remote.backupSize')), h('th', t('remote.actions')),
          )),
          h('tbody', ...state.backupList.map((b) => h('tr',
            h('td', h('div.cell-main', b.id), h('div.cell-sub', b.note || '')),
            h('td', h('span.badge.badge-neutral', b.type)),
            h('td', formatDateTime(b.createdAt)),
            h('td', `${(b.sizeBytes / 1024).toFixed(1)} KB`),
            h('td.actions',
              h('a.btn.btn-sm', { href: `/api/v1/admin/backups/${b.id}/download`, style: { textDecoration: 'none' } }, t('remote.download')),
              ' ',
              h('button.btn.btn-sm', {
                type: 'button',
                onClick: async () => {
                  const ok = await confirmDialog(t('remote.restoreBackup'),
                    t('remote.restoreConfirm', { id: b.id }),
                    t('remote.restore'));
                  if (!ok) return;
                  await api(`/admin/backups/${b.id}/restore`, { method: 'POST' });
                  toast('ok', t('remote.restored'), t('remote.restartToApply'));
                },
              }, t('remote.restore')),
              ' ',
              h('button.btn.btn-sm.btn-danger', {
                type: 'button',
                onClick: async () => {
                  const ok = await confirmDialog(t('remote.deleteBackup'), t('remote.deleteBackupConfirm', { id: b.id }), t('remote.delete'), true);
                  if (!ok) return;
                  await api(`/admin/backups/${b.id}`, { method: 'DELETE' });
                  toast('ok', t('remote.deleted'));
                  await load();
                },
              }, t('remote.delete')),
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
          await api('/admin/backups', { method: 'POST', body: { note: t('remote.backupManual') } });
          toast('ok', t('remote.backupCreated'));
          await load();
        },
      }, t('remote.backupNow')),
      h('button.btn.btn-sm', { type: 'button', onClick: load }, t('remote.refresh')),
      h('div.spacer'),
      h('span', { style: { fontSize: '12px', color: 'var(--text-faint)' } }, t('remote.autoBackupHint')),
    ),
    listBox,
  );
}
