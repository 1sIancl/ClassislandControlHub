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

import { api } from '../core/api.js?v=59';
import { h, clear, loadingBlock } from '../core/ui.js?v=59';
import { errorBlock } from '../core/errors.js?v=59';
import { auditActionLabel } from '../core/audit-actions.js?v=59';

export const meta = {
  title: '报表',
  subtitle: '在线率 / 指令执行 / 操作热点',
};

/** 时间范围（与后端 90 天上限一致）。 */
const RANGES = [
  { days: 7, label: '最近 7 天' },
  { days: 30, label: '最近 30 天' },
  { days: 90, label: '最近 90 天' },
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
      body.appendChild(errorBlock(err, { title: '读取报表失败', onRetry: load }));
    }
  }

  container.appendChild(h('div.card',
    h('div.card-head',
      h('div',
        h('h3', '报表'),
        h('p.card-desc', '数据来自已经发生的事实；要看单条明细请去审计日志或指令历史。')),
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
    label: '设备在线率',
    load: (days) => api('/admin/reports/online', { query: { days } }),
    render: renderOnline,
  },
  {
    key: 'commands',
    label: '指令执行',
    load: (days) => api('/admin/reports/commands', { query: { days } }),
    render: renderCommands,
  },
  {
    key: 'operations',
    label: '操作热点',
    load: (days) => api('/admin/reports/operations', { query: { days } }),
    render: renderOperations,
  },
];

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
      options.empty || '这个范围内没有数据。');
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
          }, row.label || '（未记录）'),
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
        h('div', '还没有采样数据。在线率由服务端每分钟采样一次——服务刚启动、或刚升级到带此功能的版本时，这里会先是空的，'
          + '过上几分钟再回来刷新即可。'))
      : null,
    h('div', { style: { display: 'flex', gap: '10px', flexWrap: 'wrap', margin: '12px 0 4px' } },
      metric('整体在线率', asPercent(data.overallRate),
        noData ? '等待采样' : `基于 ${data.sampleCount} 轮采样`),
      metric('设备数', data.deviceCount, '未停用的设备'),
      metric('采样轮次', data.sampleCount, '每分钟一轮'),
      metric('最近采样', data.lastSampledDate || '—', '看统计是否还在跑')),
    section('按日在线率', barList(data.daily, {
      value: (row) => row.rate,
      format: (row) => `${asPercent(row.rate)}（${row.extra} 轮）`,
      color: (row) => rateColor(row.rate),
    })),
    section('在线率最低的设备', barList(data.byDevice, {
      value: (row) => row.rate,
      format: (row) => `${asPercent(row.rate)}（在线 ${Math.round(row.count / 60)} 分钟）`,
      color: (row) => rateColor(row.rate),
      empty: '没有可统计的设备。',
    })),
    section('按分组', barList(data.byGroup, {
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
      metric('指令总数', data.total, '范围内下发'),
      metric('成功率', asPercent(data.successRate), '不含未完成的'),
      metric('成功', data.done, '设备已回报成功'),
      metric('失败', data.failed, '设备回报失败'),
      metric('作废', data.expired, '排队超时未取走'),
      metric('未完成', data.unfinished, '排队中或已派发未回')),
    section('按日', barList(data.daily, {
      value: (row) => row.count,
      format: (row) => `${row.count} 条${row.extra ? `（失败/作废 ${row.extra}）` : ''}`,
      color: (row) => (row.extra > 0 ? 'var(--warn)' : 'var(--accent)'),
    })),
    section('按指令类型', barList(data.byKind, {
      value: (row) => row.count,
      format: (row) => `${row.count} 条${row.extra ? `（失败/作废 ${row.extra}）` : ''}`,
      color: (row) => (row.extra > 0 ? 'var(--warn)' : 'var(--accent)'),
      empty: '范围内没有下发过指令。',
    })),
    section('失败 / 作废最多的设备', barList(data.byDevice, {
      value: (row) => row.count,
      format: (row) => `${row.count} 条${row.extra ? ` / 共 ${row.extra} 条` : ''}`,
      color: () => 'var(--danger)',
      empty: '范围期内没有失败或作废的指令。',
    })),
    section('失败原因', barList(data.failures, {
      value: (row) => row.count,
      color: () => 'var(--danger)',
      empty: '没有失败记录。',
    })),
  );
}

// ────────────────────────────── #67 操作热点 ──────────────────────────────

function renderOperations(data) {
  return h('div',
    h('div', { style: { display: 'flex', gap: '10px', flexWrap: 'wrap', margin: '12px 0 4px' } },
      metric('操作总数', data.total, '审计条数'),
      metric('参与账号', data.actorCount, '至少操作过一次'),
      metric('日均操作', data.range.days > 0 ? Math.round(data.total / data.range.days) : 0,
        `${data.range.from} ~ ${data.range.to}`)),
    section('按日操作量', barList(data.daily, {
      value: (row) => row.count,
      format: (row) => `${row.count} 次`,
    })),
    section('按类别', barList(data.byCategory, {
      value: (row) => row.count,
      format: (row) => `${row.count} 次`,
      empty: '范围内没有操作记录。',
    })),
    section('最活跃的账号', barList(data.byActor, {
      value: (row) => row.count,
      format: (row) => `${row.count} 次`,
    })),
    // 动作码在列表里显示中文（复用 #42 那份映射），键名仍附在后面方便对照文档。
    section('最频繁的动作', barList(
      (data.topActions || []).map((row) => ({ ...row, label: auditActionLabel(row.key) })),
      {
        value: (row) => row.count,
        format: (row) => `${row.count} 次　${row.key}`,
      },
    )),
    section('被操作最多的对象', barList(data.topTargets, {
      value: (row) => row.count,
      format: (row) => `${row.count} 次`,
    })),
  );
}
