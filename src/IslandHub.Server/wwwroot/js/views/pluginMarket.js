/**
 * 插件管理：#121「A 端代理缓存插件市场 + 批量下发到教室端」的管理界面。
 *
 * <para>为什么要有这一页：教室机往往没有外网（或者外网要过认证），
 * 让每台机器自己去 GitHub / 插件市场取插件包是不现实的。这一页让 A 端**替它们取**：
 * 先同步 ClassIsland 官方的插件索引，再把选中的 .cipx 缓存到本地，
 * 教室端安装时只从 A 端下载——A 端能上外网就够了。</para>
 *
 * <para>安装的落点是 ClassIsland 的待安装目录，**重启教室端才生效**；因此界面上
 * 每一步都写明「要不要重启」，卸载 / 启停也复用同一套下发与进度。</para>
 */

import { api, hasPermission } from '../core/api.js?v=153';
import { toastError, errorBlock } from '../core/errors.js?v=153';
import { hintForCode } from '../core/error-hints.js?v=153';
import { renderMarkdown } from '../core/markdown.js?v=153';
import {
  h, clear, toast, loadingBlock, field, select, modal, confirmDialog, emptyState,
  formatDateTime, relativeTime, hintIcon,
} from '../core/ui.js?v=153';
import { t } from './../core/i18n.js?v=153';

export const meta = {
  title: t('pluginMarket.title'),
  subtitle: t('pluginMarket.subtitle'),
};

const TABS = [
  { key: 'market', label: t('pluginMarket.tabMarket') },
  { key: 'installed', label: t('pluginMarket.tabInstalled') },
  { key: 'deployments', label: t('pluginMarket.tabDeployments') },
  { key: 'settings', label: t('pluginMarket.tabSettings') },
];

/** 页面状态（局部重画用）。 */
const state = {
  tab: 'market',
  items: [],
  settings: {},
  inventory: [],
  deployments: [],
  query: '',
  expanded: new Set(),
  // 默认只列与本校教室端**兼容**的插件：不兼容的装了也跑不起来（教室端会拒绝），
  // 平铺在列表里只会挤占视线。折叠数量与开关见 renderMarketCard。
  compatOnly: true,
  /** 运营标记（#104）：插件 id → { status, note, verifiedAt, updatedBy }。 */
  ops: new Map(),
};



/** 页面容器（局部刷新时重画它，避免整页 reload 丢滚动位置）。 */
let host = null;

const canWrite = () => hasPermission('plugins.write');

export async function render(container) {
  host = container;
  clear(container);
  container.appendChild(loadingBlock());

  try {
    await loadAll();
  } catch (err) {
    clear(container);
    container.appendChild(errorBlock(err, { title: t('pluginMarket.loadFailed') }));
    return;
  }

  paint();
}

async function loadAll() {
  const [market, inventory, deployments, ops] = await Promise.all([
    api('/admin/plugin-market'),
    api('/admin/plugin-inventory'),
    api('/admin/plugin-deployments'),
    // 运营标记单独一张表：市场索引每次同步整表替换，这些不能跟着一起被冲掉。
    api('/admin/plugin-ops').catch(() => []),
  ]);

  state.ops = new Map((Array.isArray(ops) ? ops : []).map((o) => [o.pluginId, o]));

  state.items = market.items ?? [];
  state.settings = market.settings ?? {};
  state.inventory = inventory.devices ?? [];
  state.deployments = deployments ?? [];
}

function paint() {
  clear(host);
  host.appendChild(h('div',
    renderHeaderCard(),
    state.tab === 'market'
      ? renderMarketCard()
      : state.tab === 'installed'
        ? renderInstalledCard()
        : state.tab === 'deployments'
          ? renderDeploymentsCard()
          : renderSettingsCard(),
  ));
}

// ────────────────────────────── 顶部：标签页与同步状态 ──────────────────────────────

function renderHeaderCard() {
  const last = state.settings.lastSyncAt;
  const error = state.settings.lastError;

  return h('div.card',
    h('div.tabs', ...TABS.map((t) => h('button', {
      class: `tab${state.tab === t.key ? ' active' : ''}`,
      type: 'button',
      onClick: () => { state.tab = t.key; paint(); },
    }, t.label))),

    h('div.card-head',
      h('div',
        h('h3', t('pluginMarket.indexTitle')),
        h('p.card-desc', last
          ? t('pluginMarket.lastSync', { time: formatDateTime(last), ago: relativeTime(last) })
          : t('pluginMarket.neverSynced')),
      ),
      h('div.card-actions',
        canWrite()
          ? h('button.btn.btn-sm', { type: 'button', onClick: () => syncNow() }, t('pluginMarket.syncIndex'))
          : null,
        h('button.btn.btn-sm', { type: 'button', onClick: () => refresh() }, t('pluginMarket.refreshHint')),
      ),
    ),

    error
      ? h('div.notice.notice-warn',
        h('span.notice-icon', '!'),
        h('div', t('pluginMarket.lastSyncFailed', { error })))
      : null,
  );
}

async function syncNow() {
  try {
    const result = await api('/admin/plugin-market/sync', { method: 'POST' });
    if (result.ok) {
      toast('ok', t('pluginMarket.indexSynced'), t('pluginMarket.indexSyncedCount', { count: result.count }));
    } else {
      toast('error', t('pluginMarket.syncFailed'), result.error ?? t('pluginMarket.unknownReason'));
    }
  } catch (err) {
    toastError(err, t('pluginMarket.syncFailed'));
  }

  await refresh();
}

async function refresh() {
  try {
    await loadAll();
  } catch (err) {
    toastError(err, t('pluginMarket.refreshFailed'));
    return;
  }

  paint();
}

// ────────────────────────────── 插件市场 ──────────────────────────────

function renderMarketCard() {
  const keyword = state.query.trim().toLowerCase();
  const baseline = hostBaseline();
  const incompatible = state.items.filter((i) => !compatInfo(i, baseline).ok);
  const visible = state.compatOnly ? state.items.filter((i) => compatInfo(i, baseline).ok) : state.items;
  const items = keyword
    ? visible.filter((i) => `${i.name} ${i.id} ${i.author}`.toLowerCase().includes(keyword))
    : visible;

  const repaintRows = () => {
    const body = document.getElementById('marketBody');
    if (body) {
      clear(body);
      body.appendChild(renderMarketRows(items));
    }
  };

  const search = h('input', {
    type: 'search',
    placeholder: t('pluginMarket.searchPlaceholder'),
    value: state.query,
    onInput: (e) => {
      state.query = e.target.value;
      repaintRows();
    },
  });

  const compatBox = h('input', { type: 'checkbox' });
  compatBox.checked = state.compatOnly;
  compatBox.addEventListener('change', () => {
    state.compatOnly = compatBox.checked;
    paint();   // 只重画，不重新请求（数据都在 state 里）
  });

  return h('div.card',
    h('div.card-head',
      h('div',
        h('h3', t('pluginMarket.tabMarket')),
        h('p.card-desc', t('pluginMarket.indexCount', { count: state.items.length })
          + (incompatible.length > 0
            ? t('pluginMarket.incompatibleCount', { count: incompatible.length, baseline: baseline.text })
            : '')
          + t('pluginMarket.cacheHint')),
      ),
      h('div.card-actions',
        h('div', { style: { width: '220px' } }, search),
        incompatible.length > 0
          ? h('label.checkbox-field', compatBox,
            h('span', t('pluginMarket.compatibleOnly'),
              hintIcon(t('pluginMarket.compatibleHint')
                + t('pluginMarket.compatibleHint2'))))
          : null,
      ),
    ),
    h('div#marketBody', renderMarketRows(items)),
    h('div.notice.notice-info',
      h('span.notice-icon', 'i'),
      h('div', t('pluginMarket.noticeLead')
        + t('pluginMarket.noticeTail'))),
  );
}

function renderMarketRows(items) {
  if (state.items.length === 0) {
    return emptyState('database', t('pluginMarket.indexEmpty'),
      t('pluginMarket.indexEmptyHint'));
  }

  if (items.length === 0) {
    return h('div.table-wrap', h('table.data',
      h('thead', h('tr', h('th', t('pluginMarket.plugin')))),
      h('tbody', h('tr', h('td', t('pluginMarket.noMatches')))),
    ));
  }

  return h('div.table-wrap', h('table.data',
    h('thead', h('tr',
      h('th', t('pluginMarket.plugin')),
      h('th', t('pluginMarket.version')),
      h('th', t('pluginMarket.requiredApi')),
      h('th', t('pluginMarket.author')),
      h('th', t('pluginMarket.downloads')),
      h('th', t('pluginMarket.cacheState')),
      h('th', { style: { width: '210px' } }, t('pluginMarket.actions')),
    )),
    h('tbody', ...items.map((item) => h('tr',
      h('td',
        // 图标 + 名称 + ID + 简介：图标让列表能一眼扫出「是不是那个插件」，
        // 简介则说明它到底干什么（索引里的说明往往就一两句话，正是列表最该露出的信息）。
        h('div.plugin-cell',
          renderPluginIcon(item),
          h('div.plugin-cell-body',
            // 名称可点开详情（role/tabindex 是为了键盘也能用——列表里的可点文字必须能 Tab 到）。
            h('button.plugin-name', {
              type: 'button',
              title: t('pluginMarket.detailHint'),
              onClick: () => openDetailDialog(item),
            }, item.name),
            h('div', { style: { fontSize: '12px', color: 'var(--text-dim)' } }, item.id),
            item.description
              ? h('div.plugin-desc', item.description)
              : null,
            renderOpsBadge(item),
          ),
        ),
      ),
      h('td', h('div',
        h('div', item.version || '—'),
        renderUpgradeBadge(item),
      )),
      h('td', renderApiCell(item)),
      h('td', item.author || '—'),
      h('td', item.downloadCount ? String(item.downloadCount) : '—'),
      h('td', renderCacheCell(item)),
      h('td', h('div.row-actions',
        h('button.btn.btn-sm', {
          type: 'button',
          title: t('pluginMarket.detailHint2'),
          onClick: () => openDetailDialog(item),
        }, t('pluginMarket.detail')),
        canWrite()
          ? h('button.btn.btn-sm', {
            type: 'button',
            title: t('pluginMarket.opsHint'),
            onClick: () => openOpsDialog(item),
          }, t('pluginMarket.ops'))
          : null,
        canWrite()
          ? h('button.btn.btn-sm', {
            type: 'button',
            title: item.cached ? t('pluginMarket.recacheHint') : t('pluginMarket.cacheHint2'),
            onClick: (e) => cachePackage(item, e.currentTarget),
          }, item.cached ? t('pluginMarket.recache') : t('pluginMarket.cache'))
          : null,
        canWrite()
          ? h('button.btn.btn-sm.btn-primary', {
            type: 'button',
            disabled: !item.cached,
            title: item.cached ? t('pluginMarket.installHint') : t('pluginMarket.installHint2'),
            onClick: () => openDeployDialog(item),
          }, t('pluginMarket.installToClassroom'))
          : null,
      )),
    ))),
  ));
}

/**
 * 插件图标（#122 后续）。
 * <para>A 端已经把索引里的图标代理到本地（`iconLocalUrl`）；拿不到就退化成首字母头像——
 * 与其留一张碎图，不如给一个稳定的视觉锚点。</para>
 */
function renderPluginIcon(item) {
  const letter = (item.name || item.id || '?').trim().charAt(0).toUpperCase() || '?';
  const fallback = h('span.plugin-icon.plugin-icon-letter', letter);

  if (!item.iconLocalUrl) {
    return fallback;
  }

  const img = h('img.plugin-icon', {
    src: item.iconLocalUrl,
    alt: '',
    loading: 'lazy',
    // 图标文件被清掉 / 换机器只搬了库时会 404：就地换成头像，不显示碎图。
    onError: () => img.replaceWith(fallback),
  });
  return img;
}

/** 解析 `1.2.3` / `1.2.3.4` 形式的版本号；解析不了返回 null（不猜）。 */
function parseVersion(text) {
  const match = /^(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?$/.exec((text ?? '').trim());
  return match ? [1, 2, 3, 4].map((i) => Number(match[i] ?? 0)) : null;
}

/** a < b ？两个版本号里只要有一个解析不了就返回 null（表示「无法比较」）。 */
function compareVersions(a, b) {
  const left = parseVersion(a);
  const right = parseVersion(b);
  if (!left || !right) {
    return null;
  }

  for (let i = 0; i < 4; i++) {
    if (left[i] !== right[i]) {
      return left[i] < right[i];
    }
  }

  return false;
}

/**
 * 兼容性基线（#121 后续）：取教室里**最低**的那台 ClassIsland 版本。
 * <para>为什么用最低而不是某一台：同一批插件要发给多台设备，只要有一台版本不够，
 * 那台就会装不上（教室端自己会拒绝）。宁可提前告诉管理员「这插件在你这儿是装不全的」。</para>
 * <para>没有任何设备上报过版本时，退回 README 里写明的真实下限 2.1.1.1。</para>
 */
function hostBaseline() {
  const versions = (state.inventory ?? [])
    .filter((d) => !d.revoked)
    .map((d) => d.classislandVersion)
    .filter((v) => parseVersion(v));

  if (versions.length === 0) {
    return { version: '2.1.1.1', text: t('pluginMarket.baselineFallback') };
  }

  const lowest = versions.reduce((min, v) => (compareVersions(v, min) === true ? v : min), versions[0]);
  return { version: lowest, text: lowest };
}

/** 某插件与基线是否兼容；不兼容给出人话原因。 */
function compatInfo(item, baseline) {
  if (!item.apiVersion) {
    return { ok: true };
  }

  const older = compareVersions(item.apiVersion, baseline.version);
  // null = 有一边不是标准版本号（比如预览版写了 -beta），不武断判定，放行。
  if (older === null) {
    return { ok: true };
  }

  return older
    ? { ok: false, reason: t('pluginMarket.requiresNewer', { required: item.apiVersion, baseline: baseline.text }) }
    : { ok: true };
}

/** 「要求 API」那一格：不兼容时标红并写明原因。 */
function renderApiCell(item) {
  const info = compatInfo(item, hostBaseline());
  if (info.ok) {
    return h('div', item.apiVersion || '—');
  }

  return h('div',
    h('span.badge.badge-warn', item.apiVersion),
    h('div', { style: { fontSize: '11.5px', color: 'var(--warn)', marginTop: '2px' } }, t('pluginMarket.incompatible')),
    h('div', { style: { fontSize: '11px', color: 'var(--text-dim)' } }, info.reason),
  );
}

/** 教室端已装的版本 → 装了它的设备名列表。没上报过就是空 Map。 */
function installedVersions(pluginId) {
  const versions = new Map();
  for (const device of state.inventory ?? []) {
    for (const plugin of device.plugins ?? []) {
      if (plugin.id !== pluginId) {
        continue;
      }

      const list = versions.get(plugin.version) ?? [];
      list.push(device.deviceName || device.deviceId);
      versions.set(plugin.version, list);
    }
  }

  return versions;
}

/**
 * 升级提示：教室端已装的版本里有**不是市场最新版**的，就值得标出来。
 * <para>数据全是页面上已有的（市场列表 + 已装清单），不必再请求一次接口。</para>
 */
function upgradeInfo(item) {
  const versions = installedVersions(item.id);
  if (versions.size === 0) {
    return null;
  }

  const outdated = [...versions.entries()].filter(([version]) => version !== item.version);
  if (outdated.length === 0) {
    return null;
  }

  return {
    devices: outdated.reduce((sum, [, names]) => sum + names.length, 0),
    detail: outdated.map(([version, names]) => t('pluginMarket.versionWithDevices', { version, count: names.length })).join('、'),
  };
}

function renderUpgradeBadge(item) {
  const info = upgradeInfo(item);
  if (!info) {
    return null;
  }

  return h('div', { style: { marginTop: '2px' } },
    h('span.badge.badge-warn', { title: t('pluginMarket.installedDetail', { detail: info.detail }) }, t('pluginMarket.upgradable', { count: info.devices })));
}

function renderCacheCell(item) {
  if (!item.cached) {
    // 索引里这一版没缓存，但可能缓存过别的版本——这正说明「有新版本可用」。
    const others = (item.cachedVersions ?? []).filter((v) => v !== item.version);
    return h('div',
      h('span.badge.badge-neutral', t('pluginMarket.notCached')),
      others.length > 0
        ? h('div', { style: { fontSize: '12px', color: 'var(--text-dim)', marginTop: '2px' } },
          t('pluginMarket.localVersions', { list: others.join(' / ') }))
        : null,
    );
  }

  const size = item.sizeBytes ? `${(item.sizeBytes / 1024).toFixed(0)} KB` : '';
  return h('div',
    h('span.badge.badge-ok', t('pluginMarket.cached')),
    h('div', { style: { fontSize: '12px', color: 'var(--text-dim)', marginTop: '2px' } },
      `${size}${item.cachedAt ? ` · ${relativeTime(item.cachedAt)}` : ''}`),
    h('div', { style: { fontSize: '11px', color: 'var(--text-dim)', fontFamily: 'var(--mono)' } },
      (item.sha256 ?? '').slice(0, 16) + '…'),
  );
}

/**
 * 插件详情（#121 后续）：元信息 + 作者的自述文件 + 与缓存 / 安装打通。
 *
 * <para>自述文件由 A 端代理（`readmeLocalUrl`，免鉴权）：索引里的地址指向 GitHub raw，
 * 管理端浏览器直连多半打不开。**首次点开会触发 A 端去取一次**（可能几秒），之后走本地缓存。</para>
 */
/** 运营标记的徽章（#104）：推荐 / 已弃用。没标记就什么都不显示。 */
function renderOpsBadge(item) {
  const ops = state.ops.get(item.id);
  if (!ops || ops.status === 'normal') return null;
  const recommended = ops.status === 'recommended';
  return h('div', { style: { marginTop: '4px' } },
    h('span', {
      class: recommended ? 'badge badge-ok' : 'badge badge-warn',
      title: ops.note || (recommended ? t('pluginMarket.recommendedHint') : t('pluginMarket.deprecatedHint')),
    }, recommended ? t('pluginMarket.recommended') : t('pluginMarket.deprecated')));
}

/**
 * 运营对话框（#104）：推荐 / 弃用标记 + 上架自检。
 *
 * <p>为什么把「上架自检」放在这里：它和"要不要推荐这个插件"是同一件事的两面——
 * 运营时既要判断"我们学校该不该用"，也要判断"要不要提交给官方市场"。</p>
 */
function openOpsDialog(item) {
  const current = state.ops.get(item.id) || { status: 'normal', note: '' };
  const statusSelect = select([
    { value: 'normal', label: t('pluginMarket.opsNormal') },
    { value: 'recommended', label: t('pluginMarket.recommended') },
    { value: 'deprecated', label: t('pluginMarket.deprecated') },
  ], current.status || 'normal', () => {});
  const noteInput = h('input', {
    type: 'text',
    value: current.note || '',
    placeholder: t('pluginMarket.opsNotePlaceholder'),
  });
  const checkBox = h('div', { style: { marginTop: '12px' } });

  const runCheck = async () => {
    clear(checkBox);
    checkBox.appendChild(h('div', { style: { fontSize: '12px', color: 'var(--text-dim)' } }, t('pluginMarket.checking')));
    try {
      const result = await api(`/admin/plugin-ops/${encodeURIComponent(item.id)}/submission`);
      clear(checkBox);
      checkBox.appendChild(h('div.config-section-title',
        result.ready ? t('pluginMarket.checkReady') : t('pluginMarket.checkMissing')));
      checkBox.appendChild(h('div.config-panel', ...result.items.map((row) => h('div.config-item',
        // 不通过的行直接标红：清单式的自检要能一眼扫到"还差哪几项"。
        h('span', { style: row.ok ? {} : { color: 'var(--danger)' } },
          `${row.ok ? '✓' : '✗'} ${row.name}`),
        h('span', {
          style: {
            marginLeft: 'auto', color: 'var(--text-dim)', fontSize: '12px', textAlign: 'right',
          },
        }, row.detail)))));
    } catch (err) {
      toastError(err, t('pluginMarket.checkFailed'));
    }
  };

  modal({
    title: t('pluginMarket.opsTitle', { name: item.name }),
    width: 'wide',
    body: h('div',
      field(t('pluginMarket.opsStatus'), statusSelect, t('pluginMarket.opsStatusHint')),
      field(t('pluginMarket.opsNote'), noteInput, t('pluginMarket.opsNoteHint')),
      h('div.card-actions', { style: { marginTop: '12px' } },
        h('button.btn.btn-sm', { type: 'button', onClick: runCheck }, t('pluginMarket.submissionCheck'))),
      checkBox),
    confirmText: t('pluginMarket.save'),
    onConfirm: async () => {
      try {
        await api(`/admin/plugin-ops/${encodeURIComponent(item.id)}`, {
          method: 'PUT',
          body: { status: statusSelect.value, note: noteInput.value },
        });
        await loadAll();
        paint();
        toast('ok', t('pluginMarket.opsSaved'));
        return true;
      } catch (err) {
        toastError(err, t('pluginMarket.saveFailed'));
        return false;
      }
    },
  });
}

function openDetailDialog(item) {
  const readmeBox = h('div.md-holder',
    h('div', { style: { color: 'var(--text-dim)', fontSize: '12.5px' } }, t('pluginMarket.readmeLoading')));

  const metaRow = (label, value) => h('div.plugin-meta-row',
    h('span.plugin-meta-key', label),
    h('span.plugin-meta-value', value));

  const compat = compatInfo(item, hostBaseline());
  const upgrade = upgradeInfo(item);

  const links = [];
  if (item.repositoryUrl) {
    links.push(h('a', {
      href: item.repositoryUrl, target: '_blank', rel: 'noopener noreferrer',
    }, t('pluginMarket.repo')));
  }

  if (item.readmeLocalUrl) {
    // 自述文件是 Markdown 原文，交给 core/markdown.js 渲染（先转义再套结构，第三方内容不会变 XSS）。
    fetch(item.readmeLocalUrl)
      .then((res) => (res.ok ? res.text() : Promise.reject(new Error(`HTTP ${res.status}`))))
      .then((text) => {
        clear(readmeBox);
        readmeBox.appendChild(renderMarkdown(text));
      })
      .catch((err) => {
        clear(readmeBox);
        readmeBox.appendChild(h('div.notice.notice-warn',
          h('span.notice-icon', '!'),
          h('div', t('pluginMarket.readmeFailed', { message: err.message }),
            item.repositoryUrl
              ? h('a', {
                href: item.repositoryUrl, target: '_blank', rel: 'noopener noreferrer',
                style: { marginLeft: '4px' },
              }, t('pluginMarket.gotoRepo'))
              : null)));
      });
  } else {
    clear(readmeBox);
    readmeBox.appendChild(h('div', { style: { color: 'var(--text-dim)', fontSize: '12.5px' } },
      t('pluginMarket.noReadme')));
  }

  modal({
    title: `${item.name} ${item.version || ''}`.trim(),
    width: 'xwide',
    body: h('div',
      h('div.plugin-meta',
        metaRow(t('pluginMarket.pluginId'), h('code', item.id)),
        metaRow(t('pluginMarket.version'), item.version || '—'),
        metaRow(t('pluginMarket.requiredApi'), compat.ok
          ? (item.apiVersion || '—')
          : h('span', { style: { color: 'var(--warn)' } }, `${item.apiVersion}（${compat.reason}）`)),
        metaRow(t('pluginMarket.author'), item.author || '—'),
        metaRow(t('pluginMarket.downloadsStars'), `${item.downloadCount ?? 0} / ${item.starsCount ?? 0}`),
        metaRow(t('pluginMarket.platforms'),
          (item.supportedOSPlatforms ?? []).length > 0 ? item.supportedOSPlatforms.join(' / ') : t('pluginMarket.notDeclared')),
        metaRow(t('pluginMarket.localCache'), item.cached
          ? t('pluginMarket.cachedDetail', { size: (item.sizeBytes / 1024).toFixed(0), hash: (item.sha256 ?? '').slice(0, 16) })
          : (item.cachedVersions ?? []).length > 0
            ? t('pluginMarket.notCachedCurrent', { list: item.cachedVersions.join(' / ') })
            : t('pluginMarket.notCached')),
        upgrade
          ? metaRow(t('pluginMarket.classrooms'), h('span', { style: { color: 'var(--warn)' } },
            t('pluginMarket.upgradeDetail', { count: upgrade.devices, detail: upgrade.detail })))
          : null,
        links.length > 0 ? metaRow(t('pluginMarket.links'), h('div', ...links)) : null,
      ),
      h('div.md-section',
        h('h4', t('pluginMarket.readme')),
        readmeBox),
    ),
    confirmText: t('pluginMarket.close'),
    onConfirm: () => true,
  });
}

async function cachePackage(item, button) {
  if (button) {
    button.disabled = true;
    button.textContent = t('pluginMarket.caching');
  }

  try {
    const result = await api('/admin/plugin-packages', {
      method: 'POST',
      body: { pluginId: item.id, version: item.version },
    });
    toast('ok', t('pluginMarket.cached'), `${item.name} ${result.version}（SHA256 ${String(result.sha256).slice(0, 12)}…）`);
  } catch (err) {
    toastError(err, t('pluginMarket.cacheFailed'));
  }

  await refresh();
}

// ────────────────────────────── 下发 ──────────────────────────────

function openDeployDialog(item) {
  const deviceBoxes = state.inventory.map((device) => ({
    device,
    box: h('input', {
      type: 'checkbox',
      checked: false,
      dataset: { deviceId: device.deviceId },
      disabled: device.revoked,
    }),
  }));

  const allOnline = h('input', { type: 'radio', name: 'scope', value: 'online', checked: true });
  const allDevices = h('input', { type: 'radio', name: 'scope', value: 'all' });
  const picked = h('input', { type: 'radio', name: 'scope', value: 'picked' });
  const includeOffline = h('input', { type: 'checkbox' });
  const restartAfter = h('input', { type: 'checkbox', checked: true });

  // 默认范围要看**当前实际情况**：一台都不在线时，「全部在线设备」等于「一台都不装」，
  // 用户点完只看到一句"没有可下发的设备"，会以为功能坏了（实测就是这样）。
  const deployableCount = state.inventory.filter((device) => !device.revoked).length;
  const onlineCount = state.inventory.filter((device) => device.online && !device.revoked).length;
  const nobodyOnline = onlineCount === 0 && deployableCount > 0;
  if (nobodyOnline) {
    allOnline.checked = false;
    allDevices.checked = true;
    includeOffline.checked = true;
  }

  const targetList = h('div.checkbox-list',
    ...deviceBoxes.map(({ device, box }) => h('label.checkbox-field',
      box,
      h('span', `${device.deviceName}`
        + `${device.online ? '' : t('pluginMarket.offlineSuffix')}`
        + `${device.revoked ? t('pluginMarket.revokedSuffix') : ''}`),
    )),
  );

  // 「指定设备」才允许勾选，避免管理员以为勾了就等于选中了。
  const syncPickedState = () => {
    const disabled = !picked.checked;
    for (const { box } of deviceBoxes) {
      box.disabled = disabled || box.dataset.disabled === 'true';
    }
    targetList.style.opacity = disabled ? '0.55' : '1';
  };
  picked.addEventListener('change', syncPickedState);
  allOnline.addEventListener('change', syncPickedState);
  allDevices.addEventListener('change', syncPickedState);
  syncPickedState();

  modal({
    title: t('pluginMarket.deployTitle', { name: item.name, version: item.version }),
    width: 'wide',
    confirmText: t('pluginMarket.deployInstall'),
    body: h('div',
      h('div.notice.notice-warn',
        h('span.notice-icon', '!'),
        h('div', t('pluginMarket.deployWarnLead')
          + t('pluginMarket.deployWarnTail'))),

      // 把"现在到底有几台能装"摆出来：默认范围选错时，用户点下去会毫无反应。
      h('p.card-desc', { style: { marginTop: '-4px' } },
        t('pluginMarket.deployScopeCount', { online: onlineCount, total: deployableCount })),
      nobodyOnline
        ? h('div.notice.notice-info',
          h('span.notice-icon', 'i'),
          h('div', t('pluginMarket.deployNobodyOnlineLead')
            + t('pluginMarket.deployNobodyOnlineTail')))
        : null,

      field(t('pluginMarket.scope'),
        h('div',
          h('label.checkbox-field', allOnline, h('span', t('pluginMarket.scopeOnline', { count: onlineCount }))),
          h('label.checkbox-field', allDevices, h('span', t('pluginMarket.scopeAll'))),
          h('label.checkbox-field', picked, h('span', t('pluginMarket.scopePicked'))),
        )),

      field(t('pluginMarket.includeOffline'), includeOffline, t('pluginMarket.includeOfflineHint')),
      field(h('span', t('pluginMarket.restartAfter'), hintIcon(
      t('pluginMarket.restartAfterHint')
      + t('pluginMarket.restartAfterHint2')
      + t('pluginMarket.restartAfterHint3'))), restartAfter),
      field(t('pluginMarket.pickDevices'), targetList),
    ),
    onConfirm: async () => {
      let scope = 'online';
      if (allDevices.checked) {
        scope = 'all';
      } else if (picked.checked) {
        scope = 'picked';
      }

      const deviceIds = scope === 'picked'
        ? deviceBoxes.filter(({ box }) => box.checked).map(({ device }) => device.deviceId)
        : [];

      if (scope === 'picked' && deviceIds.length === 0) {
        toast('warn', t('pluginMarket.noDevicePicked'), t('pluginMarket.noDevicePickedHint'));
        return false;
      }

      try {
        const result = await api('/admin/plugin-deployments', {
          method: 'POST',
          body: {
            action: 'install',
            pluginId: item.id,
            version: item.version,
            deviceIds,
            includeOffline: includeOffline.checked,
            restartAfter: restartAfter.checked,
          },
        });

        const skipped = result.skipped ?? 0;
        if (result.affected === 0) {
          toast('warn', t('pluginMarket.noTargets'), (result.skips ?? []).map((s) => s.reason).join('；')
            || t('pluginMarket.allSkipped'));
        } else {
          toast('ok', t('pluginMarket.sent'),
            t('pluginMarket.installStarted', {
              count: result.affected,
              skipped: skipped > 0 ? t('pluginMarket.skippedSuffix', { count: skipped }) : '',
            }),
            t('pluginMarket.installStartedHint'));
        }

        state.tab = 'deployments';
        state.expanded.add(result.deploymentId);
      } catch (err) {
        toastError(err, t('pluginMarket.deployFailed'));
        return false;
      }

      await refresh();
      pollDeployments();
      return true;
    },
  });
}

/** 下发后短轮询几次：教室端回报通常几秒内到达，不必让管理员手动刷新。 */
function pollDeployments(times = 4) {
  if (times <= 0 || state.tab !== 'deployments') {
    return;
  }

  setTimeout(async () => {
    try {
      state.deployments = await api('/admin/plugin-deployments');
      // 只有还停在下发记录这一页时才重画；管理员已经切走的话就别抢走他的界面。
      if (host && host.querySelector('#deploymentBody')) {
        paint();
      }
    } catch {
      // 轮询失败就算了：界面上还有手动刷新。
    }

    pollDeployments(times - 1);
  }, 2500);
}

// ────────────────────────────── 已安装清单 ──────────────────────────────

function renderInstalledCard() {
  const rows = state.inventory.filter((d) => d.plugins?.length > 0);
  const byPlugin = new Map();
  for (const device of state.inventory) {
    for (const plugin of device.plugins ?? []) {
      const key = plugin.id;
      if (!byPlugin.has(key)) {
        byPlugin.set(key, { id: key, name: plugin.name, versions: new Set(), devices: [], disabled: 0 });
      }

      const entry = byPlugin.get(key);
      entry.versions.add(plugin.version);
      entry.devices.push(device.deviceName);
      if (!plugin.isEnabled) {
        entry.disabled++;
      }
    }
  }

  return h('div.card',
    h('div.card-head',
      h('div',
        h('h3', t('pluginMarket.installedTitle')),
        h('p.card-desc', t('pluginMarket.installedSummary', { total: state.inventory.length, withPlugins: rows.length })
          + t('pluginMarket.inventoryStale')),
      ),
      h('div.card-actions',
        canWrite()
          ? h('button.btn.btn-sm', { type: 'button', onClick: () => refreshInventory() }, t('pluginMarket.refreshInventory'))
          : null,
      ),
    ),

    h('div', { style: { fontSize: '13px', color: 'var(--text-dim)', margin: '4px 0 8px' } }, t('pluginMarket.byPlugin')),
    h('div.table-wrap', h('table.data',
      h('thead', h('tr',
        h('th', t('pluginMarket.plugin')),
        h('th', t('pluginMarket.version')),
        h('th', t('pluginMarket.tabInstalled')),
        h('th', { style: { width: '190px' } }, t('pluginMarket.actions')),
      )),
      h('tbody', ...[...byPlugin.values()].map((entry) => h('tr',
        h('td', h('div', { style: { fontWeight: '600' } }, entry.name),
          h('div', { style: { fontSize: '12px', color: 'var(--text-dim)' } }, entry.id)),
        h('td', [...entry.versions].join(' / ')),
        h('td', t('pluginMarket.installedDeviceCount', {
          count: entry.devices.length,
          disabled: entry.disabled > 0 ? t('pluginMarket.disabledSuffix', { count: entry.disabled }) : '',
        })),
        h('td', h('div.row-actions',
          canWrite()
            ? h('button.btn.btn-sm', {
              type: 'button',
              onClick: () => deployQuick(entry.id, '', 'uninstall', t('pluginMarket.uninstallPlugin', { name: entry.name })),
            }, t('pluginMarket.bulkUninstall'))
            : null,
          canWrite()
            ? h('button.btn.btn-sm', {
              type: 'button',
              onClick: () => deployQuick(entry.id, '', 'disable', t('pluginMarket.disablePlugin', { name: entry.name })),
            }, t('pluginMarket.bulkDisable'))
            : null,
          canWrite()
            ? h('button.btn.btn-sm', {
              type: 'button',
              onClick: () => deployQuick(entry.id, '', 'enable', t('pluginMarket.enablePlugin', { name: entry.name })),
            }, t('pluginMarket.bulkEnable'))
            : null,
        )),
      ))),
    )),

    h('div', { style: { fontSize: '13px', color: 'var(--text-dim)', margin: '14px 0 8px' } }, t('pluginMarket.byDevice')),
    h('div.table-wrap', h('table.data',
      h('thead', h('tr',
        h('th', t('pluginMarket.device')),
        h('th', t('pluginMarket.state')),
        h('th', 'ClassIsland'),
        h('th', t('pluginMarket.pluginsInstalled')),
        h('th', t('pluginMarket.reportedAt')),
      )),
      h('tbody', ...state.inventory.map((device) => h('tr',
        h('td', device.deviceName),
        h('td', device.revoked
          ? h('span.badge.badge-danger', t('pluginMarket.revoked'))
          : device.online
            ? h('span.badge.badge-ok', t('pluginMarket.online'))
            : h('span.badge.badge-neutral', t('pluginMarket.offline'))),
        h('td', h('div',
          h('div', device.classislandVersion || '—'),
          h('div', { style: { fontSize: '12px', color: 'var(--text-dim)' } },
            device.pluginVersion ? t('pluginMarket.pluginVersion', { version: device.pluginVersion }) : t('pluginMarket.pluginVersionUnknown')),
        )),
        h('td', (device.plugins ?? []).length === 0
          ? h('span', { style: { color: 'var(--text-dim)' } }, t('pluginMarket.none'))
          : h('div', ...(device.plugins ?? []).map((p) => h('span.badge.badge-info',
            { style: { marginRight: '4px' } },
            t('pluginMarket.pluginBadge', {
              name: p.name,
              version: p.version,
              disabled: p.isEnabled ? '' : t('pluginMarket.disabledBadge'),
            }))))),
        h('td', device.reportedAt ? relativeTime(device.reportedAt) : '—'),
      ))),
    )),
  );
}

async function refreshInventory() {
  try {
    const count = await api('/admin/plugin-inventory/refresh', { method: 'POST' });
    toast('ok', t('pluginMarket.reportRequested'), t('pluginMarket.reportRequestedHint', { count }));
  } catch (err) {
    toastError(err, t('pluginMarket.reportRequestFailed'));
  }
}

async function deployQuick(pluginId, version, action, label) {
  const online = state.inventory.filter((d) => d.online && !d.revoked).length;
  const ok = await confirmDialog(label,
    t('pluginMarket.bulkConfirm', { count: online, action: label })
    + (action === 'uninstall' ? t('pluginMarket.uninstallRestartHint') : t('pluginMarket.toggleRestartHint')),
    t('pluginMarket.send'));
  if (!ok) {
    return;
  }

  try {
    const result = await api('/admin/plugin-deployments', {
      method: 'POST',
      body: { action, pluginId, version, deviceIds: [], includeOffline: false, restartAfter: false },
    });
    toast('ok', t('pluginMarket.sent'), t('pluginMarket.pluginActionSent', {
      count: result.affected,
      skipped: result.skipped > 0 ? t('pluginMarket.skippedSuffix', { count: result.skipped }) : '',
    }));
    state.tab = 'deployments';
    await refresh();
    pollDeployments();
  } catch (err) {
    toastError(err, t('pluginMarket.deployFailed'));
  }
}

// ────────────────────────────── 下发记录 ──────────────────────────────

/** 指令状态 → 文案（与「远程管理」里的指令历史同一套词，别再自创一套）。 */
const STATUS_LABELS = {
  pending: t('pluginMarket.stateQueued'),
  dispatched: t('pluginMarket.stateDispatched'),
  done: t('pluginMarket.stateDone'),
  failed: t('pluginMarket.stateFailed'),
  expired: t('pluginMarket.stateExpired'),
  canceled: t('pluginMarket.stateCancelled'),
};

function renderDeploymentsCard() {
  if (state.deployments.length === 0) {
    return h('div.card',
      h('div.card-head', h('div', h('h3', t('pluginMarket.tabDeployments')), h('p.card-desc', t('pluginMarket.noDeployments')))),
    );
  }

  return h('div.card',
    h('div.card-head',
      h('div',
        h('h3', t('pluginMarket.tabDeployments')),
        h('p.card-desc', t('pluginMarket.deploymentsHint')),
      ),
    ),
    h('div#deploymentBody', ...state.deployments.map((d) => renderDeployment(d))),
  );
}

function renderDeployment(deployment) {
  const done = deployment.done ?? 0;
  const failed = deployment.failed ?? 0;
  const total = deployment.total || 1;
  const percent = Math.round(((done + failed) / total) * 100);
  const expanded = state.expanded.has(deployment.id);

  return h('div.deploy-row',
    h('div.deploy-head',
      h('div',
        h('div', { style: { fontWeight: '600' } },
          `${deployment.pluginName} ${deployment.version} · ${actionLabel(deployment.action)}`),
        h('div', { style: { fontSize: '12px', color: 'var(--text-dim)' } },
          `${formatDateTime(deployment.createdAt)} · ${deployment.createdBy} · ${deployment.targetSummary}`),
      ),
      h('div.card-actions',
        h('span', { style: { fontSize: '12.5px', color: 'var(--text-dim)' } },
          t('pluginMarket.deploySummary', { done, failed, pending: deployment.pending })),
        h('button.btn.btn-sm', {
          type: 'button',
          onClick: () => {
            if (expanded) {
              state.expanded.delete(deployment.id);
            } else {
              state.expanded.add(deployment.id);
            }

            paint();
          },
        }, expanded ? t('pluginMarket.collapse') : t('pluginMarket.perDevice')),
      ),
    ),
    h('div.progress', h('span', { style: { width: `${percent}%` } })),
    expanded
      ? h('div.table-wrap', h('table.data',
        h('thead', h('tr', h('th', t('pluginMarket.device')), h('th', t('pluginMarket.state')), h('th', t('pluginMarket.result')))),
        h('tbody', ...(deployment.items ?? []).map((item) => {
          const hint = item.errorCode ? hintForCode(item.errorCode) : '';
          return h('tr',
            h('td', item.deviceName),
            h('td', h('span', {
              class: item.status === 'done'
                ? 'badge badge-ok'
                : item.status === 'failed' || item.status === 'expired'
                  ? 'badge badge-danger'
                  : 'badge badge-neutral',
            }, STATUS_LABELS[item.status] ?? item.status)),
            h('td',
              h('div', { style: { fontSize: '12.5px' } }, item.output || '—'),
              hint
                ? h('div', { style: { fontSize: '12px', color: 'var(--text-dim)', marginTop: '2px' } },
                  t('pluginMarket.howToFix', { hint }))
                : null,
            ),
          );
        })),
      ))
      : null,
  );
}

function actionLabel(action) {
  return {
    install: t('pluginMarket.actionInstall'),
    uninstall: t('pluginMarket.actionUninstall'),
    enable: t('pluginMarket.actionEnable'),
    disable: t('pluginMarket.actionDisable'),
  }[action] ?? action;
}

// ────────────────────────────── 市场设置 ──────────────────────────────

function renderSettingsCard() {
  const indexInput = h('input', { type: 'text', value: state.settings.indexUrl ?? '' });
  const mirrorOptions = [{ value: '-', label: t('pluginMarket.mirrorAuto') }];
  for (const name of Object.keys(state.settings.mirrors ?? {})) {
    mirrorOptions.push({ value: name, label: `${name}（${state.settings.mirrors[name]}）` });
  }

  const mirrorSelect = select(mirrorOptions, state.settings.preferredMirror || '-', null);

  return h('div.card',
    h('div.card-head',
      h('div',
        h('h3', t('pluginMarket.tabSettings')),
        h('p.card-desc', t('pluginMarket.settingsLead')
          + t('pluginMarket.settingsMirrorHint')),
      ),
    ),

    field(t('pluginMarket.indexUrl'), indexInput, t('pluginMarket.indexUrlHint')
      + t('pluginMarket.indexUrlHint2')),
    field(h('span', t('pluginMarket.preferredMirror'), hintIcon(
      t('pluginMarket.mirrorHint')
      + t('pluginMarket.mirrorHint2')
      + t('pluginMarket.mirrorHint3'))), mirrorSelect),

    canWrite()
      ? h('div.card-actions',
        h('button.btn.btn-primary', {
          type: 'button',
          onClick: async () => {
            try {
              await api('/admin/plugin-market/settings', {
                method: 'PUT',
                body: { indexUrl: indexInput.value, preferredMirror: mirrorSelect.value },
              });
              toast('ok', t('pluginMarket.saved'));
              await refresh();
            } catch (err) {
              toastError(err, t('pluginMarket.saveFailed'));
            }
          },
        }, t('pluginMarket.saveSettings')),
      )
      : null,

    h('div.notice.notice-info',
      h('span.notice-icon', 'i'),
      h('div', t('pluginMarket.offlineLead')
        + t('pluginMarket.offlineTail'))),
  );
}
