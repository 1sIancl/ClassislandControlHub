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

import { api, hasPermission } from '../core/api.js?v=147';
import { toastError, errorBlock } from '../core/errors.js?v=147';
import { hintForCode } from '../core/error-hints.js?v=147';
import { renderMarkdown } from '../core/markdown.js?v=147';
import {
  h, clear, toast, loadingBlock, field, select, modal, confirmDialog, emptyState,
  formatDateTime, relativeTime, hintIcon,
} from '../core/ui.js?v=147';

export const meta = {
  title: '插件管理',
  subtitle: '从 ClassIsland 插件市场缓存插件包，批量安装到教室终端',
};

const TABS = [
  { key: 'market', label: '插件市场' },
  { key: 'installed', label: '已安装' },
  { key: 'deployments', label: '下发记录' },
  { key: 'settings', label: '市场设置' },
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
    container.appendChild(errorBlock(err, { title: '读取插件市场失败' }));
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
        h('h3', '插件市场索引'),
        h('p.card-desc', last
          ? `上次同步：${formatDateTime(last)}（${relativeTime(last)}）。`
          : '还没有同步过索引——点右侧「同步索引」从 ClassIsland 官方插件市场取一次。'),
      ),
      h('div.card-actions',
        canWrite()
          ? h('button.btn.btn-sm', { type: 'button', onClick: () => syncNow() }, '同步索引')
          : null,
        h('button.btn.btn-sm', { type: 'button', onClick: () => refresh() }, '刷新页面数据'),
      ),
    ),

    error
      ? h('div.notice.notice-warn',
        h('span.notice-icon', '!'),
        h('div', `上次同步失败：${error}`))
      : null,
  );
}

async function syncNow() {
  try {
    const result = await api('/admin/plugin-market/sync', { method: 'POST' });
    if (result.ok) {
      toast('ok', '索引已同步', `共 ${result.count} 个插件。`);
    } else {
      toast('error', '同步失败', result.error ?? '未知原因');
    }
  } catch (err) {
    toastError(err, '同步失败');
  }

  await refresh();
}

async function refresh() {
  try {
    await loadAll();
  } catch (err) {
    toastError(err, '刷新失败');
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
    placeholder: '搜索插件名称 / ID / 作者',
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
        h('h3', '插件市场'),
        h('p.card-desc', `索引里共 ${state.items.length} 个插件`
          + (incompatible.length > 0
            ? `，其中 ${incompatible.length} 个要求的 API 高于教室端最低版本（${baseline.text}）`
            : '')
          + '；「缓存」是把 .cipx 取到 A 端本地，教室端安装时只从 A 端下载，不需要外网。'),
      ),
      h('div.card-actions',
        h('div', { style: { width: '220px' } }, search),
        incompatible.length > 0
          ? h('label.checkbox-field', compatBox,
            h('span', '只看兼容的',
              hintIcon('兼容＝插件要求的宿主 API 下限 ≤ 教室里**最低**的那台 ClassIsland 版本。'
                + '教室端装的时候还会再校验一次（版本不够会直接拒绝），这里只是提前把装不了的挑出来。')))
          : null,
      ),
    ),
    h('div#marketBody', renderMarketRows(items)),
    h('div.notice.notice-info',
      h('span.notice-icon', 'i'),
      h('div', '插件由第三方作者提供，装了它就在教室机上运行别人的代码；'
        + '只装确实需要的，并留意索引里的「要求 API」是否高于教室端的 ClassIsland 版本。')),
  );
}

function renderMarketRows(items) {
  if (state.items.length === 0) {
    return emptyState('database', '索引还是空的',
      '点上方「同步索引」从 ClassIsland 官方插件市场取一份；如果同步失败，多半是 A 端访问不了外网。');
  }

  if (items.length === 0) {
    return h('div.table-wrap', h('table.data',
      h('thead', h('tr', h('th', '插件'))),
      h('tbody', h('tr', h('td', '没有匹配的插件。'))),
    ));
  }

  return h('div.table-wrap', h('table.data',
    h('thead', h('tr',
      h('th', '插件'),
      h('th', '版本'),
      h('th', '要求 API'),
      h('th', '作者'),
      h('th', '下载量'),
      h('th', '缓存状态'),
      h('th', { style: { width: '210px' } }, '操作'),
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
              title: '查看详情与自述文件',
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
          title: '看元信息与作者的自述文件',
          onClick: () => openDetailDialog(item),
        }, '详情'),
        canWrite()
          ? h('button.btn.btn-sm', {
            type: 'button',
            title: '推荐 / 弃用标记，以及上架自检',
            onClick: () => openOpsDialog(item),
          }, '运营')
          : null,
        canWrite()
          ? h('button.btn.btn-sm', {
            type: 'button',
            title: item.cached ? '重新从镜像取一次这个包' : '把 .cipx 取到 A 端本地',
            onClick: (e) => cachePackage(item, e.currentTarget),
          }, item.cached ? '重新缓存' : '缓存')
          : null,
        canWrite()
          ? h('button.btn.btn-sm.btn-primary', {
            type: 'button',
            disabled: !item.cached,
            title: item.cached ? '下发安装到教室端' : '先缓存，再下发',
            onClick: () => openDeployDialog(item),
          }, '安装到教室')
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
    return { version: '2.1.1.1', text: '2.1.1.1（文档下限，暂无设备上报）' };
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
    ? { ok: false, reason: `要求 API ${item.apiVersion}，高于教室端最低版本 ${baseline.text}` }
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
    h('div', { style: { fontSize: '11.5px', color: 'var(--warn)', marginTop: '2px' } }, '不兼容本机教室端'),
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
    detail: outdated.map(([version, names]) => `${version}（${names.length} 台）`).join('、'),
  };
}

function renderUpgradeBadge(item) {
  const info = upgradeInfo(item);
  if (!info) {
    return null;
  }

  return h('div', { style: { marginTop: '2px' } },
    h('span.badge.badge-warn', { title: `已装 ${info.detail}` }, `可升级 ${info.devices} 台`));
}

function renderCacheCell(item) {
  if (!item.cached) {
    // 索引里这一版没缓存，但可能缓存过别的版本——这正说明「有新版本可用」。
    const others = (item.cachedVersions ?? []).filter((v) => v !== item.version);
    return h('div',
      h('span.badge.badge-neutral', '未缓存'),
      others.length > 0
        ? h('div', { style: { fontSize: '12px', color: 'var(--text-dim)', marginTop: '2px' } },
          `本地有：${others.join(' / ')}`)
        : null,
    );
  }

  const size = item.sizeBytes ? `${(item.sizeBytes / 1024).toFixed(0)} KB` : '';
  return h('div',
    h('span.badge.badge-ok', '已缓存'),
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
      title: ops.note || (recommended ? '本校推荐安装' : '本校已标记为不推荐'),
    }, recommended ? '推荐' : '已弃用'));
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
    { value: 'normal', label: '普通' },
    { value: 'recommended', label: '推荐' },
    { value: 'deprecated', label: '已弃用' },
  ], current.status || 'normal', () => {});
  const noteInput = h('input', {
    type: 'text',
    value: current.note || '',
    placeholder: '例如：1.4.8 实测可用 / 与本机宿主不兼容，别装',
  });
  const checkBox = h('div', { style: { marginTop: '12px' } });

  const runCheck = async () => {
    clear(checkBox);
    checkBox.appendChild(h('div', { style: { fontSize: '12px', color: 'var(--text-dim)' } }, '正在自检…'));
    try {
      const result = await api(`/admin/plugin-ops/${encodeURIComponent(item.id)}/submission`);
      clear(checkBox);
      checkBox.appendChild(h('div.config-section-title',
        result.ready ? '✓ 可以提交到官方索引' : '⚠ 还差几项，补齐再提交'));
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
      toastError(err, '自检失败');
    }
  };

  modal({
    title: `运营 · ${item.name}`,
    width: 'wide',
    body: h('div',
      field('运营状态', statusSelect, '「推荐」会显示在插件名下方，「已弃用」用来提醒同事别再装——比在群里说一句管用。'),
      field('备注', noteInput, '写给后来的自己：为什么这么标、上次验证是哪天。'),
      h('div.card-actions', { style: { marginTop: '12px' } },
        h('button.btn.btn-sm', { type: 'button', onClick: runCheck }, '上架自检')),
      checkBox),
    confirmText: '保存',
    onConfirm: async () => {
      try {
        await api(`/admin/plugin-ops/${encodeURIComponent(item.id)}`, {
          method: 'PUT',
          body: { status: statusSelect.value, note: noteInput.value },
        });
        await loadAll();
        paint();
        toast('ok', '运营标记已保存');
        return true;
      } catch (err) {
        toastError(err, '保存失败');
        return false;
      }
    },
  });
}

function openDetailDialog(item) {
  const readmeBox = h('div.md-holder',
    h('div', { style: { color: 'var(--text-dim)', fontSize: '12.5px' } }, '正在取作者的自述文件…'));

  const metaRow = (label, value) => h('div.plugin-meta-row',
    h('span.plugin-meta-key', label),
    h('span.plugin-meta-value', value));

  const compat = compatInfo(item, hostBaseline());
  const upgrade = upgradeInfo(item);

  const links = [];
  if (item.repositoryUrl) {
    links.push(h('a', {
      href: item.repositoryUrl, target: '_blank', rel: 'noopener noreferrer',
    }, '源码仓库'));
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
          h('div', `取不到自述文件（${err.message}）——多半是 A 端访问不了外网。`,
            item.repositoryUrl
              ? h('a', {
                href: item.repositoryUrl, target: '_blank', rel: 'noopener noreferrer',
                style: { marginLeft: '4px' },
              }, '去仓库看 →')
              : null)));
      });
  } else {
    clear(readmeBox);
    readmeBox.appendChild(h('div', { style: { color: 'var(--text-dim)', fontSize: '12.5px' } },
      '索引里这个插件没有登记自述文件。'));
  }

  modal({
    title: `${item.name} ${item.version || ''}`.trim(),
    width: 'xwide',
    body: h('div',
      h('div.plugin-meta',
        metaRow('插件 ID', h('code', item.id)),
        metaRow('版本', item.version || '—'),
        metaRow('要求 API', compat.ok
          ? (item.apiVersion || '—')
          : h('span', { style: { color: 'var(--warn)' } }, `${item.apiVersion}（${compat.reason}）`)),
        metaRow('作者', item.author || '—'),
        metaRow('下载量 / 星标', `${item.downloadCount ?? 0} / ${item.starsCount ?? 0}`),
        metaRow('支持平台',
          (item.supportedOSPlatforms ?? []).length > 0 ? item.supportedOSPlatforms.join(' / ') : '未声明'),
        metaRow('本地缓存', item.cached
          ? `已缓存 ${(item.sizeBytes / 1024).toFixed(0)} KB，SHA256 ${(item.sha256 ?? '').slice(0, 16)}…`
          : (item.cachedVersions ?? []).length > 0
            ? `未缓存当前版本（本地有 ${item.cachedVersions.join(' / ')}）`
            : '未缓存'),
        upgrade
          ? metaRow('教室端', h('span', { style: { color: 'var(--warn)' } },
            `有 ${upgrade.devices} 台装的不是当前版本（${upgrade.detail}）`))
          : null,
        links.length > 0 ? metaRow('相关链接', h('div', ...links)) : null,
      ),
      h('div.md-section',
        h('h4', '自述文件'),
        readmeBox),
    ),
    confirmText: '关闭',
    onConfirm: () => true,
  });
}

async function cachePackage(item, button) {
  if (button) {
    button.disabled = true;
    button.textContent = '缓存中…';
  }

  try {
    const result = await api('/admin/plugin-packages', {
      method: 'POST',
      body: { pluginId: item.id, version: item.version },
    });
    toast('ok', '已缓存', `${item.name} ${result.version}（SHA256 ${String(result.sha256).slice(0, 12)}…）`);
  } catch (err) {
    toastError(err, '缓存失败');
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
      h('span', `${device.deviceName}${device.online ? '' : '（离线）'}`
        + `${device.revoked ? '（已停用）' : ''}`),
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
    title: `下发安装 · ${item.name} ${item.version}`,
    width: 'wide',
    confirmText: '下发安装',
    body: h('div',
      h('div.notice.notice-warn',
        h('span.notice-icon', '!'),
        h('div', '安装会在教室机上放一份第三方代码，重启 ClassIsland 后才真正生效；'
          + '上课期间重启会让大屏短暂黑屏，建议避开上课时间。')),

      // 把"现在到底有几台能装"摆出来：默认范围选错时，用户点下去会毫无反应。
      h('p.card-desc', { style: { marginTop: '-4px' } },
        `当前在线 ${onlineCount} 台 / 共 ${deployableCount} 台可下发。`),
      nobodyOnline
        ? h('div.notice.notice-info',
          h('span.notice-icon', 'i'),
          h('div', '当前没有在线设备，已默认选中「全部设备」并勾选「包含离线设备」——'
            + '指令会排队，等教室上线后自动执行。'))
        : null,

      field('范围',
        h('div',
          h('label.checkbox-field', allOnline, h('span', `全部在线设备（${onlineCount} 台）`)),
          h('label.checkbox-field', allDevices, h('span', '全部设备（含离线，排队等它们上线）')),
          h('label.checkbox-field', picked, h('span', '指定设备（在下面勾选）')),
        )),

      field('包含离线设备', includeOffline, '离线设备会把指令排队，等它上线后再执行。'),
      field(h('span', '安装后顺带重启', hintIcon(
      'ClassIsland 只在**启动时**扫描待安装目录里的 .cipx——不重启，插件就只是「躺在那里」。'
      + '勾上＝下发完紧跟一条重启指令（会打断正在进行的课，建议排到课间）；'
      + '不勾＝你自己挑时间批量重启。')), restartAfter),
      field('指定设备', targetList),
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
        toast('warn', '没有选设备', '请至少勾选一台设备，或改成「全部在线设备」。');
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
          toast('warn', '没有可下发的设备', (result.skips ?? []).map((s) => s.reason).join('；')
            || '所有目标都被跳过了。');
        } else {
          toast('ok', '已下发',
            `${result.affected} 台设备开始安装${skipped > 0 ? `，跳过 ${skipped} 台` : ''}。`
            + '安装完成后需要重启才会生效。');
        }

        state.tab = 'deployments';
        state.expanded.add(result.deploymentId);
      } catch (err) {
        toastError(err, '下发失败');
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
        h('h3', '已安装的插件'),
        h('p.card-desc', `共 ${state.inventory.length} 台设备，其中 ${rows.length} 台装过第三方插件。`
          + '清单来自教室端上一次上报，可能不是最新的。'),
      ),
      h('div.card-actions',
        canWrite()
          ? h('button.btn.btn-sm', { type: 'button', onClick: () => refreshInventory() }, '让教室端重新上报')
          : null,
      ),
    ),

    h('div', { style: { fontSize: '13px', color: 'var(--text-dim)', margin: '4px 0 8px' } }, '按插件'),
    h('div.table-wrap', h('table.data',
      h('thead', h('tr',
        h('th', '插件'),
        h('th', '版本'),
        h('th', '已安装'),
        h('th', { style: { width: '190px' } }, '操作'),
      )),
      h('tbody', ...[...byPlugin.values()].map((entry) => h('tr',
        h('td', h('div', { style: { fontWeight: '600' } }, entry.name),
          h('div', { style: { fontSize: '12px', color: 'var(--text-dim)' } }, entry.id)),
        h('td', [...entry.versions].join(' / ')),
        h('td', `${entry.devices.length} 台${entry.disabled > 0 ? `（禁用 ${entry.disabled}）` : ''}`),
        h('td', h('div.row-actions',
          canWrite()
            ? h('button.btn.btn-sm', {
              type: 'button',
              onClick: () => deployQuick(entry.id, '', 'uninstall', `卸载 ${entry.name}`),
            }, '批量卸载')
            : null,
          canWrite()
            ? h('button.btn.btn-sm', {
              type: 'button',
              onClick: () => deployQuick(entry.id, '', 'disable', `禁用 ${entry.name}`),
            }, '批量禁用')
            : null,
          canWrite()
            ? h('button.btn.btn-sm', {
              type: 'button',
              onClick: () => deployQuick(entry.id, '', 'enable', `启用 ${entry.name}`),
            }, '批量启用')
            : null,
        )),
      ))),
    )),

    h('div', { style: { fontSize: '13px', color: 'var(--text-dim)', margin: '14px 0 8px' } }, '按设备'),
    h('div.table-wrap', h('table.data',
      h('thead', h('tr',
        h('th', '设备'),
        h('th', '状态'),
        h('th', 'ClassIsland'),
        h('th', '已装插件'),
        h('th', '上报时间'),
      )),
      h('tbody', ...state.inventory.map((device) => h('tr',
        h('td', device.deviceName),
        h('td', device.revoked
          ? h('span.badge.badge-danger', '已停用')
          : device.online
            ? h('span.badge.badge-ok', '在线')
            : h('span.badge.badge-neutral', '离线')),
        h('td', h('div',
          h('div', device.classislandVersion || '—'),
          h('div', { style: { fontSize: '12px', color: 'var(--text-dim)' } },
            device.pluginVersion ? `IslandHub 插件 ${device.pluginVersion}` : 'IslandHub 插件版本未知'),
        )),
        h('td', (device.plugins ?? []).length === 0
          ? h('span', { style: { color: 'var(--text-dim)' } }, '（无）')
          : h('div', ...(device.plugins ?? []).map((p) => h('span.badge.badge-info',
            { style: { marginRight: '4px' } },
            `${p.name} ${p.version}${p.isEnabled ? '' : '（禁用）'}`)))),
        h('td', device.reportedAt ? relativeTime(device.reportedAt) : '—'),
      ))),
    )),
  );
}

async function refreshInventory() {
  try {
    const count = await api('/admin/plugin-inventory/refresh', { method: 'POST' });
    toast('ok', '已请求上报', `已通知 ${count} 台在线设备；稍等几秒再刷新即可看到新清单。`);
  } catch (err) {
    toastError(err, '请求上报失败');
  }
}

async function deployQuick(pluginId, version, action, label) {
  const online = state.inventory.filter((d) => d.online && !d.revoked).length;
  const ok = await confirmDialog(label,
    `将向 ${online} 台在线设备下发「${label}」。`
    + (action === 'uninstall' ? '卸载后需要重启教室端才生效。' : '启停需要重启教室端才生效。'),
    '下发');
  if (!ok) {
    return;
  }

  try {
    const result = await api('/admin/plugin-deployments', {
      method: 'POST',
      body: { action, pluginId, version, deviceIds: [], includeOffline: false, restartAfter: false },
    });
    toast('ok', '已下发', `${result.affected} 台设备${result.skipped > 0 ? `，跳过 ${result.skipped} 台` : ''}。`);
    state.tab = 'deployments';
    await refresh();
    pollDeployments();
  } catch (err) {
    toastError(err, '下发失败');
  }
}

// ────────────────────────────── 下发记录 ──────────────────────────────

/** 指令状态 → 文案（与「远程管理」里的指令历史同一套词，别再自创一套）。 */
const STATUS_LABELS = {
  pending: '排队中',
  dispatched: '已派发',
  done: '已完成',
  failed: '失败',
  expired: '已过期',
  canceled: '已撤销',
};

function renderDeploymentsCard() {
  if (state.deployments.length === 0) {
    return h('div.card',
      h('div.card-head', h('div', h('h3', '下发记录'), h('p.card-desc', '还没有下发过插件。'))),
    );
  }

  return h('div.card',
    h('div.card-head',
      h('div',
        h('h3', '下发记录'),
        h('p.card-desc', '逐台结果直接来自教室端回报（安装成功的下一步通常是重启）。'),
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
          `成功 ${done} · 失败 ${failed} · 进行中 ${deployment.pending}`),
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
        }, expanded ? '收起' : '逐台明细'),
      ),
    ),
    h('div.progress', h('span', { style: { width: `${percent}%` } })),
    expanded
      ? h('div.table-wrap', h('table.data',
        h('thead', h('tr', h('th', '设备'), h('th', '状态'), h('th', '结果'))),
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
                  `怎么办：${hint}`)
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
    install: '安装',
    uninstall: '卸载',
    enable: '启用',
    disable: '禁用',
  }[action] ?? action;
}

// ────────────────────────────── 市场设置 ──────────────────────────────

function renderSettingsCard() {
  const indexInput = h('input', { type: 'text', value: state.settings.indexUrl ?? '' });
  const mirrorOptions = [{ value: '-', label: '自动（代理镜像优先，失败换下一个）' }];
  for (const name of Object.keys(state.settings.mirrors ?? {})) {
    mirrorOptions.push({ value: name, label: `${name}（${state.settings.mirrors[name]}）` });
  }

  const mirrorSelect = select(mirrorOptions, state.settings.preferredMirror || '-', null);

  return h('div.card',
    h('div.card-head',
      h('div',
        h('h3', '市场设置'),
        h('p.card-desc', '索引地址默认是 ClassIsland 官方的插件索引（一个 zip 包）；'
          + '镜像决定插件包从哪里下载，留「自动」时会优先走代理镜像。'),
      ),
    ),

    field('索引地址', indexInput, '含 {time} 占位符会自动替换成时间戳（用于穿透 CDN 缓存）；'
      + '留空不改，填 - 恢复官方默认。'),
    field(h('span', '首选镜像', hintIcon(
      '插件包与图标都从镜像根地址取（索引里给的几个：GitHub 直连、ghproxy、moeyy 等）。'
      + '选「自动」时按上面的顺序试探，某个镜像返回错误页会自动换下一个——所以坏包不会留在本地。'
      + '国内校园网通常保持「自动」即可。')), mirrorSelect),

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
              toast('ok', '已保存');
              await refresh();
            } catch (err) {
              toastError(err, '保存失败');
            }
          },
        }, '保存设置'),
      )
      : null,

    h('div.notice.notice-info',
      h('span.notice-icon', 'i'),
      h('div', 'A 端只在这里取一次索引与插件包；教室端安装时从 A 端下载，'
        + '所以教室机没有外网也能装插件。缓存目录与数据库同级（data/plugin-cache）。')),
  );
}
