/**
 * 审计日志视图：记录管理员操作与设备同步事件，便于排查与追溯。
 */

import { api, fetchBlob } from '../core/api.js?v=136';
import { toastError } from '../core/errors.js?v=136';
import {
  h, clear, formatDateTime, toast, loadingBlock, emptyState,
} from '../core/ui.js?v=136';
import { t } from '../core/i18n.js?v=136';

export const meta = {
  title: t('audit.title'),
  subtitle: t('audit.subtitle'),
};

const PAGE_SIZE = 50;

/** 精细筛选的输入提示：动作前缀是这一排里最有用的一项（按类别看）。 */
const FILTER_HINTS = {
  actor: t('audit.filterActorExact'),
  action: t('audit.filterActionHint'),
  ip: t('audit.filterIp'),
  from: t('audit.filterFrom'),
  to: t('audit.filterTo'),
};

// 筛选条件：与 /admin/audit/export 共用同一套参数，保证「屏幕上看到的就是导出的」。
let query = { page: 1, search: '', actor: '', action: '', ip: '', from: '', to: '' };

/** 把界面上的筛选条件转成接口参数（空值不发送，避免服务端按空字符串筛选）。 */
function auditParams() {
  const params = {
    search: query.search,
    actor: query.actor,
    action: query.action,
    ip: query.ip,
    from: query.from,
    to: query.to,
  };

  Object.keys(params).forEach((key) => {
    if (!params[key]) delete params[key];
  });

  return params;
}

/** 导出当前筛选条件下的审计日志（服务端默认脱敏来源 IP，并会记录这次导出）。 */
async function exportAudit() {
  const search = new URLSearchParams(auditParams()).toString();
  try {
    const blob = await fetchBlob(`/admin/audit/export${search ? `?${search}` : ''}`);
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `audit-${new Date().toISOString().slice(0, 10)}.csv`;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    toast('ok', t('audit.exported'), t('audit.exportedDesc'));
  } catch (err) {
    toastError(err, t('audit.exportFailed'));
  }
}

export async function render(container) {
  clear(container);
  container.appendChild(loadingBlock());

  const result = await api('/admin/audit', {
    query: { page: query.page, pageSize: PAGE_SIZE, ...auditParams() },
  });

  const totalPages = Math.max(1, Math.ceil(result.total / PAGE_SIZE));

  clear(container);
  container.appendChild(h('div',
    h('div.card',
      h('div.card-head',
        h('div',
          h('h3', t('audit.listTitle', { total: result.total })),
          h('p.card-desc', t('audit.listDesc')),
        ),
        h('div.card-actions',
          h('input', {
            type: 'text',
            // 带上 `.toolbar-search`：「/」快捷键靠这个类定位本页搜索框（不靠中文 placeholder）。
            class: 'toolbar-search',
            placeholder: t('audit.searchPlaceholder'),
            value: query.search,
            style: { width: '200px' },
            onChange: (e) => {
              query.search = e.target.value.trim();
              query.page = 1;
              render(document.getElementById('content'));
            },
          }),
          h('button.btn.btn-sm', {
            type: 'button',
            onClick: () => {
              query = { page: 1, search: '', actor: '', action: '', ip: '', from: '', to: '' };
              render(document.getElementById('content'));
            },
          }, t('audit.reset')),
          h('button.btn.btn-sm', { type: 'button', onClick: exportAudit }, t('audit.exportCsv')),
        ),
      ),
      // 精细筛选：动作前缀按类别看（device. 设备类 / admin.login 登录 / apikey. 密钥），
      // 时间范围用于对账与归档——「到某日」服务端会算到当天 23:59:59。
      h('div', { style: { display: 'flex', gap: '8px', flexWrap: 'wrap', marginTop: '10px' } },
        ...['actor', 'action', 'ip', 'from', 'to'].map((key) => h('input', {
          type: key === 'from' || key === 'to' ? 'date' : 'text',
          placeholder: FILTER_HINTS[key],
          value: query[key],
          style: { width: key === 'from' || key === 'to' ? '150px' : '168px' },
          onChange: (e) => {
            query[key] = e.target.value.trim();
            query.page = 1;
            render(document.getElementById('content'));
          },
        })),
      ),
      result.items.length === 0
        ? emptyState('clipboard', t('audit.emptyTitle'), query.search ? t('audit.emptySearchHint') : t('audit.emptyHint'))
        : h('div', h('div.table-wrap',
          h('table.data',
            h('thead', h('tr',
              h('th', { style: { width: '170px' } }, t('audit.colTime')),
              h('th', { style: { width: '150px' } }, t('audit.colActor')),
              h('th', { style: { width: '170px' } }, t('audit.colAction')),
              h('th', { style: { width: '150px' } }, t('audit.colTarget')),
              h('th', t('audit.colDetail')),
              h('th', { style: { width: '130px' } }, t('audit.filterIp')),
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
        }, t('audit.prev')),
        h('span', { style: { fontSize: '12.5px', color: 'var(--text-dim)' } },
          t('audit.pageInfo', { page: query.page, total: totalPages })),
        h('button.btn.btn-sm', {
          type: 'button',
          disabled: query.page >= totalPages,
          onClick: () => {
            query.page += 1;
            render(document.getElementById('content'));
          },
        }, t('audit.next')),
      )
      : null,
  ));
}
