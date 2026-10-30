/**
 * 报表页（#63 设备在线率 / #65 指令执行 / #67 操作热点）。
 *
 * 三块都只**聚合已经发生的事实**，不是为了好看而算的指标：
 * 在线率回答「哪几台教室老掉线」，指令执行回答「下发到底成不成」，操作热点回答「谁在动什么」。
 *
 * 设计取舍：
 *   - **按标签分别加载**：点哪个标签才拉哪块数据。90 天范围的聚合在几百台设备上并不便宜，
 *     没必要为没看的标签付这个成本；连点标签时用请求序号只认最后一次。
 *   - **只画聚合**，不给逐条明细入口——要追单条记录该去审计日志 / 指令历史，那里能筛选也有上下文。
 *   - 条形图用 div 宽度实现，不引图表库：形态简单，几百 KB 的依赖不值当。
 */

import { api } from '../core/api.js?v=160';
import { h, clear, formatDateTime, loadingBlock } from '../core/ui.js?v=160';
import { errorBlock } from '../core/errors.js?v=160';
import { auditActionLabel } from '../core/audit-actions.js?v=160';
import { t } from '../core/i18n.js?v=160';

export const meta = {
  title: t('reports.title'),
  subtitle: t('reports.subtitle'),
};

/** 时间范围（与后端 90 天上限一致）。 */
const RANGES = [
  { days: 7, label: t('reports.range7') },
  { days: 30, label: t('reports.range30') },
  { days: 90, label: t('reports.range90') },
];

export async function render(container) {
  clear(container);

  const state = { days: 7, tab: 'online', seq: 0 };
  const body = h('div');

  const rangeButtons = RANGES.map((range) => h('button.btn.btn-sm', {
    type: 'button',
    onClick: () => {
      state.days = range.days;
      paint();
      load();
    },
  }, range.label));

  const tabButtons = TABS.map((tab) => h('button.btn.btn-sm', {
    type: 'button',
    onClick: () => {
      state.tab = tab.key;
      paint();
      load();
    },
  }, tab.label));

  function paint() {
    // 直接改 className 而不是切 `active`：`.active` 在本项目里没有对应样式，
    // 视觉上会「点了没反应」。用既有的 btn-primary 表示选中。
    rangeButtons.forEach((button, i) => {
      button.className = `btn btn-sm${RANGES[i].days === state.days ? ' btn-primary' : ''}`;
    });
    tabButtons.forEach((button, i) => {
      button.className = `btn btn-sm${TABS[i].key === state.tab ? ' btn-primary' : ''}`;
    });
  }

  async function load() {
    const tab = TABS.find((t) => t.key === state.tab);
    const seq = ++state.seq;
    clear(body);
    body.appendChild(loadingBlock());

    try {
      const data = await tab.load(state.days);
      if (seq !== state.seq) {
        return;
      }

      clear(body);
      body.appendChild(tab.render(data));
    } catch (err) {
      if (seq !== state.seq) {
        return;
      }

      clear(body);
      body.appendChild(errorBlock(err, { title: t('reports.loadFailed'), onRetry: load }));
    }
  }

  container.appendChild(h('div.card',
    h('div.card-head',
      h('div',
        h('h3', t('reports.title')),
        h('p.card-desc', t('reports.dataNote'))),
      h('div.card-actions', ...rangeButtons)),
    h('div.card-actions', { style: { gap: '6px', marginTop: '4px' } }, ...tabButtons),
    body,
  ));

  paint();
  await load();
}

/** 标签定义：key / 标签名 / 取数 / 渲染。 */
const TABS = [
  {
    key: 'online',
    label: t('reports.onlineRate'),
    load: (days) => api('/admin/reports/online', { query: { days } }),
    render: renderOnline,
  },
  {
    key: 'commands',
    label: t('reports.commands'),
    load: (days) => api('/admin/reports/commands', { query: { days } }),
    render: renderCommands,
  },
  {
    key: 'sync',
    label: t('reports.sync'),
    load: (days) => api('/admin/reports/sync', { query: { days } }),
    render: renderSync,
  },
  {
    key: 'operations',
    label: t('reports.hotspots'),
    load: (days) => api('/admin/reports/operations', { query: { days } }),
    render: renderOperations,
  },
  {
    key: 'notifications',
    label: t('reports.noticeDelivery'),
    load: (days) => api('/admin/reports/notifications', { query: { days } }),
    render: renderNotifications,
  },
];

/**
 * 通知到达率（#7 回执的聚合）。
 *
 * <para>与「指令执行」报表的区别，正是这一块存在的理由：指令报的是
 * 「插件收下并调用了展示」，而这里报的是**教室端确认开始显示**。
 * 前者永远接近 100%，后者才可能暴露「发了没人看到」。</para>
 *
 * <para>没回报的设备**固定列出来**而不是只给个百分比：管理员的下一个动作是
 * 「去查是哪几台没到」，只给数字等于让他自己去翻设备列表。</para>
 */
function renderNotifications(data) {
  if (!data.totalNotifications) {
    return h('div',
      h('div.notice.notice-info', { style: { marginTop: '12px' } },
        h('span.notice-icon', 'i'),
        h('div', t('reports.noNotices1')
          + t('reports.noNotices2'))),
    );
  }

  return h('div',
    h('div', { style: { display: 'flex', gap: '10px', flexWrap: 'wrap', margin: '12px 0 4px' } },
      metric(t('reports.noticeCount'), data.totalNotifications, `${data.range.from} ~ ${data.range.to}`),
      // 目标数为 0 时显示「—」：写成 0% 会被读成「全部没到」，与事实相反。
      metric(t('reports.overallRate'), data.totalTargets > 0 ? asPercent(data.overallRate) : '—',
        t('reports.shownOfTargets', { shown: data.totalShown, total: data.totalTargets })),
      metric(t('reports.coveredDevices'), data.totalTargets, t('reports.summedByTargets'))),
    section(t('reports.dailyDelivery'), barList(data.daily, {
      value: (row) => row.count,
      format: (row) => t('reports.rowDeliveries', { extra: row.extra, count: row.count, rate: asPercent(row.rate) }),
      color: (row) => (row.rate >= 0.99 ? 'var(--ok)' : (row.rate >= 0.8 ? 'var(--warn)' : 'var(--danger)')),
      empty: t('reports.noNotices'),
    })),
    section(t('reports.noticeList'),
      h('div', { style: { marginTop: '2px' } }, ...data.notifications.map(renderNotificationRow))),
  );
}

/**
 * 单条通知的到达情况。
 * <para>把「没到的设备名」直接列出来：这是本报表唯一能直接促成行动的信息。</para>
 */
function renderNotificationRow(item) {
  const rate = item.arrivalRate;
  const tone = rate >= 0.99 ? 'var(--ok)' : (rate >= 0.8 ? 'var(--warn)' : 'var(--danger)');

  return h('div', {
    style: {
      display: 'flex', flexDirection: 'column', gap: '5px',
      padding: '9px 0', borderTop: '1px solid var(--border)',
    },
  },
  h('div', { style: { display: 'flex', justifyContent: 'space-between', gap: '10px', fontSize: '12.5px' } },
    h('span', {
      style: { minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
    }, t('reports.noticeItem', { at: formatDateTime(item.issuedAt), title: item.title || t('reports.untitled') })),
    h('span', {
      style: { flex: 'none', color: tone, fontVariantNumeric: 'tabular-nums' },
    }, `${item.shownCount} / ${item.targetCount} · ${asPercent(rate)}`)),
  item.pendingDevices.length > 0
    ? h('span', {
      style: { color: 'var(--warn)', fontSize: '11.5px' },
      title: item.pendingDevices.join('、'),
    }, t('reports.pendingDevices', { list: item.pendingDevices.slice(0, 6).join('、') })
      + (item.pendingDevices.length > 6 ? t('reports.pendingMore', { count: item.pendingDevices.length }) : ''))
    : null);
}

// ────────────────────────────── 通用小组件 ──────────────────────────────

/** 大数字块。 */
function metric(label, value, hint) {
  return h('div', {
    style: {
      display: 'flex', flexDirection: 'column', gap: '2px',
      minWidth: '132px', padding: '10px 12px', borderRadius: '8px',
      border: '1px solid var(--border)', background: 'var(--bg-panel)',
    },
  },
    h('span', { style: { color: 'var(--text-faint)', fontSize: '12px' } }, label),
    h('span', { style: { fontSize: '21px', fontWeight: '700' } }, value),
    hint ? h('span', { style: { color: 'var(--text-faint)', fontSize: '11.5px' } }, hint) : null,
  );
}

/** 区块标题 + 内容。 */
function section(title, content) {
  return h('div', { style: { marginTop: '16px' } },
    h('div.config-section-title', title),
    content,
  );
}

/**
 * 横向条形列表（三张报表共用）。
 * @param {Array} rows 数据桶。
 * @param {{value:Function, format?:Function, color?:Function, empty?:string}} options
 */
function barList(rows, options) {
  const list = rows || [];
  if (list.length === 0) {
    return h('p.card-desc', { style: { margin: '4px 0' } },
      options.empty || t('reports.noData'));
  }

  const max = Math.max(1, ...list.map((row) => options.value(row)));

  return h('div', { style: { display: 'flex', flexDirection: 'column', gap: '7px' } },
    ...list.map((row) => {
      const value = options.value(row);
      return h('div', { style: { display: 'flex', flexDirection: 'column', gap: '3px' } },
        h('div', { style: { display: 'flex', justifyContent: 'space-between', gap: '10px', fontSize: '12.5px' } },
          h('span', {
            style: { minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
            title: row.label,
          }, row.label || t('reports.notRecorded')),
          h('span', {
            style: { flex: 'none', color: 'var(--text-faint)', fontVariantNumeric: 'tabular-nums' },
          }, options.format ? options.format(row) : value)),
        h('div', { style: { height: '6px', borderRadius: '3px', background: 'var(--bg-hover)' } },
          h('div', {
            style: {
              // 最小 2%：值为 0 时也留一丝可见的痕迹，避免看起来像渲染坏了。
              width: `${Math.max(2, Math.round((value / max) * 100))}%`,
              height: '100%', borderRadius: '3px',
              background: options.color ? options.color(row) : 'var(--accent)',
            },
          })),
      );
    }));
}

/** 在线率颜色：越低越红——报表要看的就是这些。 */
function rateColor(rate) {
  if (rate < 0.8) return 'var(--danger)';
  if (rate < 0.95) return 'var(--warn)';
  return 'var(--ok)';
}

const asPercent = (rate) => `${Math.round((rate || 0) * 100)}%`;

// ────────────────────────────── #63 在线率 ──────────────────────────────

function renderOnline(data) {
  const noData = !data.sampleCount;

  return h('div',
    noData
      ? h('div.notice.notice-info', { style: { marginTop: '10px' } },
        h('span.notice-icon', 'i'),
        h('div', t('reports.noSamples1')
          + t('reports.noSamples2')))
      : null,
    h('div', { style: { display: 'flex', gap: '10px', flexWrap: 'wrap', margin: '12px 0 4px' } },
      metric(t('reports.overallOnline'), asPercent(data.overallRate),
        noData ? t('reports.waitingSamples') : t('reports.basedOnSamples', { count: data.sampleCount })),
      metric(t('reports.deviceCount'), data.deviceCount, t('reports.notRevoked')),
      metric(t('reports.sampleRounds'), data.sampleCount, t('reports.onePerMinute')),
      metric(t('reports.lastSample'), data.lastSampledDate || '—', t('reports.lastSampleHint'))),
    section(t('reports.dailyOnline'), barList(data.daily, {
      value: (row) => row.rate,
      format: (row) => t('reports.rateWithRounds', { rate: asPercent(row.rate), rounds: row.extra }),
      color: (row) => rateColor(row.rate),
    })),
    section(t('reports.lowestDevices'), barList(data.byDevice, {
      value: (row) => row.rate,
      format: (row) => t('reports.rateWithMinutes', { rate: asPercent(row.rate), minutes: Math.round(row.count / 60) }),
      color: (row) => rateColor(row.rate),
      empty: t('reports.noDevices'),
    })),
    section(t('reports.byGroup'), barList(data.byGroup, {
      value: (row) => row.rate,
      format: (row) => `${asPercent(row.rate)}`,
      color: (row) => rateColor(row.rate),
    })),
  );
}

// ────────────────────────────── #65 指令执行 ──────────────────────────────

function renderCommands(data) {
  return h('div',
    h('div', { style: { display: 'flex', gap: '10px', flexWrap: 'wrap', margin: '12px 0 4px' } },
      metric(t('reports.commandTotal'), data.total, t('reports.issuedInRange')),
      metric(t('reports.successRate'), asPercent(data.successRate), t('reports.excludesIncomplete')),
      metric(t('reports.succeeded'), data.done, t('reports.succeededHint')),
      metric(t('reports.failed'), data.failed, t('reports.failedHint')),
      metric(t('reports.voided'), data.expired, t('reports.voidedHint')),
      metric(t('reports.incomplete'), data.unfinished, t('reports.incompleteHint'))),
    section(t('reports.byDay'), barList(data.daily, {
      value: (row) => row.count,
      format: (row) => t('reports.dailyCommands', {
        count: row.count,
        suffix: row.extra ? t('reports.failedSuffix', { count: row.extra }) : '',
      }),
      color: (row) => (row.extra > 0 ? 'var(--warn)' : 'var(--accent)'),
    })),
    section(t('reports.byCommandKind'), barList(data.byKind, {
      value: (row) => row.count,
      format: (row) => t('reports.dailyCommands', {
        count: row.count,
        suffix: row.extra ? t('reports.failedSuffix', { count: row.extra }) : '',
      }),
      color: (row) => (row.extra > 0 ? 'var(--warn)' : 'var(--accent)'),
      empty: t('reports.noCommands'),
    })),
    section(t('reports.worstDevices'), barList(data.byDevice, {
      value: (row) => row.count,
      format: (row) => t('reports.dailyCommands', {
        count: row.count,
        suffix: row.extra ? t('reports.sharedSuffix', { count: row.extra }) : '',
      }),
      color: () => 'var(--danger)',
      empty: t('reports.noFailures'),
    })),
    section(t('reports.failReasons'), barList(data.failures, {
      value: (row) => row.count,
      color: () => 'var(--danger)',
      empty: t('reports.noFailureRecords'),
    })),
  );
}

// ────────────────────────────── #64 配置同步 ──────────────────────────────

function renderSync(data) {
  if (!data.pushCount) {
    return h('div',
      h('div.notice.notice-info', { style: { marginTop: '12px' } },
        h('span.notice-icon', 'i'),
        h('div', t('reports.noPushes1')
          + t('reports.noPushes2'))),
    );
  }

  return h('div',
    h('div', { style: { display: 'flex', gap: '10px', flexWrap: 'wrap', margin: '12px 0 4px' } },
      metric(t('reports.pushBatches'), data.pushCount, `${data.range.from} ~ ${data.range.to}`),
      // 可统计数为 0（目标全被停用/删除）时显示「—」：写成 0% 会被读成「全部失败」，与事实相反。
      metric(t('reports.overallCoverage'), data.totalTargets > 0 ? asPercent(data.overallRate) : '—',
        t('reports.syncedOfTargets', { synced: data.totalSynced, total: data.totalTargets })),
      metric(t('reports.fullySynced'), data.fullySyncedPushes, t('reports.totalBatches', { count: data.pushCount }))),
    section(t('reports.dailyPushes'), barList(data.daily, {
      value: (row) => row.count,
      format: (row) => t('reports.timesCount', { count: row.count }),
    })),
    section(t('reports.laggingRooms'), barList(data.stuckDevices, {
      value: (row) => row.count,
      format: (row) => t('reports.laggingCount', { count: row.count }),
      color: () => 'var(--danger)',
      empty: t('reports.noLagging'),
    })),
    section(t('reports.pushDetails'),
      h('div', { style: { marginTop: '2px' } }, ...data.pushes.map(renderPushRow))),
  );
}

/**
 * 单批下发的覆盖情况。
 * <para>用「已跟上 / 等待 / 离线」三段进度条而不是一个百分比：百分比看不出**为什么**没到 100%，
 * 而「还有 2 台离线、3 台在线但没拉取」才决定了下一步该做什么。</para>
 */
function renderPushRow(push) {
  const unsettled = push.waiting + push.offline;
  const total = Math.max(1, push.synced + unsettled);
  const width = (value) => `${(value / total) * 100}%`;

  return h('div', {
    style: {
      display: 'flex', flexDirection: 'column', gap: '5px',
      padding: '9px 0', borderTop: '1px solid var(--border)',
    },
  },
  h('div', { style: { display: 'flex', justifyContent: 'space-between', gap: '10px', fontSize: '12.5px' } },
    h('span', {
      style: { minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
    }, `${formatDateTime(push.createdAt)} · ${push.scopeLabel}`),
    h('span', {
      style: {
        flex: 'none',
        color: unsettled === 0 ? 'var(--ok)' : 'var(--warn)',
        fontVariantNumeric: 'tabular-nums',
      },
    }, (push.countable > 0
      // 可统计数为 0 说明这一批的目标全被停用/删掉了：显示「—」而不是 0/0，更不做成 0%。
      ? `${push.synced}/${push.countable}`
      : t('reports.allTargetsDisabled'))
      + (push.averageSeconds ? t('reports.averageSeconds', { seconds: Math.round(push.averageSeconds) }) : ''))),
  h('div', {
    style: {
      display: 'flex', height: '6px', borderRadius: '3px',
      overflow: 'hidden', background: 'var(--bg-hover)',
    },
  },
  push.synced ? h('div', { style: { width: width(push.synced), background: 'var(--ok)' } }) : null,
  push.waiting ? h('div', { style: { width: width(push.waiting), background: 'var(--warn)' } }) : null,
  push.offline ? h('div', { style: { width: width(push.offline), background: 'var(--text-faint)' } }) : null),
  h('div', { style: { color: 'var(--text-faint)', fontSize: '11.5px' } },
    [
      t('reports.pushedBy', { name: push.createdBy || t('reports.unknownActor') }),
      t('reports.pushRevision', { revision: push.revision }),
      push.excluded ? t('reports.pushExcluded', { count: push.excluded }) : '',
      push.message ? t('reports.pushMessage', { message: push.message }) : '',
    ].filter(Boolean).join(' · ')));
}

// ────────────────────────────── #67 操作热点 ──────────────────────────────

function renderOperations(data) {
  return h('div',
    h('div', { style: { display: 'flex', gap: '10px', flexWrap: 'wrap', margin: '12px 0 4px' } },
      metric(t('reports.actionTotal'), data.total, t('reports.auditCount')),
      metric(t('reports.activeAccounts'), data.actorCount, t('reports.activeAccountsHint')),
      metric(t('reports.dailyAverage'), data.range.days > 0 ? Math.round(data.total / data.range.days) : 0,
        `${data.range.from} ~ ${data.range.to}`)),
    section(t('reports.dailyActions'), barList(data.daily, {
      value: (row) => row.count,
      format: (row) => t('reports.timesCount', { count: row.count }),
    })),
    section(t('reports.byCategory'), barList(data.byCategory, {
      value: (row) => row.count,
      format: (row) => t('reports.timesCount', { count: row.count }),
      empty: t('reports.noActions'),
    })),
    section(t('reports.topAccounts'), barList(data.byActor, {
      value: (row) => row.count,
      format: (row) => t('reports.timesCount', { count: row.count }),
    })),
    // 动作码在列表里显示中文（复用 #42 那份映射），键名仍附在后面方便对照文档。
    section(t('reports.topActions'), barList(
      (data.topActions || []).map((row) => ({ ...row, label: auditActionLabel(row.key) })),
      {
        value: (row) => row.count,
        format: (row) => t('reports.rankRow', { count: row.count, key: row.key }),
      },
    )),
    section(t('reports.topTargets'), barList(data.topTargets, {
      value: (row) => row.count,
      format: (row) => t('reports.timesCount', { count: row.count }),
    })),
  );
}
