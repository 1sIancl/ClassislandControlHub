/**
 * 设备管理视图：按「楼栋 → 楼层 → 教室」三级结构管理教室终端。
 * 顶层分组是楼栋，下层分组是楼层，每台设备就是一间教室；支持拖拽调整归属，
 * 并可切换到列表视图查看完整状态明细。注册码管理一并放在本页。
 */

import { api, fetchBlob } from '../core/api.js?v=162';
import { toastError, errorBlock } from '../core/errors.js?v=162';
import {
  h, clear, formatDateTime, relativeTime, toast, loadingBlock, skeletonRows,
  modal, confirmDialog, deviceStateBadge, syncBadge,
  emptyState, field, select, copyText, append, undoBar,
} from '../core/ui.js?v=162';
import { showContextMenu } from '../core/contextmenu.js?v=162';
import { getLayout, saveLayout } from '../core/prefs.js?v=162';
import { auditTimelineSection } from '../core/audit-timeline.js?v=162';
// 富内容预览（#11）：通知正文支持少量 Markdown，这里给它一个实时预览。
import { createRichPreview } from '../core/markdown.js?v=162';
import { selectionSummary } from '../core/batch-summary.js?v=162';
import { t } from './../core/i18n.js?v=162';

export const meta = {
  title: t('devices.title'),
  subtitle: t('devices.subtitle'),
};

/** 分组标识色调色板，与服务端 DeviceEndpoints.GroupColors 保持一致。 */
const GROUP_COLORS = ['blue', 'cyan', 'green', 'lime', 'amber', 'orange', 'red', 'violet', 'pink', 'slate'];

/** 未分组区块的筛选值。 */
const NO_BUILDING = '__none__';

let cache = { devices: [], groups: [], profiles: [], codes: [] };
let filter = { keyword: '', groupId: '', state: '', buildingId: '', tagId: '' };
let view = 'groups';

/** 拖拽中的设备 ID（HTML5 DnD 的 dataTransfer 在 dragover 阶段读不到数据，用模块变量兜底）。 */
let draggedDeviceId = null;

/** 列表视图里勾选的设备 ID（用于批量下发 / 通知 / 重启）。 */
const selection = new Set();

// ── 设备表格列定义（支持显隐配置，操作列固定） ──
const COLUMN_DEFS = [
  {
    key: 'name', label: t('devices.colRoom'),
    cell: (d) => h('td',
      h('div.cell-main', d.name),
      h('div.cell-sub', [d.machineName, d.ipAddress].filter(Boolean).join(' · ') || d.id.slice(0, 8)),
      d.remark ? h('div.cell-sub', { style: { color: 'var(--text-faint)' } }, t('devices.remarkLine', { remark: d.remark })) : null,
    ),
  },
  {
    key: 'group', label: t('devices.colLocation'),
    cell: (d) => h('td', groupPathBadge(d)),
  },
  {
    key: 'tags', label: t('devices.colTags'),
    cell: (d) => h('td', (d.tags || []).length
      ? h('div', { style: { display: 'flex', flexWrap: 'wrap', gap: '4px' } }, ...d.tags.map(tagBadge))
      : h('span', { style: { color: 'var(--text-faint)' } }, '—')),
  },
  {
    key: 'state', label: t('devices.colState'),
    cell: (d) => h('td',
      deviceStateBadge(d),
      d.lastError ? h('div', {
        title: d.lastError,
        style: { fontSize: '11px', color: 'var(--danger)', marginTop: '3px', maxWidth: '190px', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
      }, d.lastError) : (d.offlineReason ? h('div', {
        // 离线原因诊断：把「为什么看不到这台设备」直接摆在状态下方，鼠标悬停可看完整说明。
        title: d.offlineReason,
        style: { fontSize: '11px', color: 'var(--text-faint)', marginTop: '3px', maxWidth: '190px', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
      }, d.offlineReason) : null),
    ),
  },
  {
    key: 'sync', label: t('devices.colSync'),
    cell: (d) => h('td', syncBadge(d),
      h('div', { style: { fontSize: '11px', color: 'var(--text-faint)', marginTop: '3px' } },
        t('devices.profileRevision', { revision: d.serverRevision }))),
  },
  {
    key: 'plan', label: t('devices.colTimetable'),
    cell: (d) => h('td', d.currentClassPlanName || h('span', { style: { color: 'var(--text-faint)' } }, '—')),
  },
  {
    key: 'version', label: t('devices.colVersion'),
    cell: (d) => h('td',
      h('div', { style: { fontSize: '12px' } }, `CI ${d.classIslandVersion || '—'}`),
      h('div', { style: { fontSize: '11px', color: 'var(--text-faint)' } }, t('devices.pluginVersionLine', { version: d.pluginVersion || '—' })),
    ),
  },
  {
    key: 'heartbeat', label: t('devices.colLastSeen'),
    cell: (d) => h('td',
      h('div', { style: { fontSize: '12.5px' } }, relativeTime(d.lastSeenAt)),
      h('div', { style: { fontSize: '11px', color: 'var(--text-faint)' } },
        d.lastSyncAt ? t('devices.syncedAgo', { at: relativeTime(d.lastSyncAt) }) : t('devices.neverSynced')),
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

// ── 分组树与视觉标识 ────────────────────────────────────────────────────

/** 分组标识色：优先用管理员选定的，否则按分组 ID 推导一个稳定颜色。 */
function groupColor(group) {
  if (group && GROUP_COLORS.includes(group.color)) {
    return group.color;
  }

  let hash = 0;
  for (const ch of String(group?.id ?? '')) {
    hash = (hash * 31 + ch.charCodeAt(0)) % 1000003;
  }

  return GROUP_COLORS[hash % GROUP_COLORS.length];
}

function groupOf(device) {
  return cache.groups.find((g) => g.id === device.groupId) || null;
}

/** 按名字自然排序：让「2 层」排在「10 层」前面。 */
function byName(a, b) {
  return String(a.name).localeCompare(String(b.name), 'zh-Hans-CN', { numeric: true });
}

/**
 * 把扁平的分组列表组织成「楼栋 → 楼层」结构。
 * 顶层分组即楼栋，parentId 指向它的分组即楼层。
 */
function buildTree() {
  const byId = new Map(cache.groups.map((g) => [g.id, g]));
  const roots = cache.groups
    .filter((g) => !g.parentId || !byId.has(g.parentId))
    .sort(byName);

  return roots.map((root) => ({
    root,
    floors: cache.groups.filter((g) => g.parentId === root.id).sort(byName),
  }));
}

/** 分组 + 其全部下级分组的 ID。 */
function groupFamilyIds(groupId) {
  const ids = new Set([groupId]);
  let changed = true;
  while (changed) {
    changed = false;
    for (const g of cache.groups) {
      if (g.parentId && ids.has(g.parentId) && !ids.has(g.id)) {
        ids.add(g.id);
        changed = true;
      }
    }
  }

  return ids;
}

/** 拖拽目标里可选的楼层（含「楼栋整体」「未分组」）。 */
function assignmentOptions() {
  const options = [{ value: '', label: t('devices.ungroupedParent') }];
  for (const { root, floors } of buildTree()) {
    options.push({ value: root.id, label: t('devices.wholeBuilding', { name: root.name }) });
    for (const floor of floors) {
      options.push({ value: floor.id, label: `${root.name} / ${floor.name}` });
    }
  }

  return options;
}

/** 设备所在「楼栋 / 楼层」的彩色徽标。 */
function groupPathBadge(device) {
  const group = groupOf(device);
  if (!group) {
    return h('span.badge.badge-neutral', t('devices.ungrouped'));
  }

  const parent = group.parentId ? cache.groups.find((g) => g.id === group.parentId) : null;
  return h('span', { style: { display: 'inline-flex', flexWrap: 'wrap', gap: '4px', alignItems: 'center' } },
    parent ? h('span.badge.badge-neutral', parent.name) : null,
    h('span.badge.badge-group', { dataset: { color: groupColor(group) } }, group.name));
}

/**
 * 标签徽标（#10）。
 * <para>颜色来自服务端白名单（只收 <c>#rgb</c> / <c>#rrggbb</c>），这里再兜一次底：
 * 万一有历史数据绕过了校验，也不该把任意字符串拼进 style。</para>
 */
function tagBadge(tag) {
  const color = /^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$/.test(tag.color) ? tag.color : '';
  return h('span.badge.tag-badge', {
    title: t('devices.tagLine', { name: tag.name }),
    style: color
      // 8 位十六进制（后两位是透明度）——比再去算 rgba 简单，现代浏览器都支持。
      ? { borderColor: color, color, background: `${color}22` }
      : null,
  }, tag.name);
}

/**
 * 标签勾选器：把选择结果直接写进传入的 Set，由调用方在保存时提交。
 * <para>刻意不做成「勾一下就发一个请求」：标签是**整体替换**语义，
 * 分多次提交时中间一旦失败，界面上的勾选和库里的结果就对不上了。</para>
 */
function tagPicker(selection) {
  const box = h('div.tag-picker');

  function paint() {
    clear(box);

    if (!cache.tags || cache.tags.length === 0) {
      box.appendChild(h('p.card-desc', { style: { margin: '0 0 6px' } },
        t('devices.noTags')));
    } else {
      box.appendChild(h('div.tag-picker-list', ...cache.tags.map((tag) => {
        const input = h('input', { type: 'checkbox' });
        input.checked = selection.has(tag.id);
        input.addEventListener('change', () => {
          if (input.checked) {
            selection.add(tag.id);
          } else {
            selection.delete(tag.id);
          }
        });

        return h('label.tag-picker-item', input, tagBadge(tag));
      })));
    }

    // 就地新建：打标签时最自然的动作就是「没有我要的那个 → 现在建一个」，
    // 让人为此跑去另一个页面是本末倒置。
    const nameInput = h('input', {
      type: 'text', placeholder: t('devices.newTagPlaceholder'), maxlength: '24',
    });
    const addButton = h('button.btn.btn-sm', {
      type: 'button',
      onClick: async () => {
        const name = nameInput.value.trim();
        if (!name) {
          toast('warn', t('devices.tagNameRequired'));
          return;
        }

        try {
          const tag = await api('/admin/tags', { method: 'POST', body: { name, color: '' } });
          cache.tags.push(tag);
          cache.tags.sort((a, b) => a.name.localeCompare(b.name, 'zh'));
          // 新建出来的标签默认就选上：这一步的意图本来就是「给它贴上这个」。
          selection.add(tag.id);
          nameInput.value = '';
          paint();
          toast('ok', t('devices.tagCreatedTitle'), t('devices.tagCreated', { name: tag.name }));
        } catch (err) {
          toastError(err, t('devices.tagCreateFailed'));
        }
      },
    }, t('devices.create'));

    box.appendChild(h('div.tag-picker-new', nameInput, addButton));
  }

  paint();
  return box;
}

/** 卡片上用的一行状态摘要。 */
function deviceStateKey(device) {
  if (device.revoked) return 'off';
  if (!device.online) return 'offline';
  if (device.state === 'error') return 'error';
  if (device.state === 'syncing') return 'syncing';
  return device.upToDate ? 'online' : 'pending';
}

const STATE_LABEL = {
  online: t('devices.stateOnline'),
  syncing: t('devices.stateSyncing'),
  pending: t('devices.statePending'),
  error: t('devices.stateError'),
  offline: t('devices.stateOffline'),
  off: t('devices.stateRevoked'),
};

// ── 页面 ────────────────────────────────────────────────────────────────

export async function render(container, params = {}) {
  // 兼容旧链接 #/devices?group=xxx。
  if (params.group !== undefined) {
    filter.groupId = params.group || '';
  }

  // 全局搜索（#41）跳过来时带着关键字：预填搜索框，落地直接看到那一台，
  // 而不是把人丢进几百台的列表里再自己找一遍。
  if (params.kw !== undefined) {
    filter.keyword = params.kw || '';
  }

  // 仪表盘「待同步 3」这类卡片点过来时带着状态：落地就只看那一类。
  // 这样用户从「看到一个数字」到「看到那些设备」中间不用再自己筛一次。
  if (params.status !== undefined) {
    filter.state = params.status || '';
  }

  clear(container);
  container.appendChild(skeletonRows());

  const [devices, groups, profiles, codes, tags] = await Promise.all([
    api('/admin/devices'),
    api('/admin/groups'),
    api('/admin/profiles'),
    api('/admin/enroll-codes'),
    api('/admin/tags'),
  ]);

  cache = { devices, groups, profiles, codes, tags };

  clear(container);
  container.appendChild(h('div',
    renderToolbar(),
    h('div#deviceBoardHost', renderBoard()),
    renderEnrollCodes(),
  ));

  bindListSelectionShortcuts();
}

function renderToolbar() {
  const tree = buildTree();

  return h('div.toolbar',
    h('input', {
      type: 'text',
      // class 供键盘快捷键「/」定位搜索框（见 core/shortcuts.js）
      class: 'toolbar-search',
      placeholder: t('devices.searchPlaceholder'),
      value: filter.keyword,
      onInput: (e) => {
        filter.keyword = e.target.value.trim();
        repaintBoard();
      },
    }),
    statusTabs(),
    // 标签筛选（#10）：标签多起来之后，靠这一项就能落到「高考考场那批」。
    cache.tags && cache.tags.length > 0
      ? select([
        { value: '', label: t('devices.allTags') },
        ...cache.tags.map((t) => ({ value: t.id, label: `${t.name}（${t.deviceCount}）` })),
      ], filter.tagId, (v) => {
        filter.tagId = v;
        repaintBoard();
      })
      : null,
    view === 'list'
      ? select(assignmentOptions(), filter.groupId, (v) => {
        filter.groupId = v;
        repaintBoard();
      })
      : null,
    h('div.spacer'),
    h('button.btn.btn-sm', { type: 'button', onClick: exportDevices }, t('devices.exportCsv')),
    h('button.btn.btn-sm', { type: 'button', onClick: openImportDialog }, t('devices.bulkImport')),
    view === 'groups' && tree.length > 0
      ? h('div.segmented',
        h('button', {
          class: `segmented-item${filter.buildingId === '' ? ' active' : ''}`,
          type: 'button',
          onClick: () => switchBuilding(''),
        }, t('devices.filterAll')),
        ...tree.map(({ root }) => h('button', {
          class: `segmented-item${filter.buildingId === root.id ? ' active' : ''}`,
          type: 'button',
          onClick: () => switchBuilding(root.id),
        }, root.name)),
        h('button', {
          class: `segmented-item${filter.buildingId === NO_BUILDING ? ' active' : ''}`,
          type: 'button',
          onClick: () => switchBuilding(NO_BUILDING),
        }, t('devices.ungrouped')),
      )
      : null,
    h('div.segmented',
      h('button', {
        class: `segmented-item${view === 'groups' ? ' active' : ''}`,
        type: 'button',
        onClick: () => switchView('groups'),
      }, t('devices.viewBoard')),
      h('button', {
        class: `segmented-item${view === 'list' ? ' active' : ''}`,
        type: 'button',
        onClick: () => switchView('list'),
      }, t('devices.viewList')),
    ),
    h('button.btn.btn-sm.btn-primary', { type: 'button', onClick: () => openGroupDialog(null) }, t('devices.newBuilding')),
    view === 'list'
      ? h('button.btn.btn-sm', { type: 'button', onClick: openColumnCustomize }, t('devices.columnSettings'))
      : null,
    h('button.btn.btn-sm', { type: 'button', onClick: () => refresh(true) }, t('devices.refresh')),
  );
}

function switchView(next) {
  view = next;
  repaintAll();
}

function switchBuilding(id) {
  filter.buildingId = filter.buildingId === id ? '' : id;
  repaintAll();
}

function repaintAll() {
  const container = document.getElementById('content');
  if (!container) return;
  clear(container);
  container.appendChild(h('div', renderToolbar(), h('div#deviceBoardHost', renderBoard()), renderEnrollCodes()));
}

function renderBoard() {
  return view === 'groups' ? renderBoardTree() : renderTable();
}

/** 局部重绘看板，避免整页刷新丢掉滚动位置。 */
function repaintBoard() {
  const host = document.getElementById('deviceBoardHost');
  if (host) {
    clear(host);
    host.appendChild(renderBoard());
  }
}

async function refresh(showToast = false) {
  const [devices, groups, profiles, codes, tags] = await Promise.all([
    api('/admin/devices'),
    api('/admin/groups'),
    api('/admin/profiles'),
    api('/admin/enroll-codes'),
    api('/admin/tags'),
  ]);

  cache = { devices, groups, profiles, codes, tags };
  repaintAll();

  if (showToast) toast('ok', t('devices.refreshed'));
}

/**
 * 单台设备是否命中某个状态筛选。
 * <para>抽出来是为了让「状态计数条上的数字」与「点下去之后的列表条数」共用**同一套判定**——
 * 两处各写一份迟早会对不上，而「数字和列表不一致」是最招人怀疑的那种 bug。</para>
 */
function matchesState(device, state) {
  switch (state) {
    case 'online': return !!device.online;
    case 'offline': return !device.online && !device.revoked;
    case 'pending': return !device.upToDate && !device.revoked;
    case 'error': return device.state === 'error';
    case 'revoked': return !!device.revoked;
    default: return true;
  }
}

/** 状态计数条可选的状态（顺序即展示顺序：常见问题靠前）。 */
const STATE_TABS = [
  { value: '', label: t('devices.filterAll') },
  { value: 'online', label: t('devices.stateOnline') },
  { value: 'pending', label: t('devices.statePending') },
  { value: 'error', label: t('devices.stateError') },
  { value: 'offline', label: t('devices.stateOffline') },
  { value: 'revoked', label: t('devices.stateRevoked') },
];

/**
 * 状态计数条：**点数字即筛选**。
 * <para>比原来的下拉多一个信息——「有多少台」。管理员真正想知道的是「离线 3 台」而不是
 * 「有一个叫离线的选项」，把数字摆在按钮上，一眼就知道该点哪个。</para>
 */
function statusTabs() {
  return h('div.status-tabs',
    ...STATE_TABS.map((tab) => h('button', {
      type: 'button',
      class: `status-tab${filter.state === tab.value ? ' active' : ''}`,
      title: tab.value ? t('devices.tabFilter', { label: tab.label }) : t('devices.showAll'),
      onClick: () => {
        filter.state = tab.value;
        // 选中态画在工具栏上，所以要整页重画而不只是看板。
        repaintAll();
      },
    },
    tab.label,
    h('span.status-tab-count', String(
      cache.devices.filter((d) => matchesState(d, tab.value)).length,
    )))));
}

/** 设备是否带某个标签（<c>tagId</c> 为空时恒真）。 */
function matchesTag(device, tagId) {
  if (!tagId) {
    return true;
  }

  return (device.tags || []).some((tag) => tag.id === tagId);
}

function filteredDevices() {
  const keyword = filter.keyword.toLowerCase();
  return cache.devices.filter((d) => {
    if (view === 'list' && filter.groupId && d.groupId !== filter.groupId) return false;
    if (!matchesState(d, filter.state)) return false;
    if (!matchesTag(d, filter.tagId)) return false;

    if (!keyword) return true;
    return [d.name, d.machineName, d.ipAddress, d.classIslandVersion, d.currentClassPlanName]
      .filter(Boolean)
      .some((v) => String(v).toLowerCase().includes(keyword));
  });
}

// ── 看板：楼栋 → 楼层 → 教室 ────────────────────────────────────────────

function renderBoardTree() {
  const devices = filteredDevices();
  const known = new Set(cache.groups.map((g) => g.id));
  const ungrouped = devices.filter((d) => !d.groupId || !known.has(d.groupId));
  const tree = buildTree();

  const blocks = [];
  if (filter.buildingId !== NO_BUILDING) {
    const shown = filter.buildingId ? tree.filter((b) => b.root.id === filter.buildingId) : tree;
    for (const block of shown) {
      blocks.push(buildingBlock(block, devices));
    }
  }

  if (filter.buildingId === '' || filter.buildingId === NO_BUILDING) {
    blocks.push(ungroupedBlock(ungrouped));
  }

  return h('div',
    overviewBar(tree),
    // 批量条：看板视图也要有，否则「选中」是死功能——勾了台设备却发现没地方执行操作。
    h('div#boardSelectionHost', selectionBar()),
    tree.length === 0
      ? emptyState('folder', t('devices.noBuildings'),
        t('devices.noBuildingsHint'),
        h('button.btn.btn-primary', { type: 'button', onClick: () => openGroupDialog(null) }, t('devices.newBuildingTitle')))
      : h('div.board-stack', ...blocks),
  );
}

/** 顶部概览：楼栋 / 楼层 / 教室数量与在线率。 */
function overviewBar(tree) {
  const total = cache.devices.length;
  const online = cache.devices.filter((d) => d.online && !d.revoked).length;
  const floors = tree.reduce((n, b) => n + b.floors.length, 0);
  const rate = total === 0 ? 0 : Math.round((online / total) * 100);
  const allBuildings = tree.length > 0 && tree.every((b) => b.root.kind === 'building');

  return h('div.board-overview',
    h('div.ov-stat', h('b', String(tree.length)), h('span', allBuildings ? t('devices.unitBuilding') : t('devices.unitTopGroups'))),
    h('div.ov-stat', h('b', String(floors)), h('span', t('devices.unitFloor'))),
    h('div.ov-stat', h('b', String(total)), h('span', t('devices.unitRooms'))),
    h('div.ov-online',
      h('div.ov-online-head',
        h('span', t('devices.onlineDevices')),
        h('b', `${online} / ${total}`)),
      h('div.ov-bar', h('i', { style: { width: `${rate}%` } })),
    ),
  );
}

function buildingBlock({ root, floors }, devices) {
  const color = groupColor(root);
  const bare = devices.filter((d) => d.groupId === root.id);
  const family = groupFamilyIds(root.id);
  const all = devices.filter((d) => d.groupId && family.has(d.groupId));
  const online = all.filter((d) => d.online && !d.revoked).length;
  const profile = cache.profiles.find((p) => p.id === root.defaultProfileId);

  // 没有楼层时，楼栋自己就是放置区；已存在楼层时才多给一张「未分层」卡片。
  const cards = [];
  if (bare.length > 0 || floors.length === 0) {
    cards.push(floorCard(root, null, bare));
  }

  for (const floor of floors) {
    cards.push(floorCard(root, floor, devices.filter((d) => d.groupId === floor.id)));
  }

  return h('section.bblock', { dataset: { color } },
    h('div.bblock-head',
      h('span.gcard-badge', (root.name || '?').slice(0, 1)),
      h('div.bblock-title',
        h('span.bblock-name', root.name,
          h('em.bblock-kind', root.kind === 'building' ? t('devices.building') : t('devices.group'))),
        h('span.gcard-meta',
          t('devices.buildingStats', { floors: floors.length, rooms: all.length, online })
          + (profile ? ` · ${profile.name}` : '')),
      ),
      h('div.gcard-actions',
        h('button.gcard-act', { type: 'button', title: t('devices.deployBuildingHint'), onClick: () => pushGroup(root) }, t('devices.deploy')),
        h('button.gcard-act', { type: 'button', title: t('devices.editBuilding'), onClick: () => openGroupDialog(root) }, t('devices.edit')),
        h('button.gcard-act.danger', { type: 'button', title: t('devices.deleteBuildingHint'), onClick: () => removeGroup(root) }, t('devices.delete')),
      ),
    ),
    h('div.bblock-floors',
      ...cards,
      h('button.floor-add', {
        type: 'button',
        title: t('devices.addFloorUnder', { name: root.name }),
        onClick: () => openGroupDialog(null, { parent: root }),
      }, t('devices.addFloor')),
    ),
  );
}

/**
 * 楼层卡片（教室芯片网格）。`floor` 为 null 表示「未分层」，
 * 即设备直接挂在楼栋上，此时放置目标是楼栋本身。
 */
function floorCard(building, floor, members) {
  const isBare = floor === null;
  const groupId = isBare ? building.id : floor.id;
  const online = members.filter((d) => d.online && !d.revoked).length;

  const body = h('div.fcard-body');
  if (members.length === 0) {
    body.appendChild(h('div.fcard-empty', isBare ? t('devices.noUnfloored') : t('devices.dragRoomHere')));
  } else {
    body.append(...members.map((d) => deviceChip(d)));
  }

  // 楼栋还没有楼层时，这张卡片直接叫「本栋教室」，避免出现「未分层」这种别扭的说法。
  const hasFloors = cache.groups.some((g) => g.parentId === building.id);
  const bareTitle = hasFloors ? t('devices.unfloored') : t('devices.thisBuildingRooms');

  const head = h('div.fcard-head',
    h('span.fcard-name', isBare ? bareTitle : floor.name),
    h('span.fcard-meta', t('devices.floorMeta', {
      count: members.length,
      online: online > 0 ? t('devices.floorOnlineSuffix', { count: online }) : '',
    })),
    isBare
      ? null
      : h('div.fcard-actions',
        h('button.fcard-act', { type: 'button', title: t('devices.editFloor'), onClick: () => openGroupDialog(floor) }, t('devices.edit')),
        h('button.fcard-act.danger', { type: 'button', title: t('devices.deleteFloor'), onClick: () => removeGroup(floor) }, t('devices.delete')),
      ),
  );

  // 注意：h() 的标签简写按 [.#] 切分，类名里不能再带空格，所以这里用三元挑标签。
  const card = h(isBare ? 'div.fcard.bare' : 'div.fcard', head, body);
  bindDropTarget(card, body, groupId);
  return card;
}

function ungroupedBlock(members) {
  const body = h('div.fcard-body');
  if (members.length === 0) {
    body.appendChild(h('div.fcard-empty', t('devices.noUngroupedDevices')));
  } else {
    body.append(...members.map((d) => deviceChip(d)));
  }

  const card = h('div.fcard.bare',
    h('div.fcard-head',
      h('span.fcard-name', t('devices.ungroupedDevices')),
      h('span.fcard-meta', t('devices.roomsCount', { count: members.length })),
    ),
    body);
  bindDropTarget(card, body, '');

  return h('section.bblock.ungrouped', { dataset: { color: 'slate' } },
    h('div.bblock-head',
      h('span.gcard-badge', '—'),
      h('div.bblock-title',
        h('span.bblock-name', t('devices.notInBuilding')),
        h('span.gcard-meta', t('devices.dragToUngroup')),
      ),
    ),
    h('div.bblock-floors', card),
  );
}

/**
 * 一台教室设备。
 * <para>单击**选中**、双击打开详情、拖拽调整归属。</para>
 *
 * <para>为什么不沿用「单击直接开详情」：那样选中状态就没有存在的余地，而右键菜单与批量条
 * 都要先回答「对谁操作」。选中是批量操作的前提，也是「当前对象」的可见表达。</para>
 */
function deviceChip(device) {
  const state = deviceStateKey(device);
  const group = groupOf(device);
  const picked = selection.has(device.id);

  const chip = h(`div.dchip.st-${state}${picked ? '.selected' : ''}`, {
    draggable: 'true',
    // 带上 id：右键菜单、Ctrl+A 全选、Shift 范围选择都按它认设备。
    dataset: { deviceId: device.id },
    // 无障碍（#49）：这张卡是「可选中」的，就得能 Tab 到、回车/空格能选。
    // 没有 role/tabindex 的 div 对屏幕阅读器和键盘用户等于不存在。
    role: 'button',
    tabindex: '0',
    'aria-pressed': picked ? 'true' : 'false',
    'aria-label': `${device.name || device.id}，${STATE_LABEL[state]}`,
    onKeyDown: (event) => {
      if (event.key === 'Enter' || event.key === ' ') {
        event.preventDefault();
        chip.click();
      }
    },
    title: `${device.name}\n${STATE_LABEL[state]}`
      + (group ? `\n${group.name}` : '\n未分组')
      + (device.machineName ? `\n${device.machineName}` : '')
      + '\n单击选中（可批量操作），双击查看详情，拖拽可调整楼层',
  },
    h('span.dchip-dot'),
    h('span.dchip-name', device.name || device.id.slice(0, 8)),
    device.remark ? h('span.dchip-remark', device.remark) : null,
    state === 'pending' ? h('span.dchip-tag', `v${device.appliedRevision}`) : null,
  );

  // 单击与双击的区分：单击延迟一小段再生效，双击则取消它。
  //
  // 延迟取 200ms 是权衡的结果：再长，勾选会明显「跟不上手」；再短，会和系统的
  // 双击判定（约 200~500ms，因系统设置而异）打架，导致双击时闪一下选中又消失。
  // 注意这里**不重建整块看板**（那会丢滚动位置），只改这一张卡片 + 刷新批量条。
  let clickTimer = null;
  chip.addEventListener('click', (event) => {
    // Shift + 单击是「从上次点的那台选到这台」：它是明确的一次性手势，
    // 不必等双击判定，立即生效（否则连点几下时勾选会明显跟不上手）。
    if (event.shiftKey && selectDeviceRange(device.id)) {
      clearTimeout(clickTimer);
      clickTimer = null;
      return;
    }

    if (clickTimer) {
      return; // 这是双击的第二下，交给 dblclick 处理
    }

    clickTimer = setTimeout(() => {
      clickTimer = null;
      toggleChipSelection(device.id, chip);
    }, 200);
  });

  chip.addEventListener('dblclick', () => {
    clearTimeout(clickTimer);
    clickTimer = null;
    // 双击的设备顺手选中：用户点两下说明「就是它」，
    // 而后面往往接着要发通知 / 下发配置（Finder 也是这个行为）。
    selection.add(device.id);
    chip.classList.add('selected');
    refreshSelectionBar();
    openDeviceDialog(device);
  });

  chip.addEventListener('dragstart', (e) => {
    draggedDeviceId = device.id;
    e.dataTransfer.effectAllowed = 'move';
    try {
      e.dataTransfer.setData('text/plain', device.id);
    } catch { /* 非安全上下文下可能不可用，忽略即可 */ }
    chip.classList.add('dragging');
  });
  chip.addEventListener('dragend', () => {
    draggedDeviceId = null;
    chip.classList.remove('dragging');
  });

  return chip;
}

/** 切换一台设备的选中状态（看板视图用）。 */
function toggleChipSelection(id, chip) {
  if (selection.has(id)) {
    selection.delete(id);
  } else {
    selection.add(id);
  }

  lastPickedId = id;
  chip?.classList.toggle('selected', selection.has(id));
  refreshSelectionBar();
}

// ────────────────────────── 多选：全选与范围选择 ──────────────────────────
//
// 选中集合（`selection`）在本模块里，所以这两个操作也留在这里：
// 放到 core/shortcuts.js 就只能按 DOM 反查，而「全选」要的恰恰是**当前筛选后的可见集合**
// （分页 / 筛选 / 切换视图后两者会不一致）。

/** 最近一次点选的设备：Shift + 点击的范围选择以它为锚点。 */
let lastPickedId = null;

/**
 * 当前视图里**看得见**的设备 id，按 DOM 顺序。
 * <para>用 DOM 顺序而不是 `cache.devices`：范围选择要的是「从锚点到这一台之间的那几台」
 * ——那是眼睛看到的那一段；而缓存里的顺序与可见顺序在筛选、分页、看板/列表切换后并不一致。</para>
 */
function visibleDeviceIds() {
  return [...document.querySelectorAll('.dchip[data-device-id], table.data tbody tr[data-device-id]')]
    .filter((el) => el.offsetParent !== null)
    .map((el) => el.dataset.deviceId)
    .filter(Boolean);
}

/**
 * 选中「锚点 → 这一台」之间的全部设备（Shift + 点击）。
 * <para>与文件管理器一致：区间内的**全部**被选中，而不是只有点到的那一台。
 * 连勾五台教室时，这是最省事的一个手势。</para>
 * @returns {boolean} 有没有按范围处理（没有锚点时返回 false，让调用方退回普通点选）
 */
function selectDeviceRange(id) {
  const ids = visibleDeviceIds();
  const from = ids.indexOf(lastPickedId);
  const to = ids.indexOf(id);
  if (from < 0 || to < 0) return false;

  const [lo, hi] = from <= to ? [from, to] : [to, from];
  for (let i = lo; i <= hi; i += 1) selection.add(ids[i]);
  repaintAll();
  return true;
}

/**
 * Ctrl / Cmd + A：全选「当前筛选结果」，等价于点表头那个全选复选框。
 *
 * <para>为什么要自己接管而不是交给 `core/shortcuts.js`：那里只做「按选择器找元素」，
 * 而这里要写的是本模块的选中集合。另外**必须挡掉两种场景**——焦点在输入框里
 * （用户想全选搜索框里的文字）与有弹窗时（在弹窗里全选文本），
 * 否则这个快捷键会变成「按了没法选文字」的干扰项。</para>
 */
function bindListSelectionShortcuts() {
  if (bindListSelectionShortcuts.bound) return;
  bindListSelectionShortcuts.bound = true;

  document.addEventListener('keydown', (event) => {
    if (!(event.ctrlKey || event.metaKey) || event.shiftKey || event.altKey) return;
    if (event.key.toLowerCase() !== 'a') return;
    // 只在设备页生效：别的页面按 Ctrl+A 应该保持浏览器的默认行为。
    if (!document.getElementById('deviceBoardHost')) return;
    if (document.getElementById('modalHost')?.hidden === false) return;

    const tag = event.target?.tagName;
    if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || event.target?.isContentEditable) return;

    const ids = visibleDeviceIds();
    if (ids.length === 0) return;

    event.preventDefault();
    for (const id of ids) selection.add(id);
    lastPickedId = ids[0];
    repaintAll();
    toast('info', t('devices.selectedCount', { count: ids.length }),
      t('devices.batchHint'), 4000);
  });
}

/**
 * 局部刷新看板视图的批量条。
 * <para>不整板重画：选中一台教室就重建整页会让滚动位置跳回顶部，
 * 而「勾几台 → 再勾几台」恰恰是最需要保持视野连贯的操作。</para>
 */
function refreshSelectionBar() {
  const host = document.getElementById('boardSelectionHost');
  if (!host) {
    return;
  }

  clear(host);
  const bar = selectionBar();
  if (bar) {
    host.appendChild(bar);
  }
}

/** 把卡片变成放置目标：拖入设备即改归属。 */
function bindDropTarget(card, body, groupId) {
  body.addEventListener('dragover', (e) => {
    if (!draggedDeviceId) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
    card.classList.add('drop-target');
  });

  body.addEventListener('dragleave', (e) => {
    if (!body.contains(e.relatedTarget)) {
      card.classList.remove('drop-target');
    }
  });

  body.addEventListener('drop', async (e) => {
    e.preventDefault();
    card.classList.remove('drop-target');
    if (draggedDeviceId) {
      await moveDevice(draggedDeviceId, groupId);
    }
  });
}

/** 把设备移动到目标分组；`groupId` 为空串表示移出分组。 */
async function moveDevice(deviceId, groupId) {
  const device = cache.devices.find((d) => d.id === deviceId);
  draggedDeviceId = null;
  if (!device || (device.groupId || '') === groupId) {
    return;
  }

  try {
    const updated = await api(`/admin/devices/${device.id}`, {
      method: 'PUT',
      body: { name: device.name, groupId, profileId: device.profileId || '' },
    });

    Object.assign(device, updated);
    const target = groupId ? cache.groups.find((g) => g.id === groupId) : null;
    toast('ok', t('devices.moved'), `${device.name} → ${target ? groupPathLabel(target) : t('devices.ungrouped')}`);
    repaintBoard();
  } catch (err) {
    toastError(err, t('devices.moveFailed'));
  }
}

function groupPathLabel(group) {
  const parent = group.parentId ? cache.groups.find((g) => g.id === group.parentId) : null;
  return parent ? `${parent.name} / ${group.name}` : t('devices.groupWholeBuilding', { name: group.name });
}

async function pushGroup(group) {
  if (!await confirmDialog(t('devices.deployToGroup'),
    t('devices.deployGroupConfirm', { name: group.name }), t('devices.deploy'))) {
    return;
  }

  try {
    const result = await api('/admin/push', {
      method: 'POST',
      body: { scope: 'group', targetIds: [group.id], force: false, message: '' },
    });
    toast('ok', t('devices.deploySent'), t('devices.affectedDevices', { count: result.affected }));
  } catch (err) {
    toastError(err, t('devices.deployFailed'));
  }
}

// ── 楼栋 / 楼层增删改 ───────────────────────────────────────────────────

/**
 * 新建或编辑分组。
 * `options.parent` 存在时按「在该楼栋下新增楼层」预填。
 */
function openGroupDialog(group, options = {}) {
  const parentOfSelf = group?.parentId ?? options.parent?.id ?? '';
  const isFloor = Boolean(parentOfSelf) || group?.kind === 'floor';

  const nameInput = h('input', {
    type: 'text',
    value: group?.name || '',
    placeholder: isFloor ? t('devices.floorPlaceholder') : t('devices.buildingPlaceholder'),
  });
  const descInput = h('input', { type: 'text', value: group?.description || '', placeholder: t('devices.optional') });

  // 上级：顶层 = 楼栋；选中某个楼栋 = 楼层。
  const roots = buildTree().map((b) => b.root).filter((r) => r.id !== group?.id);
  const parentSelect = select(
    [{ value: '', label: t('devices.topLevelOption') }, ...roots.map((r) => ({ value: r.id, label: r.name }))],
    parentOfSelf,
  );

  const profileSelect = select(
    [
      { value: '', label: t('devices.inheritProfileOption') },
      ...cache.profiles.map((p) => ({ value: p.id, label: t('devices.profileOption', { name: p.name, revision: p.revision }) })),
    ],
    group?.defaultProfileId || '',
  );

  const chosen = {
    color: group ? groupColor(group) : (options.parent ? groupColor(options.parent) : GROUP_COLORS[0]),
  };
  const badgePreview = h('span.gcard-badge');
  const swatches = h('div.color-picker');

  const paintSwatches = () => {
    badgePreview.dataset.color = chosen.color;
    badgePreview.textContent = (nameInput.value.trim() || t('devices.badgeNew')).slice(0, 1);
    swatches.replaceChildren(...GROUP_COLORS.map((key) => h('button', {
      class: `color-swatch${key === chosen.color ? ' active' : ''}`,
      type: 'button',
      dataset: { color: key },
      title: key,
      onClick: () => {
        chosen.color = key;
        paintSwatches();
      },
    })));
  };

  nameInput.addEventListener('input', paintSwatches);
  paintSwatches();

  const hint = h('div.notice.notice-info',
    h('span.notice-icon', 'i'),
    h('div', ''));

  const updateHint = () => {
    const parentId = parentSelect.value;
    if (parentId) {
      const parent = cache.groups.find((g) => g.id === parentId);
      hint.lastChild.textContent =
        t('devices.floorUnderHint', { parent: parent ? parent.name : '' });
    } else {
      hint.lastChild.textContent = t('devices.topLevelHint');
    }
  };

  parentSelect.addEventListener('change', updateHint);
  updateHint();

  modal({
    title: group ? t('devices.editGroupTitle', { name: group.name }) : (options.parent ? t('devices.addFloorTitle', { name: options.parent.name }) : t('devices.newBuildingTitle')),
    width: 'wide',
    body: h('div',
      field(t('devices.fieldName'), nameInput),
      field(t('devices.fieldRemark'), descInput, t('devices.fieldRemarkHint')),
      field(t('devices.fieldParent'), parentSelect, t('devices.fieldParentHint')),
      hint,
      field(t('devices.fieldColor'), h('div.color-row', badgePreview, swatches),
        t('devices.fieldColorHint')),
      field(t('devices.fieldProfile'), profileSelect,
        t('devices.fieldProfileHint')),
    ),
    confirmText: group ? t('devices.save') : t('devices.createGroup'),
    onConfirm: async () => {
      const body = {
        name: nameInput.value.trim(),
        description: descInput.value.trim(),
        color: chosen.color,
        parentId: parentSelect.value,
        kind: parentSelect.value ? 'floor' : 'building',
        defaultProfileId: profileSelect.value,
      };

      if (!body.name) {
        toast('warn', t('devices.nameRequired'));
        return false;
      }

      try {
        if (group) {
          await api(`/admin/groups/${group.id}`, { method: 'PUT', body });
          toast('ok', t('devices.saved'), t('devices.savedDeployHint'));
        } else {
          await api('/admin/groups', { method: 'POST', body });
          toast('ok', body.parentId ? t('devices.floorCreated') : t('devices.buildingCreated'));
        }
      } catch (err) {
        toastError(err, t('devices.saveFailed'));
        return false;
      }

      await refresh();
      return true;
    },
  });
}

async function removeGroup(group) {
  const family = groupFamilyIds(group.id);
  const floors = cache.groups.filter((g) => g.parentId === group.id);
  const affected = cache.devices.filter((d) => d.groupId && family.has(d.groupId));

  let message;
  if (floors.length > 0) {
    message = t('devices.deleteBuildingLine1', { name: group.name, floors: floors.length })
      + t('devices.deleteBuildingLine2', { count: affected.length });
  } else if (affected.length > 0) {
    message = t('devices.deleteFloorConfirm', { name: group.name, count: affected.length });
  } else {
    message = t('devices.deleteGroupConfirm', { name: group.name });
  }

  if (!await confirmDialog(t('devices.deleteGroupTitle'), message, t('devices.delete'), true)) return;

  await api(`/admin/groups/${group.id}`, { method: 'DELETE' });
  toast('ok', t('devices.deleted'));
  await refresh();
}

// ── 列表视图 ────────────────────────────────────────────────────────────

function renderTable() {
  const devices = filteredDevices();

  if (cache.devices.length === 0) {
    // 首次使用的空状态：只说「没有设备」没用，要告诉人**接下来做什么**，
    // 否则他会以为这页坏了（下方「注册码」区块就是入口）。
    return h('div.card',
      emptyState('monitor', t('devices.noDevices'),
        t('devices.noDevicesHint'), null, [
          t('devices.step1'),
          t('devices.step2'),
          t('devices.step3'),
        ]),
    );
  }

  if (devices.length === 0) {
    // 筛选结果为空时给一个出口：否则用户只能自己回想「我刚才是按什么筛的」。
    return h('div.card',
      emptyState('search', t('devices.noMatches'), t('devices.noMatchesHint'),
        h('button.btn.btn-sm', {
          type: 'button',
          onClick: () => {
            filter.keyword = '';
            filter.state = '';
            filter.groupId = '';
            repaintAll();
          },
        }, t('devices.clearFilters'))),
    );
  }

  const allSelected = devices.length > 0 && devices.every((d) => selection.has(d.id));
  const headBox = h('input', { type: 'checkbox' });
  headBox.checked = allSelected;
  headBox.title = t('devices.selectAllFiltered');
  headBox.addEventListener('change', () => {
    if (headBox.checked) {
      devices.forEach((d) => selection.add(d.id));
    } else {
      devices.forEach((d) => selection.delete(d.id));
    }
    repaintAll();
  });

  return h('div',
    selectionBar(),
    h('div.table-wrap',
      h('table.data',
        h('thead', h('tr',
          h('th', { style: { width: '34px' } }, headBox),
          ...visibleColumns().map((c) => h('th', c.label)),
          h('th', { style: { textAlign: 'right' } }, t('devices.colActions')),
        )),
        h('tbody', ...devices.map((d) => {
          const box = h('input', { type: 'checkbox' });
          box.checked = selection.has(d.id);
          // Shift + 点击行首复选框：选中区间。这里必须拦下默认行为——
          // 让它先把自己勾上再重绘，会出现「区间选中了、锚点那一台却被取消」的错觉。
          box.addEventListener('click', (event) => {
            if (event.shiftKey && selectDeviceRange(d.id)) {
              event.preventDefault();
            }
          });
          box.addEventListener('change', () => {
            if (box.checked) {
              selection.add(d.id);
            } else {
              selection.delete(d.id);
            }
            lastPickedId = d.id;
            repaintAll();
          });

          return h('tr', { dataset: { deviceId: d.id } },
            h('td', box),
            ...visibleColumns().map((c) => c.cell(d)),
            h('td.actions',
              h('button.btn.btn-sm', { type: 'button', onClick: () => openDeviceDialog(d) }, t('devices.edit')),
              ' ',
              h('button.btn.btn-sm', { type: 'button', onClick: () => openLogsDialog(d) }, t('devices.logs')),
              ' ',
              h('button.btn.btn-sm', {
                type: 'button',
                onClick: () => toggleRevoke(d),
              }, d.revoked ? t('devices.restore') : t('devices.disable')),
            ),
          );
        })),
      ),
    ),
  );
}

/**
 * 设备行的右键菜单。
 *
 * <para>挂在 `document` 上做**事件委托**，而不是给每一行绑 `contextmenu`：设备表格会随
 * 筛选、排序、切视图整块重画，逐行绑定要么漏掉新出现的行，要么得在每次重画后重新绑一遍。
 * 挂在 document 上只绑一次，任何带 `data-device-id` 的元素右键都能出菜单
 * （表格行与看板卡片都遵守这个约定）。</para>
 *
 * <para>菜单项刻意**复用底部批量条的那几个处理函数**：「右键关机」和「批量关机」
 * 的行为必须完全一致（同样的影响摘要、同样的撤销窗口）；两套逻辑迟早做出两种行为。</para>
 */
document.addEventListener('contextmenu', (event) => {
  const row = event.target instanceof Element ? event.target.closest('[data-device-id]') : null;
  if (!row) {
    return;
  }

  const id = row.dataset.deviceId;
  const device = cache.devices.find((d) => d.id === id);
  if (!device) {
    return;
  }

  event.preventDefault();
  showContextMenu(event.clientX, event.clientY, [
    { label: t('devices.openDetail'), action: () => openDeviceDialog(device) },
    { label: t('devices.sendNotice'), action: () => bulkNotify([id]) },
    { label: t('devices.openLogs'), action: () => openLogsDialog(device) },
    '-',
    { label: t('devices.wakeUp'), action: () => bulkWake([id]) },
    { label: t('devices.restartClassIsland'), action: () => bulkRestart([id]) },
    {
      label: t('devices.shutdown'),
      danger: true,
      action: () => bulkPower([id], 'power.shutdown', t('devices.shutdown'),
        t('devices.shutdownConfirm')),
    },
  ]);
});

/** 批量操作条：看板与列表两个视图共用，选中设备后出现。 */
function selectionBar() {
  if (selection.size === 0) {
    return null;
  }

  const ids = [...selection];
  const active = () => ids.filter((id) => cache.devices.some((d) => d.id === id && !d.revoked));

  return h('div.bulk-bar',
    h('span.bulk-text', t('devices.selectedCountShort', { count: ids.length })),
    h('button.btn.btn-sm', {
      type: 'button',
      onClick: async () => {
        const targets = active();
        if (targets.length === 0) { toast('warn', t('devices.allSelectedDisabled')); return; }
        if (!await confirmDialog(t('devices.batchDeploy'),
          `将通知 ${targets.length} 台设备立即重新拉取配置。\n`
          + `${selectionSummary(cache.devices, ids)}\n\n确定继续吗？`, t('devices.deploy'))) {
          return;
        }

        const r = await api('/admin/push', {
          method: 'POST',
          body: { scope: 'device', targetIds: targets, force: true, message: '' },
        });
        toast('ok', t('devices.deploySentShort'), t('devices.affectedDevicesShort', { count: r.affected }));
        selection.clear();
        await refresh();
      },
    }, t('devices.batchDeploy')),
    h('button.btn.btn-sm', {
      type: 'button',
      onClick: () => bulkNotify(active()),
    }, t('devices.sendNotice')),
    h('button.btn.btn-sm', {
      type: 'button',
      onClick: () => bulkWake(active()),
    }, t('devices.wakeShort')),
    h('button.btn.btn-sm', {
      type: 'button',
      onClick: () => bulkRestart(active()),
    }, t('devices.restartClassIsland')),
    h('button.btn.btn-sm.btn-danger', {
      type: 'button',
      onClick: () => bulkPower(active(), 'power.restart', t('devices.restartComputer'), t('devices.restartConfirm')),
    }, t('devices.restartComputer')),
    h('button.btn.btn-sm.btn-danger', {
      type: 'button',
      onClick: () => bulkPower(active(), 'power.shutdown', t('devices.shutdown'), t('devices.shutdownConfirm')),
    }, t('devices.shutdown')),
    h('button.btn.btn-sm', {
      type: 'button',
      onClick: () => bulkPower(active(), 'power.sleep', t('devices.sleep'), t('devices.sleepConfirm')),
    }, t('devices.sleep')),
    h('button.btn.btn-sm.btn-ghost', {
      type: 'button',
      onClick: () => { selection.clear(); repaintAll(); },
    }, t('devices.clearSelection')),
  );
}

// ── 批量导入 / 导出（CSV）────────────────────────────────────────────────

/** 下载一个受保护的文件（管理端接口都需要令牌，不能用 <a href> 直链）。 */
async function downloadFromApi(path, fileName, okMessage) {
  const blob = await fetchBlob(path);
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
  if (okMessage) toast('ok', t('devices.downloaded'), okMessage);
}

/** 导出设备清单为 CSV：默认脱敏内网 IP —— 导出文件常被当附件转发，默认脱敏比事后追责划算。 */
async function exportDevices() {
  try {
    await downloadFromApi('/admin/devices/export',
      `devices-${new Date().toISOString().slice(0, 10)}.csv`,
      t('devices.exportMasked'));
  } catch (err) {
    toastError(err, t('devices.exportFailed'));
  }
}

/**
 * 批量导入：粘贴 CSV → **先预演**（只出报告）→ 确认后正式导入。
 * <para>预注册出来的设备还没有令牌，等教室端以相同机器名首次注册时自动认领分组 / 档案 / 备注。</para>
 */
function openImportDialog() {
  const csvBox = h('textarea', {
    rows: '9',
    placeholder: t('devices.csvPasteHint'),
    style: { width: '100%', fontFamily: 'ui-monospace, Consolas, monospace', fontSize: '12.5px' },
  });
  const result = h('div', { style: { marginTop: '12px' } });

  const run = async (dryRun) => {
    if (!csvBox.value.trim()) { toast('warn', t('devices.csvEmpty')); return; }
    clear(result);
    result.appendChild(loadingBlock(dryRun ? t('devices.dryRunning') : t('devices.importing')));
    try {
      const report = await api('/admin/devices/import', {
        method: 'POST',
        body: { csv: csvBox.value, dryRun },
      });
      clear(result);
      result.appendChild(renderImportReport(report, dryRun));
      if (!dryRun) await refresh();
    } catch (err) {
      clear(result);
      result.appendChild(h('div.notice.notice-danger', h('span.notice-icon', '!'), h('div', err.message)));
    }
  };

  return modal({
    title: t('devices.importTitle'),
    width: 'xwide',
    hideFooter: true,
    body: h('div',
      h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', t('devices.importIntro')
          + t('devices.importMatchHint')
          + t('devices.importEmptyCellHint'))),
      h('div', { style: { marginTop: '12px' } }, csvBox),
      h('div', { style: { display: 'flex', gap: '8px', marginTop: '10px', flexWrap: 'wrap' } },
        h('button.btn', { type: 'button', onClick: () => run(true) }, t('devices.dryRun')),
        h('button.btn.btn-primary', { type: 'button', onClick: () => run(false) }, t('devices.importForReal')),
        h('div.spacer'),
        h('button.btn.btn-ghost', {
          type: 'button',
          onClick: () => downloadFromApi('/admin/devices/import/template', 'devices-import-template.csv'),
        }, t('devices.downloadTemplate')),
      ),
      result,
    ),
  });
}

/** 逐行结果表：把「哪几行会新增、哪几行报错」直接摊开，避免盲导。 */
function renderImportReport(report, dryRun) {
  const head = h('div', { style: { fontSize: '12.5px', marginBottom: '6px' } },
    t('devices.importResultLine', { kind: dryRun ? t('devices.dryRunResult') : t('devices.importResult') }),
    h('b', `${report.created}`), t('devices.importCreated'),
    h('b', `${report.updated}`), t('devices.importUpdated'),
    report.failed > 0 ? h('span', { style: { color: 'var(--danger)' } }, t('devices.importFailedRows', { count: report.failed })) : t('devices.importNoErrors'));

  if (report.rows.length === 0) {
    return h('div', head, t('devices.importNoRows'));
  }

  const table = h('div.table-wrap', { style: { maxHeight: '260px', overflowY: 'auto' } },
    h('table.table',
      h('thead', h('tr', h('th', t('devices.colRow')), h('th', t('devices.colAction')), h('th', t('devices.colDevice')), h('th', t('devices.colNote')))),
      h('tbody', ...report.rows.map((row) => h('tr',
        h('td', `${row.line}`),
        h('td', h('span.badge' + (row.action === t('devices.colError') ? '.badge-danger' : '.badge-info'), row.action)),
        h('td', row.deviceName || '—'),
        h('td', { style: { fontSize: '12px' } }, row.message || '—'),
      )))));

  return h('div', head, table, dryRun
    ? h('div', { style: { marginTop: '8px', fontSize: '12px', color: 'var(--text-dim)' } },
      t('devices.dryRunNotice'))
    : null);
}

/** 批量通知：填一次内容，向选中的多台设备统一下发（离线设备不排队，通知讲时效）。 */
function bulkNotify(ids) {
  if (ids.length === 0) { toast('warn', t('devices.allSelectedDisabled')); return; }

  const titleInput = h('input', { type: 'text', placeholder: t('devices.noticeTitlePlaceholder') });
  const msgInput = h('textarea', { placeholder: t('devices.noticeBodyPlaceholder'), style: { minHeight: '80px' } });
  const speakChk = h('input', { type: 'checkbox' });

  // 富内容预览（#11）：与定时提醒共用同一套安全渲染器，边写边看排版。
  const preview = createRichPreview(() => msgInput.value);
  msgInput.addEventListener('input', preview.refresh);

  modal({
    title: t('devices.noticeTitleWithCount', { count: ids.length }),
    body: h('div',
      // 通知是「讲时效」的：离线设备不排队——等它上线再收到这条通知，内容往往已经没意义了（#52）。
      h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', selectionSummary(cache.devices, [...selection], {
          offlineNote: t('devices.noticeOfflineNote'),
        }))),
      field(t('devices.noticeTitle'), titleInput, t('devices.noticeTitleHint')),
      field(t('devices.noticeBody'), h('div', msgInput, preview.element), t('devices.noticeBodyHint')),
      h('label.checkbox-field', speakChk,
        h('span', t('devices.noticeSpeech')),
        h('span', { style: { color: 'var(--text-faint)', fontSize: '12px' } }, t('devices.noticeSpeechHint'))),
    ),
    confirmText: t('devices.send'),
    onConfirm: async () => {
      const title = titleInput.value.trim();
      const message = msgInput.value.trim();
      if (!title && !message) { toast('warn', t('devices.noticeBodyRequired')); return false; }

      const r = await api('/admin/devices/command', {
        method: 'POST',
        body: {
          kind: 'notify',
          payload: JSON.stringify({ title, message, speak: speakChk.checked }),
          deviceIds: ids,
          includeOffline: false,
        },
      });
      toast('ok', t('devices.sent'), broadcastSummary(r));
      selection.clear();
      await refresh();
      return true;
    },
  });
}

/** 批量重启 ClassIsland 进程（不动 Windows 本身，大屏会自动恢复）。 */
async function bulkRestart(ids) {
  if (ids.length === 0) { toast('warn', t('devices.allSelectedDisabled')); return; }
  if (!await confirmDialog(t('devices.restartClassIsland'),
    `将重启 ${ids.length} 台设备上的 ClassIsland 进程（大屏会短暂黑屏后自动恢复）。\n`
    + `${selectionSummary(cache.devices, [...selection])}\n\n确定继续吗？`, t('devices.restart'))) {
    return;
  }

  await sendGuardedCommand({ kind: 'restart', deviceIds: ids, includeOffline: false }, t('devices.restartClassIsland'));
  selection.clear();
  await refresh();
}

/** 批量电源操作：关机 / 重启 / 睡眠（仅 Windows 终端，二次确认后立即执行，不可撤销）。 */
async function bulkPower(ids, kind, label, warning) {
  if (ids.length === 0) { toast('warn', t('devices.allSelectedDisabled')); return; }
  if (!await confirmDialog(t('devices.batchAction', { label }),
    `将对 ${ids.length} 台设备执行「${label}」。\n`
    + `${selectionSummary(cache.devices, [...selection])}\n`
    + `${warning}\n\n该操作不可撤销，确定继续吗？`,
    label, true)) {
    return;
  }

  await sendGuardedCommand({ kind, deviceIds: ids, includeOffline: false }, t('devices.batchAction', { label }));
  selection.clear();
  await refresh();
}

/**
 * 批量唤醒（Wake-on-LAN，#20）。
 * <para>与关机 / 重启**不是同一套机制**：要唤醒的机器按定义是关着的、插件也没在跑，
 * 指令队列根本没法送达，只能由 A 端直接发魔术包。所以这里逐台列出跳过原因——
 * 「没上报 MAC」「不在同一广播域」这些都不是重试能解决的，含糊一句"部分失败"帮不上忙。</para>
 */
async function bulkWake(ids) {
  if (ids.length === 0) { toast('warn', t('devices.allSelectedDisabled')); return; }
  if (!await confirmDialog(t('devices.batchWake'),
    `将向 ${ids.length} 台设备发送唤醒魔术包（Wake-on-LAN）。\n`
    + `${selectionSummary(cache.devices, ids)}\n`
    + t('devices.wakePrereq1')
    + t('devices.wakePrereq2'),
    t('devices.wakeShort'))) {
    return;
  }

  try {
    const r = await api('/admin/devices/wake', { method: 'POST', body: { deviceIds: ids } });
    const skipped = r?.skipped ?? [];
    if (skipped.length === 0) {
      toast('ok', t('devices.wakeSent'), t('devices.wakeSentCount', { count: r?.sent ?? 0 }));
    } else {
      toast('warn', t('devices.wakeSentSkipped', { sent: r?.sent ?? 0, skipped: skipped.length }),
        skipped.slice(0, 3).map((s) => `${s.deviceName}：${s.reason}`).join('；')
        + (skipped.length > 3 ? t('devices.wakeSkippedMore', { count: skipped.length }) : ''));
    }
  } catch (e) {
    toastError(e, t('devices.wakeFailed'));
  }

  selection.clear();
  await refresh();
}

/**
 * 下发危险指令：延迟 15 秒执行，并在右下角给出撤销窗口。
 * <para>服务端在 not_before 之前不会派发，所以这段时间内撤销是真实生效的。</para>
 */
async function sendGuardedCommand(body, label) {
  const r = await api('/admin/devices/command', {
    method: 'POST',
    body: { ...body, delaySeconds: 15 },
  });
  toast('ok', t('devices.deploySentShort'), t('devices.willRunIn15', { summary: broadcastSummary(r) }));
  undoBar(t('devices.willRunIn15Short', { label }), 15, async () => {
    const canceled = await api('/admin/devices/commands/cancel', {
      method: 'POST',
      body: { ids: r.commandIds || [] },
    });
    toast('ok', t('devices.cancelled'), canceled > 0 ? t('devices.cancelledCount', { count: canceled }) : t('devices.cancelTooLate'));
  });
  return r;
}

/** 批量操作结果的统一文案（不同接口返回字段略有差异）。 */
function broadcastSummary(result) {
  if (typeof result === 'number') return t('devices.affectedDevicesRaw', { count: result });
  if (result && typeof result.affected === 'number') return t('devices.affectedDevices', { count: result.affected });
  if (result && typeof result.queued === 'number') return t('devices.queuedCount', { count: result.queued });
  return t('devices.commandSent');
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
    title: t('devices.columnsTitle'),
    body: container,
    confirmText: t('devices.done'),
    onConfirm: () => {
      repaintBoard();
      return true;
    },
  });
}

/** 指令类型 → 中文名。未列出的类型直接显示原文，不猜。 */
const COMMAND_KIND_LABELS = {
  shell: t('devices.cmdRun'),
  notify: t('devices.cmdNotice'),
  'appearance.apply': t('devices.cmdAppearance'),
  'plugin.refresh': t('devices.cmdRefreshPlugins'),
  'automation.list': t('devices.cmdAutomationQuery'),
  'automation.trigger': t('devices.cmdAutomationTrigger'),
  screenshot: t('devices.cmdScreenshot'),
  restart: t('devices.restartClassIsland'),
  shutdown: t('devices.shutdown'),
  'power.off': t('devices.shutdown'),
  'power.reboot': t('devices.cmdRestart'),
};

function commandKindLabel(kind) {
  if (!kind) return t('devices.cmdUnknown');
  return COMMAND_KIND_LABELS[kind] ? `${COMMAND_KIND_LABELS[kind]}（${kind}）` : kind;
}

/** 把秒数写成紧凑文本：3 天 2 小时 / 5 分 30 秒。 */
function shortDuration(seconds) {
  const s = Math.max(0, Math.floor(seconds));
  const days = Math.floor(s / 86400);
  const hours = Math.floor((s % 86400) / 3600);
  const minutes = Math.floor((s % 3600) / 60);
  if (days > 0) return t('ui.durationDays', { days, hours });
  if (hours > 0) return t('ui.durationHours', { hours, minutes });
  if (minutes > 0) return t('ui.durationMinutes', { minutes, seconds: s % 60 });
  return t('ui.durationSeconds', { seconds: s });
}

/**
 * 倒计时文本。
 * @param {string} iso 目标时刻。
 * @param {'expires'|'from'} kind `expires` = 距离作废还剩多久；`from` = 距离生效还有多久。
 */
function countdownText(iso, kind) {
  const diff = new Date(iso).getTime() - Date.now();
  const text = shortDuration(Math.abs(diff) / 1000);
  if (kind === 'expires') {
    return diff <= 0 ? t('devices.expired') : t('devices.remaining', { text });
  }
  return diff <= 0 ? t('devices.dueNow') : t('devices.takesEffectIn', { text });
}

/**
 * 「待执行指令」区块（#3）：看过期时间、看排队位置，必要时取消。
 *
 * 解决的问题：指令发出去之后，管理端只能看到「已下发」，分不清它是**排在队里等设备上线**、
 * 还是**已经派发给设备但设备没回**。队列 + 剩余有效期摆出来，管理员才知道该等、该重发，还是该取消。
 *
 * @returns {{el: HTMLElement, stop: Function}} 弹窗关闭时**必须**调 `stop()`，否则倒计时会一直跑。
 */
function pendingCommandsSection(device) {
  const box = h('div');
  let timer = null;

  /** 每秒只改倒计时文本，不重新请求接口（省流量、也不闪）。 */
  function tick() {
    for (const cell of box.querySelectorAll('[data-countdown]')) {
      cell.textContent = countdownText(cell.dataset.countdown, cell.dataset.countdownKind);
    }
  }

  function renderQueue(rows, reload) {
    const title = h('div.config-section-title', t('devices.pendingCommands'));

    if (rows.length === 0) {
      return h('div',
        title,
        h('p.card-desc', { style: { margin: '0 0 4px' } }, device.online
          ? t('devices.queueEmpty')
          : t('devices.queueEmptyOffline')),
      );
    }

    return h('div',
      title,
      h('p.card-desc', { style: { margin: '0 0 8px' } },
        t('devices.pendingCount', { count: rows.length })
        + `${device.online ? '' : t('devices.offlineQueueNote')}。`
        + t('devices.queueExpiryHint')),
      h('div.config-panel', ...rows.map((row) => {
        const pendingNotBefore = row.notBefore && new Date(row.notBefore).getTime() > Date.now();
        return h('div.config-item',
          h('div', { style: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 } },
            h('span', { style: { fontWeight: '600', fontSize: '13px' } }, commandKindLabel(row.kind)),
            h('span', { style: { color: 'var(--text-faint)', fontSize: '12px' } },
              t('devices.issuedBy', { by: row.issuedBy || '—', at: formatDateTime(row.issuedAt) })),
          ),
          h('span', {
            style: {
              marginLeft: 'auto', flex: 'none', display: 'flex', alignItems: 'center', gap: '10px',
            },
          },
            pendingNotBefore
              ? h('span.dchip-tag', {
                dataset: { countdown: row.notBefore, countdownKind: 'from' },
              }, countdownText(row.notBefore, 'from'))
              : null,
            row.expiresAt
              ? h('span', {
                style: { color: 'var(--text-faint)', fontSize: '12px', fontVariantNumeric: 'tabular-nums' },
                dataset: { countdown: row.expiresAt, countdownKind: 'expires' },
              }, countdownText(row.expiresAt, 'expires'))
              : h('span', { style: { color: 'var(--text-faint)', fontSize: '12px' } }, t('devices.noExpiry')),
            h('button.btn.btn-sm.btn-danger', {
              type: 'button',
              title: t('devices.cancelCommandHint'),
              onClick: async () => {
                if (!await confirmDialog(t('devices.cancelCommand'),
                  t('devices.cancelCommandConfirm', { kind: commandKindLabel(row.kind) }),
                  t('devices.cancelCommand'), true)) {
                  return;
                }

                try {
                  await api(`/admin/devices/commands/${row.id}`, { method: 'DELETE' });
                  toast('ok', t('devices.cancelledShort'));
                  await reload();
                } catch (err) {
                  toastError(err, t('devices.cancelFailed'));
                }
              },
            }, t('devices.cancel')),
          ),
        );
      })),
    );
  }

  async function load() {
    clear(box);
    box.appendChild(h('p.card-desc', t('devices.loadingCommands')));
    try {
      const rows = await api(`/admin/devices/${device.id}/commands/queue`);
      clear(box);
      box.appendChild(renderQueue(rows, load));
      tick();
    } catch (err) {
      clear(box);
      box.appendChild(errorBlock(err, { title: t('devices.loadCommandsFailed'), onRetry: load }));
    }
  }

  load();
  timer = setInterval(tick, 1000);

  return { el: box, stop: () => clearInterval(timer) };
}

/** 设备详情：改名称/归属/档案，并可直接查看日志、停用或删除。 */
function openDeviceDialog(device) {
  const nameInput = h('input', { type: 'text', value: device.name });
  const remarkInput = h('input', {
    type: 'text',
    value: device.remark || '',
    placeholder: t('devices.remarkPlaceholder'),
  });

  const groupSelect = select(assignmentOptions(), device.groupId || '');

  const profileSelect = select(
    [
      { value: '', label: t('devices.inheritProfile') },
      ...cache.profiles.map((p) => ({ value: p.id, label: t('devices.profileOption', { name: p.name, revision: p.revision }) })),
    ],
    device.profileId || '',
  );

  // 标签勾选状态（#10）：勾选结果先攒在集合里，点「保存」时整体提交。
  const tagSelection = new Set((device.tags || []).map((tag) => tag.id));

  // 待执行指令队列（#3）：弹窗关闭时必须停掉它的倒计时。
  const pendingCommands = pendingCommandsSection(device);
  // 操作历史时间线（#42）：谁在什么时候改过这台设备。
  const auditTimeline = auditTimelineSection(device.name);

  modal({
    title: t('devices.detailTitle', { name: device.name }),
    width: 'wide',
    body: h('div',
      h('div.device-summary',
        h('div.summary-line', deviceStateBadge(device), syncBadge(device)),
        h('div.summary-line',
          h('span.summary-key', t('devices.machineName')), device.machineName || '—',
          h('span.summary-key', 'IP'), device.ipAddress || '—'),
        // 网卡地址单独一行并写明用途：点「唤醒」失败时，第一个要查的就是它有没有上报（#20）。
        h('div.summary-line',
          h('span.summary-key', t('devices.macAddress')),
          device.macAddress || t('devices.macNotReported')),
        // 离线原因诊断：在线时不显示，离线时给出可行动的原因（从未连接 / 离线多久 / 最近报错）。
        device.offlineReason
          ? h('div.summary-line', { style: { color: 'var(--text-dim)' } },
            h('span.summary-key', t('devices.offlineReason')), device.offlineReason)
          : null,
        h('div.summary-line',
          h('span.summary-key', 'ClassIsland'), device.classIslandVersion || '—',
          h('span.summary-key', t('devices.plugin')), device.pluginVersion || '—'),
        h('div.summary-line',
          h('span.summary-key', t('devices.colTimetable')), d0(device.currentClassPlanName)),
        h('div.summary-line',
          h('span.summary-key', t('devices.colLastSeen')), relativeTime(device.lastSeenAt),
          h('span.summary-key', t('devices.enrolledAt')), formatDateTime(device.createdAt)),
      ),
      field(t('devices.nameField'), nameInput, t('devices.nameFieldHint')),
      field(t('devices.locationField'), groupSelect, t('devices.locationFieldHint')),
      field(t('devices.profileField'), profileSelect,
        t('devices.profileFieldHint')),
      field(t('devices.fieldRemark'), remarkInput, t('devices.remarkFieldHint')),
      field(t('devices.colTags'), tagPicker(tagSelection),
        t('devices.tagsFieldHint1')
        + t('devices.tagsFieldHint2')),
      pendingCommands.el,
      auditTimeline.el,
      h('div.card-actions',
        h('button.btn.btn-sm', { type: 'button', onClick: () => openLogsDialog(device) }, t('devices.openLogs')),
        h('button.btn.btn-sm', {
          type: 'button',
          onClick: async () => {
            await toggleRevoke(device);
          },
        }, device.revoked ? t('devices.restoreDevice') : t('devices.disableDevice')),
        h('button.btn.btn-sm.btn-danger', {
          type: 'button',
          onClick: async () => {
            if (!await confirmDialog(t('devices.deleteDevice'),
              t('devices.deleteDeviceConfirm', { name: device.name }), t('devices.delete'), true)) {
              return;
            }

            await api(`/admin/devices/${device.id}`, { method: 'DELETE' });
            toast('ok', t('devices.deleted'));
            await refresh();
          },
        }, t('devices.deleteDevice')),
      ),
    ),
    confirmText: t('devices.save'),
    onClose: () => pendingCommands.stop(),
    onConfirm: async () => {
      await api(`/admin/devices/${device.id}`, {
        method: 'PUT',
        body: {
          name: nameInput.value.trim(),
          groupId: groupSelect.value,
          profileId: profileSelect.value,
          remark: remarkInput.value.trim(),
        },
      });
      // 标签单独提交（整体替换语义）。放在设备本体保存**之后**：
      // 万一标签这步失败，名称 / 归属这些改动也已经生效，用户不用白改一遍。
      await api(`/admin/devices/${device.id}/tags`, {
        method: 'PUT',
        body: { tagIds: [...tagSelection] },
      });

      toast('ok', t('devices.saved'), t('devices.updatedToast', { name: nameInput.value.trim() }));
      await refresh();
      return true;
    },
  });
}

/** 空值占位。 */
function d0(value) {
  return value || '—';
}

async function toggleRevoke(device) {
  const revoking = !device.revoked;
  if (revoking) {
    const ok = await confirmDialog(t('devices.disableDevice'),
      t('devices.disableConfirm', { name: device.name }),
      t('devices.disable'), true);
    if (!ok) return;
  }

  await api(`/admin/devices/${device.id}/revoke`, {
    method: 'POST',
    body: { revoked: revoking },
  });
  toast('ok', revoking ? t('devices.stateRevoked') : t('devices.restored'), device.name);
  await refresh();
}

async function openLogsDialog(device) {
  const dialog = modal({
    title: t('devices.logsTitle', { name: device.name }),
    width: 'xwide',
    hideFooter: true,
    body: loadingBlock(t('devices.loadingLogs')),
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
    h('span', { style: { fontSize: '12.5px', color: 'var(--text-dim)' } }, t('devices.logsCount', { count: logs.length })),
    h('div.spacer'),
    h('button.btn.btn-sm', {
      type: 'button',
      onClick: async () => {
        if (!await confirmDialog(t('devices.clearLogs'), t('devices.clearLogsConfirm', { name: device.name }), t('devices.clear'), true)) return;
        await api(`/admin/devices/${device.id}/logs`, { method: 'DELETE' });
        toast('ok', t('devices.cleared'));
        dialog.close();
      },
    }, t('devices.clearLogs')),
  );

  const list = logs.length === 0
    ? emptyState('profiles', t('devices.noLogs'), t('devices.noLogsHint'))
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
        h('h3', t('devices.enrollCodesTitle')),
        h('p.card-desc', t('devices.enrollCodesDesc')),
      ),
      h('button.btn.btn-primary.btn-sm', { type: 'button', onClick: openCreateCodeDialog }, t('devices.newEnrollCode')),
    ),
    codes.length === 0
      ? emptyState('key', t('devices.noEnrollCodes'), t('devices.noEnrollCodesHint'),
        h('button.btn.btn-primary', { type: 'button', onClick: openCreateCodeDialog }, t('devices.generateEnrollCode')))
      : h('div.table-wrap',
        h('table.data',
          h('thead', h('tr',
            h('th', t('devices.enrollCode')),
            h('th', t('devices.fieldRemark')),
            h('th', t('devices.usage')),
            h('th', t('devices.validity')),
            h('th', t('devices.createdAt')),
            h('th', { style: { textAlign: 'right' } }, t('devices.colActions')),
          )),
          h('tbody', ...codes.map(renderCodeRow)),
        ),
      ),
  );
}

function renderCodeRow(code) {
  const remaining = code.remainingUses < 0 ? t('devices.unlimited') : code.remainingUses;
  const expired = code.expiresAt && new Date(code.expiresAt) < new Date();
  const exhausted = code.remainingUses === 0;
  const unavailable = code.available === false;
  // 明文不可用时用指纹引用操作该行（#36）：界面拿不到注册码本体，但编辑 / 删除仍然可用。
  const target = unavailable ? code.reference : code.code;

  return h('tr',
    h('td',
      h('div', { style: { display: 'flex', alignItems: 'center', gap: '8px' } },
        unavailable
          ? [
            h('span.badge.badge-danger', t('devices.unavailable')),
            h('span', { style: { color: 'var(--text-faint)', fontSize: '12px' } },
              t('devices.codeKeyChanged', { ref: code.reference })),
          ]
          : [
            h('code', { style: { fontSize: '14px', letterSpacing: '1.5px', fontWeight: '700' } }, code.code),
            h('button.btn.btn-ghost.btn-sm', {
              type: 'button',
              title: t('devices.copyEnrollCode'),
              onClick: () => copyText(code.code, t('devices.enrollCodeCopied')),
            }, t('devices.copy')),
          ],
      ),
    ),
    h('td', code.note || h('span', { style: { color: 'var(--text-faint)' } }, '—')),
    h('td',
      h('span', t('devices.codeUsage', { used: code.usedCount, left: remaining })),
    ),
    h('td',
      expired ? h('span.badge.badge-danger', t('devices.expiredShort'))
        : exhausted ? h('span.badge.badge-danger', t('devices.usesExhausted'))
          : code.expiresAt
            ? h('span', { style: { fontSize: '12px' } }, formatDateTime(code.expiresAt))
            : h('span.badge.badge-ok', t('devices.noExpiry')),
    ),
    h('td', { style: { fontSize: '12px', color: 'var(--text-faint)' } }, formatDateTime(code.createdAt)),
    h('td.actions',
      h('button.btn.btn-sm', { type: 'button', onClick: () => openEditCodeDialog(code) }, t('devices.edit')),
      ' ',
      h('button.btn.btn-sm.btn-danger', {
        type: 'button',
        onClick: async () => {
          if (!await confirmDialog(t('devices.deleteEnrollCode'), t('devices.deleteCodeConfirm', { target }), t('devices.delete'), true)) return;
          await api(`/admin/enroll-codes/${encodeURIComponent(target)}`, { method: 'DELETE' });
          toast('ok', t('devices.deleted'));
          await refresh();
        },
      }, t('devices.delete')),
    ),
  );
}

function openCreateCodeDialog() {
  const noteInput = h('input', { type: 'text', placeholder: t('devices.enrollNotePlaceholder') });
  const maxUsesInput = h('input', { type: 'number', value: '1', min: '0' });
  const hoursInput = h('input', { type: 'number', value: '72', min: '0' });

  modal({
    title: t('devices.generateEnrollCode'),
    width: 'wide',
    body: h('div',
      field(t('devices.fieldRemark'), noteInput, t('devices.enrollNoteHint')),
      h('div.form-row',
        field(t('devices.maxUses'), maxUsesInput, t('devices.maxUsesHint')),
        field(t('devices.validHours'), hoursInput, t('devices.validHoursHint')),
      ),
    ),
    confirmText: t('devices.generate'),
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
        title: t('devices.enrollCodeGenerated'),
        width: 'wide',
        hideFooter: true,
        body: h('div',
          h('p', { style: { marginTop: '0', color: 'var(--text-dim)' } },
            t('devices.enrollCodeGeneratedHint')),
          h('div.copy-row',
            h('div.code-block', result.code),
            h('button.btn', { type: 'button', onClick: () => copyText(result.code, t('devices.enrollCodeCopied')) }, t('devices.copy')),
          ),
          h('div.notice.notice-info', { style: { marginTop: '14px' } },
            h('span.notice-icon', 'i'),
            h('div', t('devices.codeMaxUses', { value: result.maxUses === 0 ? t('devices.unlimited') : result.maxUses })
              + t('devices.codeValidity', { value: result.expiresAt ? formatDateTime(result.expiresAt) : t('devices.noExpiry') })),
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
  // 明文不可用时用指纹引用操作该行（#36）。
  const unavailable = code.available === false;
  const target = unavailable ? code.reference : code.code;

  modal({
    title: unavailable ? t('devices.editCodeUnavailable', { ref: code.reference }) : t('devices.editCode', { code: code.code }),
    width: 'wide',
    body: h('div',
      field(t('devices.fieldRemark'), noteInput),
      h('div.form-row',
        field(t('devices.maxUses'), maxUsesInput, t('devices.zeroUnlimited')),
        field(t('devices.resetValidity'), hoursInput, t('devices.resetValidityHint')),
      ),
      h('div.notice.notice-warn',
        h('span.notice-icon', '!'),
        h('div', t('devices.resetValidityWarn')),
      ),
    ),
    confirmText: t('devices.save'),
    onConfirm: async () => {
      await api(`/admin/enroll-codes/${encodeURIComponent(target)}`, {
        method: 'PUT',
        body: {
          note: noteInput.value.trim(),
          maxUses: Number(maxUsesInput.value) || 0,
          validHours: Number(hoursInput.value) || 0,
        },
      });
      toast('ok', t('devices.saved'));
      await refresh();
      return true;
    },
  });
}
