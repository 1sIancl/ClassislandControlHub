/**
 * 仪表盘视图：总体概览、服务器信息与最近事件。
 *
 * 「统计卡片」与「页面模块」都支持自定义显隐与顺序（#40）：本机存 localStorage，
 * 同时同步到账号（见 core/prefs.js），换台电脑登录后布局保持一致。
 */

import { api, session } from '../core/api.js?v=75';
import {
  h, clear, formatDateTime, formatDuration, relativeTime,
  loadingBlock, modal, append, icon, toast, guard,
} from '../core/ui.js?v=75';
import {
  getLayout, saveLayout, getLayoutSyncState, onLayoutSyncChange,
} from '../core/prefs.js?v=75';

export const meta = {
  title: '仪表盘',
  subtitle: '集控运行概览',
};

// 当前视图容器与参数（供「自定义模块」面板保存后原地刷新）。
let _container = null;
let _params = null;


// 统计模块定义：key 用于持久化，label 用于配置面板，build 生成卡片。
const STAT_DEFS = [
  { key: 'total', label: '设备总数', build: (s) => stat('设备总数', s.deviceCount, `分 ${s.groupCount} 个组`, '', 'monitor', '#/devices') },
  { key: 'online', label: '在线设备', build: (s) => stat('在线设备', s.onlineDeviceCount, `在线率 ${Math.round((s.onlineRate || 0) * 100)}%`, 'ok', 'checkCircle', '#/devices?status=online') },
  { key: 'pending', label: '待同步', build: (s) => stat('待同步', s.pendingDeviceCount, s.pendingDeviceCount > 0 ? '等待客户端拉取' : '全部已同步', s.pendingDeviceCount > 0 ? 'warn' : 'ok', 'clock', '#/devices?status=pending') },
  { key: 'error', label: '同步异常', build: (s) => stat('同步异常', s.errorDeviceCount, s.errorDeviceCount > 0 ? '需查看设备日志' : '无异常', s.errorDeviceCount > 0 ? 'danger' : 'ok', 'alert', '#/devices?status=error') },
  { key: 'profiles', label: '配置档案', build: (s) => stat('配置档案', s.profileCount, `当前版本 ${s.revision}`, 'info', 'profiles', '#/profiles') },
  // 「近 24h 新增」没有对应的筛选视图，就不做成可点的——点了跳不到具体东西的入口，比不给更让人困惑。
  { key: 'recent', label: '近 24h 新增', build: (s) => stat('近 24h 新增', s.recentEnrollCount, '新注册设备', '', 'plusCircle', null) },
];

const STAT_KEYS = () => STAT_DEFS.map((d) => d.key);

/**
 * 快捷操作区：把四个最高频的动作直接摆在总览上。
 *
 * <para>解决的是「新用户不知道『发通知』在哪」——过去要先进「远程管理」才找得到，
 * 而那个名字并不能让人联想到「给教室发条消息」。</para>
 *
 * <para>刻意做成**跳转**而不是直接执行：直接执行就得先问「对哪些设备」，
 * 而「挑设备」是设备页才有的上下文。跳过去（并预置筛选）反而更快，
 * 至少用户能**先看到**将要操作哪些设备。</para>
 */
const QUICK_ACTIONS = [
  { label: '发送通知', hint: '给选中的教室推送文字或图片', icon: 'bell', to: '#/devices?status=online' },
  { label: '下发配置', hint: '按楼栋 / 楼层 / 设备推送档案', icon: 'send', to: '#/deploy' },
  { label: '远程管理', hint: '指令、插件、外观与诊断', icon: 'monitor', to: '#/remote' },
  { label: '定时提醒', hint: '按日期与周期自动推送到大屏', icon: 'clock', to: '#/reminders' },
];

function renderQuickActions() {
  return h('div.quick-actions', ...QUICK_ACTIONS.map((action) => h('a.quick-action', {
    href: action.to,
    title: action.hint,
  },
  h('span.quick-action-icon', icon(action.icon, 18)),
  h('span.quick-action-body',
    h('span.quick-action-label', action.label),
    h('span.quick-action-hint', action.hint)))));
}

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
    h('div', { style: { display: 'flex', justifyContent: 'flex-end', marginBottom: '10px' } },
      h('button.btn.btn-sm', {
        type: 'button',
        onClick: () => guard('打开自定义仪表盘', () => openCustomize()),
      }, '自定义仪表盘')),
    renderStats(stats),
    renderQuickActions(),
    ...renderCards({ stats, info, devices }),
  ));
}

/** 统计卡片：显隐与顺序由 `dashboard.stats` 布局决定；全部关掉时整块不占位。 */
function renderStats(stats) {
  const layout = getLayout('dashboard.stats', STAT_KEYS());
  const defs = layout
    .filter((l) => l.enabled !== false)
    .map((l) => STAT_DEFS.find((d) => d.key === l.key))
    .filter(Boolean);

  if (defs.length === 0) return null;
  return h('div.stat-grid', ...defs.map((d) => d.build(stats)));
}

// 页面模块定义（#40）：与统计卡片一样可显隐 / 排序。build 在无内容时返回 null（此时自然不占位）。
const CARD_DEFS = [
  { key: 'server', label: '服务器信息', build: (ctx) => renderServerCard(ctx.info, ctx.stats) },
  { key: 'events', label: '最近事件', build: (ctx) => renderEventCard(ctx.stats) },
  { key: 'onboarding', label: '快速开始', build: (ctx) => renderOnboarding(ctx.stats, ctx.devices) },
  { key: 'devices', label: '设备状态速览', build: (ctx) => renderDevicePreview(ctx.devices) },
];

const CARD_KEYS = () => CARD_DEFS.map((d) => d.key);

/** 「服务器信息」与「最近事件」是半宽卡片：相邻且都启用时并排两列，否则各占一行。 */
const PAIR_KEYS = new Set(['server', 'events']);

function renderCards(ctx) {
  const layout = getLayout('dashboard.cards', CARD_KEYS());
  const defs = layout
    .filter((l) => l.enabled !== false)
    .map((l) => CARD_DEFS.find((d) => d.key === l.key))
    .filter(Boolean);

  const nodes = [];
  for (let i = 0; i < defs.length; i++) {
    const def = defs[i];
    const next = defs[i + 1];
    if (next && PAIR_KEYS.has(def.key) && PAIR_KEYS.has(next.key)) {
      nodes.push(h('div.grid-2', def.build(ctx), next.build(ctx)));
      i++;
      continue;
    }
    const node = def.build(ctx);
    if (node) nodes.push(node);
  }
  return nodes;
}

/**
 * 统计卡片。
 * @param {string} label 标题。
 * @param {number|string} value 数值。
 * @param {string} hint 副标题。
 * @param {string} [tone] 语义色（ok / warn / danger / info）。
 * @param {string} [iconKey] 图标名。
 * @param {string} [to] 点击后跳转的 hash（可带筛选，如 `#/devices?status=pending`）。
 *   <para>「数字可点」才是这张卡片的价值：看到「待同步 3」，下一个动作就是去看那 3 台。
 *   不该让人记住功能在哪个菜单、再自己去筛一遍——那样数字就只是装饰。</para>
 */
function stat(label, value, hint, tone, iconKey, to = null) {
  const iconEl = h('span.stat-icon');
  iconEl.appendChild(icon(iconKey, 17));

  const body = [
    h('div.stat-label', iconEl, h('span.stat-label-text', label)),
    h('div.stat-value', String(value ?? 0)),
    h('div.stat-hint', hint),
  ];

  if (!to) {
    return h(`div.stat${tone ? '.' + tone : ''}`, ...body);
  }

  const go = () => {
    window.location.hash = to;
  };

  return h(`div.stat.stat-link${tone ? '.' + tone : ''}`, {
    role: 'link',
    tabindex: '0',
    title: `${label} · 点击查看明细`,
    onClick: go,
    onKeydown: (event) => {
      // 键盘用户同样要能用（无障碍方向）：Enter / 空格等同于点击。
      if (event.key === 'Enter' || event.key === ' ') {
        event.preventDefault();
        go();
      }
    },
  }, ...body, h('span.stat-go', '›'));
}

/**
 * 「自定义仪表盘」面板：两组（统计卡片 / 页面模块）各自勾选显隐与上移 / 下移。
 * 面板底部显示布局偏好的同步状态——同步失败会明确写「只保存在本机」，不让改动悄悄消失。
 */
function openCustomize() {
  const container = h('div');

  function moveItem(scope, keys, key, delta) {
    const layout = getLayout(scope, keys);
    const idx = layout.findIndex((l) => l.key === key);
    const target = idx + delta;
    if (idx < 0 || target < 0 || target >= layout.length) return;
    [layout[idx], layout[target]] = [layout[target], layout[idx]];
    saveLayout(scope, layout);
    rerender();
  }

  function toggleItem(scope, keys, key, enabled) {
    const layout = getLayout(scope, keys);
    const item = layout.find((l) => l.key === key);
    if (item) item.enabled = enabled;
    saveLayout(scope, layout);
    rerender();
  }

  function buildSection(scope, defs, title, hint) {
    const keys = defs.map((d) => d.key);
    const layout = getLayout(scope, keys);
    return h('div.config-section',
      h('div.config-section-title', title),
      h('p.card-desc', { style: { margin: '0 0 8px' } }, hint),
      h('div.config-panel', ...layout.map((item, idx) => {
        const def = defs.find((d) => d.key === item.key);
        return h('div.config-item' + (item.enabled === false ? '.disabled' : ''),
          // 只显示序号，不做拖拽：原先放了个 ⠿ 把手却拖不动，属于「看起来能操作其实不能」。
          h('span.item-order', String(idx + 1)),
          h('span.item-label', def ? def.label : item.key),
          h('button.move-btn', { type: 'button', disabled: idx === 0, onClick: () => moveItem(scope, keys, item.key, -1) }, '↑'),
          h('button.move-btn', { type: 'button', disabled: idx === layout.length - 1, onClick: () => moveItem(scope, keys, item.key, 1) }, '↓'),
          h('input', { type: 'checkbox', checked: item.enabled !== false, onChange: (e) => toggleItem(scope, keys, item.key, e.target.checked) }),
        );
      })),
    );
  }

  function syncNotice() {
    const state = getLayoutSyncState();
    const failed = state.state === 'error';
    return h('div.notice' + (failed ? '.notice-warn' : '.notice-info'), { style: { marginTop: '14px' } },
      h('span.notice-icon', failed ? '!' : 'i'),
      h('div', state.message || '布局偏好会同步到你的账号：换台电脑登录后仍然生效。'),
    );
  }

  function buildPanel() {
    return h('div',
      buildSection('dashboard.stats', STAT_DEFS, '统计卡片', '页面上方那排数字，关掉不关心的指标即可。'),
      buildSection('dashboard.cards', CARD_DEFS, '页面模块', '整块卡片；「服务器信息」与「最近事件」相邻时会并排显示。'),
      syncNotice(),
    );
  }

  function rerender() {
    clear(container);
    append(container, buildPanel());
  }

  // 同步是异步的：推送完成后刷新一次，免得面板里一直停在「正在同步…」。
  onLayoutSyncChange(() => {
    if (document.body.contains(container)) rerender();
  });

  rerender();

  modal({
    title: '自定义仪表盘',
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