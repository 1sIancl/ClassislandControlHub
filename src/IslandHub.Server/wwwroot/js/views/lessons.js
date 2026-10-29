/**
 * 快捷换课：教室端课表实时状态 + 临时换课 + 调休。
 *
 * <para>「实时」这部分的数只有教室端算得准 —— 教师可能在教室机本地改过课表，
 * 也可能刚在那边换过课，服务端按自己的档案推算出来的是另一节课。所以下节课的
 * 科目与开始时间一律来自教室端心跳（见插件 LessonBridge）。</para>
 *
 * <para>与「档案页 → 临时换课」的区别（界面上也要写清，否则管理员会以为重复）：
 * 那一套记在 A 端、由服务端在下发时合成进课表，适合「提前排好一次调整、全楼统一」；
 * 这一页把指令直接发到教室，写进 ClassIsland 自己的临时层课表 / 日期预定，
 * 效果与在教室大屏上手动换课一致，适合「现在就改这一节」。</para>
 */

import { api } from '../core/api.js?v=156';
import { errorBlock } from '../core/errors.js?v=156';
import {
  h, clear, toast, loadingBlock, field, select, confirmDialog,
} from '../core/ui.js?v=156';
import { t } from '../core/i18n.js?v=156';

export const meta = {
  title: t('lessons.title'),
  subtitle: t('lessons.subtitle'),
};

/** 倒计时定时器（离开页面时自动停）。 */
let ticker = null;

/** 最近一次 live 数据，供倒计时与表单复用。 */
let live = null;

/** 两张卡片各自的下拉框填充函数（目录拉回来之后调用）。 */
let swapFill = null;
let adjustFill = null;

/** 「全部设备」在下拉框里的取值（不是设备 ID，用一个不可能撞车的字符串）。 */
const ALL_TARGETS = '__all__';

/** 页面上的目标教室下拉框；刷新设备列表时要跟着重画。 */
const targetSelects = [];

/**
 * 目标教室下拉框。
 *
 * <para>为什么必须有它：换课是「改某个班这一节」，而服务端在请求里**没带目标时默认发给全部设备**
 * （见 LessonEndpoints.ResolveTargetsAsync）。早先这里一个目标都不传、下面的节次与科目又是从
 * 某一台设备读来的，于是「把高一(1)班第 3 节换成数学」会变成「所有教室都把第 3 节换成数学」——
 * 而界面上没有任何地方提示这一点。</para>
 */
function targetField(onChange) {
  const sel = h('select');
  paintTargets(sel, catalogDeviceId());
  sel.addEventListener('change', () => onChange?.(sel.value));
  targetSelects.push(sel);
  return sel;
}

/** 用当前设备列表重画目标下拉；`preferred` 是尽量保留的原选择。 */
function paintTargets(sel, preferred) {
  const keep = sel.value || preferred;
  clear(sel);

  for (const device of live.devices) {
    sel.appendChild(h('option', { value: device.deviceId }, t('lessons.deviceOption', {
      name: device.deviceName,
      group: device.groupName ? t('lessons.deviceOptionGroup', { group: device.groupName }) : '',
      offline: device.isOnline ? '' : t('lessons.offlineSuffix'),
    })));
  }

  // 「全部设备」放在最后并写明「慎用」：它是给「整栋楼同一处调整」用的，
  // 日常只想改一个班时顺手选错，代价是整个学校一起变。
  sel.appendChild(h('option', { value: ALL_TARGETS }, t('lessons.allDevices')));

  if (keep && [...sel.options].some((o) => o.value === keep)) {
    sel.value = keep;
  }
}

/** 请求里要带的目标设备 ID 列表。 */
function targetsOf(sel) {
  if (sel.value === ALL_TARGETS) {
    return live.devices.map((d) => d.deviceId);
  }

  return sel.value ? [sel.value] : [];
}

export async function render(container) {
  clear(container);
  container.appendChild(loadingBlock());

  live = await api('/admin/lessons/live').catch(() => null);
  if (!live) {
    clear(container);
    container.appendChild(errorBlock(t('lessons.loadFailed'), t('lessons.loadFailedHint')));
    return;
  }

  clear(container);
  container.appendChild(h('div',
    renderLiveCard(),
    renderSwapCard(),
    renderAdjustCard(),
  ));

  await loadCatalogFor();
  startTicker();
}

// ────────────────────────────── 实时状态 ──────────────────────────────

function renderLiveCard() {
  const online = live.devices.filter((d) => d.isOnline).length;

  return h('div.card',
    h('div.card-head',
      h('div',
        h('h3', t('lessons.liveCard')),
        h('p.card-desc', t('lessons.liveSummary', { total: live.devices.length, online })
          + t('lessons.liveCardDesc')),
      ),
      h('div.card-actions',
        h('button.btn.btn-sm', { type: 'button', onClick: () => refreshLive() }, t('lessons.refresh')),
      ),
    ),
    h('div#lessonLiveHost', renderDeviceRows()),
  );
}

function renderDeviceRows() {
  if (live.devices.length === 0) {
    return h('p.card-desc', t('lessons.noDevices'));
  }

  return h('div.config-panel', ...live.devices.map((device) => {
    const lesson = device.lesson;
    // 秒数按「上报时刻 + 剩余秒」在前端本地递减：心跳每分钟一次，
    // 但倒计时必须每秒都在走，否则看起来像卡住了。
    const left = lesson && device.isOnline
      ? Math.max(0, lesson.leftSeconds - elapsedSeconds(lesson.reportedAt))
      : null;

    return h('div.config-item',
      h('div', { style: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 } },
        h('span', { style: { fontWeight: '600', fontSize: '13px' } },
          device.deviceName + (device.groupName ? ` · ${device.groupName}` : '')),
        h('span', { style: { color: 'var(--text-faint)', fontSize: '12px' } },
          lesson
            ? `${stateLabel(lesson)}${lesson.classPlanName ? ` · ${lesson.classPlanName}` : ''}`
              // 多周轮换的「现在第几周」只有教室端算得准（它按宿主的轮换起点 / 偏移算），
              // 这里照它上报的原样显示，不做二次推算。
              + (lesson.rotationWeeks >= 2 && lesson.rotationWeek >= 1
                ? t('lessons.rotationWeek', { week: lesson.rotationWeek, weeks: lesson.rotationWeeks })
                : '')
            : t('lessons.noLessonReport')),
      ),
      h('span', { style: { marginLeft: 'auto', flex: 'none', display: 'flex', alignItems: 'center', gap: '14px' } },
        lesson
          ? h('span', { style: { fontSize: '12.5px', color: 'var(--text-dim)', textAlign: 'right' } },
            lesson.state === 'onClass'
              ? t('lessons.nowInClass', { subject: lesson.currentSubject || '—' })
              : t('lessons.nextClass', { subject: lesson.nextSubject || '—', at: lesson.nextStartAt || '' }))
          : null,
        left !== null
          ? h('span.lesson-countdown', {
            dataset: { left: String(left), at: String(Date.now()) },
            style: {
              fontFamily: 'var(--mono)', fontSize: '13px', minWidth: '62px', textAlign: 'right',
              color: left <= 60 ? 'var(--warn, #f0a020)' : 'var(--text)',
            },
          }, formatLeft(left))
          : null,
        h('span.badge' + (device.isOnline ? '' : '.badge-neutral'), device.isOnline ? t('lessons.online') : t('lessons.offline')),
      ),
    );
  }));
}

/** 每秒刷新倒计时（只改文本，不重画整页——重画会把选中项与滚动位置弄丢）。 */
function startTicker() {
  if (ticker) {
    clearInterval(ticker);
  }

  ticker = setInterval(() => {
    if (!document.getElementById('lessonLiveHost')) {
      clearInterval(ticker);
      ticker = null;
      return;
    }

    for (const el of document.querySelectorAll('.lesson-countdown')) {
      const base = Number(el.dataset.left) || 0;
      const at = Number(el.dataset.at) || Date.now();
      const left = Math.max(0, base - Math.floor((Date.now() - at) / 1000));
      el.textContent = formatLeft(left);
      el.style.color = left <= 60 ? 'var(--warn, #f0a020)' : 'var(--text)';
    }
  }, 1000);
}

async function refreshLive() {
  live = await api('/admin/lessons/live').catch(() => live);
  const host = document.getElementById('lessonLiveHost');
  if (host && live) {
    clear(host);
    host.appendChild(renderDeviceRows());
    // 设备列表可能有增减（新接入 / 被停用），目标下拉要跟着走，
    // 否则会留下一个指向已经不存在设备的选项。
    for (const sel of targetSelects) {
      paintTargets(sel);
    }
  }
}

// ────────────────────────────── 换课 ──────────────────────────────

function renderSwapCard() {
  const dateInput = h('input', { type: 'date', value: todayText() });
  const slotSelect = h('select');
  // 目标教室：换课改的是「某个班这一节」。换了目标就顺手把课表重读一遍——
  // 节次与科目都来自教室端，换个班就得换一份，否则会把 A 班的第 3 节
  // 按 B 班的科目显示出来。
  const targetSelect = targetField(async (value) => {
    if (value !== ALL_TARGETS) {
      await loadCatalogFor(value);
    }
  });
  const modeSelect = select([
    { value: 'replace', label: t('lessons.modeSwapSubject') },
    { value: 'swap', label: t('lessons.modeSwapPeriod') },
  ], 'replace', () => paintSwapFields());
  const subjectSelect = h('select');
  const swapWithSelect = h('select');

  const subjectField = field(t('lessons.subjectLabel'), subjectSelect, t('lessons.subjectHint'));
  const swapField = field(t('lessons.swapWithLabel'), swapWithSelect, t('lessons.swapWithHint'));

  function paintSwapFields() {
    subjectField.hidden = modeSelect.value !== 'replace';
    swapField.hidden = modeSelect.value !== 'swap';
  }

  function fillOptions(catalog) {
    clear(slotSelect);
    clear(subjectSelect);
    clear(swapWithSelect);

    if (!catalog) {
      slotSelect.appendChild(h('option', { value: '' }, t('lessons.readFirst')));
      subjectSelect.appendChild(h('option', { value: '' }, t('lessons.readFirst')));
      swapWithSelect.appendChild(h('option', { value: '' }, t('lessons.readFirst')));
      return;
    }

    for (const slot of catalog.slots) {
      slotSelect.appendChild(h('option', { value: String(slot.index) },
        t('lessons.slotOption', {
          number: slot.number,
          at: slot.startAt,
          subject: slot.subjectName ? t('lessons.slotSubject', { subject: slot.subjectName }) : '',
          changed: slot.isChanged ? t('lessons.slotChanged') : '',
        })));
      swapWithSelect.appendChild(h('option', { value: String(slot.index) },
        t('lessons.slotLabel', { number: slot.number, at: slot.startAt })));
    }

    for (const subject of catalog.subjects) {
      subjectSelect.appendChild(h('option', { value: subject.id }, subject.name));
    }
  }

  fillOptions(null);
  if (modeSelect.value === 'replace') {
    swapField.hidden = true;
  } else {
    subjectField.hidden = true;
  }

  const card = h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', t('lessons.changeCard')),
        h('p.card-desc', t('lessons.changeCardDesc1')
          + t('lessons.changeCardDesc2')),
      ),
      h('div.card-actions',
        h('button.btn.btn-sm', { type: 'button', onClick: () => readCatalog(targetSelect.value) }, t('lessons.readTimetable')),
        h('button.btn.btn-sm', {
          type: 'button',
          onClick: async () => {
            if (!await confirmDialog(t('lessons.restore'),
              t('lessons.restoreConfirmMessage', { date: dateInput.value }),
              t('lessons.restoreConfirm'), true)) {
              return;
            }

            await dispatch('/admin/lessons/swap', { swap: { restore: true, date: dateInput.value } },
              t('lessons.restoreSent'), { deviceIds: targetsOf(targetSelect) });
          },
        }, t('lessons.restore')),
      ),
    ),
    h('div.form-row',
      field(t('lessons.targetRoom'), targetSelect, t('lessons.targetRoomHint')),
      field(t('lessons.date'), dateInput, t('lessons.dateHint')),
    ),
    h('div.form-row',
      field(t('lessons.period'), slotSelect, t('lessons.periodHint')),
      field(t('lessons.mode'), modeSelect, t('lessons.modeHint')),
    ),
    swapField,
    subjectField,
    h('div.card-actions',
      h('button.btn.btn-primary.btn-sm', {
        type: 'button',
        onClick: async () => {
          const body = {
            date: dateInput.value,
            classIndex: Number(slotSelect.value) || 0,
            swap: modeSelect.value === 'swap',
            swapWithIndex: Number(swapWithSelect.value) || -1,
            subjectId: subjectSelect.value,
            subjectName: subjectSelect.options[subjectSelect.selectedIndex]?.textContent || '',
          };

          await dispatch('/admin/lessons/swap', { swap: body }, t('lessons.changeSent'),
            { deviceIds: targetsOf(targetSelect) });
        },
      }, t('lessons.sendChange')),
    ),
  );

  swapFill = fillOptions;
  return card;
}

/** 调休：给某一天指定另一张课表（等价于在 ClassIsland 里手动把那天绑到别的课表）。 */
function renderAdjustCard() {
  const dateInput = h('input', { type: 'date', value: todayText() });
  const planSelect = h('select');
  // 调休也要选目标：它的语义是「给这些教室的那一天绑一张课表」。
  // 调休常是整栋楼一起（那就选「全部设备」），但默认仍落到当前这一台，
  // 免得「给某个班调一天」顺手把全校都改了。
  const targetSelect = targetField(async (value) => {
    if (value !== ALL_TARGETS) {
      await loadCatalogFor(value);
    }
  });
  planSelect.appendChild(h('option', { value: '' }, t('lessons.readFirstAbove')));

  const card = h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', t('lessons.makeupCard')),
        h('p.card-desc', t('lessons.makeupDesc1')
          + t('lessons.makeupDesc2')),
      ),
      h('div.card-actions',
        h('button.btn.btn-sm', {
          type: 'button',
          onClick: async () => {
            if (!await confirmDialog(t('lessons.cancelMakeup'),
              t('lessons.cancelMakeupMessage', { date: dateInput.value }),
              t('lessons.cancelConfirm'), true)) {
              return;
            }

            await dispatch('/admin/lessons/adjust', { adjust: { date: dateInput.value, restore: true } },
              t('lessons.makeupCancelled'), { deviceIds: targetsOf(targetSelect) });
          },
        }, t('lessons.cancelMakeup')),
      ),
    ),
    h('div.form-row',
      field(t('lessons.targetRoom'), targetSelect, t('lessons.makeupTargetHint')),
      field(t('lessons.whichDay'), dateInput, t('lessons.whichDayHint')),
    ),
    h('div.form-row',
      field(t('lessons.whichTimetable'), planSelect, t('lessons.whichTimetableHint')),
    ),
    h('div.card-actions',
      h('button.btn.btn-primary.btn-sm', {
        type: 'button',
        onClick: async () => {
          await dispatch('/admin/lessons/adjust', {
            adjust: {
              date: dateInput.value,
              classPlanId: planSelect.value,
              classPlanName: planSelect.options[planSelect.selectedIndex]?.textContent || '',
            },
          }, t('lessons.makeupSent'), { deviceIds: targetsOf(targetSelect) });
        },
      }, t('lessons.sendMakeup')),
    ),
  );

  adjustFill = (catalog) => {
    clear(planSelect);
    if (!catalog || catalog.classPlans.length === 0) {
      planSelect.appendChild(h('option', { value: '' }, t('lessons.noTimetables')));
      return;
    }

    for (const plan of catalog.classPlans) {
      if (plan.isOverlay) {
        continue; // 临时层是换课的副产物，不该出现在「按哪张课表上」里
      }

      planSelect.appendChild(h('option', { value: plan.id },
        plan.name + (plan.isEnabled ? '' : t('lessons.disabledByDefault'))));
    }
  };

  return card;
}

// ────────────────────────────── 数据与下发 ──────────────────────────────

/** 当前用于取课表目录的设备：优先在线的那台（离线设备读了也没意义）。 */
function catalogDeviceId() {
  return live?.devices.find((d) => d.isOnline)?.deviceId
    || live?.devices[0]?.deviceId
    || '';
}

/**
 * 读取课表目录并刷新两个下拉框。
 *
 * @param {string} [deviceId] 指定读哪台；省略则用「优先在线的那台」
 *   （页面刚打开时用户还没选过目标）。
 */
async function loadCatalogFor(deviceId) {
  const target = deviceId || catalogDeviceId();
  if (!target) {
    return;
  }

  const catalog = await api(`/admin/lessons/catalog?deviceId=${encodeURIComponent(target)}`).catch(() => null);
  if (catalog) {
    applyCatalog(catalog);
  }
}

async function readCatalog(deviceId) {
  // 「全部设备」没有单一份课表可读（各班的课表本来就不同），退回优先在线的那台。
  const target = deviceId && deviceId !== ALL_TARGETS ? deviceId : catalogDeviceId();
  if (!target) {
    toast('warn', t('lessons.noReadableDevices'), t('lessons.noReadableDevicesHint'));
    return;
  }

  // 「读取课表」是**指令**：教室端读完主动回报，所以要下发之后等一会儿再来取结果。
  await dispatch('/admin/lessons/catalog', { deviceIds: [target] }, t('lessons.reportRequested'), { silent: true });

  for (let i = 0; i < 12; i += 1) {
    await new Promise((r) => setTimeout(r, 1000));
    const catalog = await api(`/admin/lessons/catalog?deviceId=${encodeURIComponent(target)}`)
      .catch(() => null);
    if (catalog) {
      applyCatalog(catalog);
      toast('ok', t('lessons.timetableRead'), t('lessons.catalogSummary', { periods: catalog.slots.length, subjects: catalog.subjects.length }));
      return;
    }
  }

  toast('warn', t('lessons.notReceived'), t('lessons.notReceivedHint'));
}

/** 把目录填进两个表单（换课与调休的下拉框由各自的卡片负责填充）。 */
function applyCatalog(catalog) {
  swapFill?.(catalog);
  adjustFill?.(catalog);
  live.catalog = catalog;
}

/**
 * 下发一条课表指令。
 *
 * <para>`options.deviceIds` 是**必填**：服务端在请求里没带目标时默认发给**全部设备**
 * （见 LessonEndpoints.ResolveTargetsAsync 的「两边都没给就取全部」分支），
 * 界面上一个目标都不给，等于「全校一起改」——而这一页的定位是「改某个班这一节」。</para>
 */
async function dispatch(path, body, okTitle, options = {}) {
  const payload = { deviceIds: options.deviceIds ?? [], ...body };
  if (payload.deviceIds.length === 0) {
    toast('warn', t('lessons.pickTarget'), t('lessons.pickTargetHint'));
    return;
  }

  try {
    const affected = await api(path, { method: 'POST', body: payload });
    if (!options.silent) {
      toast('ok', okTitle, t('lessons.sentTo', { count: affected }));
    }

    await refreshLive();
  } catch (err) {
    toast('error', t('lessons.sendFailed'), err.message || t('lessons.retryLater'));
  }
}

// ────────────────────────────── 小工具 ──────────────────────────────

function todayText() {
  const now = new Date();
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
}

/** 距上报过了多少秒（心跳之间的倒计时要自己走）。 */
function elapsedSeconds(reportedAt) {
  const at = Date.parse(reportedAt);
  return Number.isNaN(at) ? 0 : Math.max(0, Math.floor((Date.now() - at) / 1000));
}

function formatLeft(seconds) {
  const s = Math.max(0, Math.floor(seconds));
  if (s >= 3600) {
    return `${Math.floor(s / 3600)}:${String(Math.floor((s % 3600) / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`;
  }

  return `${String(Math.floor(s / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`;
}

function stateLabel(lesson) {
  switch (lesson.state) {
    case 'onClass': return t('lessons.stateInClass');
    case 'prepare': return t('lessons.statePreparing');
    case 'breaking': return t('lessons.stateBreak');
    case 'afterSchool': return t('lessons.stateFinished');
    default: return t('lessons.stateNotStarted');
  }
}
