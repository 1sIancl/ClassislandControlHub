/**
 * 操作审批（路线图 #35）：危险操作的「发起」与「确认」分开。
 *
 * 谁会用到这一页：
 *   · **超级管理员**——看别人提交的申请，确认后才执行（恢复备份这类操作会整库覆盖）；
 *   · **普通管理员**——看自己提交的申请卡在什么状态（只能看自己的，服务端过滤）。
 *
 * 按钮是否显示由服务端给的 `canDecide` 决定，不在前端猜角色：
 * 超管的权限在服务端是「展开成全选」的，前端拿不到角色时判断不准，猜错就是「能点但一点就 403」。
 */
import { api } from '../core/api.js?v=146';
import { toastError } from '../core/errors.js';
import {
  h, clear, formatDateTime, toast, loadingBlock, confirmDialog,
} from '../core/ui.js?v=146';
import { t } from '../core/i18n.js?v=146';

export const meta = {
  title: t('approvals.title'),
  subtitle: t('approvals.subtitle'),
};

const STATUS_TEXT = {
  pending: t('approvals.statusPending'),
  approved: t('approvals.statusApproved'),
  rejected: t('approvals.statusRejected'),
  failed: t('approvals.statusFailed'),
  cancelled: t('approvals.statusCancelled'),
};

const STATUS_TONE = {
  pending: 'warn',
  approved: 'ok',
  rejected: 'danger',
  failed: 'danger',
};

const KIND_TEXT = {
  'backup.restore': t('approvals.kindBackupRestore'),
};

export async function render(container) {
  clear(container);
  container.appendChild(loadingBlock());

  const state = { status: 'pending' };
  // 专属类名：给验证脚本一个稳定的锚点（页面上别处也有表格，靠 `table` 去等会等到别页面的旧 DOM）。
  const host = h('div.approvals-page');
  const body = h('div', { style: { marginTop: '12px' } });

  const paint = async () => {
    body.replaceChildren(loadingBlock());
    let data;
    try {
      const query = state.status === 'all' ? '' : `?status=${state.status}`;
      data = await api(`/admin/approvals${query}`);
    } catch (e) {
      toastError(e, t('approvals.loadFailed'));
      body.replaceChildren();
      return;
    }

    const items = data.items || [];
    if (items.length === 0) {
      body.replaceChildren(h('div.notice',
        h('div', state.status === 'pending'
          ? t('approvals.emptyPending')
          : t('approvals.emptyAll'))));
      return;
    }

    body.replaceChildren(h('div.table-wrap',
      h('table.table',
        h('thead', h('tr',
          h('th', t('approvals.colKind')),
          h('th', t('approvals.colTitle')),
          h('th', t('approvals.colRequester')),
          h('th', t('approvals.colTime')),
          h('th', t('approvals.colStatus')),
          h('th', t('approvals.colResult')),
          data.canDecide ? h('th', t('approvals.colActions')) : null)),
        h('tbody', ...items.map((item) => h('tr',
          h('td', KIND_TEXT[item.kind] || item.kind),
          h('td', { style: { maxWidth: '380px' } }, item.title),
          h('td', item.requestedBy),
          h('td', { style: { whiteSpace: 'nowrap' } }, formatDateTime(item.requestedAt)),
          h('td', h('span', { class: `badge badge-${STATUS_TONE[item.status] || 'plain'}` },
            STATUS_TEXT[item.status] || item.status)),
          h('td', { style: { fontSize: '12px', color: 'var(--text-dim)' } },
            item.result || item.comment || '—'),
          data.canDecide ? h('td', { style: { whiteSpace: 'nowrap' } }, ...decideButtons(item, paint)) : null,
        ))))));
  };

  const decideButtons = (item, repaint) => {
    if (item.status !== 'pending') {
      return [h('span', { style: { fontSize: '12px', color: 'var(--text-faint)' } },
        `${item.decidedBy || ''}`)];
    }

    const approve = h('button.btn.btn-sm.btn-primary', {
      type: 'button',
      onClick: async () => {
        if (!await confirmDialog(t('approvals.approveTitle'),
          t('approvals.approveConfirm', { title: item.title }), t('approvals.approve'))) {
          return;
        }

        approve.disabled = true;
        try {
          const done = await api(`/admin/approvals/${encodeURIComponent(item.id)}/approve`, { method: 'POST', body: {} });
          toast('ok', t('approvals.approved'), done.result || '');
          await repaint();
        } catch (e) {
          toastError(e, t('approvals.approveFailed'));
          approve.disabled = false;
        }
      },
    }, t('approvals.approve'));

    const reject = h('button.btn.btn-sm', {
      type: 'button',
      onClick: async () => {
        if (!await confirmDialog(t('approvals.rejectTitle'),
          t('approvals.rejectConfirm', { title: item.title }), t('approvals.reject'), true)) {
          return;
        }

        reject.disabled = true;
        try {
          await api(`/admin/approvals/${encodeURIComponent(item.id)}/reject`, { method: 'POST', body: {} });
          toast('ok', t('approvals.statusRejected'), t('approvals.rejectedHint'));
          await repaint();
        } catch (e) {
          toastError(e, t('approvals.rejectFailed'));
          reject.disabled = false;
        }
      },
    }, t('approvals.reject'));

    return [approve, reject];
  };

  const pickStatus = (value) => {
    state.status = value;
    for (const button of host.querySelectorAll('[data-approval-status]')) {
      button.classList.toggle('active', button.dataset.approvalStatus === value);
    }

    paint();
  };

  host.appendChild(h('div.toolbar',
    h('span.toolbar-label', t('approvals.colStatus')),
    h('div.segmented',
      ...[['pending', t('approvals.statusPending')], ['all', t('approvals.filterAll')]].map(([value, label]) => h('button', {
        type: 'button',
        class: state.status === value ? 'active' : '',
        dataset: { approvalStatus: value },
        onClick: () => pickStatus(value),
      }, label))),
    h('div.spacer'),
    h('button.btn.btn-sm', { type: 'button', onClick: paint }, t('approvals.refresh')),
  ));
  host.appendChild(body);

  clear(container);
  container.appendChild(host);
  await paint();
}
