/**
 * 审计日志视图：记录管理员操作与设备同步事件，便于排查与追溯。
 */

import { api } from '../core/api.js?v=6';
import {
  h, clear, formatDateTime, toast, loadingBlock, emptyState,
} from '../core/ui.js?v=6';

export const meta = {
  title: '审计日志',
  subtitle: '管理员操作与设备同步记录',
};

const PAGE_SIZE = 50;
let query = { page: 1, search: '' };

export async function render(container) {
  clear(container);
  container.appendChild(loadingBlock());

  const result = await api('/admin/audit', {
    query: { page: query.page, pageSize: PAGE_SIZE, search: query.search },
  });

  const totalPages = Math.max(1, Math.ceil(result.total / PAGE_SIZE));

  clear(container);
  container.appendChild(h('div',
    h('div.card',
      h('div.card-head',
        h('div',
          h('h3', `操作记录（共 ${result.total} 条）`),
          h('p.card-desc', '包含客户端注册、配置下发、设备调整等操作。日志按保留策略自动清理。'),
        ),
        h('div.card-actions',
          h('input', {
            type: 'text',
            placeholder: '搜索操作人 / 动作 / 内容…',
            value: query.search,
            style: { width: '240px' },
            onChange: (e) => {
              query.search = e.target.value.trim();
              query.page = 1;
              render(document.getElementById('content'));
            },
          }),
          h('button.btn.btn-sm', {
            type: 'button',
            onClick: () => {
              query = { page: 1, search: '' };
              render(document.getElementById('content'));
            },
          }, '重置'),
        ),
      ),
      result.items.length === 0
        ? emptyState('📋', '没有匹配的记录', query.search ? '尝试调整搜索关键词。' : '系统还没有产生任何操作记录。')
        : h('div', h('div.table-wrap',
          h('table.data',
            h('thead', h('tr',
              h('th', { style: { width: '170px' } }, '时间'),
              h('th', { style: { width: '150px' } }, '操作人'),
              h('th', { style: { width: '170px' } }, '动作'),
              h('th', { style: { width: '150px' } }, '目标'),
              h('th', '详情'),
              h('th', { style: { width: '130px' } }, '来源 IP'),
            )),
            h('tbody', ...result.items.map((item) => h('tr',
              h('td', { style: { fontSize: '12px', color: 'var(--text-faint)' } }, formatDateTime(item.timestamp)),
              h('td', h('span', { style: { fontSize: '12.5px' } }, item.actor || '—')),
              h('td', h('code', { style: { fontSize: '11.5px' } }, item.action)),
              h('td', { style: { fontSize: '12.5px' } }, item.target || '—'),
              h('td', { style: { fontSize: '12.5px', color: 'var(--text-dim)', maxWidth: '380px' } }, item.detail || '—'),
              h('td', { style: { fontSize: '11.5px', fontFamily: 'var(--mono)', color: 'var(--text-faint)' } }, item.ipAddress || '—'),
            ))),
          ),
        )),
    ),
    totalPages > 1
      ? h('div', {
        style: {
          display: 'flex', alignItems: 'center', justifyContent: 'center',
          gap: '12px', marginTop: '16px',
        },
      },
        h('button.btn.btn-sm', {
          type: 'button',
          disabled: query.page <= 1,
          onClick: () => {
            query.page -= 1;
            render(document.getElementById('content'));
          },
        }, '上一页'),
        h('span', { style: { fontSize: '12.5px', color: 'var(--text-dim)' } },
          `第 ${query.page} / ${totalPages} 页`),
        h('button.btn.btn-sm', {
          type: 'button',
          disabled: query.page >= totalPages,
          onClick: () => {
            query.page += 1;
            render(document.getElementById('content'));
          },
        }, '下一页'),
      )
      : null,
  ));
}
