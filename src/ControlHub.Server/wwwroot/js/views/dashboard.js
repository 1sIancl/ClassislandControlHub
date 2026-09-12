/**
 * 仪表盘视图：总体概览、服务器信息与最近事件。
 */

import { api, session } from '../core/api.js';
import {
  h, clear, formatDateTime, formatDuration, relativeTime,
  toast, loadingBlock,
} from '../core/ui.js';

export const meta = {
  title: '仪表盘',
  subtitle: '集控运行概览',
};

export async function render(container) {
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
  const onlineRate = Math.round((stats.onlineRate || 0) * 100);
  return h('div.stat-grid',
    stat('设备总数', stats.deviceCount, `分 ${stats.groupCount} 个组`, ''),
    stat('在线设备', stats.onlineDeviceCount, `在线率 ${onlineRate}%`, 'ok'),
    stat('待同步', stats.pendingDeviceCount, stats.pendingDeviceCount > 0 ? '等待客户端拉取' : '全部已同步',
      stats.pendingDeviceCount > 0 ? 'warn' : 'ok'),
    stat('同步异常', stats.errorDeviceCount,
      stats.errorDeviceCount > 0 ? '需查看设备日志' : '无异常', stats.errorDeviceCount > 0 ? 'danger' : 'ok'),
    stat('配置档案', stats.profileCount, `当前版本 ${stats.revision}`, 'info'),
    stat('近 24h 新增', stats.recentEnrollCount, '新注册设备', ''),
  );
}

function stat(label, value, hint, tone) {
  return h(`div.stat${tone ? '.' + tone : ''}`,
    h('div.stat-label', label),
    h('div.stat-value', String(value ?? 0)),
    h('div.stat-hint', hint),
  );
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
