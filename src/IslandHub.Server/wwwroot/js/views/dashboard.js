/**
 * 仪表盘视图：总体概览、服务器信息与最近事件。
 *
 * 「统计卡片」与「页面模块」都支持自定义显隐与顺序（#40）：本机存 localStorage，
 * 同时同步到账号（见 core/prefs.js），换台电脑登录后布局保持一致。
 */

import { api, session } from '../core/api.js?v=128';
import {
  h, clear, formatDateTime, formatDuration, relativeTime,
  loadingBlock, modal, append, icon, toast, guard,
} from '../core/ui.js?v=128';
import {
  getLayout, saveLayout, getLayoutSyncState, onLayoutSyncChange,
} from '../core/prefs.js?v=128';
import { t } from '../core/i18n.js?v=128';

export const meta = {
  title: t('dashboard.title'),
  subtitle: t('dashboard.subtitle'),
};

// 当前视图容器与参数（供「自定义模块」面板保存后原地刷新）。
let _container = null;
let _params = null;


// 统计模块定义：key 用于持久化，label 用于配置面板，build 生成卡片。
const STAT_DEFS = [
  { key: 'total', label: t('dashboard.statTotal'), build: (s) => stat(t('dashboard.statTotal'), s.deviceCount, t('dashboard.statGroups', { count: s.groupCount }), '', 'monitor', '#/devices') },
  { key: 'online', label: t('dashboard.statOnline'), build: (s) => stat(t('dashboard.statOnline'), s.onlineDeviceCount, t('dashboard.statOnlineRate', { rate: Math.round((s.onlineRate || 0) * 100) }), 'ok', 'checkCircle', '#/devices?status=online') },
  { key: 'pending', label: t('dashboard.statPending'), build: (s) => stat(t('dashboard.statPending'), s.pendingDeviceCount, s.pendingDeviceCount > 0 ? t('dashboard.statPendingHint') : t('dashboard.statAllSynced'), s.pendingDeviceCount > 0 ? 'warn' : 'ok', 'clock', '#/devices?status=pending') },
  { key: 'error', label: t('dashboard.statError'), build: (s) => stat(t('dashboard.statError'), s.errorDeviceCount, s.errorDeviceCount > 0 ? t('dashboard.statErrorHint') : t('dashboard.statNoError'), s.errorDeviceCount > 0 ? 'danger' : 'ok', 'alert', '#/devices?status=error') },
  { key: 'profiles', label: t('dashboard.statProfiles'), build: (s) => stat(t('dashboard.statProfiles'), s.profileCount, t('dashboard.statRevision', { revision: s.revision }), 'info', 'profiles', '#/profiles') },
  // 「近 24h 新增」没有对应的筛选视图，就不做成可点的——点了跳不到具体东西的入口，比不给更让人困惑。
  { key: 'recent', label: t('dashboard.statRecent'), build: (s) => stat(t('dashboard.statRecent'), s.recentEnrollCount, t('dashboard.statRecentHint'), '', 'plusCircle', null) },
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
  { label: t('dashboard.actionNotify'), hint: t('dashboard.actionNotifyHint'), icon: 'bell', to: '#/devices?status=online' },
  { label: t('dashboard.actionDeploy'), hint: t('dashboard.actionDeployHint'), icon: 'send', to: '#/deploy' },
  { label: t('dashboard.actionRemote'), hint: t('dashboard.actionRemoteHint'), icon: 'monitor', to: '#/remote' },
  { label: t('dashboard.actionReminder'), hint: t('dashboard.actionReminderHint'), icon: 'clock', to: '#/reminders' },
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
        onClick: () => guard(t('dashboard.openCustomize'), () => openCustomize()),
      }, t('dashboard.customize'))),
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
  { key: 'server', label: t('dashboard.cardServer'), build: (ctx) => renderServerCard(ctx.info, ctx.stats) },
  { key: 'events', label: t('dashboard.cardEvents'), build: (ctx) => renderEventCard(ctx.stats) },
  { key: 'onboarding', label: t('dashboard.cardOnboarding'), build: (ctx) => renderOnboarding(ctx.stats, ctx.devices) },
  { key: 'devices', label: t('dashboard.cardDevices'), build: (ctx) => renderDevicePreview(ctx.devices) },
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
    title: t('dashboard.statDetail', { label }),
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
      h('div', state.message || t('dashboard.layoutSyncNotice')),
    );
  }

  function buildPanel() {
    return h('div',
      buildSection('dashboard.stats', STAT_DEFS, t('dashboard.panelStats'), t('dashboard.panelStatsDesc')),
      buildSection('dashboard.cards', CARD_DEFS, t('dashboard.panelCards'), t('dashboard.panelCardsDesc')),
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
    title: t('dashboard.customize'),
    body: container,
    confirmText: t('dashboard.done'),
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
        h('h3', t('dashboard.cardServer')),
        h('p.card-desc', info.serverName || t('dashboard.serverFallback')),
      ),
    ),
    h('div', { style: { display: 'grid', gap: '9px', fontSize: '13px' } },
      infoRow(t('dashboard.serverVersion'), info.version),
      infoRow(t('dashboard.protocolVersion'), info.protocolVersion),
      infoRow(t('dashboard.configRevision'), `#${info.revision}`),
      infoRow(t('dashboard.uptime'), formatDuration(info.uptimeSeconds)),
      infoRow(t('dashboard.startedAt'), formatDateTime(info.startedAt)),
      infoRow(t('dashboard.httpPort'), String(info.httpPort)),
      infoRow(t('dashboard.discoveryPort'), info.discoveryEnabled ? String(info.discoveryPort) : t('dashboard.disabled')),
      infoRow(t('dashboard.requireEnrollCode'), info.requiresEnrollCode ? t('dashboard.yes') : t('dashboard.no')),
      infoRow(t('dashboard.dataDirectory'), info.dataDirectory, true),
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
        h('h3', t('dashboard.cardEvents')),
        h('p.card-desc', t('dashboard.eventsDesc')),
      ),
    ),
    events.length === 0
      ? h('div.empty', { style: { padding: '30px 10px' } }, h('p', t('dashboard.noEvents')))
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
      title: t('dashboard.step1Title'),
      text: t('dashboard.step1Text'),
      action: { label: t('dashboard.step1Action'), hash: '#/profiles' },
    });
  }

  if (devices.length === 0) {
    steps.push({
      title: t('dashboard.step2Title'),
      text: t('dashboard.step2Text'),
      action: { label: t('dashboard.step2Action'), hash: '#/devices' },
    });
  }

  if (stats.profileCount > 0 && stats.deviceCount > 0 && stats.pendingDeviceCount === 0) {
    return null;
  }

  if (stats.profileCount > 0 && devices.length > 0) {
    steps.push({
      title: t('dashboard.step3Title'),
      text: t('dashboard.step3Text'),
      action: { label: t('dashboard.step3Action'), hash: '#/deploy' },
    });
  }

  if (steps.length === 0) return null;

  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', t('dashboard.cardOnboarding')),
        h('p.card-desc', t('dashboard.onboardingDesc')),
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
        h('h3', pending.length > 0 ? t('dashboard.pendingTitle', { count: pending.length }) : t('dashboard.cardDevices')),
        h('p.card-desc', pending.length > 0 ? t('dashboard.pendingDesc') : t('dashboard.allSyncedDesc')),
      ),
      h('a', { href: '#/devices', class: 'btn btn-sm', style: { textDecoration: 'none' } }, t('dashboard.viewAllDevices')),
    ),
    h('div.table-wrap',
      h('table.data',
        h('thead', h('tr',
          h('th', t('dashboard.colDevice')),
          h('th', t('dashboard.colGroup')),
          h('th', t('dashboard.colState')),
          h('th', t('dashboard.colRevision')),
          h('th', t('dashboard.colLastSeen')),
        )),
        h('tbody', ...preview.map((d) => h('tr',
          h('td',
            h('div.cell-main', d.name),
            h('div.cell-sub', d.machineName || d.id.slice(0, 8)),
          ),
          h('td', d.groupName || t('dashboard.ungrouped')),
          h('td', d.revoked
            ? h('span.badge.badge-danger', t('ui.stateRevoked'))
            : d.online
              ? h('span.badge.badge-ok', t('ui.stateOnline'))
              : h('span.badge.badge-neutral', t('ui.stateOffline'))),
          h('td', d.upToDate
            ? h('span.badge.badge-ok', t('dashboard.revisionLatest'))
            : h('span.badge.badge-warn', `${d.appliedRevision} / ${d.serverRevision}`)),
          h('td', { style: { color: 'var(--text-faint)', fontSize: '12px' } }, relativeTime(d.lastSeenAt)),
        ))),
      ),
    ),
  );
}