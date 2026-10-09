/**
 * 「操作历史」时间线（#42）。
 *
 * 审计日志页回答的是「整个系统发生了什么」；时间线回答的是**单个对象**的问题：
 * 「这台教室 / 这个档案，谁在什么时候改了什么」——排查「配置怎么被改掉了」时这才是要看的。
 *
 * 数据源是审计接口的 `target` **精确**过滤（不是全文 `search`）：
 * search 会把「详情里顺带提到这个名字」的记录也捞进来，噪声大到没法看。
 *
 * 注意 `target` 存的是**当时的名称**：对象改过名，改名前的那段历史查不到（界面里会说明）。
 */

import { api } from './api.js?v=82';
import { h, clear, formatDateTime, relativeTime, modal } from './ui.js?v=82';
import { errorBlock } from './errors.js?v=82';
import { auditActionLabel } from './audit-actions.js?v=82';

/** 时间线默认取多少条（够看清近期改动，又不至于把弹窗撑爆）。 */
const DEFAULT_LIMIT = 30;

/** 拉取某个对象的操作历史。 */
export async function fetchTimeline(target, limit = DEFAULT_LIMIT) {
  const result = await api('/admin/audit', {
    query: { target, pageSize: limit, page: 1 },
  });
  return result?.items || [];
}

/** 渲染成一行行的时间线（时间 · 操作者 · 动作 · 详情）。 */
function renderRows(items) {
  if (items.length === 0) {
    return h('p.card-desc', { style: { margin: '0' } }, '还没有以这个名称记录的操作。');
  }

  return h('div.config-panel', ...items.map((item) => h('div.config-item',
    { style: { alignItems: 'flex-start' } },
    h('div', { style: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 } },
      h('span', { style: { fontWeight: '600', fontSize: '13px' } }, auditActionLabel(item.action)),
      item.detail
        ? h('span', { style: { color: 'var(--text-faint)', fontSize: '12px', wordBreak: 'break-word' } },
          item.detail)
        : null,
      h('span', { style: { color: 'var(--text-faint)', fontSize: '11.5px' } },
        `${item.actor || '—'} · ${item.timestamp ? formatDateTime(item.timestamp) : '—'}`
        + `${item.timestamp ? `（${relativeTime(item.timestamp)}）` : ''}`),
    ),
  )));
}

/**
 * 内联时间线区块（用于设备详情这类已有弹窗/详情区的地方）。
 * @param {string} target 对象名称（审计里的 `target`）。
 * @returns {{el: HTMLElement, stop: Function}} 需要手动刷新时可调 `el` 上的重新加载。
 */
export function auditTimelineSection(target) {
  // 标题与说明放在**不会被清空的**外层，只有记录列表那个内层容器会被重画——
  // 否则 `clear(box)` 会把标题一起清掉（本轮实测就是踩了这个：有记录、没标题）。
  const listBox = h('div');
  const box = h('div',
    h('div.config-section-title', '操作历史'),
    h('p.card-desc', { style: { margin: '0 0 8px' } },
      '只显示以当前名称记录的操作；改过名的话，更早的历史请到「审计日志」里查。'),
    listBox,
  );

  const load = async () => {
    clear(listBox);
    listBox.appendChild(h('p.card-desc', '正在读取操作历史…'));
    try {
      const items = await fetchTimeline(target);
      clear(listBox);
      listBox.appendChild(renderRows(items));
    } catch (err) {
      clear(listBox);
      listBox.appendChild(errorBlock(err, { title: '读取操作历史失败', onRetry: load }));
    }
  };

  load();

  return { el: box, stop: () => {} };
}

/** 以弹窗形式显示时间线（用于没有内联空间的卡片，例如配置档案）。 */
export function openAuditTimeline(title, target) {
  const box = h('div');

  const load = async () => {
    clear(box);
    box.appendChild(h('p.card-desc', '正在读取操作历史…'));
    try {
      const items = await fetchTimeline(target);
      clear(box);
      box.appendChild(renderRows(items));
    } catch (err) {
      clear(box);
      box.appendChild(errorBlock(err, { title: '读取操作历史失败', onRetry: load }));
    }
  };

  load();

  modal({
    title: `操作历史 · ${title}`,
    width: 'wide',
    hideFooter: true,
    body: h('div',
      h('p.card-desc', { style: { margin: '0 0 10px' } },
        '只显示以当前名称记录的操作；改过名的话，更早的历史请到「审计日志」里查。'),
      box,
    ),
  });
}
