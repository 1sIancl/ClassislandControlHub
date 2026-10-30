/**
 * 配置档案列表视图。
 * 档案是 IslandHub 下发的最小单元：一个档案 = 一套时间表 + 课表 + 科目 + 自定义设置。
 */

import { api } from '../core/api.js?v=162';
import { toastError, errorBlock } from '../core/errors.js?v=162';
import { openAuditTimeline } from '../core/audit-timeline.js?v=162';
import {
  h, clear, formatDateTime, toast, loadingBlock, modal, confirmDialog,
  emptyState, field, select,
} from '../core/ui.js?v=162';
import { t } from './../core/i18n.js?v=162';

export const meta = {
  title: t('profiles.title'),
  subtitle: t('profiles.subtitle'),
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
          h('h3', t('profiles.listTitle', { count: profiles.length })),
          h('p.card-desc', t('profiles.listLead')),
        ),
        h('div.card-actions',
          h('button.btn.btn-sm', {
            type: 'button',
            title: t('profiles.conflictHint'),
            onClick: openConflictsDialog,
          }, t('profiles.conflictCheck')),
          h('button.btn.btn-sm', { type: 'button', onClick: () => openAiImportDialog(profiles) }, t('profiles.aiImport')),
          h('button.btn.btn-sm', { type: 'button', onClick: () => openImportCsesDialog(profiles) }, t('profiles.csesImport')),
          h('button.btn.btn-sm', {
            type: 'button',
            title: t('profiles.cidataHint'),
            onClick: () => openImportCidataDialog(profiles),
          }, t('profiles.cidataImport')),
          h('button.btn.btn-sm', { type: 'button', onClick: () => openCreateDialog(false) }, t('profiles.newBlank')),
          h('button.btn.btn-primary.btn-sm', { type: 'button', onClick: () => openCreateDialog(true) }, t('profiles.newSample')),
        ),
      ),
      profiles.length === 0
        ? emptyState('book', t('profiles.empty'),
          t('profiles.emptyHint'),
          h('button.btn.btn-primary', { type: 'button', onClick: () => openCreateDialog(true) }, t('profiles.createSample')))
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
        profile.code
          ? h('span.badge.badge-accent', { style: { fontFamily: 'var(--mono)', letterSpacing: '1px' } }, profile.code)
          : null,
        profile.isDefault ? h('span.badge.badge-accent', t('profiles.globalDefault')) : null,
        h('span.badge.badge-neutral', t('profiles.contentRevision', { revision: profile.revision })),
      ),
      profile.description
        ? h('div', { style: { fontSize: '12.5px', color: 'var(--text-faint)', marginTop: '4px' } }, profile.description)
        : null,
      h('div', { style: { fontSize: '12px', color: 'var(--text-faint)', marginTop: '6px' } },
        t('profiles.summary', { layouts: layoutCount, plans: planCount, subjects: subjectCount, devices: profile.boundDeviceCount ?? 0 })
        + t('profiles.updatedAt', { time: formatDateTime(profile.updatedAt) })),
    ),
    h('div.card-actions',
      h('button.btn.btn-sm.btn-primary', {
        type: 'button',
        onClick: () => window.location.hash = `#/profiles/${profile.id}`,
      }, t('profiles.editContent')),
      h('button.btn.btn-sm', { type: 'button', onClick: () => pushProfile(profile) }, t('profiles.pushNow')),
      h('button.btn.btn-sm', { type: 'button', onClick: () => openVersionsDialog(profile) }, t('profiles.versions')),
      h('button.btn.btn-sm', {
        type: 'button',
        title: t('profiles.auditHint'),
        onClick: () => openAuditTimeline(profile.name, profile.name),
      }, t('profiles.audit')),
      h('button.btn.btn-sm', { type: 'button', onClick: () => openOverridesDialog(profile) }, t('profiles.overrides')),
      h('button.btn.btn-sm', { type: 'button', onClick: () => duplicateProfile(profile) }, t('profiles.duplicate')),
      profile.isDefault
        ? null
        : h('button.btn.btn-sm', { type: 'button', onClick: () => setDefault(profile) }, t('profiles.setDefault')),
      h('button.btn.btn-sm.btn-danger', { type: 'button', onClick: () => removeProfile(profile) }, t('profiles.delete')),
    ),
  );
}

/** 星期序号 → 文案（0 = 周日，与协议一致）。 */
function weekdayText(weekdays) {
  if (!weekdays) return t('profiles.everyDay');
  // 星期标签走文案键（英文是 Mon/Tue…，不再拼「周」字前缀）。
  const labels = [
    'profiles.wdShortSun', 'profiles.wdShortMon', 'profiles.wdShortTue', 'profiles.wdShortWed',
    'profiles.wdShortThu', 'profiles.wdShortFri', 'profiles.wdShortSat',
  ];
  return weekdays.split(',').filter(Boolean)
    .map((d) => t('profiles.weekDay', {
      day: labels[Number(d)] ? t(labels[Number(d)]) : d,
    }))
    .join(t('profiles.listSep'));
}

function planLabelOf(plans, classPlanId) {
  if (!classPlanId) return t('profiles.allTimetables');
  return plans.find((p) => p.id === classPlanId)?.name || t('profiles.deletedTimetable');
}

function subjectLabelOf(subjects, subjectId) {
  if (!subjectId) return t('profiles.toEmpty');
  return subjects.find((s) => s.id === subjectId)?.name || t('profiles.unknownSubject');
}

/**
 * 临时换课：在指定日期范围内把某节课换成别的科目。
 * 覆盖不改动档案本身，只在设备拉取配置时合成，因此到期自动还原、也可随时撤销。
 */
async function openOverridesDialog(profile) {
  let detail;
  try {
    detail = await api(`/admin/profiles/${profile.id}`);
  } catch (err) {
    toastError(err, t('profiles.loadFailed'));
    return;
  }

  const content = detail.content || {};
  const plans = content.classPlans || [];
  const subjects = content.subjects || [];

  const todayText = new Date().toLocaleDateString('sv-SE');
  const startInput = h('input', { type: 'date', value: todayText });
  const endInput = h('input', { type: 'date' });
  const reasonInput = h('input', { type: 'text', placeholder: t('profiles.overrideReasonPlaceholder') });

  const planSelect = select(
    [{ value: '', label: t('profiles.allTimetablesInProfile') },
      ...plans.map((p) => ({ value: p.id, label: p.name || t('profiles.unnamedTimetable') }))],
    '',
  );
  const subjectSelect = select(
    [{ value: '', label: t('profiles.toEmptySuffix') }, ...subjects.map((s) => ({ value: s.id, label: s.name }))],
    '',
  );
  const slotSelect = select([], '0');

  const refreshSlots = () => {
    const target = planSelect.value ? plans.filter((p) => p.id === planSelect.value) : plans;
    let maxIndex = -1;
    for (const plan of target) {
      for (const slot of (plan.slots || [])) {
        maxIndex = Math.max(maxIndex, slot.index);
      }
    }

    const max = maxIndex >= 0 ? maxIndex : 7;
    clear(slotSelect);
    for (let i = 0; i <= max; i++) {
      slotSelect.appendChild(h('option', { value: String(i) }, t('profiles.periodN', { n: i + 1 })));
    }
  };

  planSelect.addEventListener('change', refreshSlots);
  refreshSlots();

  const weekdayBoxes = [
    'profiles.wdShortSun', 'profiles.wdShortMon', 'profiles.wdShortTue', 'profiles.wdShortWed',
    'profiles.wdShortThu', 'profiles.wdShortFri', 'profiles.wdShortSat',
  ].map((key, index) => {
    const box = h('input', { type: 'checkbox' });
    return { index, box, el: h('label.weekday-chip', box, h('span', t(key))) };
  });

  const listBox = h('div', { style: { marginTop: '8px' } });

  const load = async () => {
    clear(listBox);
    listBox.appendChild(loadingBlock());
    try {
      const rows = await api(`/admin/timetable-overrides?profileId=${encodeURIComponent(profile.id)}`);
      clear(listBox);
      if (rows.length === 0) {
        listBox.appendChild(h('div.notice.notice-info',
          h('span.notice-icon', 'i'),
          h('div', t('profiles.noOverrides'))));
        return;
      }

      listBox.appendChild(h('div.table-wrap',
        h('table.data',
          h('thead', h('tr',
            h('th', t('profiles.dateRangeLabel')),
            h('th', t('profiles.weekday')),
            h('th', t('profiles.timetablePeriod')),
            h('th', t('profiles.tempSubject')),
            h('th', t('profiles.reason')),
            h('th', { style: { textAlign: 'right' } }, t('profiles.actions')),
          )),
          h('tbody', ...rows.map((row) => h('tr',
            h('td', { style: { fontSize: '12px' } },
              t('profiles.dateRange', {
                from: row.startDate,
                to: row.endDate || t('profiles.untilRevoked'),
              })),
            h('td', { style: { fontSize: '12px' } }, weekdayText(row.weekdays)),
            h('td', { style: { fontSize: '12px' } }, t('profiles.planPeriod', { plan: planLabelOf(plans, row.classPlanId), n: row.slotIndex + 1 })),
            h('td', { style: { fontSize: '12px' } }, subjectLabelOf(subjects, row.subjectId)),
            h('td', { style: { fontSize: '12px' } }, row.reason || '—'),
            h('td.actions',
              h('button.btn.btn-sm.btn-danger', {
                type: 'button',
                onClick: async () => {
                  if (!await confirmDialog(t('profiles.revokeOverride'), t('profiles.revokeConfirm'), t('profiles.revoke'), true)) {
                    return;
                  }

                  await api(`/admin/timetable-overrides/${row.id}`, { method: 'DELETE' });
                  toast('ok', t('profiles.revoked'));
                  await load();
                },
              }, t('profiles.revoke')),
            ),
          ))),
        ),
      ));
    } catch (err) {
      clear(listBox);
      listBox.appendChild(h('div.notice.notice-danger', h('span.notice-icon', '!'), h('div', err.message)));
    }
  };

  const previewBtn = h('button.btn.btn-sm', {
    type: 'button',
    onClick: async () => {
      const date = startInput.value || todayText;
      try {
        const rows = await api(`/admin/timetable-overrides/preview?profileId=${encodeURIComponent(profile.id)}&date=${encodeURIComponent(date)}`);
        const byPlan = new Map();
        for (const row of rows) {
          if (!byPlan.has(row.classPlanName)) byPlan.set(row.classPlanName, []);
          byPlan.get(row.classPlanName).push(row);
        }

        const body = h('div');
        for (const [name, items] of byPlan) {
          body.appendChild(h('div', { style: { marginBottom: '12px' } },
            h('strong', { style: { fontSize: '13px' } }, name),
            h('div', { style: { display: 'flex', flexWrap: 'wrap', gap: '6px', marginTop: '6px' } },
              ...items.map((s) => h('span.badge' + (s.isOverridden ? '.badge-accent' : '.badge-neutral'), {
                title: s.isOverridden
                  ? t('profiles.overrideTitle', { reason: s.overrideReason || t('profiles.adjusted') })
                  : t('profiles.asIs'),
              }, t('profiles.slotLine', { n: s.slotIndex + 1, subject: s.subjectName })))),
          ));
        }

        modal({
          title: t('profiles.previewTitle', { date }),
          width: 'wide',
          hideFooter: true,
          body: h('div',
            h('p', { style: { marginTop: 0, color: 'var(--text-dim)', fontSize: '12.5px' } },
              t('profiles.previewHint')),
            body),
        });
      } catch (err) {
        toastError(err, t('profiles.previewFailed'));
      }
    },
  }, t('profiles.previewDay'));

  const createBtn = h('button.btn.btn-primary.btn-sm', {
    type: 'button',
    onClick: async () => {
      const body = {
        profileId: profile.id,
        classPlanId: planSelect.value,
        slotIndex: Number(slotSelect.value) || 0,
        subjectId: subjectSelect.value,
        startDate: startInput.value,
        endDate: endInput.value,
        weekdays: weekdayBoxes.filter((w) => w.box.checked).map((w) => w.index),
        reason: reasonInput.value.trim(),
      };

      if (!body.startDate) { toast('warn', t('profiles.needStartDate')); return; }
      try {
        await api('/admin/timetable-overrides', { method: 'POST', body });
        toast('ok', t('profiles.overrideCreated'), t('profiles.overrideCreatedHint'));
        await load();
      } catch (err) {
        toastError(err, t('profiles.createFailed'));
      }
    },
  }, t('profiles.addOverride'));

  modal({
    title: t('profiles.overridesOf', { name: profile.name }),
    width: 'wide',
    hideFooter: true,
    body: h('div',
      h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', t('profiles.overrideLead'))),
      h('div.form-row',
        field(t('profiles.startDate'), startInput),
        field(t('profiles.endDate'), endInput, t('profiles.endDateHint')),
      ),
      field(t('profiles.weekdays'), h('div.weekday-row', ...weekdayBoxes.map((w) => w.el)), t('profiles.weekdaysHint')),
      h('div.form-row',
        field(t('profiles.timetable'), planSelect),
        field(t('profiles.period'), slotSelect),
      ),
      field(t('profiles.tempSubject'), subjectSelect, t('profiles.toEmptyHint')),
      field(t('profiles.reason'), reasonInput, t('profiles.reasonHint')),
      h('div', { style: { marginTop: '10px' } }, createBtn, ' ', previewBtn),
      h('div', { style: { marginTop: '18px' } },
        h('div', { style: { fontSize: '13px', fontWeight: '600' } }, t('profiles.existingOverrides')),
        listBox),
    ),
  });

  load();
}

/** 历史版本面板：列出保存前的自动快照，可一键回滚（回滚前也会自动留一份）。 */
function openVersionsDialog(profile) {
  const listBox = h('div');

  const load = async () => {
    clear(listBox);
    listBox.appendChild(loadingBlock());
    try {
      const versions = await api(`/admin/profiles/${profile.id}/versions`);
      clear(listBox);
      if (versions.length === 0) {
        listBox.appendChild(h('div.notice.notice-info',
          h('span.notice-icon', 'i'),
          h('div', t('profiles.noVersions'))));
        return;
      }

      listBox.appendChild(h('div.table-wrap',
        h('table.data',
          h('thead', h('tr',
            h('th', t('profiles.time')),
            h('th', t('profiles.contentRevisionLabel')),
            h('th', t('profiles.reason')),
            h('th', t('profiles.actor')),
            h('th', { style: { textAlign: 'right' } }, t('profiles.actions')),
          )),
          h('tbody', ...versions.map((v) => h('tr',
            h('td', { style: { fontSize: '12px' } }, formatDateTime(v.createdAt)),
            h('td', `v${v.revision}`),
            h('td', { style: { fontSize: '12px' } }, v.reason || '—'),
            h('td', { style: { fontSize: '12px' } }, v.createdBy || '—'),
            h('td.actions',
              h('button.btn.btn-sm.btn-primary', {
                type: 'button',
                onClick: async () => {
                  if (!await confirmDialog(t('profiles.rollbackTo'),
                    `将把「${profile.name}」回滚到 ${formatDateTime(v.createdAt)} 的快照（版本 v${v.revision}）。\n`
                    + t('profiles.rollbackConfirm'), t('profiles.rollback'))) {
                    return;
                  }

                  await api(`/admin/profiles/${profile.id}/versions/${v.id}/restore`, { method: 'POST' });
                  toast('ok', t('profiles.rolledBack'), t('profiles.rolledBackHint'));
                  await load();
                },
              }, t('profiles.rollback')),
              h('button.btn.btn-sm.btn-ghost', {
                type: 'button',
                onClick: async () => {
                  if (!await confirmDialog(t('profiles.deleteSnapshot'), t('profiles.deleteSnapshotHint'), t('profiles.delete'), true)) {
                    return;
                  }

                  await api(`/admin/profiles/${profile.id}/versions/${v.id}`, { method: 'DELETE' });
                  toast('ok', t('profiles.deleted'));
                  await load();
                },
              }, t('profiles.delete')),
            ),
          ))),
        ),
      ));
    } catch (err) {
      clear(listBox);
      listBox.appendChild(h('div.notice.notice-danger', h('span.notice-icon', '!'), h('div', err.message)));
    }
  };

  modal({
    title: t('profiles.versionsOf', { name: profile.name }),
    width: 'wide',
    hideFooter: true,
    body: listBox,
  });

  load();
}

/**
 * 「冲突检测」（#44）：扫一遍全部档案，把「上课那天才会发现」的问题提前摆出来。
 *
 * 分两档：**必须处理**（引用坏了 / 时间重叠 / 同一时间多套课表 —— 必然显示不对）与
 * **待确认**（同一教师同一时间出现在两个档案 —— 可能只是同名老师，也可能就是合班上课）。
 */
async function openConflictsDialog() {
  const box = h('div', h('p.card-desc', t('profiles.scanning')));

  const dialog = modal({
    title: t('profiles.conflictTitle'),
    width: 'xwide',
    hideFooter: true,
    body: box,
  });

  try {
    const report = await api('/admin/profiles/conflicts');
    clear(box);
    box.appendChild(renderConflictReport(report, dialog));
  } catch (err) {
    clear(box);
    box.appendChild(errorBlock(err, { title: t('profiles.conflictFailed') }));
  }
}

/** 渲染冲突报告。 */
function renderConflictReport(report, dialog) {
  const items = report.conflicts || [];
  const blocking = items.filter((c) => c.blocking);
  const warnings = items.filter((c) => !c.blocking);

  const head = h('div',
    h('p.card-desc', { style: { margin: '0 0 10px' } },
      t('profiles.conflictSummary', { profiles: report.profileCount, plans: report.classPlanCount })),
    items.length === 0
      ? h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', t('profiles.conflictCleanLead')
          + t('profiles.conflictCleanTail')))
      : null,
    blocking.length > 0
      ? h('div.notice.notice-danger',
        h('span.notice-icon', '!'),
        h('div', t('profiles.conflictBlocking', { count: blocking.length })))
      : null,
    warnings.length > 0
      ? h('div.notice.notice-warn',
        h('span.notice-icon', '!'),
        h('div', t('profiles.conflictWarnings', { count: warnings.length })))
      : null,
    report.truncated
      ? h('p.card-desc', { style: { marginTop: '8px' } }, t('profiles.conflictTruncated'))
      : null,
  );

  if (items.length === 0) {
    return head;
  }

  const rows = items.map((c) => h('div.config-item', { style: { alignItems: 'flex-start' } },
    h('span.badge' + (c.blocking ? '.badge-danger' : ''), c.blocking ? t('profiles.mustFix') : t('profiles.toReview')),
    h('div', { style: { display: 'flex', flexDirection: 'column', gap: '3px', minWidth: 0 } },
      h('span', { style: { fontSize: '13px', wordBreak: 'break-word' } }, c.message),
      c.profileName
        ? h('span', { style: { color: 'var(--text-faint)', fontSize: '12px' } }, t('profiles.conflictProfile', { name: c.profileName }))
        : null,
    ),
    c.profileId
      ? h('button.btn.btn-sm', {
        type: 'button',
        style: { flex: 'none' },
        onClick: () => {
          // 跳过去处理：编辑器是最能说明问题的地方（能看到那一节到底排了什么）。
          dialog.close();
          window.location.hash = `#/profiles/${c.profileId}`;
        },
      }, t('profiles.goFix'))
      : null,
  ));

  return h('div', head, h('div.config-panel', { style: { marginTop: '10px' } }, ...rows));
}

/** 「从 CSES 导入」对话框：选择目标档案 + 选择/粘贴 CSES 文件内容。 */
function openImportCsesDialog(profiles) {
  if (!profiles || profiles.length === 0) {
    toast('warn', t('profiles.needProfile'), t('profiles.csesNeedProfile'));
    return;
  }

  const profileSelect = select(
    profiles.map((p) => ({ value: p.id, label: p.name })),
    profiles[0].id,
  );

  const textarea = h('textarea', {
    placeholder: t('profiles.csesPlaceholder'),
    style: { minHeight: '180px' },
  });

  const fileInput = h('input', {
    type: 'file',
    accept: '.yml,.yaml,.txt',
    style: { display: 'none' },
    onChange: async (e) => {
      const file = e.target.files?.[0];
      if (!file) return;
      textarea.value = await file.text();
    },
  });

  const pickBtn = h('button.btn.btn-sm', { type: 'button', onClick: () => fileInput.click() }, t('profiles.pickCsesFile'));

  modal({
    title: t('profiles.csesImport'),
    width: 'wide',
    body: h('div',
      field(t('profiles.importInto'), profileSelect),
      h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', t('profiles.csesLead'))),
      h('div', { style: { display: 'flex', alignItems: 'center', gap: '8px', margin: '12px 0 6px' } },
        pickBtn, fileInput,
        h('span', { style: { fontSize: '12px', color: 'var(--text-faint)' } }, t('profiles.utf8Hint')),
      ),
      textarea,
    ),
    confirmText: t('profiles.import'),
    onConfirm: async () => {
      const yaml = textarea.value.trim();
      if (!yaml) {
        toast('warn', t('profiles.needCses'));
        return false;
      }

      const result = await api('/admin/profiles/import-cses', {
        method: 'POST',
        body: { profileId: profileSelect.value, yaml },
      });

      const parts = [
        t('profiles.addedSubjects', { count: result.addedSubjects }),
        t('profiles.addedLayouts', { count: result.addedTimeLayouts }),
        t('profiles.addedPlans', { count: result.addedClassPlans }),
      ];
      if (result.updatedClassPlans > 0) parts.push(t('profiles.updatedPlans', { count: result.updatedClassPlans }));
      if (result.enrichedSubjects > 0) parts.push(t('profiles.enrichedSubjects', { count: result.enrichedSubjects }));

      toast('ok', t('profiles.importDone'), `${parts.join('、')}。`);
      await render(document.getElementById('content'));
      return true;
    },
  });
}

// 0/7 都给「周日」：AI 解析那边用的是 1..7，IslandHub 的档案用的是 0..6（0=周日），两种都容得下。
const DAY_LABEL = { 0: t('profiles.weekdaySun'), 1: t('profiles.weekdayMon'), 2: t('profiles.weekdayTue'), 3: t('profiles.weekdayWed'), 4: t('profiles.weekdayThu'), 5: t('profiles.weekdayFri'), 6: t('profiles.weekdaySat'), 7: t('profiles.weekdaySun') };

/**
 * 「从 .cidata 导入」对话框：上传 ClassIsland 的数据迁移包 → 预览 → 确认合并进档案。
 *
 * 这个包和 CSES 不是一回事：CSES 只传课表骨架，`.cidata` 是宿主数据目录的整包
 * （应用设置、自动化、插件都在里面）。**其中只有时间表 / 课表 / 科目 / 课表群能落到 IslandHub 的档案里**，
 * 其余部分由服务端逐条列出来，这里必须原样显示，不能只报「导入成功 N 项」。
 */
function openImportCidataDialog(profiles) {
  if (!profiles || profiles.length === 0) {
    toast('warn', t('profiles.needProfile'), t('profiles.cidataNeedProfile'));
    return;
  }

  let parsed = null;

  const profileSelect = select(profiles.map((p) => ({ value: p.id, label: p.name })), profiles[0].id);
  const preview = h('div');
  const status = h('div', { style: { fontSize: '12.5px', color: 'var(--text-faint)', marginTop: '8px' } },
    t('profiles.cidataNoFile'));

  const fileInput = h('input', {
    type: 'file',
    accept: '.cidata',
    style: { display: 'none' },
  });

  const pickBtn = h('button.btn.btn-sm.btn-primary', {
    type: 'button',
    onClick: () => fileInput.click(),
  }, t('profiles.pickCidataFile'));

  fileInput.addEventListener('change', async (e) => {
    const file = e.target.files?.[0];
    if (!file) return;

    parsed = null;
    clear(preview);
    status.textContent = t('profiles.cidataParsing', { name: file.name, size: (file.size / 1024 / 1024).toFixed(1) });
    status.style.color = 'var(--text-faint)';
    pickBtn.disabled = true;

    try {
      const form = new FormData();
      form.append('file', file);
      parsed = await api('/admin/profiles/import-cidata/parse', { method: 'POST', body: form });

      const total = parsed.profiles.reduce((sum, p) => sum + p.classPlans, 0);
      status.textContent = t('profiles.cidataParsed', { profiles: parsed.profiles.length, plans: total });
      status.style.color = 'var(--ok)';
      preview.appendChild(renderCidataPreview(parsed));
    } catch (err) {
      status.textContent = err.message;
      status.style.color = 'var(--danger)';
    } finally {
      pickBtn.disabled = false;
    }
  });

  modal({
    title: t('profiles.cidataImport'),
    width: 'wide',
    body: h('div',
      field(t('profiles.importInto'), profileSelect),
      h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', t('profiles.cidataLead')
          + t('profiles.cidataBody')
          + t('profiles.cidataTail')
          + t('profiles.cidataMergeHint'))),
      h('div', { style: { display: 'flex', alignItems: 'center', gap: '8px', margin: '14px 0 8px' } },
        pickBtn, fileInput,
        h('span', { style: { fontSize: '12px', color: 'var(--text-faint)' } }, t('profiles.cidataMaxSize'))),
      status,
      preview,
    ),
    confirmText: t('profiles.importInto'),
    onConfirm: async () => {
      if (!parsed) {
        toast('warn', t('profiles.cidataNeedFile'), t('profiles.cidataPreviewHint'));
        return false;
      }

      const picked = preview.querySelector('select.js-cidata-profile');
      const result = await api('/admin/profiles/import-cidata/apply', {
        method: 'POST',
        body: {
          token: parsed.token,
          profileFile: picked ? picked.value : parsed.profiles[0].fileName,
          profileId: profileSelect.value,
        },
      });

      const parts = [
        t('profiles.addedSubjects', { count: result.addedSubjects }),
        t('profiles.addedLayouts', { count: result.addedTimeLayouts }),
        t('profiles.addedPlans', { count: result.addedClassPlans }),
      ];
      if (result.updatedClassPlans > 0) parts.push(t('profiles.updatedPlans', { count: result.updatedClassPlans }));
      if (result.enrichedSubjects > 0) parts.push(t('profiles.enrichedSubjects', { count: result.enrichedSubjects }));
      if ((result.repairs || []).length > 0) parts.push(t('profiles.cidataRepairs', { count: result.repairs.length }));

      toast('ok', t('profiles.cidataImportDone'), t('profiles.importSummary', { parts: parts.join(t('profiles.listSep')), revision: result.revision }));
      await render(document.getElementById('content'));
      return true;
    },
  });
}

/** 渲染 `.cidata` 的解析预览：哪一份档案、能导入多少、哪些导不进来。 */
function renderCidataPreview(parsed) {
  const box = h('div');

  if (parsed.profiles.length > 1) {
    const picker = select(
      parsed.profiles.map((p) => ({
        value: p.fileName,
        label: `${p.profileName}（${p.fileName}）`,
      })),
      parsed.profiles[0].fileName,
    );
    picker.classList.add('js-cidata-profile');
    box.appendChild(field(t('profiles.cidataWhichProfile'), picker));
  } else {
    // 只有一份时也放一个（隐藏的）选择器：确认导入时统一从这里取值。
    const hidden = select([{ value: parsed.profiles[0].fileName, label: parsed.profiles[0].fileName }],
      parsed.profiles[0].fileName);
    hidden.classList.add('js-cidata-profile');
    hidden.style.display = 'none';
    box.appendChild(hidden);
  }

  for (const profile of parsed.profiles) {
    const counts = [
      [t('profiles.subjects'), profile.subjects],
      [t('profiles.timeLayouts'), profile.timeLayouts],
      [t('profiles.timetable'), profile.classPlans],
      [t('profiles.timetableGroups'), (profile.groups || []).length],
      // 分割线与行动是**独立时间点**（宿主 TimeType 2 / 3），数清它们才知道作息有没有完整搬过来。
      [t('profiles.cidataSeparators'), profile.separators ?? 0],
      [t('profiles.cidataActions'), profile.actions ?? 0],
    ];

    box.appendChild(h('div', { style: { marginTop: '12px', borderTop: '1px solid var(--border)', paddingTop: '10px' } },
      h('div', { style: { fontWeight: '600', marginBottom: '6px' } },
        t('profiles.cidataProfileName', { name: profile.profileName })),
      h('div', { style: { display: 'flex', gap: '14px', flexWrap: 'wrap', fontSize: '13px' } },
        ...counts.map(([label, value]) => h('span', `${label} ${value}`)),
        (profile.rotationWeeks || 1) >= 2
          ? h('span', { style: { color: 'var(--accent)' } },
            t('profiles.cidataRotation', { weeks: profile.rotationWeeks })
            + (profile.semesterStartDate ? t('profiles.cidataRotationStart', { date: profile.semesterStartDate }) : t('profiles.cidataRotationStartLocal')))
          : null,
        profile.orderedSchedules > 0 ? h('span', { style: { color: 'var(--warn)' } }, t('profiles.cidataSchedulesSkipped', { count: profile.orderedSchedules })) : null,
        profile.overlayPlans > 0 ? h('span', { style: { color: 'var(--warn)' } }, t('profiles.cidataOverlaysSkipped', { count: profile.overlayPlans })) : null),
      (profile.classPlanNames || []).length > 0
        ? h('div.cell-sub', { style: { marginTop: '4px' } }, t('profiles.cidataPlanNames', { names: profile.classPlanNames.join(t('profiles.listSep')) })
          + (profile.classPlans > profile.classPlanNames.length ? ' …' : ''))
        : null,
      (profile.groups || []).length > 0
        ? h('div.cell-sub', { style: { marginTop: '2px' } },
          t('profiles.cidataGroupNames', { names: profile.groups.map((g) => g.name).join(t('profiles.listSep')) }))
        : null,
      // 行动明细：让管理员一眼看出"到点要做什么"有没有跟着来（内容本身不随包导入）。
      (profile.actionDetails || []).length > 0
        ? h('div.cell-sub', { style: { marginTop: '4px' } },
          t('profiles.cidataActionNames', {
            names: profile.actionDetails.slice(0, 5)
              .map((action) => `${action.time} ${action.name || action.type}`)
              .join(t('profiles.listSep')),
          }) + (profile.actions > 5 ? ' …' : ''))
        : null));
  }

  const warnings = parsed.profiles.flatMap((p) => p.warnings || []);
  if (warnings.length > 0) {
    box.appendChild(h('div.notice.notice-warn', { style: { marginTop: '12px' } },
      h('span.notice-icon', '!'),
      h('div',
        h('div', { style: { fontWeight: '600' } }, t('profiles.cidataWarnings', { count: warnings.length })),
        h('ul', { style: { margin: '6px 0 0 18px', padding: 0 } },
          ...warnings.map((w) => h('li', { style: { marginBottom: '3px' } }, w))))));
  }

  // 说明类单独一块：这些数据**没丢**，只是处理方式与界面上看到的不同。
  // 和上面黄色那块分开，管理员才不会把「已保留」误读成「要处理」。
  const notices = parsed.profiles.flatMap((p) => p.notices || []);
  if (notices.length > 0) {
    // `notice-notes` 用来和下面「未导入」那块区分（两块样式相同，靠类名才选得准）。
    box.appendChild(h('div.notice.notice-info.notice-notes', { style: { marginTop: '10px' } },
      h('span.notice-icon', 'i'),
      h('div',
        h('div', { style: { fontWeight: '600' } }, t('profiles.cidataNotices', { count: notices.length })),
        h('ul', { style: { margin: '6px 0 0 18px', padding: 0 } },
          ...notices.map((n) => h('li', { style: { marginBottom: '3px' } }, n))))));
  }

  if ((parsed.notImported || []).length > 0) {
    box.appendChild(h('div.notice.notice-info', { style: { marginTop: '10px' } },
      h('span.notice-icon', 'i'),
      h('div',
        h('div', { style: { fontWeight: '600' } }, t('profiles.cidataSkippedLead')),
        h('ul', { style: { margin: '6px 0 0 18px', padding: 0 } },
          ...parsed.notImported.map((n) => h('li', { style: { marginBottom: '3px' } }, n))))));
  }

  return box;
}

/**
 * 「AI 导入课表」对话框：粘贴任意格式的课表文本 → 交给模型解析 → 预览确认 → 合并进档案。
 * 解析与导入分成两步，导入用的是预览过的结果，不会重复调用模型。
 */
function openAiImportDialog(profiles) {
  if (!profiles || profiles.length === 0) {
    toast('warn', t('profiles.needProfile'), t('profiles.aiNeedProfile'));
    return;
  }

  let parsed = null;

  const profileSelect = select(profiles.map((p) => ({ value: p.id, label: p.name })), profiles[0].id);
  const preview = h('div');
  const parseStatus = h('div', { style: { fontSize: '12.5px', color: 'var(--text-faint)', marginTop: '8px' } },
    t('profiles.aiNotParsed'));

  const textarea = h('textarea', {
    placeholder: t('profiles.aiPlaceholder'),
    style: { minHeight: '170px' },
  });

  const fileInput = h('input', {
    type: 'file',
    accept: '.txt,.csv,.md,.html',
    style: { display: 'none' },
    onChange: async (e) => {
      const file = e.target.files?.[0];
      if (!file) return;
      textarea.value = await file.text();
      parsed = null;
      clear(preview);
      parseStatus.textContent = t('profiles.aiFileRead', { name: file.name });
      parseStatus.style.color = 'var(--text-faint)';
    },
  });

  const pickBtn = h('button.btn.btn-sm', { type: 'button', onClick: () => fileInput.click() }, t('profiles.pickFile'));

  const parseBtn = h('button.btn.btn-sm.btn-primary', {
    type: 'button',
    onClick: async () => {
      const text = textarea.value.trim();
      if (!text) {
        toast('warn', t('profiles.needTimetableText'));
        return;
      }

      parseBtn.disabled = true;
      parsed = null;
      clear(preview);
      parseStatus.textContent = t('profiles.aiParsing');
      parseStatus.style.color = 'var(--text-faint)';

      try {
        parsed = await api('/admin/ai/parse', { method: 'POST', body: { text } });
        parseStatus.textContent = t('profiles.aiParsedLead', { periods: parsed.periodCount, days: parsed.dayCount })
          + t('profiles.aiParsedTail', { subjects: parsed.subjectCount, lessons: parsed.courseCount });
        parseStatus.style.color = 'var(--ok)';
        preview.appendChild(renderParsedPreview(parsed.bundle));
      } catch (err) {
        parseStatus.textContent = err.message;
        parseStatus.style.color = 'var(--danger)';
      } finally {
        parseBtn.disabled = false;
      }
    },
  }, t('profiles.aiParse'));

  modal({
    title: t('profiles.aiImport'),
    width: 'wide',
    body: h('div',
      field(t('profiles.importInto'), profileSelect),
      h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', t('profiles.aiPrivacyHint')
          + t('profiles.aiMergeHint'))),
      h('div', { style: { display: 'flex', alignItems: 'center', gap: '8px', margin: '14px 0 8px' } },
        pickBtn, fileInput, parseBtn),
      textarea,
      parseStatus,
      preview,
    ),
    confirmText: t('profiles.importInto'),
    onConfirm: async () => {
      if (!parsed) {
        toast('warn', t('profiles.aiParseFirst'), t('profiles.aiPreviewHint'));
        return false;
      }

      const result = await api('/admin/ai/apply', {
        method: 'POST',
        body: { profileId: profileSelect.value, bundle: parsed.bundle },
      });

      const parts = [
        t('profiles.addedSubjects', { count: result.addedSubjects }),
        t('profiles.addedLayouts', { count: result.addedTimeLayouts }),
        t('profiles.addedPlans', { count: result.addedClassPlans }),
      ];
      if (result.updatedClassPlans > 0) parts.push(t('profiles.updatedPlans', { count: result.updatedClassPlans }));
      if (result.enrichedSubjects > 0) parts.push(t('profiles.enrichedSubjects', { count: result.enrichedSubjects }));

      toast('ok', t('profiles.cidataImportDone'), t('profiles.importSummary', { parts: parts.join(t('profiles.listSep')), revision: result.revision }));
      await render(document.getElementById('content'));
      return true;
    },
  });
}

/** 把解析结果渲染成「节次 × 星期」的紧凑预览。 */
function renderParsedPreview(bundle) {
  const layout = (bundle.timeLayouts || [])[0];
  if (!layout) {
    return h('div.notice.notice-warn', h('span.notice-icon', '!'), h('div', t('profiles.aiNoLayout')));
  }

  const periods = (layout.items || []).filter((item) => item.kind === 'class');
  const subjects = new Map((bundle.subjects || []).map((s) => [s.id, s]));
  const plans = new Map((bundle.classPlans || []).map((p) => [(p.daysOfWeek || [])[0], p]));
  const days = [...plans.keys()].filter((d) => d !== undefined).sort((a, b) => a - b);

  const cells = [
    h('div.preview-cell.head', t('profiles.period')),
    ...days.map((day) => h('div.preview-cell.head', DAY_LABEL[day] || t('profiles.aiDay', { day }))),
  ];

  periods.forEach((period, index) => {
    cells.push(h('div.preview-cell.side', `${index + 1} · ${period.startTime.slice(0, 5)}`));
    for (const day of days) {
      const slot = (plans.get(day).slots || []).find((s) => s.index === index);
      const subject = slot && slot.subjectId ? subjects.get(slot.subjectId) : null;
      cells.push(subject
        ? h('div.preview-cell.on', subject.initial || subject.name)
        : h('div.preview-cell'));
    }
  });

  return h('div',
    h('div.preview-summary', t('profiles.aiAboutLead', { name: layout.name, periods: periods.length })
      + t('profiles.aiAboutTail', { days: days.length, subjects: (bundle.subjects || []).length })),
    h('div.preview-grid', {
      style: { gridTemplateColumns: `86px repeat(${days.length}, minmax(44px, 1fr))` },
    }, ...cells),
  );
}

function openCreateDialog(withSample) {
  const nameInput = h('input', {
    type: 'text',
    value: withSample ? t('profiles.sampleProfile') : '',
    placeholder: t('profiles.namePlaceholder'),
  });
  const descInput = h('input', { type: 'text', placeholder: t('profiles.optional') });

  modal({
    title: withSample ? t('profiles.createSample') : t('profiles.newBlank'),
    width: 'wide',
    body: h('div',
      field(t('profiles.profileName'), nameInput),
      field(t('profiles.note'), descInput),
      withSample
        ? h('div.notice.notice-info',
          h('span.notice-icon', 'i'),
          h('div', t('profiles.sampleHint')))
        : h('div.notice.notice-warn',
          h('span.notice-icon', '!'),
          h('div', t('profiles.blankHint'))),
    ),
    confirmText: t('profiles.create'),
    onConfirm: async () => {
      const name = nameInput.value.trim();
      if (!name) {
        toast('warn', t('profiles.needName'));
        return false;
      }

      const result = await api(withSample ? '/admin/profiles/sample' : '/admin/profiles', {
        method: 'POST',
        body: { name, description: descInput.value.trim(), content: null },
      });

      toast('ok', t('profiles.created'), result.notes && result.notes.length > 0
        ? t('profiles.createdNotes', { count: result.notes.length })
        : '');

      window.location.hash = `#/profiles/${result.profile.id}`;
      return true;
    },
  });
}

/**
 * 复制档案：在既有配置上改出新方案（例如「夏季作息」→「夏季作息（九年级）」）。
 * <para>复制品是独立的新档案：不继承「默认」标记、不绑定任何设备，因此不会影响教室当前配置。</para>
 */
function duplicateProfile(profile) {
  const nameInput = h('input', { type: 'text', value: t('profiles.copyName', { name: profile.name }) });

  modal({
    title: t('profiles.duplicateTitle', { name: profile.name }),
    confirmText: t('profiles.duplicate'),
    body: h('div',
      h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', t('profiles.duplicateLead')
          + t('profiles.duplicateTail'))),
      h('div', { style: { marginTop: '10px', display: 'grid', gap: '4px' } },
        h('span', { style: { fontSize: '12.5px' } }, t('profiles.newName')),
        nameInput),
    ),
    onConfirm: async () => {
      try {
        const result = await api(`/admin/profiles/${profile.id}/duplicate`, {
          method: 'POST',
          body: { name: nameInput.value.trim() },
        });
        toast('ok', t('profiles.duplicated'), t('profiles.duplicatedHint', { name: result.profile.name }));
        await render(document.getElementById('content'));
        return true;
      } catch (err) {
        toastError(err, t('profiles.duplicateFailed'));
        return false;
      }
    },
  });
}

async function setDefault(profile) {
  const ok = await confirmDialog(t('profiles.setDefaultTitle'),
    t('profiles.setDefaultConfirm', { name: profile.name }),
    t('profiles.setDefault'));
  if (!ok) return;

  await api(`/admin/profiles/${profile.id}/default`, { method: 'POST' });
  toast('ok', t('profiles.defaultSet'));
  await render(document.getElementById('content'));
}

async function pushProfile(profile) {
  const ok = await confirmDialog(t('profiles.pushNow'),
    t('profiles.pushConfirm', { name: profile.name }),
    t('profiles.push'));
  if (!ok) return;

  const result = await api(`/admin/profiles/${profile.id}/push`, {
    method: 'POST',
    body: { scope: 'all', targetIds: [], message: '' },
  });

  toast('ok', t('profiles.pushed'), t('profiles.pushedAffected', { count: result.affected }));
}

async function removeProfile(profile) {
  const ok = await confirmDialog(t('profiles.deleteTitle'),
    t('profiles.deleteConfirm', { name: profile.name }),
    t('profiles.delete'), true);
  if (!ok) return;

  await api(`/admin/profiles/${profile.id}`, { method: 'DELETE' });
  toast('ok', t('profiles.deleted'));
  await render(document.getElementById('content'));
}
