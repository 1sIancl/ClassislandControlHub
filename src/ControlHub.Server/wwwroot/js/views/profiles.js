/**
 * 配置档案列表视图。
 * 档案是集控下发的最小单元：一个档案 = 一套时间表 + 课表 + 科目 + 自定义设置。
 */

import { api } from '../core/api.js?v=7';
import {
  h, clear, formatDateTime, toast, loadingBlock, modal, confirmDialog,
  emptyState, field,
} from '../core/ui.js?v=7';

export const meta = {
  title: '配置档案',
  subtitle: '管理可下发的课表、时间表与科目集合',
};

export async function render(container) {
  clear(container);
  container.appendChild(loadingBlock());

  const profiles = await api('/admin/profiles');

  clear(container);
  container.appendChild(h('div',
    h('div.card',
      h('div.card-head',
        h('div',
          h('h3', `配置档案（${profiles.length}）`),
          h('p.card-desc', '一个档案包含时间表、课表、科目与自定义设置。设备按「单独指定 → 分组默认 → 全局默认」的顺序取用档案。'),
        ),
        h('div.card-actions',
          h('button.btn.btn-sm', { type: 'button', onClick: () => openCreateDialog(false) }, '新建空白档案'),
          h('button.btn.btn-primary.btn-sm', { type: 'button', onClick: () => openCreateDialog(true) }, '+ 新建示例档案'),
        ),
      ),
      profiles.length === 0
        ? emptyState('📚', '还没有配置档案',
          '建议先用「示例档案」快速生成一套标准作息与课表，再按实际情况调整。',
          h('button.btn.btn-primary', { type: 'button', onClick: () => openCreateDialog(true) }, '新建示例档案'))
        : h('div', { style: { display: 'grid', gap: '12px' } }, ...profiles.map(renderCard)),
    ),
  ));
}

function renderCard(profile) {
  const content = profile.content || {};
  const layoutCount = (content.timeLayouts || []).length;
  const planCount = (content.classPlans || []).length;
  const subjectCount = (content.subjects || []).length;

  return h('div', {
    style: {
      border: '1px solid var(--border)',
      borderRadius: 'var(--radius)',
      padding: '15px 17px',
      background: 'var(--bg-panel-2)',
      display: 'flex',
      gap: '16px',
      alignItems: 'center',
      flexWrap: 'wrap',
    },
  },
    h('div', { style: { flex: '1 1 260px', minWidth: '220px' } },
      h('div', { style: { display: 'flex', alignItems: 'center', gap: '9px', flexWrap: 'wrap' } },
        h('strong', { style: { fontSize: '15px' } }, profile.name),
        profile.isDefault ? h('span.badge.badge-accent', '全局默认') : null,
        h('span.badge.badge-neutral', `内容版本 ${profile.revision}`),
      ),
      profile.description
        ? h('div', { style: { fontSize: '12.5px', color: 'var(--text-faint)', marginTop: '4px' } }, profile.description)
        : null,
      h('div', { style: { fontSize: '12px', color: 'var(--text-faint)', marginTop: '6px' } },
        `时间表 ${layoutCount} · 课表 ${planCount} · 科目 ${subjectCount} · 覆盖设备 ${profile.boundDeviceCount ?? 0} 台`
        + ` · 更新于 ${formatDateTime(profile.updatedAt)}`),
    ),
    h('div.card-actions',
      h('button.btn.btn-sm.btn-primary', {
        type: 'button',
        onClick: () => window.location.hash = `#/profiles/${profile.id}`,
      }, '编辑内容'),
      h('button.btn.btn-sm', { type: 'button', onClick: () => pushProfile(profile) }, '立即推送'),
      profile.isDefault
        ? null
        : h('button.btn.btn-sm', { type: 'button', onClick: () => setDefault(profile) }, '设为默认'),
      h('button.btn.btn-sm.btn-danger', { type: 'button', onClick: () => removeProfile(profile) }, '删除'),
    ),
  );
}

function openCreateDialog(withSample) {
  const nameInput = h('input', {
    type: 'text',
    value: withSample ? '示例档案' : '',
    placeholder: '例如：2026 春季学期',
  });
  const descInput = h('input', { type: 'text', placeholder: '可选' });

  modal({
    title: withSample ? '新建示例档案' : '新建空白档案',
    width: 'wide',
    body: h('div',
      field('档案名称', nameInput),
      field('备注', descInput),
      withSample
        ? h('div.notice.notice-info',
          h('span.notice-icon', 'i'),
          h('div', '将自动生成一套标准作息（8 节课）、周一至周五课表与常用科目，创建后可直接修改。'))
        : h('div.notice.notice-warn',
          h('span.notice-icon', '!'),
          h('div', '空白档案没有任何时间表。课表必须以时间表为基准，因此请先创建时间表。')),
    ),
    confirmText: '创建',
    onConfirm: async () => {
      const name = nameInput.value.trim();
      if (!name) {
        toast('warn', '请填写档案名称');
        return false;
      }

      const result = await api(withSample ? '/admin/profiles/sample' : '/admin/profiles', {
        method: 'POST',
        body: { name, description: descInput.value.trim(), content: null },
      });

      toast('ok', '档案已创建', result.notes && result.notes.length > 0
        ? `自动处理了 ${result.notes.length} 项内容问题。`
        : '');

      window.location.hash = `#/profiles/${result.profile.id}`;
      return true;
    },
  });
}

async function setDefault(profile) {
  const ok = await confirmDialog('设为默认档案',
    `「${profile.name}」将作为所有未指定档案且未加入分组的设备的默认配置。确定继续吗？`,
    '设为默认');
  if (!ok) return;

  await api(`/admin/profiles/${profile.id}/default`, { method: 'POST' });
  toast('ok', '已设为默认档案');
  await render(document.getElementById('content'));
}

async function pushProfile(profile) {
  const ok = await confirmDialog('立即推送',
    `将立即通知所有正在使用「${profile.name}」的设备重新拉取配置，通常数秒内生效。确定继续吗？`,
    '推送');
  if (!ok) return;

  const result = await api(`/admin/profiles/${profile.id}/push`, {
    method: 'POST',
    body: { scope: 'all', targetIds: [], message: '' },
  });

  toast('ok', '推送已发出', `影响 ${result.affected} 台设备。`);
}

async function removeProfile(profile) {
  const ok = await confirmDialog('删除配置档案',
    `删除「${profile.name}」后，原先绑定它的设备会回退到分组默认或全局默认档案。此操作不可撤销。`,
    '删除', true);
  if (!ok) return;

  await api(`/admin/profiles/${profile.id}`, { method: 'DELETE' });
  toast('ok', '已删除');
  await render(document.getElementById('content'));
}
