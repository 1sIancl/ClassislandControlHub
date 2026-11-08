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
 * 复制一套编辑器会立刻产生两份真相，改一处忘另一处。总览只做**只读与跳转**，
 * 编辑进入 `#/layouts/{档案}/{时间表}` 这个二级子页面（复用同一个编辑器模块，只是只显示该分区）。
 * </p>
 */

import { api } from '../core/api.js?v=173';
import { toastError, errorBlock } from '../core/errors.js?v=173';
import {
  h, clear, toast, loadingBlock, emptyState, confirmDialog,
} from '../core/ui.js?v=173';
import { t } from './../core/i18n.js?v=173';

export const meta = {
  title: t('layouts.title'),
  subtitle: t('layouts.subtitle'),
};

/**
 * 一个档案绑定了哪些教室端（也就是「哪些班级在用」）。
 *
 * <p>
 * 只看「被几张课表引用」不够——管理员想知道的是**哪几个班**在吃这套作息。
 * 设备名就是班级名，绑定关系在设备的 `profileId` 上。
 * </p>
 */
function classesOfProfile(devices, profileId) {
  return devices
    .filter((device) => device.profileId === profileId && !device.revoked)
    .map((device) => device.name);
}

/** 班级列：最多列 3 个，其余折成「等 N 个」，避免一行撑爆。 */
function classesCell(names) {
  if (names.length === 0) {
    return h('span.cell-sub', t('layouts.noClass'));
  }

  const shown = names.slice(0, 3).join('、');
  const more = names.length > 3 ? t('layouts.classListMore', { rest: names.length - 3 }) : '';
  return h('span', { title: names.join('、') }, shown + more);
}

/** 一张时间表在表格里的一行。拆成函数是为了不让 h() 嵌套到数不清括号。 */
function layoutRow(profile, row, classes) {
  // 编辑进**时间表自己的页面**（`#/layouts/{时间表}`），与档案无关。
  const editLink = h('a.btn.btn-sm', {
    href: `#/layouts/${row.layout.id}`,
  }, t('layouts.edit'));

  return h('tr',
    h('td', row.layout.name),
    h('td', h('span.cell-sub', t('layouts.pointsCount', { count: row.points }))),
    // 档案列：这份时间表属于哪份档案（同一档案下的时间表通常就是给这批班用的）。
    h('td', profile.name),
    h('td', classesCell(classes)),
    h('td', h('span.cell-sub', t('layouts.refsCount', { count: row.refs }))),
    h('td', editLink));
}

/** 一个档案的卡片：标题（档案名 + 统计）+ 它的全部时间表。 */
function profileCard(profile, rows, classes) {
  const table = h('table.data.data-compact',
    h('thead', h('tr',
      h('th', t('layouts.name')),
      h('th', { style: { width: '90px' } }, t('layouts.points')),
      h('th', { style: { width: '150px' } }, t('layouts.profile')),
      h('th', t('layouts.classes')),
      h('th', { style: { width: '110px' } }, t('layouts.refs')),
      h('th', { style: { width: '90px' } }, ''))),
    h('tbody', ...rows.map((row) => layoutRow(profile, row, classes))));

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
  let devices;
  let library;
  try {
    // 设备列表用来回答「哪些班级在用这份作息」——只看课表张数答不了这个问题；
    // 时间表库用来列出「没有任何档案引用」的条目（它们不在下面的分组里，等于隐身）。
    [profiles, devices, library] = await Promise.all([
      api('/admin/profiles'),
      api('/admin/devices').catch(() => []),
      api('/admin/time-layouts').catch(() => []),
    ]);
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
      const classes = classesOfProfile(devices ?? [], profile.id);

      const rows = layouts
        .map((layout) => ({
          layout,
          points: (layout.items ?? []).length,
          refs: plans.filter((plan) => plan.timeLayoutId === layout.id).length,
        }))
        .filter((row) => !keyword
          || row.layout.name.toLowerCase().includes(keyword)
          || profile.name.toLowerCase().includes(keyword)
          || classes.some((name) => name.toLowerCase().includes(keyword)));

      if (rows.length === 0) {
        continue;
      }

      shown += rows.length;
      listHost.appendChild(profileCard(profile, rows, classes));
    }

    if (shown === 0) {
      listHost.appendChild(emptyState(t('layouts.noMatch'), t('layouts.noMatchHint')));
    }

    // 没被任何档案引用的时间表单独列出来（#138）。
    // 它们不属于任何档案，下面的分组视图里根本看不到；删档案不删时间表，
    // 于是这类条目会慢慢攒起来。这里只提示 + 提供删除，不自动清。
    const unused = (library ?? []).filter((layout) => (layout.usedByProfiles ?? 0) === 0);
    if (unused.length > 0 && !keyword) {
      listHost.appendChild(h('div.card', { style: { marginBottom: '12px' } },
        h('div.card-head',
          h('div',
            h('h3', t('layouts.unusedTitle', { count: unused.length })),
            h('p.card-desc', t('layouts.unusedLead')))),
        h('div.table-wrap',
          h('table.data.data-compact',
            h('thead', h('tr',
              h('th', t('layouts.name')),
              h('th', { style: { width: '110px' } }, t('layouts.points')),
              h('th', { style: { width: '170px' } }, ''))),
            h('tbody', ...unused.map((layout) => h('tr',
              h('td', layout.name),
              h('td', h('span.cell-sub', t('layouts.pointsCount', {
                count: (layout.items ?? []).length,
              }))),
              h('td', h('button.btn.btn-sm.btn-ghost', {
                type: 'button',
                onClick: async () => {
                  const ok = await confirmDialog(
                    t('layouts.unusedDelete'),
                    t('layouts.unusedDeleteConfirm', { name: layout.name }),
                    t('layouts.unusedDelete'),
                    true,
                  );
                  if (!ok) {
                    return;
                  }

                  try {
                    await api(`/admin/time-layouts/${layout.id}`, { method: 'DELETE' });
                    library = library.filter((x) => x.id !== layout.id);
                    paint();
                    toast('ok', t('layouts.unusedDeleted'));
                  } catch (error) {
                    toastError(error, t('layouts.unusedDeleteFailed'));
                  }
                },
              }, t('layouts.unusedDelete'))))))))));
    }
  };

  searchInput.oninput = paint;

  // 新建时间表：时间表是全局实体，不需要先挑档案。
  const newBtn = h('button.btn.btn-sm.btn-primary', {
    type: 'button',
    onClick: async () => {
      const id = (typeof crypto !== 'undefined' && crypto.randomUUID)
        ? crypto.randomUUID()
        : `layout-${Date.now()}`;

      try {
        await api(`/admin/time-layouts/${id}`, {
          method: 'PUT',
          body: { id, name: t('layouts.newLayoutName'), items: [] },
        });
        window.location.hash = `#/layouts/${id}`;
      } catch (error) {
        toastError(error, t('layouts.createFailed'));
      }
    },
  }, t('layouts.newLayout'));

  const toolbar = h('div.toolbar',
    h('span.toolbar-label', t('layouts.searchLabel')),
    searchInput,
    h('div.spacer'),
    newBtn);

  container.appendChild(h('div',
    h('p.card-desc', t('layouts.lead')),
    toolbar,
    listHost));

  paint();
}
