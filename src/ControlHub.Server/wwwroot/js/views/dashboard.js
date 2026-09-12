/**
 * 仪表盘视图：总体概览、服务器信息与最近事件。
 * 统计模块支持自定义（显隐与顺序，持久化于本地偏好 `controlhub.ui.layout.dashboard.stats`）。
 */

import { api, session } from '../core/api.js?v=7';
import {
  h, clear, formatDateTime, formatDuration, relativeTime,
  loadingBlock, modal, append,
} from '../core/ui.js?v=7';
import { getLayout, saveLayout } from '../core/prefs.js?v=7';

export const meta = {
  title: '仪表盘',
  subtitle: '集控运行概览',
};

// 当前视图容器与参数（供「自定义模块」面板保存后原地刷新）。
let _container = null;
let _params = null;

// 统计模块图标（内联 SVG，描边风格）。
const STAT_ICONS = {
  total: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="3" width="20" height="14" rx="2"/><path d="M8 21h8M12 17v4"/></svg>',
  online: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="m8.5 12 2.5 2.5 4.5-5"/></svg>',
  pending: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M12 7v5l3.5 2"/></svg>',
  error: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z"/><path d="M12 9v4M12 17h.01"/></svg>',
  profiles: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6"/></svg>',
  recent: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M12 8v8M8 12h8"/></svg>',
};

// 统计模块定义：key 用于持久化，label 用于配置面板，build 生成卡片。
const STAT_DEFS = [
  { key: 'total', label: '设备总数', build: (s) => stat('设备总数', s.deviceCount, `分 ${s.groupCount} 个组`, '', 'total') },
  { key: 'online', label: '在线设备', build: (s) => stat('在线设备', s.onlineDeviceCount, `在线率 ${Math.round((s.onlineRate || 0) * 100)}%`, 'ok', 'online') },
  { key: 'pending', label: '待同步', build: (s) => stat('待同步', s.pendingDeviceCount, s.pendingDeviceCount > 0 ? '等待客户端拉取' : '全部已同步', s.pendingDeviceCount > 0 ? 'warn' : 'ok', 'pending') },
  { key: 'error', label: '同步异常', build: (s) => stat('同步异常', s.errorDeviceCount, s.errorDeviceCount > 0 ? '需查看设备日志' : '无异常', s.errorDeviceCount > 0 ? 'danger' : 'ok', 'error') },
  { key: 'profiles', label: '配置档案', build: (s) => stat('配置档案', s.profileCount, `当前版本 ${s.revision}`, 'info', 'profiles') },
  { key: 'recent', label: '近 24h 新增', build: (s) => stat('近 24h 新增', s.recentEnrollCount, '新注册设备', '', 'recent') },
];

const STAT_KEYS = () => STAT_DEFS.map((d) => d.key);

export async function render(container, params) {
  _container = container;
  _params = params;
  clear(container);
  container.appendChild(loadingBlock());

  const [stats, info, devices] = await Promise.all([
    api('/admin/dashboard'),
    api('/server/info', { auth: false }),
    api('/admin/devices'),
  ]);

  session.serverInfo = info;
  session.revision = stats.revision;

  clear(container);
  container.appendChild(h('div',
    renderStats(stats),
    h('div.grid-2',
      renderServerCard(info, stats),
      renderEventCard(stats),
    ),
    renderOnboarding(stats, devices),
    renderDevicePreview(devices),
  ));
}

function renderStats(stats) {
  const layout = getLayout('dashboard.stats', STAT_KEYS());
  const defs = layout
    .filter((l) => l.enabled !== false)
    .map((l) => STAT_DEFS.find((d) => d.key === l.key))
    .filter(Boolean);

  const customizeBtn = h('button.btn.btn-sm', { onClick: openStatCustomize }, '⚙ 自定义统计模块');

  if (defs.length === 0) {
    return h('div', { style: { marginBottom: '18px', textAlign: 'right' } }, customizeBtn);
  }

  return h('div', { style: { marginBottom: '18px' } },
    h('div', { style: { display: 'flex', justifyContent: 'flex-end', marginBottom: '8px' } }, customizeBtn),
    h('div.stat-grid', ...defs.map((d) => d.build(stats))),
  );
}

function stat(label, value, hint, tone, iconKey) {
  const iconEl = h('span.stat-icon');
  iconEl.innerHTML = STAT_ICONS[iconKey] || '';
  return h(`div.stat${tone ? '.' + tone : ''}`,
    h('div.stat-label', iconEl, h('span.stat-label-text', label)),
    h('div.stat-value', String(value ?? 0)),
    h('div.stat-hint', hint),
  );
}

/** 「自定义统计模块」面板：勾选显隐 + 上移/下移排序。 */
function openStatCustomize() {
  const container = h('div');

  function moveItem(key, delta) {
    const layout = getLayout('dashboard.stats', STAT_KEYS());
    const idx = layout.findIndex((l) => l.key === key);
    const target = idx + delta;
    if (target < 0 || target >= layout.length) return;
    [layout[idx], layout[target]] = [layout[target], layout[idx]];
    saveLayout('dashboard.stats', layout);
    rerender();
  }

  function toggleItem(key, enabled) {
    const layout = getLayout('dashboard.stats', STAT_KEYS());
    const item = layout.find((l) => l.key === key);
    if (item) item.enabled = enabled;
    saveLayout('dashboard.stats', layout);
    rerender();
  }

  function buildPanel() {
    const layout = getLayout('dashboard.stats', STAT_KEYS());
    const panel = h('div.config-panel');
    layout.forEach((item, idx) => {
      const def = STAT_DEFS.find((d) => d.key === item.key);
      const label = def ? def.label : item.key;
      panel.appendChild(h('div.config-item' + (item.enabled === false ? '.disabled' : ''),
        h('span.drag-handle', '⠿'),
        h('span.item-label', label),
        h('button.move-btn', { type: 'button', disabled: idx === 0, onClick: () => moveItem(item.key, -1) }, '↑'),
        h('button.move-btn', { type: 'button', disabled: idx === layout.length - 1, onClick: () => moveItem(item.key, 1) }, '↓'),
        h('input', { type: 'checkbox', checked: item.enabled !== false, onChange: (e) => toggleItem(item.key, e.target.checked) }),
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
    title: '自定义统计模块',
    body: container,
    confirmText: '完成',
    onConfirm: () => {
      render(_container, _params);
      return true;
    },
  });
}

function renderServerCard(info, stats) {
  return h('div.card',
    h('div.card-head',
      h('div',
        h('h3', '服务器信息'),
        h('p.card-desc', info.serverName || '集控服务器'),
      ),
    ),
    h('div', { style: { display: 'grid', gap: '9px', fontSize: '13px' } },
      infoRow('服务版本', info.version),
      infoRow('协议版本', info.protocolVersion),
      infoRow('配置版本', `#${info.revision}`),
      infoRow('运行时长', formatDuration(info.uptimeSeconds)),
      infoRow('启动时间', formatDateTime(info.startedAt)),
      infoRow('HTTP 端口', String(info.httpPort)),
      infoRow('发现端口', info.discoveryEnabled ? String(info.discoveryPort) : '已关闭'),
      infoRow('要求注册码', info.requiresEnrollCode ? '是' : '否'),
      infoRow('数据目录', info.dataDirectory, true),
    ),
  );
}

function infoRow(label, value, mono = false) {
  return h('div', { style: { display: 'flex', justifyContent: 'space-between', gap: '16px', alignItems: 'baseline' } },
    h('span', { style: { color: 'var(--text-dim)', flex: 'none' } }, label),
    h('span', {
      style: {
        fontFamily: mono ? 'var(--mono)' : 'inherit',
        fontSize: mono ? '12px' : 'inherit',
        textAlign: 'right',
        wordBreak: 'break-all',
      },
    }, String(value ?? '—')),
  );
}

function renderEventCard(stats) {
  const events = stats.recentEvents || [];
  return h('div.card',
    h('div.card-head',
      h('div',
        h('h3', '最近事件'),
        h('p.card-desc', '客户端注册、配置下发与同步记录'),
      ),
    ),
    events.length === 0
      ? h('div.empty', { style: { padding: '30px 10px' } }, h('p', '暂无事件记录'))
      : h('div.timeline', ...events.map((e) => h('div.timeline-item',
        h('div.timeline-time', relativeTime(e.timestamp)),
        h('div.timeline-body',
          h('strong', e.action),
          h('div.timeline-detail', `${e.actor} · ${e.detail || e.target || ''}`),
        ),
      ))),
  );
}

function renderOnboarding(stats, devices) {
  const steps = [];

  if (stats.profileCount === 0) {
    steps.push({
      title: '① 创建一个配置档案',
      text: '档案里放着时间表、课表和科目，是下发给客户端的实际内容。可直接用「示例档案」起步。',
      action: { label: '前往配置档案', hash: '#/profiles' },
    });
  }

  if (devices.length === 0) {
    steps.push({
      title: '② 生成注册码并安装插件',
      text: '在「设备管理 → 注册码」生成注册码，然后在教室电脑的 ClassIsland 中安装集控插件并填入注册码。',
      action: { label: '前往注册码', hash: '#/devices' },
    });
  }

  if (stats.profileCount > 0 && stats.deviceCount > 0 && stats.pendingDeviceCount === 0) {
    return null;
  }

  if (stats.profileCount > 0 && devices.length > 0) {
    steps.push({
      title: '③ 下发配置并观察同步结果',
      text: '在「配置下发」中选择目标设备或分组执行推送，客户端会在数秒内自动拉取新配置。',
      action: { label: '前往配置下发', hash: '#/deploy' },
    });
  }

  if (steps.length === 0) return null;

  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', '快速开始'),
        h('p.card-desc', '按顺序完成以下步骤即可让教室大屏接入集控'),
      ),
    ),
    h('div', { style: { display: 'grid', gap: '12px' } }, ...steps.map((s) => h('div', {
      style: {
        display: 'flex', gap: '14px', alignItems: 'center', justifyContent: 'space-between',
        padding: '12px 14px', background: 'var(--bg-panel-2)', borderRadius: 'var(--radius-sm)',
      },
    },
      h('div',
        h('div', { style: { fontWeight: '600', marginBottom: '2px' } }, s.title),
        h('div', { style: { fontSize: '12.5px', color: 'var(--text-faint)' } }, s.text),
      ),
      h('a', { href: s.action.hash, class: 'btn btn-sm', style: { textDecoration: 'none' } }, s.action.label),
    ))),
  );
}

function renderDevicePreview(devices) {
  if (devices.length === 0) return null;

  const pending = devices.filter((d) => !d.upToDate && !d.revoked);
  const preview = (pending.length > 0 ? pending : devices).slice(0, 8);

  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', pending.length > 0 ? `待同步设备（${pending.length}）` : '设备状态速览'),
        h('p.card-desc', pending.length > 0 ? '以下设备尚未取到最新配置' : '全部设备均已同步到最新配置'),
      ),
      h('a', { href: '#/devices', class: 'btn btn-sm', style: { textDecoration: 'none' } }, '查看全部设备'),
    ),
    h('div.table-wrap',
      h('table.data',
        h('thead', h('tr',
          h('th', '设备'),
          h('th', '分组'),
          h('th', '状态'),
          h('th', '版本'),
          h('th', '最近心跳'),
        )),
        h('tbody', ...preview.map((d) => h('tr',
          h('td',
            h('div.cell-main', d.name),
            h('div.cell-sub', d.machineName || d.id.slice(0, 8)),
          ),
          h('td', d.groupName || '未分组'),
          h('td', d.revoked
            ? h('span.badge.badge-danger', '已停用')
            : d.online
              ? h('span.badge.badge-ok', '在线')
              : h('span.badge.badge-neutral', '离线')),
          h('td', d.upToDate
            ? h('span.badge.badge-ok', '最新')
            : h('span.badge.badge-warn', `${d.appliedRevision} / ${d.serverRevision}`)),
          h('td', { style: { color: 'var(--text-faint)', fontSize: '12px' } }, relativeTime(d.lastSeenAt)),
        ))),
      ),
    ),
  );
}
