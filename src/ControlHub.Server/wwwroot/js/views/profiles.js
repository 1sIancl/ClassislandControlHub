/**
 * 配置档案列表视图。
 * 档案是集控下发的最小单元：一个档案 = 一套时间表 + 课表 + 科目 + 自定义设置。
 */

import { api } from '../core/api.js?v=26';
import {
  h, clear, formatDateTime, toast, loadingBlock, modal, confirmDialog,
  emptyState, field, select,
} from '../core/ui.js?v=26';

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
          h('button.btn.btn-sm', { type: 'button', onClick: () => openAiImportDialog(profiles) }, 'AI 导入课表'),
          h('button.btn.btn-sm', { type: 'button', onClick: () => openImportCsesDialog(profiles) }, '从 CSES 导入'),
          h('button.btn.btn-sm', { type: 'button', onClick: () => openCreateDialog(false) }, '新建空白档案'),
          h('button.btn.btn-primary.btn-sm', { type: 'button', onClick: () => openCreateDialog(true) }, '+ 新建示例档案'),
        ),
      ),
      profiles.length === 0
        ? emptyState('book', '还没有配置档案',
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
        profile.code
          ? h('span.badge.badge-accent', { style: { fontFamily: 'var(--mono)', letterSpacing: '1px' } }, profile.code)
          : null,
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
      h('button.btn.btn-sm', { type: 'button', onClick: () => openVersionsDialog(profile) }, '历史版本'),
      profile.isDefault
        ? null
        : h('button.btn.btn-sm', { type: 'button', onClick: () => setDefault(profile) }, '设为默认'),
      h('button.btn.btn-sm.btn-danger', { type: 'button', onClick: () => removeProfile(profile) }, '删除'),
    ),
  );
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
          h('div', '还没有历史版本。每次保存档案前会自动留一份快照（最多保留 20 份）。')));
        return;
      }

      listBox.appendChild(h('div.table-wrap',
        h('table.data',
          h('thead', h('tr',
            h('th', '时间'),
            h('th', '内容版本'),
            h('th', '原因'),
            h('th', '操作人'),
            h('th', { style: { textAlign: 'right' } }, '操作'),
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
                  if (!await confirmDialog('回滚到该版本',
                    `将把「${profile.name}」回滚到 ${formatDateTime(v.createdAt)} 的快照（版本 v${v.revision}）。\n`
                    + '回滚前会先把当前内容也存一份快照，可以再滚回来。确定继续吗？', '回滚')) {
                    return;
                  }

                  await api(`/admin/profiles/${profile.id}/versions/${v.id}/restore`, { method: 'POST' });
                  toast('ok', '已回滚', '档案内容已恢复，可到「配置下发」推送给教室。');
                  await load();
                },
              }, '回滚'),
              h('button.btn.btn-sm.btn-ghost', {
                type: 'button',
                onClick: async () => {
                  if (!await confirmDialog('删除该快照', '仅删除这份历史记录，不影响当前档案内容。', '删除', true)) {
                    return;
                  }

                  await api(`/admin/profiles/${profile.id}/versions/${v.id}`, { method: 'DELETE' });
                  toast('ok', '已删除');
                  await load();
                },
              }, '删除'),
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
    title: `历史版本 · ${profile.name}`,
    width: 'wide',
    hideFooter: true,
    body: listBox,
  });

  load();
}

/** 「从 CSES 导入」对话框：选择目标档案 + 选择/粘贴 CSES 文件内容。 */
function openImportCsesDialog(profiles) {
  if (!profiles || profiles.length === 0) {
    toast('warn', '请先创建档案', 'CSES 导入需要指定一个目标档案，请先新建一个档案。');
    return;
  }

  const profileSelect = select(
    profiles.map((p) => ({ value: p.id, label: p.name })),
    profiles[0].id,
  );

  const textarea = h('textarea', {
    placeholder: '在此粘贴 CSES（.yml / .yaml）文件内容，或点击下方按钮选择文件…',
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

  const pickBtn = h('button.btn.btn-sm', { type: 'button', onClick: () => fileInput.click() }, '选择 CSES 文件');

  modal({
    title: '从 CSES 导入',
    width: 'wide',
    body: h('div',
      field('导入到档案', profileSelect),
      h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', 'CSES（通用课表交换格式）可从 ClassIsland 档案编辑器的「导入 / 导出」导出。导入会合并其中的时间表与科目，同名科目 / 同时间表会自动跳过。')),
      h('div', { style: { display: 'flex', alignItems: 'center', gap: '8px', margin: '12px 0 6px' } },
        pickBtn, fileInput,
        h('span', { style: { fontSize: '12px', color: 'var(--text-faint)' } }, '文件编码需为 UTF-8'),
      ),
      textarea,
    ),
    confirmText: '导入',
    onConfirm: async () => {
      const yaml = textarea.value.trim();
      if (!yaml) {
        toast('warn', '请粘贴或选择 CSES 内容');
        return false;
      }

      const result = await api('/admin/profiles/import-cses', {
        method: 'POST',
        body: { profileId: profileSelect.value, yaml },
      });

      const parts = [
        `新增科目 ${result.addedSubjects} 个`,
        `时间表 ${result.addedTimeLayouts} 个`,
        `课表 ${result.addedClassPlans} 张`,
      ];
      if (result.updatedClassPlans > 0) parts.push(`覆盖课表 ${result.updatedClassPlans} 张`);
      if (result.enrichedSubjects > 0) parts.push(`补全科目信息 ${result.enrichedSubjects} 个`);

      toast('ok', '导入成功', `${parts.join('、')}。`);
      await render(document.getElementById('content'));
      return true;
    },
  });
}

const DAY_LABEL = { 1: '周一', 2: '周二', 3: '周三', 4: '周四', 5: '周五', 6: '周六', 7: '周日' };

/**
 * 「AI 导入课表」对话框：粘贴任意格式的课表文本 → 交给模型解析 → 预览确认 → 合并进档案。
 * 解析与导入分成两步，导入用的是预览过的结果，不会重复调用模型。
 */
function openAiImportDialog(profiles) {
  if (!profiles || profiles.length === 0) {
    toast('warn', '请先创建档案', 'AI 导入需要指定一个目标档案，请先新建一个档案。');
    return;
  }

  let parsed = null;

  const profileSelect = select(profiles.map((p) => ({ value: p.id, label: p.name })), profiles[0].id);
  const preview = h('div');
  const parseStatus = h('div', { style: { fontSize: '12.5px', color: 'var(--text-faint)', marginTop: '8px' } },
    '尚未解析。点击「AI 解析」后先预览，确认无误再导入。');

  const textarea = h('textarea', {
    placeholder: '把课表粘贴到这里：从 Excel 复制的表格、从教务系统网页复制的内容，或手工整理的文本都可以。',
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
      parseStatus.textContent = `已读取 ${file.name}，请点击「AI 解析」。`;
      parseStatus.style.color = 'var(--text-faint)';
    },
  });

  const pickBtn = h('button.btn.btn-sm', { type: 'button', onClick: () => fileInput.click() }, '选择文件');

  const parseBtn = h('button.btn.btn-sm.btn-primary', {
    type: 'button',
    onClick: async () => {
      const text = textarea.value.trim();
      if (!text) {
        toast('warn', '请先粘贴或选择课表内容');
        return;
      }

      parseBtn.disabled = true;
      parsed = null;
      clear(preview);
      parseStatus.textContent = '正在调用模型解析，请稍候……';
      parseStatus.style.color = 'var(--text-faint)';

      try {
        parsed = await api('/admin/ai/parse', { method: 'POST', body: { text } });
        parseStatus.textContent = `解析完成：作息 ${parsed.periodCount} 节 · ${parsed.dayCount} 天 · `
          + `${parsed.subjectCount} 个科目 · ${parsed.courseCount} 节有课。`;
        parseStatus.style.color = 'var(--ok)';
        preview.appendChild(renderParsedPreview(parsed.bundle));
      } catch (err) {
        parseStatus.textContent = err.message;
        parseStatus.style.color = 'var(--danger)';
      } finally {
        parseBtn.disabled = false;
      }
    },
  }, 'AI 解析');

  modal({
    title: 'AI 导入课表',
    width: 'wide',
    body: h('div',
      field('导入到档案', profileSelect),
      h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', '课表原文会发送到「系统设置 → AI 辅助导入」里配置的接口。'
          + '导入时同名科目与相同作息的时间表会自动复用，同一天的课表会被本次结果覆盖。')),
      h('div', { style: { display: 'flex', alignItems: 'center', gap: '8px', margin: '14px 0 8px' } },
        pickBtn, fileInput, parseBtn),
      textarea,
      parseStatus,
      preview,
    ),
    confirmText: '导入到档案',
    onConfirm: async () => {
      if (!parsed) {
        toast('warn', '请先点击「AI 解析」', '预览确认后再导入。');
        return false;
      }

      const result = await api('/admin/ai/apply', {
        method: 'POST',
        body: { profileId: profileSelect.value, bundle: parsed.bundle },
      });

      const parts = [
        `新增科目 ${result.addedSubjects} 个`,
        `时间表 ${result.addedTimeLayouts} 个`,
        `课表 ${result.addedClassPlans} 张`,
      ];
      if (result.updatedClassPlans > 0) parts.push(`覆盖课表 ${result.updatedClassPlans} 张`);
      if (result.enrichedSubjects > 0) parts.push(`补全科目信息 ${result.enrichedSubjects} 个`);

      toast('ok', '导入完成', `${parts.join('、')}。配置版本已更新为 ${result.revision}。`);
      await render(document.getElementById('content'));
      return true;
    },
  });
}

/** 把解析结果渲染成「节次 × 星期」的紧凑预览。 */
function renderParsedPreview(bundle) {
  const layout = (bundle.timeLayouts || [])[0];
  if (!layout) {
    return h('div.notice.notice-warn', h('span.notice-icon', '!'), h('div', '解析结果里没有时间表。'));
  }

  const periods = (layout.items || []).filter((item) => item.kind === 'class');
  const subjects = new Map((bundle.subjects || []).map((s) => [s.id, s]));
  const plans = new Map((bundle.classPlans || []).map((p) => [(p.daysOfWeek || [])[0], p]));
  const days = [...plans.keys()].filter((d) => d !== undefined).sort((a, b) => a - b);

  const cells = [
    h('div.preview-cell.head', '节次'),
    ...days.map((day) => h('div.preview-cell.head', DAY_LABEL[day] || `第 ${day} 天`)),
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
    h('div.preview-summary', `即将导入：作息「${layout.name}」共 ${periods.length} 节，`
      + `${days.length} 天课表，${(bundle.subjects || []).length} 个科目。`),
    h('div.preview-grid', {
      style: { gridTemplateColumns: `86px repeat(${days.length}, minmax(44px, 1fr))` },
    }, ...cells),
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
