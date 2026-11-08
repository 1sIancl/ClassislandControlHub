/**
 * 课表总览（独立入口，与「时间表」并列）。
 *
 * <p>
 * 课表和时间表是两件事：时间表决定「一天有几节课、几点上下课」，课表决定「哪一天第几节上什么」。
 * 以前都塞在档案编辑器的两个页签里，找起来要先进档案；现在各自有总览与二级子页面。
 * </p>
 *
 * <p>
 * 与「时间表」一样，这里只做**总览与跳转**，编辑进入 `#/timetables/{档案}`，
 * 复用档案编辑器的课表分区——编辑逻辑只有一份，数据只有一个来源。
 * </p>
 */

import { api } from '../core/api.js?v=173';
import { errorBlock } from '../core/errors.js?v=173';
import { h, clear, loadingBlock, emptyState } from '../core/ui.js?v=173';
import { t } from './../core/i18n.js?v=173';

export const meta = {
  title: t('timetables.title'),
  subtitle: t('timetables.subtitle'),
};

const WEEKDAY_NAMES = ['周日', '周一', '周二', '周三', '周四', '周五', '周六'];

/** 生效星期：多天用「、」连起来；一条都没有时说明它只在特定日期生效。 */
function weekdayText(plan) {
  const days = (plan.daysOfWeek ?? []).map((d) => WEEKDAY_NAMES[d] ?? String(d));
  if (days.length === 0) {
    return t('timetables.byDate');
  }

  return days.join('、');
}

/** 轮换：0 表示每周都生效；否则「周期 N 周 · 第 M 周」。 */
function rotationText(plan) {
  if (!plan.weekInterval || plan.weekInterval < 2) {
    return t('timetables.everyWeek');
  }

  return t('timetables.rotationValue', {
    total: plan.weekInterval,
    index: (plan.weekOffset ?? 0) + 1,
  });
}

function planRow(profile, plan, layoutName, classes) {
  const shown = classes.slice(0, 3).join('、');
  const more = classes.length > 3 ? t('layouts.classListMore', { rest: classes.length - 3 }) : '';

  return h('tr',
    h('td', plan.name),
    h('td', h('span.cell-sub', layoutName || '—')),
    h('td', h('span.cell-sub', weekdayText(plan))),
    h('td', h('span.cell-sub', rotationText(plan))),
    h('td', classes.length > 0
      ? h('span', { title: classes.join('、') }, shown + more)
      : h('span.cell-sub', t('layouts.noClass'))),
    h('td', h('a.btn.btn-sm', { href: `#/timetables/${profile.id}` }, t('timetables.edit'))));
}

function profileCard(profile, rows, classes) {
  const table = h('table.data.data-compact',
    h('thead', h('tr',
      h('th', t('timetables.name')),
      h('th', { style: { width: '150px' } }, t('timetables.layout')),
      h('th', { style: { width: '120px' } }, t('timetables.weekday')),
      h('th', { style: { width: '150px' } }, t('timetables.rotation')),
      h('th', t('layouts.classes')),
      h('th', { style: { width: '90px' } }, ''))),
    h('tbody', ...rows.map((row) => planRow(profile, row.plan, row.layoutName, classes))));

  const plans = (profile.content?.classPlans ?? []).length;
  const layouts = (profile.content?.timeLayouts ?? []).length;

  return h('div.card', { style: { marginBottom: '12px' } },
    h('div.card-head',
      h('div',
        h('h3', profile.name),
        h('p.card-desc', t('timetables.profileSummary', { plans, layouts })))),
    h('div.table-wrap', table));
}

export async function render(container) {
  clear(container);
  container.appendChild(loadingBlock(t('timetables.loading')));

  let profiles;
  let devices;
  try {
    [profiles, devices] = await Promise.all([
      api('/admin/profiles'),
      api('/admin/devices').catch(() => []),
    ]);
  } catch (error) {
    clear(container);
    container.appendChild(errorBlock(error, t('timetables.loadFailed')));
    return;
  }

  if (profiles.length === 0) {
    clear(container);
    container.appendChild(emptyState(t('timetables.empty'), t('timetables.emptyHint')));
    return;
  }

  const details = [];
  for (const profile of profiles) {
    try {
      details.push(await api(`/admin/profiles/${profile.id}`));
    } catch {
      // 一份档案坏了不该让整页空白。
    }
  }

  clear(container);

  const searchInput = h('input', {
    type: 'text',
    placeholder: t('timetables.searchPlaceholder'),
    style: { width: '100%', maxWidth: '320px' },
  });

  const listHost = h('div');

  const paint = () => {
    clear(listHost);

    const keyword = searchInput.value.trim().toLowerCase();
    let shown = 0;

    for (const profile of details) {
      const plans = profile.content?.classPlans ?? [];
      if (plans.length === 0) {
        continue;
      }

      const layouts = profile.content?.timeLayouts ?? [];
      const classes = (devices ?? [])
        .filter((device) => device.profileId === profile.id && !device.revoked)
        .map((device) => device.name);

      const rows = plans
        .map((plan) => ({
          plan,
          layoutName: layouts.find((layout) => layout.id === plan.timeLayoutId)?.name ?? '',
        }))
        .filter((row) => !keyword
          || row.plan.name.toLowerCase().includes(keyword)
          || row.layoutName.toLowerCase().includes(keyword)
          || profile.name.toLowerCase().includes(keyword)
          || classes.some((name) => name.toLowerCase().includes(keyword)));

      if (rows.length === 0) {
        continue;
      }

      shown += rows.length;
      listHost.appendChild(profileCard(profile, rows, classes));
    }

    if (shown === 0) {
      listHost.appendChild(emptyState(t('timetables.noMatch'), t('timetables.noMatchHint')));
    }
  };

  searchInput.oninput = paint;

  container.appendChild(h('div',
    h('p.card-desc', t('timetables.lead')),
    h('div.toolbar',
      h('span.toolbar-label', t('layouts.searchLabel')),
      searchInput),
    listHost));

  paint();
}
