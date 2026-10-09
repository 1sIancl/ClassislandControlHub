/**
 * 设备管理视图：按「楼栋 → 楼层 → 教室」三级结构管理教室终端。
 * 顶层分组是楼栋，下层分组是楼层，每台设备就是一间教室；支持拖拽调整归属，
 * 并可切换到列表视图查看完整状态明细。注册码管理一并放在本页。
 */

import { api, fetchBlob } from '../core/api.js?v=82';
import { toastError, errorBlock } from '../core/errors.js?v=82';
import {
  h, clear, formatDateTime, relativeTime, toast, loadingBlock, skeletonRows,
  modal, confirmDialog, deviceStateBadge, syncBadge,
  emptyState, field, select, copyText, append, undoBar,
} from '../core/ui.js?v=82';
import { showContextMenu } from '../core/contextmenu.js?v=82';
import { getLayout, saveLayout } from '../core/prefs.js?v=82';
import { auditTimelineSection } from '../core/audit-timeline.js?v=82';
import { selectionSummary } from '../core/batch-summary.js?v=82';

export const meta = {
  title: '设备管理',
  subtitle: '按楼栋 / 楼层 / 教室查看归属，拖拽即可调整',
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
    key: 'name', label: '教室 / 设备',
    cell: (d) => h('td',
      h('div.cell-main', d.name),
      h('div.cell-sub', [d.machineName, d.ipAddress].filter(Boolean).join(' · ') || d.id.slice(0, 8)),
      d.remark ? h('div.cell-sub', { style: { color: 'var(--text-faint)' } }, `备注：${d.remark}`) : null,
    ),
  },
  {
    key: 'group', label: '楼栋 / 楼层',
    cell: (d) => h('td', groupPathBadge(d)),
  },
  {
    key: 'tags', label: '标签',
    cell: (d) => h('td', (d.tags || []).length
      ? h('div', { style: { display: 'flex', flexWrap: 'wrap', gap: '4px' } }, ...d.tags.map(tagBadge))
      : h('span', { style: { color: 'var(--text-faint)' } }, '—')),
  },
  {
    key: 'state', label: '状态',
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
    key: 'sync', label: '配置同步',
    cell: (d) => h('td', syncBadge(d),
      h('div', { style: { fontSize: '11px', color: 'var(--text-faint)', marginTop: '3px' } },
        `档案版本 ${d.serverRevision}`)),
  },
  {
    key: 'plan', label: '当前课表',
    cell: (d) => h('td', d.currentClassPlanName || h('span', { style: { color: 'var(--text-faint)' } }, '—')),
  },
  {
    key: 'version', label: '版本信息',
    cell: (d) => h('td',
      h('div', { style: { fontSize: '12px' } }, `CI ${d.classIslandVersion || '—'}`),
      h('div', { style: { fontSize: '11px', color: 'var(--text-faint)' } }, `插件 ${d.pluginVersion || '—'}`),
    ),
  },
  {
    key: 'heartbeat', label: '最近心跳',
    cell: (d) => h('td',
      h('div', { style: { fontSize: '12.5px' } }, relativeTime(d.lastSeenAt)),
      h('div', { style: { fontSize: '11px', color: 'var(--text-faint)' } },
        d.lastSyncAt ? `同步 ${relativeTime(d.lastSyncAt)}` : '尚未同步'),
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
  const options = [{ value: '', label: '（未分组）' }];
  for (const { root, floors } of buildTree()) {
    options.push({ value: root.id, label: `${root.name} · 整栋` });
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
    return h('span.badge.badge-neutral', '未分组');
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
    title: `标签：${tag.name}`,
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
        '还没有标签。在下面新建一个，比如「高考考场」「待维修」。'));
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
      type: 'text', placeholder: '新建标签…', maxlength: '24',
    });
    const addButton = h('button.btn.btn-sm', {
      type: 'button',
      onClick: async () => {
        const name = nameInput.value.trim();
        if (!name) {
          toast('warn', '请先填标签名');
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
          toast('ok', '已新建标签', `「${tag.name}」已勾选，点「保存」后生效。`);
        } catch (err) {
          toastError(err, '新建标签失败');
        }
      },
    }, '新建');

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
  online: '在线',
  syncing: '同步中',
  pending: '待同步',
  error: '异常',
  offline: '离线',
  off: '已停用',
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
}

function renderToolbar() {
  const tree = buildTree();

  return h('div.toolbar',
    h('input', {
      type: 'text',
      // class 供键盘快捷键「/」定位搜索框（见 core/shortcuts.js）
      class: 'toolbar-search',
      placeholder: '搜索教室名 / 机器名 / IP…',
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
        { value: '', label: '全部标签' },
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
    h('button.btn.btn-sm', { type: 'button', onClick: exportDevices }, '导出 CSV'),
    h('button.btn.btn-sm', { type: 'button', onClick: openImportDialog }, '批量导入'),
    view === 'groups' && tree.length > 0
      ? h('div.segmented',
        h('button', {
          class: `segmented-item${filter.buildingId === '' ? ' active' : ''}`,
          type: 'button',
          onClick: () => switchBuilding(''),
        }, '全部'),
        ...tree.map(({ root }) => h('button', {
          class: `segmented-item${filter.buildingId === root.id ? ' active' : ''}`,
          type: 'button',
          onClick: () => switchBuilding(root.id),
        }, root.name)),
        h('button', {
          class: `segmented-item${filter.buildingId === NO_BUILDING ? ' active' : ''}`,
          type: 'button',
          onClick: () => switchBuilding(NO_BUILDING),
        }, '未分组'),
      )
      : null,
    h('div.segmented',
      h('button', {
        class: `segmented-item${view === 'groups' ? ' active' : ''}`,
        type: 'button',
        onClick: () => switchView('groups'),
      }, '看板视图'),
      h('button', {
        class: `segmented-item${view === 'list' ? ' active' : ''}`,
        type: 'button',
        onClick: () => switchView('list'),
      }, '列表视图'),
    ),
    h('button.btn.btn-sm.btn-primary', { type: 'button', onClick: () => openGroupDialog(null) }, '+ 新建楼栋'),
    view === 'list'
      ? h('button.btn.btn-sm', { type: 'button', onClick: openColumnCustomize }, '列设置')
      : null,
    h('button.btn.btn-sm', { type: 'button', onClick: () => refresh(true) }, '刷新'),
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

  if (showToast) toast('ok', '已刷新');
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
  { value: '', label: '全部' },
  { value: 'online', label: '在线' },
  { value: 'pending', label: '待同步' },
  { value: 'error', label: '异常' },
  { value: 'offline', label: '离线' },
  { value: 'revoked', label: '已停用' },
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
      title: tab.value ? `只看「${tab.label}」的设备` : '显示全部设备',
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
      ? emptyState('folder', '还没有楼栋',
        '按「楼栋 → 楼层 → 教室」组织：先建一栋楼，再往楼里加楼层，最后把教室设备拖进对应楼层。',
        h('button.btn.btn-primary', { type: 'button', onClick: () => openGroupDialog(null) }, '新建楼栋'))
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
    h('div.ov-stat', h('b', String(tree.length)), h('span', allBuildings ? '栋' : '个顶层分组')),
    h('div.ov-stat', h('b', String(floors)), h('span', '层')),
    h('div.ov-stat', h('b', String(total)), h('span', '间教室')),
    h('div.ov-online',
      h('div.ov-online-head',
        h('span', '在线设备'),
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
          h('em.bblock-kind', root.kind === 'building' ? '楼栋' : '分组')),
        h('span.gcard-meta',
          `${floors.length} 层 · ${all.length} 间 · 在线 ${online}`
          + (profile ? ` · ${profile.name}` : '')),
      ),
      h('div.gcard-actions',
        h('button.gcard-act', { type: 'button', title: '让该楼栋设备立即重新拉取配置', onClick: () => pushGroup(root) }, '下发'),
        h('button.gcard-act', { type: 'button', title: '编辑楼栋', onClick: () => openGroupDialog(root) }, '编辑'),
        h('button.gcard-act.danger', { type: 'button', title: '删除楼栋（连同楼层）', onClick: () => removeGroup(root) }, '删除'),
      ),
    ),
    h('div.bblock-floors',
      ...cards,
      h('button.floor-add', {
        type: 'button',
        title: `在「${root.name}」下新增楼层`,
        onClick: () => openGroupDialog(null, { parent: root }),
      }, '＋ 添加楼层'),
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
    body.appendChild(h('div.fcard-empty', isBare ? '暂无未分层设备' : '把教室拖到这里'));
  } else {
    body.append(...members.map((d) => deviceChip(d)));
  }

  // 楼栋还没有楼层时，这张卡片直接叫「本栋教室」，避免出现「未分层」这种别扭的说法。
  const hasFloors = cache.groups.some((g) => g.parentId === building.id);
  const bareTitle = hasFloors ? '未分层' : '本栋教室';

  const head = h('div.fcard-head',
    h('span.fcard-name', isBare ? bareTitle : floor.name),
    h('span.fcard-meta', `${members.length} 间${online > 0 ? ` · 在线 ${online}` : ''}`),
    isBare
      ? null
      : h('div.fcard-actions',
        h('button.fcard-act', { type: 'button', title: '编辑楼层', onClick: () => openGroupDialog(floor) }, '编辑'),
        h('button.fcard-act.danger', { type: 'button', title: '删除楼层', onClick: () => removeGroup(floor) }, '删除'),
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
    body.appendChild(h('div.fcard-empty', '没有未分组的设备'));
  } else {
    body.append(...members.map((d) => deviceChip(d)));
  }

  const card = h('div.fcard.bare',
    h('div.fcard-head',
      h('span.fcard-name', '未分组设备'),
      h('span.fcard-meta', `${members.length} 间`),
    ),
    body);
  bindDropTarget(card, body, '');

  return h('section.bblock.ungrouped', { dataset: { color: 'slate' } },
    h('div.bblock-head',
      h('span.gcard-badge', '—'),
      h('div.bblock-title',
        h('span.bblock-name', '未归入楼栋'),
        h('span.gcard-meta', '把教室拖到这里即可移出分组'),
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
  chip.addEventListener('click', () => {
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

  chip?.classList.toggle('selected', selection.has(id));
  refreshSelectionBar();
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
    toast('ok', '已调整归属', `${device.name} → ${target ? groupPathLabel(target) : '未分组'}`);
    repaintBoard();
  } catch (err) {
    toastError(err, '调整归属失败');
  }
}

function groupPathLabel(group) {
  const parent = group.parentId ? cache.groups.find((g) => g.id === group.parentId) : null;
  return parent ? `${parent.name} / ${group.name}` : `${group.name} · 整栋`;
}

async function pushGroup(group) {
  if (!await confirmDialog('下发到分组',
    `将立即通知「${group.name}」及其下楼层里的在线设备重新拉取配置。确定继续吗？`, '下发')) {
    return;
  }

  try {
    const result = await api('/admin/push', {
      method: 'POST',
      body: { scope: 'group', targetIds: [group.id], force: false, message: '' },
    });
    toast('ok', '下发已发出', `影响 ${result.affected} 台设备。`);
  } catch (err) {
    toastError(err, '下发失败');
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
    placeholder: isFloor ? '例如：3 层' : '例如：一号教学楼',
  });
  const descInput = h('input', { type: 'text', value: group?.description || '', placeholder: '可选' });

  // 上级：顶层 = 楼栋；选中某个楼栋 = 楼层。
  const roots = buildTree().map((b) => b.root).filter((r) => r.id !== group?.id);
  const parentSelect = select(
    [{ value: '', label: '（顶层：楼栋 / 独立分组）' }, ...roots.map((r) => ({ value: r.id, label: r.name }))],
    parentOfSelf,
  );

  const profileSelect = select(
    [
      { value: '', label: '（不指定，向上回退到楼栋 / 全局默认档案）' },
      ...cache.profiles.map((p) => ({ value: p.id, label: `${p.name}（内容版本 ${p.revision}）` })),
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
    badgePreview.textContent = (nameInput.value.trim() || '新').slice(0, 1);
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
        `将作为「${parent ? parent.name : ''}」下的楼层。教室设备直接拖进楼层即可。`;
    } else {
      hint.lastChild.textContent = '顶层分组按「楼栋」呈现；不填上级时它就是一棵独立的楼栋 / 分组。';
    }
  };

  parentSelect.addEventListener('change', updateHint);
  updateHint();

  modal({
    title: group ? `编辑 · ${group.name}` : (options.parent ? `在「${options.parent.name}」下新增楼层` : '新建楼栋'),
    width: 'wide',
    body: h('div',
      field('名称', nameInput),
      field('备注', descInput, '仅用于管理端展示。'),
      field('上级', parentSelect, '层级最多两层：楼栋 → 楼层；教室不再建分组，直接用设备表示。'),
      hint,
      field('标识色', h('div.color-row', badgePreview, swatches),
        '楼栋用这个颜色标识，楼层默认沿用所属楼栋的颜色。'),
      field('默认配置档案', profileSelect,
        '留空时楼层会回退到所属楼栋的默认档案，再回退到全局默认档案。'),
    ),
    confirmText: group ? '保存' : '创建',
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
        toast('warn', '请填写名称');
        return false;
      }

      try {
        if (group) {
          await api(`/admin/groups/${group.id}`, { method: 'PUT', body });
          toast('ok', '已保存', '该分组下的设备会立即重新拉取配置。');
        } else {
          await api('/admin/groups', { method: 'POST', body });
          toast('ok', body.parentId ? '楼层已创建' : '楼栋已创建');
        }
      } catch (err) {
        toastError(err, '保存失败');
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
    message = `删除「${group.name}」会连同其下 ${floors.length} 个楼层一起删除，`
      + `其中 ${affected.length} 台设备将变为未分组。确定继续吗？`;
  } else if (affected.length > 0) {
    message = `删除「${group.name}」后，其中 ${affected.length} 台设备会变为未分组（并回退到楼栋 / 全局默认档案）。确定继续吗？`;
  } else {
    message = `确定删除「${group.name}」吗？`;
  }

  if (!await confirmDialog('删除分组', message, '删除', true)) return;

  await api(`/admin/groups/${group.id}`, { method: 'DELETE' });
  toast('ok', '已删除');
  await refresh();
}

// ── 列表视图 ────────────────────────────────────────────────────────────

function renderTable() {
  const devices = filteredDevices();

  if (cache.devices.length === 0) {
    // 首次使用的空状态：只说「没有设备」没用，要告诉人**接下来做什么**，
    // 否则他会以为这页坏了（下方「注册码」区块就是入口）。
    return h('div.card',
      emptyState('monitor', '还没有设备接入',
        '三步接入第一台教室电脑（下方「注册码」区块可随时生成）。', null, [
          '生成一个注册码并复制',
          '在教室电脑的 ClassIsland 里安装集控插件，填入该注册码',
          '设备会自动出现在本页，再拖到对应的楼层即可',
        ]),
    );
  }

  if (devices.length === 0) {
    // 筛选结果为空时给一个出口：否则用户只能自己回想「我刚才是按什么筛的」。
    return h('div.card',
      emptyState('search', '没有匹配的设备', '试试放宽条件或换个关键词。',
        h('button.btn.btn-sm', {
          type: 'button',
          onClick: () => {
            filter.keyword = '';
            filter.state = '';
            filter.groupId = '';
            repaintAll();
          },
        }, '清空筛选')),
    );
  }

  const allSelected = devices.length > 0 && devices.every((d) => selection.has(d.id));
  const headBox = h('input', { type: 'checkbox' });
  headBox.checked = allSelected;
  headBox.title = '全选当前筛选结果';
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
          h('th', { style: { textAlign: 'right' } }, '操作'),
        )),
        h('tbody', ...devices.map((d) => {
          const box = h('input', { type: 'checkbox' });
          box.checked = selection.has(d.id);
          box.addEventListener('change', () => {
            if (box.checked) {
              selection.add(d.id);
            } else {
              selection.delete(d.id);
            }
            repaintAll();
          });

          return h('tr', { dataset: { deviceId: d.id } },
            h('td', box),
            ...visibleColumns().map((c) => c.cell(d)),
            h('td.actions',
              h('button.btn.btn-sm', { type: 'button', onClick: () => openDeviceDialog(d) }, '编辑'),
              ' ',
              h('button.btn.btn-sm', { type: 'button', onClick: () => openLogsDialog(d) }, '日志'),
              ' ',
              h('button.btn.btn-sm', {
                type: 'button',
                onClick: () => toggleRevoke(d),
              }, d.revoked ? '恢复' : '停用'),
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
    { label: '查看详情', action: () => openDeviceDialog(device) },
    { label: '发送通知', action: () => bulkNotify([id]) },
    { label: '查看日志', action: () => openLogsDialog(device) },
    '-',
    { label: '重启 ClassIsland', action: () => bulkRestart([id]) },
    {
      label: '关机',
      danger: true,
      action: () => bulkPower([id], 'power.shutdown', '关机',
        '计算机会立即关机，未保存的工作会丢失。'),
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
    h('span.bulk-text', `已选 ${ids.length} 台`),
    h('button.btn.btn-sm', {
      type: 'button',
      onClick: async () => {
        const targets = active();
        if (targets.length === 0) { toast('warn', '所选设备均已被停用'); return; }
        if (!await confirmDialog('批量下发配置',
          `将通知 ${targets.length} 台设备立即重新拉取配置。\n`
          + `${selectionSummary(cache.devices, ids)}\n\n确定继续吗？`, '下发')) {
          return;
        }

        const r = await api('/admin/push', {
          method: 'POST',
          body: { scope: 'device', targetIds: targets, force: true, message: '' },
        });
        toast('ok', '已下发', `影响 ${r.affected} 台设备。`);
        selection.clear();
        await refresh();
      },
    }, '批量下发配置'),
    h('button.btn.btn-sm', {
      type: 'button',
      onClick: () => bulkNotify(active()),
    }, '发送通知'),
    h('button.btn.btn-sm', {
      type: 'button',
      onClick: () => bulkRestart(active()),
    }, '重启 ClassIsland'),
    h('button.btn.btn-sm.btn-danger', {
      type: 'button',
      onClick: () => bulkPower(active(), 'power.restart', '重启计算机', '计算机将立即重启，未保存的工作会丢失。'),
    }, '重启计算机'),
    h('button.btn.btn-sm.btn-danger', {
      type: 'button',
      onClick: () => bulkPower(active(), 'power.shutdown', '关机', '计算机会立即关机，未保存的工作会丢失。'),
    }, '关机'),
    h('button.btn.btn-sm', {
      type: 'button',
      onClick: () => bulkPower(active(), 'power.sleep', '睡眠', '计算机将进入睡眠，按任意键或电源键即可唤醒。'),
    }, '睡眠'),
    h('button.btn.btn-sm.btn-ghost', {
      type: 'button',
      onClick: () => { selection.clear(); repaintAll(); },
    }, '取消选择'),
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
  if (okMessage) toast('ok', '已下载', okMessage);
}

/** 导出设备清单为 CSV：默认脱敏内网 IP —— 导出文件常被当附件转发，默认脱敏比事后追责划算。 */
async function exportDevices() {
  try {
    await downloadFromApi('/admin/devices/export',
      `devices-${new Date().toISOString().slice(0, 10)}.csv`,
      '文件里的内网 IP 已脱敏；需要完整地址请用接口加 mask=false。');
  } catch (err) {
    toastError(err, '导出失败');
  }
}

/**
 * 批量导入：粘贴 CSV → **先预演**（只出报告）→ 确认后正式导入。
 * <para>预注册出来的设备还没有令牌，等教室端以相同机器名首次注册时自动认领分组 / 档案 / 备注。</para>
 */
function openImportDialog() {
  const csvBox = h('textarea', {
    rows: '9',
    placeholder: '粘贴 CSV 内容（需含表头）。可以先「导出 CSV」拿一份改，或用下方「下载模板」。',
    style: { width: '100%', fontFamily: 'ui-monospace, Consolas, monospace', fontSize: '12.5px' },
  });
  const result = h('div', { style: { marginTop: '12px' } });

  const run = async (dryRun) => {
    if (!csvBox.value.trim()) { toast('warn', '请先粘贴 CSV 内容'); return; }
    clear(result);
    result.appendChild(loadingBlock(dryRun ? '正在预演…' : '正在导入…'));
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
    title: '批量导入设备（CSV）',
    width: 'xwide',
    hideFooter: true,
    body: h('div',
      h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', '先用「预演」看一遍逐行结果，确认无误后再正式导入。'
          + '分组与配置档案按**名称**匹配，名字写错的那一行会报错并跳过；'
          + '更新已有设备时，空单元格表示「这一项不改」。')),
      h('div', { style: { marginTop: '12px' } }, csvBox),
      h('div', { style: { display: 'flex', gap: '8px', marginTop: '10px', flexWrap: 'wrap' } },
        h('button.btn', { type: 'button', onClick: () => run(true) }, '预演（不写入）'),
        h('button.btn.btn-primary', { type: 'button', onClick: () => run(false) }, '正式导入'),
        h('div.spacer'),
        h('button.btn.btn-ghost', {
          type: 'button',
          onClick: () => downloadFromApi('/admin/devices/import/template', 'devices-import-template.csv'),
        }, '下载模板'),
      ),
      result,
    ),
  });
}

/** 逐行结果表：把「哪几行会新增、哪几行报错」直接摊开，避免盲导。 */
function renderImportReport(report, dryRun) {
  const head = h('div', { style: { fontSize: '12.5px', marginBottom: '6px' } },
    `${dryRun ? '预演结果' : '导入结果'}：`,
    h('b', `${report.created}`), ' 台预注册，',
    h('b', `${report.updated}`), ' 台更新，',
    report.failed > 0 ? h('span', { style: { color: 'var(--danger)' } }, `${report.failed} 行出错`) : '0 行出错');

  if (report.rows.length === 0) {
    return h('div', head, '没有可处理的数据行。');
  }

  const table = h('div.table-wrap', { style: { maxHeight: '260px', overflowY: 'auto' } },
    h('table.table',
      h('thead', h('tr', h('th', '行'), h('th', '动作'), h('th', '设备'), h('th', '说明'))),
      h('tbody', ...report.rows.map((row) => h('tr',
        h('td', `${row.line}`),
        h('td', h('span.badge' + (row.action === '错误' ? '.badge-danger' : '.badge-info'), row.action)),
        h('td', row.deviceName || '—'),
        h('td', { style: { fontSize: '12px' } }, row.message || '—'),
      )))));

  return h('div', head, table, dryRun
    ? h('div', { style: { marginTop: '8px', fontSize: '12px', color: 'var(--text-dim)' } },
      '这只是预演，还没有写入任何数据。确认无误后点「正式导入」。')
    : null);
}

/** 批量通知：填一次内容，向选中的多台设备统一下发（离线设备不排队，通知讲时效）。 */
function bulkNotify(ids) {
  if (ids.length === 0) { toast('warn', '所选设备均已被停用'); return; }

  const titleInput = h('input', { type: 'text', placeholder: '例如：紧急通知' });
  const msgInput = h('textarea', { placeholder: '提醒内容…', style: { minHeight: '80px' } });
  const speakChk = h('input', { type: 'checkbox' });

  modal({
    title: `向 ${ids.length} 台设备发送通知`,
    body: h('div',
      // 通知是「讲时效」的：离线设备不排队——等它上线再收到这条通知，内容往往已经没意义了（#52）。
      h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', selectionSummary(cache.devices, [...selection], {
          offlineNote: '不会收到（通知不排队：等它上线，这条内容通常已经过时）',
        }))),
      field('标题', titleInput, '可留空，只发正文。支持变量：{教室名} {机器名} {分组} {IP} {时间} {日期}'),
      field('内容', msgInput, '同样支持上面那组变量，按各教室自动替换——例如「请 {教室名} 于 {时间} 关闭投影」。'),
      h('label.checkbox-field', speakChk,
        h('span', '语音播报'),
        h('span', { style: { color: 'var(--text-faint)', fontSize: '12px' } }, '（教室大屏会朗读这条内容）')),
    ),
    confirmText: '发送',
    onConfirm: async () => {
      const title = titleInput.value.trim();
      const message = msgInput.value.trim();
      if (!title && !message) { toast('warn', '请填写通知内容'); return false; }

      const r = await api('/admin/devices/command', {
        method: 'POST',
        body: {
          kind: 'notify',
          payload: JSON.stringify({ title, message, speak: speakChk.checked }),
          deviceIds: ids,
          includeOffline: false,
        },
      });
      toast('ok', '已发送', broadcastSummary(r));
      selection.clear();
      await refresh();
      return true;
    },
  });
}

/** 批量重启 ClassIsland 进程（不动 Windows 本身，大屏会自动恢复）。 */
async function bulkRestart(ids) {
  if (ids.length === 0) { toast('warn', '所选设备均已被停用'); return; }
  if (!await confirmDialog('重启 ClassIsland',
    `将重启 ${ids.length} 台设备上的 ClassIsland 进程（大屏会短暂黑屏后自动恢复）。\n`
    + `${selectionSummary(cache.devices, [...selection])}\n\n确定继续吗？`, '重启')) {
    return;
  }

  await sendGuardedCommand({ kind: 'restart', deviceIds: ids, includeOffline: false }, '重启 ClassIsland');
  selection.clear();
  await refresh();
}

/** 批量电源操作：关机 / 重启 / 睡眠（仅 Windows 终端，二次确认后立即执行，不可撤销）。 */
async function bulkPower(ids, kind, label, warning) {
  if (ids.length === 0) { toast('warn', '所选设备均已被停用'); return; }
  if (!await confirmDialog(`批量${label}`,
    `将对 ${ids.length} 台设备执行「${label}」。\n`
    + `${selectionSummary(cache.devices, [...selection])}\n`
    + `${warning}\n\n该操作不可撤销，确定继续吗？`,
    label, true)) {
    return;
  }

  await sendGuardedCommand({ kind, deviceIds: ids, includeOffline: false }, `批量${label}`);
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
  toast('ok', '已下发', `${broadcastSummary(r)}将在 15 秒后执行。`);
  undoBar(`${label}将在 15 秒后执行`, 15, async () => {
    const canceled = await api('/admin/devices/commands/cancel', {
      method: 'POST',
      body: { ids: r.commandIds || [] },
    });
    toast('ok', '已撤销', canceled > 0 ? `取消了 ${canceled} 条指令。` : '指令已派发或已结束，无法撤销。');
  });
  return r;
}

/** 批量操作结果的统一文案（不同接口返回字段略有差异）。 */
function broadcastSummary(result) {
  if (typeof result === 'number') return `影响 ${result} 台设备。`;
  if (result && typeof result.affected === 'number') return `影响 ${result.affected} 台设备。`;
  if (result && typeof result.queued === 'number') return `已排队 ${result.queued} 条指令。`;
  return '指令已下发。';
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
    title: '自定义设备表格列',
    body: container,
    confirmText: '完成',
    onConfirm: () => {
      repaintBoard();
      return true;
    },
  });
}

/** 指令类型 → 中文名。未列出的类型直接显示原文，不猜。 */
const COMMAND_KIND_LABELS = {
  shell: '执行命令',
  notify: '提醒通知',
  'appearance.apply': '下发外观',
  'plugin.refresh': '刷新插件列表',
  'automation.list': '查询自动化信号',
  'automation.trigger': '触发自动化',
  screenshot: '屏幕截图',
  restart: '重启 ClassIsland',
  shutdown: '关机',
  'power.off': '关机',
  'power.reboot': '重启设备',
};

function commandKindLabel(kind) {
  if (!kind) return '（未知指令）';
  return COMMAND_KIND_LABELS[kind] ? `${COMMAND_KIND_LABELS[kind]}（${kind}）` : kind;
}

/** 把秒数写成紧凑文本：3 天 2 小时 / 5 分 30 秒。 */
function shortDuration(seconds) {
  const s = Math.max(0, Math.floor(seconds));
  const days = Math.floor(s / 86400);
  const hours = Math.floor((s % 86400) / 3600);
  const minutes = Math.floor((s % 3600) / 60);
  if (days > 0) return `${days} 天 ${hours} 小时`;
  if (hours > 0) return `${hours} 小时 ${minutes} 分`;
  if (minutes > 0) return `${minutes} 分 ${s % 60} 秒`;
  return `${s} 秒`;
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
    return diff <= 0 ? '已到期' : `还剩 ${text}`;
  }
  return diff <= 0 ? '已到时间' : `${text}后生效`;
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
    const title = h('div.config-section-title', '待执行指令');

    if (rows.length === 0) {
      return h('div',
        title,
        h('p.card-desc', { style: { margin: '0 0 4px' } }, device.online
          ? '队列是空的：已下发的指令都已经派发给这台设备。'
          : '队列是空的。这台设备当前离线，新下发的指令会先排队，等它上线后自动派发。'),
      );
    }

    return h('div',
      title,
      h('p.card-desc', { style: { margin: '0 0 8px' } },
        `有 ${rows.length} 条指令还没派发`
        + `${device.online ? '' : '（设备当前离线，上线后会自动派发）'}。`
        + '过期未派发的会自动作废，也可以在这里手动取消。'),
      h('div.config-panel', ...rows.map((row) => {
        const pendingNotBefore = row.notBefore && new Date(row.notBefore).getTime() > Date.now();
        return h('div.config-item',
          h('div', { style: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 } },
            h('span', { style: { fontWeight: '600', fontSize: '13px' } }, commandKindLabel(row.kind)),
            h('span', { style: { color: 'var(--text-faint)', fontSize: '12px' } },
              `${row.issuedBy || '—'} 于 ${formatDateTime(row.issuedAt)} 下发`),
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
              : h('span', { style: { color: 'var(--text-faint)', fontSize: '12px' } }, '长期有效'),
            h('button.btn.btn-sm.btn-danger', {
              type: 'button',
              title: '取消这条指令（设备尚未取走）',
              onClick: async () => {
                if (!await confirmDialog('取消指令',
                  `取消这条「${commandKindLabel(row.kind)}」？设备还没取走，取消后不会执行。`,
                  '取消指令', true)) {
                  return;
                }

                try {
                  await api(`/admin/devices/commands/${row.id}`, { method: 'DELETE' });
                  toast('ok', '已取消');
                  await reload();
                } catch (err) {
                  toastError(err, '取消失败');
                }
              },
            }, '取消'),
          ),
        );
      })),
    );
  }

  async function load() {
    clear(box);
    box.appendChild(h('p.card-desc', '正在读取待执行指令…'));
    try {
      const rows = await api(`/admin/devices/${device.id}/commands/queue`);
      clear(box);
      box.appendChild(renderQueue(rows, load));
      tick();
    } catch (err) {
      clear(box);
      box.appendChild(errorBlock(err, { title: '读取待执行指令失败', onRetry: load }));
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
    placeholder: '例如：三楼东侧 / 班主任 张老师',
  });

  const groupSelect = select(assignmentOptions(), device.groupId || '');

  const profileSelect = select(
    [
      { value: '', label: '（继承楼层 / 楼栋 / 默认档案）' },
      ...cache.profiles.map((p) => ({ value: p.id, label: `${p.name}（内容版本 ${p.revision}）` })),
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
    title: `教室 · ${device.name}`,
    width: 'wide',
    body: h('div',
      h('div.device-summary',
        h('div.summary-line', deviceStateBadge(device), syncBadge(device)),
        h('div.summary-line',
          h('span.summary-key', '机器名'), device.machineName || '—',
          h('span.summary-key', 'IP'), device.ipAddress || '—'),
        // 离线原因诊断：在线时不显示，离线时给出可行动的原因（从未连接 / 离线多久 / 最近报错）。
        device.offlineReason
          ? h('div.summary-line', { style: { color: 'var(--text-dim)' } },
            h('span.summary-key', '离线原因'), device.offlineReason)
          : null,
        h('div.summary-line',
          h('span.summary-key', 'ClassIsland'), device.classIslandVersion || '—',
          h('span.summary-key', '插件'), device.pluginVersion || '—'),
        h('div.summary-line',
          h('span.summary-key', '当前课表'), d0(device.currentClassPlanName)),
        h('div.summary-line',
          h('span.summary-key', '最近心跳'), relativeTime(device.lastSeenAt),
          h('span.summary-key', '注册时间'), formatDateTime(device.createdAt)),
      ),
      field('教室 / 设备名称', nameInput, '显示在管理界面与客户端中的名称，例如「高一(3)班」。'),
      field('所属楼栋 / 楼层', groupSelect, '也可以在看板里直接把设备拖到目标楼层。'),
      field('指定配置档案', profileSelect,
        '优先级：设备指定 → 所属楼层 → 所属楼栋 → 全局默认。'),
      field('备注', remarkInput, '仅管理端可见，用于在列表里快速认出这台设备。'),
      field('标签', tagPicker(tagSelection),
        '标签与「楼栋 / 楼层」是两回事：分组说明它在哪，标签说明它是什么（如「高考考场」「待维修」）。'
        + '一台设备可以有多个标签，也可以用标签批量挑设备。'),
      pendingCommands.el,
      auditTimeline.el,
      h('div.card-actions',
        h('button.btn.btn-sm', { type: 'button', onClick: () => openLogsDialog(device) }, '查看日志'),
        h('button.btn.btn-sm', {
          type: 'button',
          onClick: async () => {
            await toggleRevoke(device);
          },
        }, device.revoked ? '恢复设备' : '停用设备'),
        h('button.btn.btn-sm.btn-danger', {
          type: 'button',
          onClick: async () => {
            if (!await confirmDialog('删除设备',
              `删除「${device.name}」后，该设备需要重新用注册码接入。确定继续吗？`, '删除', true)) {
              return;
            }

            await api(`/admin/devices/${device.id}`, { method: 'DELETE' });
            toast('ok', '已删除');
            await refresh();
          },
        }, '删除设备'),
      ),
    ),
    confirmText: '保存',
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

      toast('ok', '已保存', `${nameInput.value.trim()} 的配置已更新。`);
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
    const ok = await confirmDialog('停用设备',
      `停用后「${device.name}」将无法再连接集控服务器，也不会收到新配置。确定继续吗？`,
      '停用', true);
    if (!ok) return;
  }

  await api(`/admin/devices/${device.id}/revoke`, {
    method: 'POST',
    body: { revoked: revoking },
  });
  toast('ok', revoking ? '已停用' : '已恢复', device.name);
  await refresh();
}

async function openLogsDialog(device) {
  const dialog = modal({
    title: `运行日志 · ${device.name}`,
    width: 'xwide',
    hideFooter: true,
    body: loadingBlock('正在读取日志…'),
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
    h('span', { style: { fontSize: '12.5px', color: 'var(--text-dim)' } }, `共 ${logs.length} 条`),
    h('div.spacer'),
    h('button.btn.btn-sm', {
      type: 'button',
      onClick: async () => {
        if (!await confirmDialog('清空日志', `确定清空「${device.name}」的全部上报日志吗？`, '清空', true)) return;
        await api(`/admin/devices/${device.id}/logs`, { method: 'DELETE' });
        toast('ok', '已清空');
        dialog.close();
      },
    }, '清空日志'),
  );

  const list = logs.length === 0
    ? emptyState('profiles', '暂无日志', '客户端会在同步或异常时上报日志。')
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
        h('h3', '设备注册码'),
        h('p.card-desc', '把注册码填入客户端的集控插件设置中即可完成接入；注册码可限制使用次数与有效期。'),
      ),
      h('button.btn.btn-primary.btn-sm', { type: 'button', onClick: openCreateCodeDialog }, '+ 生成注册码'),
    ),
    codes.length === 0
      ? emptyState('key', '还没有注册码', '生成一个注册码用于新设备接入。',
        h('button.btn.btn-primary', { type: 'button', onClick: openCreateCodeDialog }, '生成注册码'))
      : h('div.table-wrap',
        h('table.data',
          h('thead', h('tr',
            h('th', '注册码'),
            h('th', '备注'),
            h('th', '使用情况'),
            h('th', '有效期'),
            h('th', '创建时间'),
            h('th', { style: { textAlign: 'right' } }, '操作'),
          )),
          h('tbody', ...codes.map(renderCodeRow)),
        ),
      ),
  );
}

function renderCodeRow(code) {
  const remaining = code.remainingUses < 0 ? '不限' : code.remainingUses;
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
            h('span.badge.badge-danger', '不可用'),
            h('span', { style: { color: 'var(--text-faint)', fontSize: '12px' } },
              `加密密钥已更换，${code.reference}（请重新生成）`),
          ]
          : [
            h('code', { style: { fontSize: '14px', letterSpacing: '1.5px', fontWeight: '700' } }, code.code),
            h('button.btn.btn-ghost.btn-sm', {
              type: 'button',
              title: '复制注册码',
              onClick: () => copyText(code.code, '注册码已复制'),
            }, '复制'),
          ],
      ),
    ),
    h('td', code.note || h('span', { style: { color: 'var(--text-faint)' } }, '—')),
    h('td',
      h('span', `已用 ${code.usedCount} / 剩余 ${remaining}`),
    ),
    h('td',
      expired ? h('span.badge.badge-danger', '已过期')
        : exhausted ? h('span.badge.badge-danger', '次数已用尽')
          : code.expiresAt
            ? h('span', { style: { fontSize: '12px' } }, formatDateTime(code.expiresAt))
            : h('span.badge.badge-ok', '长期有效'),
    ),
    h('td', { style: { fontSize: '12px', color: 'var(--text-faint)' } }, formatDateTime(code.createdAt)),
    h('td.actions',
      h('button.btn.btn-sm', { type: 'button', onClick: () => openEditCodeDialog(code) }, '编辑'),
      ' ',
      h('button.btn.btn-sm.btn-danger', {
        type: 'button',
        onClick: async () => {
          if (!await confirmDialog('删除注册码', `确定删除注册码 ${target} 吗？已注册的设备不受影响。`, '删除', true)) return;
          await api(`/admin/enroll-codes/${encodeURIComponent(target)}`, { method: 'DELETE' });
          toast('ok', '已删除');
          await refresh();
        },
      }, '删除'),
    ),
  );
}

function openCreateCodeDialog() {
  const noteInput = h('input', { type: 'text', placeholder: '例如：高一（3）班' });
  const maxUsesInput = h('input', { type: 'number', value: '1', min: '0' });
  const hoursInput = h('input', { type: 'number', value: '72', min: '0' });

  modal({
    title: '生成注册码',
    width: 'wide',
    body: h('div',
      field('备注', noteInput, '仅用于管理端识别，不会下发给客户端。'),
      h('div.form-row',
        field('可注册次数', maxUsesInput, '填 0 表示不限次数（适合整班批量部署）。'),
        field('有效小时数', hoursInput, '填 0 表示长期有效。'),
      ),
    ),
    confirmText: '生成',
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
        title: '注册码已生成',
        width: 'wide',
        hideFooter: true,
        body: h('div',
          h('p', { style: { marginTop: '0', color: 'var(--text-dim)' } },
            '请把下面的注册码填入客户端的集控插件设置中：'),
          h('div.copy-row',
            h('div.code-block', result.code),
            h('button.btn', { type: 'button', onClick: () => copyText(result.code, '注册码已复制') }, '复制'),
          ),
          h('div.notice.notice-info', { style: { marginTop: '14px' } },
            h('span.notice-icon', 'i'),
            h('div', `可用次数：${result.maxUses === 0 ? '不限' : result.maxUses}；`
              + `有效期：${result.expiresAt ? formatDateTime(result.expiresAt) : '长期有效'}`),
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
    title: unavailable ? `编辑注册码 · ${code.reference}（不可用）` : `编辑注册码 · ${code.code}`,
    width: 'wide',
    body: h('div',
      field('备注', noteInput),
      h('div.form-row',
        field('可注册次数', maxUsesInput, '填 0 表示不限。'),
        field('重置有效期（小时）', hoursInput, '从当前时间起重新计算，填 0 表示长期有效。'),
      ),
      h('div.notice.notice-warn',
        h('span.notice-icon', '!'),
        h('div', '保存会重置有效期，已使用次数保持不变。'),
      ),
    ),
    confirmText: '保存',
    onConfirm: async () => {
      await api(`/admin/enroll-codes/${encodeURIComponent(target)}`, {
        method: 'PUT',
        body: {
          note: noteInput.value.trim(),
          maxUses: Number(maxUsesInput.value) || 0,
          validHours: Number(hoursInput.value) || 0,
        },
      });
      toast('ok', '已保存');
      await refresh();
      return true;
    },
  });
}
