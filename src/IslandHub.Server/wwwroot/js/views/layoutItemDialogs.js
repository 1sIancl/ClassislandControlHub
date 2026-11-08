/**
 * 时间点上的「附加信息」编辑：插件附加设置（`AttachedObjects`）与行动组（`ActionSet`）。
 *
 * <p>
 * 独立成模块，是因为时间表编辑页与档案编辑器都要用同一套编辑器：
 * 时间表已经是全局实体，附加信息是**时间点**的属性，不能因为页面换了就编辑不到。
 * </p>
 *
 * <p>
 * 两条克制（与之前一致）：
 * ① 不按 GUID 硬编码语义——键由插件作者定义、没有公开契约，只对**已知的 3 个**给中文名；
 * ② 值怎么存就怎么显示，枚举值照数字给，猜错的名字比数字更误导。
 * </p>
 */

import { h, clear, modal, confirmDialog, toast } from '../core/ui.js?v=185';
import { t } from './../core/i18n.js?v=185';

/** 已知附加设置的 GUID → 名称与字段中文（依据真实 2.1.1.1 包统计，59 个时间点上稳定出现）。 */
export const KNOWN_ATTACHED = {
  '7625de96-38aa-4b71-b478-3f156dd9458d': {
    nameKey: 'attached.known.weather.name',
    fields: {
      AlertShowMode: 'attached.known.weather.alertShowMode',
      ForecastShowMode: 'attached.known.weather.forecastShowMode',
      IsAttachSettingsEnabled: 'attached.known.common.enabled',
      IsActive: 'attached.known.common.active',
    },
  },
  '08f0d9c3-c770-4093-a3d0-02f3d90c24bc': {
    nameKey: 'attached.known.classNotice.name',
    fields: {
      IsClassOnNotificationEnabled: 'attached.known.classNotice.on',
      IsClassOnPreparingNotificationEnabled: 'attached.known.classNotice.preparing',
      IsClassOffNotificationEnabled: 'attached.known.classNotice.off',
      ClassPreparingDeltaTime: 'attached.known.classNotice.deltaSeconds',
      ClassOnPreparingText: 'attached.known.classNotice.preparingText',
      OutdoorClassOnPreparingText: 'attached.known.classNotice.outdoorPreparingText',
      ClassOnPreparingMaskText: 'attached.known.classNotice.preparingMask',
      OutdoorClassOnPreparingMaskText: 'attached.known.classNotice.outdoorPreparingMask',
      ClassOnMaskText: 'attached.known.classNotice.onMask',
      ClassOffMaskText: 'attached.known.classNotice.offMask',
      ClassOffOverlayText: 'attached.known.classNotice.offOverlay',
      IsAttachSettingsEnabled: 'attached.known.common.enabled',
      IsActive: 'attached.known.common.active',
    },
  },
  '58e5b69a-764a-472b-bcf7-003b6a8c7fdf': {
    nameKey: 'attached.known.extraInfo.name',
    fields: {
      ShowExtraInfoOnTimePoint: 'attached.known.extraInfo.show',
      ExtraInfoType: 'attached.known.extraInfo.type',
      IsCountdownEnabled: 'attached.known.extraInfo.countdown',
      CountdownSeconds: 'attached.known.extraInfo.countdownSeconds',
      ExtraInfo4ShowSecondsSeconds: 'attached.known.extraInfo.showSecondsSeconds',
      ScheduleSpacing: 'attached.known.extraInfo.spacing',
      ShowCurrentLessonOnlyOnClass: 'attached.known.extraInfo.currentLessonOnly',
      IsNonExactCountdownEnabled: 'attached.known.extraInfo.nonExactCountdown',
      IsAttachSettingsEnabled: 'attached.known.common.enabled',
      IsActive: 'attached.known.common.active',
    },
  },
};

/** 通用字段名词表（只是词表，不是语义解释）。 */
const FIELD_LABELS = {
  format: '格式',
  countdownformat: '倒计时格式',
  color: '颜色',
  colour: '颜色',
  enabled: '启用',
  isenabled: '启用',
  visible: '显示',
  isvisible: '显示',
  duration: '时长',
  seconds: '秒',
  opacity: '不透明度',
  size: '大小',
  fontsize: '字号',
  text: '文本',
  content: '内容',
  title: '标题',
};

const COLOR_PATTERN = /^#([0-9a-f]{3}|[0-9a-f]{6}|[0-9a-f]{8})$/i;

/** 附加设置项数。 */
export const attachedCountOf = (item) => Object.keys(item?.attachedSettings ?? {}).length;

/** 行动组里的动作个数（0 表示没配动作）。 */
export const actionCountOf = (item) => {
  try {
    const parsed = JSON.parse(item?.actionSetJson ?? '{}');
    return Array.isArray(parsed?.Actions) ? parsed.Actions.length : 0;
  } catch {
    return 0;
  }
};

/** 一个字段的编辑控件：按值类型给，对象/数组给 JSON 框。 */
function fieldEditor(value, onChange) {
  if (typeof value === 'boolean') {
    const box = h('input', { type: 'checkbox' });
    box.checked = value;
    box.onchange = () => onChange(box.checked);
    return h('label.checkbox-field', box,
      h('span', value ? t('profiles.editor.attachedOn') : t('profiles.editor.attachedOff')));
  }

  if (typeof value === 'number') {
    const input = h('input', { type: 'number', step: 'any', value: String(value) });
    input.oninput = () => onChange(input.value === '' ? 0 : Number(input.value));
    return input;
  }

  if (typeof value === 'string') {
    const input = h('input', { type: 'text', value });
    input.oninput = () => onChange(input.value);
    return input;
  }

  // 对象 / 数组 / null：JSON 框（清空 = null），格式错只标红不打断输入。
  const area = h('textarea', { rows: 3, style: { width: '100%' } });
  area.value = value === null || value === undefined ? '' : JSON.stringify(value, null, 2);
  area.oninput = () => {
    const text = area.value.trim();
    if (text === '') {
      onChange(null);
      area.style.borderColor = '';
      return;
    }

    try {
      onChange(JSON.parse(text));
      area.style.borderColor = '';
    } catch {
      area.style.borderColor = 'var(--danger)';
    }
  };
  return area;
}

/**
 * 附加设置编辑弹窗。
 *
 * @param {object} item 时间点（要有 attachedSettings 字段）。
 * @param {Function} onChange 保存后的回调（通常是 markDirty）。
 */
export function openAttachedDialog(item, onChange) {
  const draft = JSON.parse(JSON.stringify(item.attachedSettings ?? {}));
  const host = h('div');

  const paint = () => {
    clear(host);

    const guids = Object.keys(draft);
    if (guids.length === 0) {
      host.appendChild(h('p.card-desc', t('profiles.editor.attachedEmpty')));
      return;
    }

    for (const guid of guids) {
      const value = draft[guid];
      const known = KNOWN_ATTACHED[guid.trim().toLowerCase()] ?? KNOWN_ATTACHED[guid.trim()];
      const isObject = value && typeof value === 'object' && !Array.isArray(value);

      const body = isObject
        ? h('div.attach-fields', ...Object.entries(value).map(([key, inner]) => {
          const label = known?.fields?.[key]
            ? t(known.fields[key])
            : FIELD_LABELS[key.toLowerCase()];
          const row = h('div.attach-row',
            h('span.attach-key', key, label ? h('span.attach-key-hint', `（${label}）`) : null));

          row.appendChild(fieldEditor(inner, (next) => {
            draft[guid][key] = next;
          }));

          // 颜色值额外给块色板，便于确认改对了（不改语义，只是看得见）。
          if (typeof inner === 'string' && COLOR_PATTERN.test(inner)) {
            row.appendChild(h('span.kind-dot', { style: { background: inner, marginLeft: '6px' } }));
          }

          return row;
        }))
        : h('div.attach-row', fieldEditor(value, (next) => {
          draft[guid] = next;
        }));

      const head = h('div.attach-head',
        known ? h('span.attach-known', t(known.nameKey)) : null,
        h('code.attach-guid', { title: guid }, guid),
        h('div.spacer'),
        h('button.btn.btn-sm.btn-ghost', {
          type: 'button',
          onClick: async () => {
            const ok = await confirmDialog(
              t('profiles.editor.attachedRemove'),
              t('profiles.editor.attachedRemoveConfirm', { guid }),
              t('profiles.editor.delete'),
              true,
            );
            if (!ok) {
              return;
            }

            delete draft[guid];
            paint();
          },
        }, t('profiles.editor.delete')));

      const addField = isObject
        ? h('button.btn.btn-sm', {
          type: 'button',
          style: { marginTop: '6px' },
          onClick: () => {
            const name = prompt(t('profiles.editor.attachedNewField'));
            if (!name) {
              return;
            }

            draft[guid][name.trim()] = null;
            paint();
          },
        }, t('profiles.editor.attachedNewField'))
        : null;

      host.appendChild(h('div.attach-group', head, body, addField));
    }
  };

  paint();

  modal({
    title: t('profiles.editor.attachedTitle', { count: Object.keys(draft).length }),
    width: 'wide',
    body: h('div',
      h('p.card-desc', t('profiles.editor.attachedEditNote')),
      host,
      h('button.btn.btn-sm', {
        type: 'button',
        style: { marginTop: '10px' },
        onClick: () => {
          const guid = prompt(t('profiles.editor.attachedNewGuid'));
          if (!guid) {
            return;
          }

          draft[guid.trim()] = {};
          paint();
        },
      }, t('profiles.editor.attachedNew')),
      h('details.attach-raw',
        h('summary', t('profiles.editor.attachedRaw')),
        h('pre.attach-json', JSON.stringify(draft, null, 2)))),
    confirmText: t('profiles.editor.save'),
    onConfirm: () => {
      // 空对象视同没有附加设置：留一个 {} 只会让徽章显示「附加 1」却点开是空的。
      const cleaned = {};
      for (const [guid, value] of Object.entries(draft)) {
        if (value && typeof value === 'object' && !Array.isArray(value)
          && Object.keys(value).length === 0) {
          continue;
        }

        cleaned[guid] = value;
      }

      item.attachedSettings = Object.keys(cleaned).length > 0 ? cleaned : null;
      onChange?.();
      return true;
    },
  });
}

/**
 * 行动组编辑（宿主真实结构：`ActionSet { Name, Actions[] }`）。
 *
 * <p>
 * 每个动作是「`Id` + `Settings`」，`Settings` 的类型由 `Id` 决定，是开放集合。
 * 所以不逐字段造表单：列出动作类型让人看清"这条行动会做什么"，改内容编整包 JSON——
 * 硬造表单只会在遇到没见过的动作时把字段丢掉（这正是之前 8 条行动配置丢失的原因）。
 * </p>
 */
export function openActionSetDialog(item, onChange) {
  let parsed = null;
  try {
    parsed = JSON.parse(item.actionSetJson || '{}');
  } catch {
    parsed = null;
  }

  const actions = Array.isArray(parsed?.Actions) ? parsed.Actions : [];
  const nameInput = h('input', { type: 'text', value: parsed?.Name ?? '' });

  const summary = h('div.cell-sub');
  if (actions.length === 0) {
    summary.appendChild(h('span', t('profiles.editor.actionSetEmpty')));
  } else {
    summary.appendChild(h('div', { style: { marginBottom: '6px' } },
      t('profiles.editor.actionSetLead', { count: actions.length })));
    summary.appendChild(h('div.attach-fields', ...actions.map((action, index) => h('div.attach-row',
      h('span.attach-key', `${index + 1}. ${action?.Id ?? t('profiles.editor.actionSetUnknown')}`),
      h('span.cell-sub', t('profiles.editor.actionSetHasSettings', {
        state: action?.Settings ? t('profiles.editor.actionSetYes') : t('profiles.editor.actionSetNo'),
      }))))));
  }

  const area = h('textarea', {
    rows: 12,
    spellcheck: 'false',
    style: {
      width: '100%', fontFamily: 'monospace', fontSize: '12.5px', lineHeight: '1.5',
    },
  });
  area.value = item.actionSetJson || '';

  const error = h('div.attach-dim', { hidden: true, style: { color: 'var(--danger)' } });
  area.oninput = () => {
    try {
      const next = JSON.parse(area.value);
      if (!next || typeof next !== 'object' || Array.isArray(next)) {
        throw new Error('not an object');
      }

      area.style.borderColor = '';
      error.hidden = true;
    } catch {
      area.style.borderColor = 'var(--danger)';
      error.hidden = false;
      error.textContent = t('profiles.editor.actionBadJson');
    }
  };

  modal({
    title: t('profiles.editor.actionTitle'),
    width: 'wide',
    body: h('div',
      h('p.card-desc', t('profiles.editor.actionSetNote')),
      nameInput,
      summary,
      h('div', { style: { marginTop: '10px' } }, t('profiles.editor.actionRaw')),
      area,
      error),
    confirmText: t('profiles.editor.save'),
    onConfirm: () => {
      if (!error.hidden) {
        return false;
      }

      try {
        const next = JSON.parse(area.value || '{}');
        if (nameInput.value.trim()) {
          next.Name = nameInput.value.trim();
        }

        item.actionSetJson = JSON.stringify(next);
        item.actionSetName = next.Name ?? null;
        item.actionSetCount = Array.isArray(next.Actions) ? next.Actions.length : 0;
        onChange?.();
        return true;
      } catch {
        error.hidden = false;
        error.textContent = t('profiles.editor.actionBadJson');
        return false;
      }
    },
  });
}
