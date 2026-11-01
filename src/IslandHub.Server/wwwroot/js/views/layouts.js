/**
 * 时间表总览（独立入口）。
 *
 * <p>
 * 时间表原本只作为「配置档案」里的第一个页签存在，要从导航点两层才找得到；
 * 而对教务来说「调作息」本身就是一件独立的事，不必每次都先进到某个档案里。
 * </p>
 *
 * <p>
 * 刻意**不新建一份时间表编辑逻辑**：时间表的数据属于档案（一个档案 = 一套时间表 + 课表 + 科目），
 * 复制一套编辑器会立刻产生两份真相，改一处忘另一处。这里只做**总览与跳转**，
 * 编辑仍回到档案编辑器的时间表页——数据完整性优先于"看起来更独立"。
 * </p>
 */

import { api } from '../core/api.js?v=168';
import { toastError, errorBlock } from '../core/errors.js?v=168';
import { h, clear, toast, loadingBlock, emptyState } from '../core/ui.js?v=168';
import { t } from './../core/i18n.js?v=168';

export const meta = {
  title: t('layouts.title'),
  subtitle: t('layouts.subtitle'),
};

/** 一张时间表在表格里的一行。拆成函数是为了不让 h() 嵌套到数不清括号。 */
function layoutRow(profile, row) {
  const editLink = h('a.btn.btn-sm', {
    href: `#/profiles/${profile.id}`,
    onClick: () => toast('ok', t('layouts.opening'), row.layout.name),
  }, t('layouts.edit'));

  const refsCell = row.refs > 0
    ? h('span.badge.badge-accent', t('layouts.refsCount', { count: row.refs }))
    : h('span.cell-sub', t('layouts.refsNone'));

  return h('tr',
    h('td', row.layout.name),
    h('td', h('span.cell-sub', t('layouts.pointsCount', { count: row.points }))),
    h('td', refsCell),
    h('td', editLink));
}

/** 一个档案的卡片：标题（档案名 + 统计）+ 它的全部时间表。 */
function profileCard(profile, rows) {
  const table = h('table.data.data-compact',
    h('thead', h('tr',
      h('th', t('layouts.name')),
      h('th', { style: { width: '110px' } }, t('layouts.points')),
      h('th', { style: { width: '140px' } }, t('layouts.refs')),
      h('th', { style: { width: '110px' } }, ''))),
    h('tbody', ...rows.map((row) => layoutRow(profile, row))));

  const layouts = (profile.content?.timeLayouts ?? []).length;
  const plans = (profile.content?.classPlans ?? []).length;

  return h('div.card', { style: { marginBottom: '12px' } },
    h('div.card-head',
      h('div',
        h('h3', profile.name),
        h('p.card-desc', t('layouts.profileSummary', { layouts, plans })))),
    h('div.table-wrap', table));
}

export async function render(container) {
  clear(container);
  container.appendChild(loadingBlock(t('layouts.loading')));

  let profiles;
  try {
    profiles = await api('/admin/profiles');
  } catch (error) {
    clear(container);
    container.appendChild(errorBlock(error, t('layouts.loadFailed')));
    return;
  }

  if (profiles.length === 0) {
    clear(container);
    container.appendChild(emptyState(t('layouts.empty'), t('layouts.emptyHint')));
    return;
  }

  // 列表接口只给元信息，时间表在 content 里，逐个取（档案一般就一两份，串行也够快）。
  const details = [];
  for (const profile of profiles) {
    try {
      details.push(await api(`/admin/profiles/${profile.id}`));
    } catch {
      // 某份档案取不到就跳过：总览页不该因为一份坏数据整页空白。
    }
  }

  clear(container);

  const searchInput = h('input', {
    type: 'text',
    placeholder: t('layouts.searchPlaceholder'),
    style: { width: '100%', maxWidth: '320px' },
  });

  const listHost = h('div');

  const paint = () => {
    clear(listHost);

    const keyword = searchInput.value.trim().toLowerCase();
    let shown = 0;

    for (const profile of details) {
      const layouts = profile.content?.timeLayouts ?? [];
      if (layouts.length === 0) {
        continue;
      }

      // 每张时间表被几张课表引用（轮换周次时往往多张共用一份）。
      const plans = profile.content?.classPlans ?? [];
      const rows = layouts
        .map((layout) => ({
          layout,
          points: (layout.items ?? []).length,
          refs: plans.filter((plan) => plan.timeLayoutId === layout.id).length,
        }))
        .filter((row) => !keyword
          || row.layout.name.toLowerCase().includes(keyword)
          || profile.name.toLowerCase().includes(keyword));

      if (rows.length === 0) {
        continue;
      }

      shown += rows.length;
      listHost.appendChild(profileCard(profile, rows));
    }

    if (shown === 0) {
      listHost.appendChild(emptyState(t('layouts.noMatch'), t('layouts.noMatchHint')));
    }
  };

  searchInput.oninput = paint;

  const toolbar = h('div.toolbar',
    h('span.toolbar-label', t('layouts.searchLabel')),
    searchInput);

  container.appendChild(h('div',
    h('p.card-desc', t('layouts.lead')),
    toolbar,
    listHost));

  paint();
}
