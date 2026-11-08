/**
 * 班级看板（#141）：挂在教室大屏上，学生抬头就能看到。
 *
 * <p>
 * 与「大屏看板」（board.js）的分工：那个给值班室看设备与异常，这个只回答教室里
 * 最常被问的三件事——**现在几点、这节上什么、还剩多久**。
 * 所以它：
 * </p>
 *
 * <ul>
 *   <li>整屏铺满、盖掉侧栏与顶栏，没有可点的操作入口（学生碰不到设置）；</li>
 *   <li>字要大、对比要高，隔三排座位也能看清；</li>
 *   <li>每秒只更新时钟那一个节点，不整页重绘——否则大屏上会一直闪；</li>
 *   <li>页面切走自己停表（本仓视图没有 unmount 钩子，靠 <c>container.isConnected</c> 自清）。</li>
 * </ul>
 */

import { api } from '../core/api.js?v=186';
import { h, clear } from '../core/ui.js?v=186';
import { t } from '../core/i18n.js?v=186';

export const meta = {
  title: t('classBoard.title'),
  subtitle: t('classBoard.subtitle'),
};

/** 每 60 秒重取一次数据：课表与公告不该因为页面开着就不更新。 */
const REFRESH_MS = 60000;

/**
 * 外观配置存在**本机**（localStorage），不进档案。
 *
 * <p>
 * 理由：这是"这间教室那块屏"的偏好（亮不亮、字多大、要不要显示天气），
 * 不是全班共用的教学配置。放进档案会让「改个配色」变成一次下发，
 * 而且别的班跟着被改。老师在那台机器上设一次就够。
 * </p>
 */
const STYLE_KEY = 'islandhub.classboard.style';

/** 可选主色。都按深色底调过对比度，不像纯色那样刺眼。 */
const ACCENTS = {
  blue: '#4f8cff',
  teal: '#2ec4b6',
  amber: '#f2a03d',
  violet: '#8b7bff',
};

const CLOCK_SIZES = { large: '1', medium: '0.74', small: '0.56' };

const DEFAULT_STYLE = {
  theme: 'dark',
  accent: 'blue',
  clock: 'large',
  weather: true,
  notice: true,
  marquee: true,
};

/** 每个配置项允许的取值。写入前不校验，读到非法值就会让样式整体失效。 */
const STYLE_VALUES = {
  theme: ['dark', 'light'],
  accent: Object.keys(ACCENTS),
  clock: Object.keys(CLOCK_SIZES),
};

function loadStyle() {
  let merged = { ...DEFAULT_STYLE };

  try {
    const raw = window.localStorage.getItem(STYLE_KEY);
    if (raw) {
      merged = { ...DEFAULT_STYLE, ...JSON.parse(raw) };
    }
  } catch {
    // 存储被禁用 / 内容损坏：退回默认外观，不要让看板打不开。
    return { ...DEFAULT_STYLE };
  }

  // 白名单校验：本地存过非法值（本轮就把 theme 写成过中文「浅色」），
  // 不校验的话 className 会变成 theme-浅色、字号变量查不到，**样式整体失效且看不出原因**。
  for (const [key, allowed] of Object.entries(STYLE_VALUES)) {
    if (!allowed.includes(merged[key])) {
      merged[key] = DEFAULT_STYLE[key];
    }
  }

  // 开关只认布尔；其它类型（字符串 "false" 之类）一律按开处理。
  for (const key of ['weather', 'notice', 'marquee']) {
    merged[key] = merged[key] !== false;
  }

  return merged;
}

function saveStyle(style) {
  try {
    window.localStorage.setItem(STYLE_KEY, JSON.stringify(style));
  } catch {
    /* 存不进去只影响"下次还记得"，不影响本次显示 */
  }
}

/** 轮播间隔：比刷新慢，免得屏幕一直在跳，学生看不清。 */
const ROTATE_MS = 30000;

let clockTimer = null;
let refreshTimer = null;
let rotateTimer = null;

const WEEK_DAYS = [
  t('profiles.editor.wdSun'),
  t('profiles.editor.wdMon'),
  t('profiles.editor.wdTue'),
  t('profiles.editor.wdWed'),
  t('profiles.editor.wdThu'),
  t('profiles.editor.wdFri'),
  t('profiles.editor.wdSat'),
];

/** `08:05:00` 或 `08:05` → 当天分钟数。 */
function minutesOf(text) {
  const [hour, minute] = String(text ?? '').split(':').map((x) => Number(x));
  return Number.isFinite(hour) && Number.isFinite(minute) ? hour * 60 + minute : null;
}

function pad(num) {
  return String(num).padStart(2, '0');
}

async function loadBoard(profileId) {
  const [profile, weather, profiles] = await Promise.all([
    api(`/admin/profiles/${profileId}`).catch(() => null),
    api('/admin/weather').catch(() => null),
    // 档案列表用于右上角切换：老师换班不需要去改 URL。
    api('/admin/profiles').catch(() => []),
  ]);

  return {
    profile,
    profiles: Array.isArray(profiles) ? profiles : [],
    weather: weather?.available ? weather : null,
    at: new Date(),
  };
}

export async function render(container, params) {
  clear(container);
  stopTimers();

  // 档案 ID 支持逗号分隔的多个值：`#/classboard/a,b,c`
  // —— 一间教室挂多个班、或值班室轮巡几间教室时，屏幕自己轮着放，
  // 不用开多个标签页各占一块屏。
  const routeIds = String(params?.profileId ?? '')
    .split(',')
    .map((id) => id.trim())
    .filter(Boolean);

  let profileId = routeIds[0];
  if (!profileId) {
    const profiles = await api('/admin/profiles').catch(() => []);
    profileId = profiles.find((p) => p.isDefault)?.id ?? profiles[0]?.id;
  }

  if (!profileId) {
    container.appendChild(h('div.notice.notice-warn', h('div', t('classBoard.noProfile'))));
    return;
  }

  let data;
  try {
    data = await loadBoard(profileId);
  } catch (error) {
    container.appendChild(h('div.notice.notice-warn', h('div', error.message)));
    return;
  }

  const host = h('div.classboard');
  container.appendChild(host);

  // 外观配置：本机偏好，改了立刻重画，不用刷新也不用下发。
  const style = loadStyle();

  // 轮播游标必须在 paint 之前声明：paint 会读它，
  // 声明在下面会踩 TDZ（Cannot access before initialization）。
  let rotateIndex = 0;

  const applyStyle = () => {
    host.className = `classboard theme-${style.theme} clock-${style.clock}`;
    host.style.setProperty('--cb-accent', ACCENTS[style.accent] ?? ACCENTS.blue);
    host.style.setProperty('--cb-clock-scale', CLOCK_SIZES[style.clock] ?? CLOCK_SIZES.large);
  };

  const paint = () => {
    clear(host);
    applyStyle();

    // 把轮播位置挂在数据上再渲染：renderTop 只管画，不关心轮播状态存在哪。
    data.rotateIndex = rotateIndex;
    data.rotateTotal = routeIds.length;

    // 逐个判空后再 append：这块是**直接** appendChild，不经过 h() 的子节点过滤，
    // 没有公告时 renderMarquee 返回 null，appendChild(null) 会直接抛
    // "parameter 1 is not of type 'Node'"，整页白屏。
    const blocks = [
      renderUrgent(data),
      style.marquee ? renderMarquee(data) : null,
      renderTop(data),
      renderTick(data),
      renderMain(data, style),
    ];
    for (const block of blocks) {
      if (block) {
        host.appendChild(block);
      }
    }

    host.appendChild(renderConfigButton(data, style, () => paint()));
  };

  paint();

  // 心跳：只动时钟与倒计时两个节点，整页保持不动。
  clockTimer = setInterval(() => {
    if (!container.isConnected) {
      stopTimers();
      return;
    }

    const clock = host.querySelector('[data-cb-clock]');
    if (clock) {
      const now = new Date();
      clock.textContent = `${pad(now.getHours())}:${pad(now.getMinutes())}:${pad(now.getSeconds())}`;
    }

    const remain = host.querySelector('[data-cb-remain]');
    if (remain) {
      const text = remainText(data.profile);
      remain.textContent = text;
    }
  }, 1000);

  // 轮播（仅当 URL 里给了多个档案）：每 ROTATE_MS 换下一个。
  // 与 refreshTimer 分开，因为它们的职责不同——刷新是"同一份数据更新"，
  // 轮播是"换一份数据"，节奏通常也不一样（刷新要频繁，轮播不要抢戏）。
  if (routeIds.length > 1) {
    rotateTimer = setInterval(async () => {
      if (!container.isConnected) {
        stopTimers();
        return;
      }

      rotateIndex = (rotateIndex + 1) % routeIds.length;
      profileId = routeIds[rotateIndex];

      try {
        data = await loadBoard(profileId);
        paint();
      } catch {
        /* 单个档案取失败就停在这一屏，下一轮再试，不黑屏 */
      }
    }, ROTATE_MS);
  }

  refreshTimer = setInterval(async () => {
    if (!container.isConnected) {
      stopTimers();
      return;
    }

    try {
      data = await loadBoard(profileId);
      paint();
    } catch {
      /* 自动刷新失败保持上一屏：大屏上跳错误框比数据旧一会儿更难看 */
    }
  }, REFRESH_MS);

  // 点击右上角留一个极小的退出入口（老师偶尔需要回管理台，学生不会注意）。
  host.appendChild(h('a.classboard-exit', { href: '#/profiles' }, t('classBoard.exit')));
}

function stopTimers() {
  if (clockTimer) {
    clearInterval(clockTimer);
    clockTimer = null;
  }

  if (refreshTimer) {
    clearInterval(refreshTimer);
    refreshTimer = null;
  }

  if (rotateTimer) {
    clearInterval(rotateTimer);
    rotateTimer = null;
  }
}

/** 顶部：日期、星期、档案名（轮播时带「第几张 / 共几张」）。 */
function renderTop(data) {
  const now = data.at;
  const name = data.profile?.name ?? '';

  // 轮播时必须看得出"还会换"，否则老师会以为屏幕串班了。
  const badge = data.rotateTotal > 1
    ? ` · ${data.rotateIndex + 1}/${data.rotateTotal}`
    : '';

  return h('div.classboard-top',
    h('div.classboard-date',
      h('span.classboard-ymd', t('classBoard.date', {
        year: now.getFullYear(),
        month: now.getMonth() + 1,
        day: now.getDate(),
      })),
      h('span.classboard-week', WEEK_DAYS[now.getDay()])),
    name ? h('div.classboard-profile', `${name}${badge}`) : null);
}

/**
 * 紧急插播：盖满整屏 10 秒，然后自己退场。
 *
 * <p>
 * 触发条件是公告**以「【紧急】」开头**——不另开字段，因为老师发布紧急通知时
 * 本来就会这样起头，多一个开关反而要多学一次。
 * </p>
 *
 * <p>
 * 必须限频：看板每 60 秒刷新一次，不限频就会每分钟弹一次，把上课内容挡掉。
 * 用 sessionStorage 记「上次弹的时间」，一小时内最多一次；刷新页面重置会话，
 * 所以关掉再开还能再看到。
 * </p>
 */
function renderUrgent(data) {
  const raw = String(data.profile?.content?.settings?.announcement ?? '').trim();
  if (!raw.startsWith('【紧急】')) {
    return null;
  }

  const key = 'islandhub.classboard.urgentAt';
  const last = Number(window.sessionStorage.getItem(key) ?? 0);
  if (Number.isFinite(last) && Date.now() - last < 60 * 60 * 1000) {
    return null;
  }

  window.sessionStorage.setItem(key, String(Date.now()));

  const text = raw.replace(/^【紧急】\s*/, '');
  const overlay = h('div.classboard-urgent',
    h('div.classboard-urgent-label', t('classBoard.urgent')),
    h('div.classboard-urgent-text', text));

  // 10 秒后自己消失：大屏上不该有"需要人点掉"的东西。
  setTimeout(() => overlay.remove(), 10000);
  return overlay;
}

/**
 * 顶部滚动条：警示类信息（预警、临时通知）。
 *
 * <p>
 * 与右栏「公告」卡片分工：卡片适合**停下来读**，滚动条适合**必须被看见**——
 * 一条来不及看的通知，放在角落等于没发。所以这里只放一句、一直滚，
 * 且刻意不提供暂停按钮（学生用不上，老师要停有退出入口）。
 * </p>
 *
 * <p>
 * 太短的文本不滚：十几个字滚起来比静止更难看，也读不快。
 * </p>
 */
function renderMarquee(data) {
  // 预警优先于普通公告：预警是"必须被看见"里优先级最高的一类。
  //
  // 天气接口目前**不返回** alerts（Open-Meteo 没有预警数据），所以这里
  // 探测到就用、探测不到就退回公告——**不做假数据占位**。
  // 将来接上 NMC / 和风 / 彩云时，只要 DTO 里多一个 alerts 数组，这里零改动生效。
  const alerts = Array.isArray(data.weather?.alerts) ? data.weather.alerts : [];
  const raw = alerts.length > 0
    ? alerts
      .map((alert) => [alert?.title, alert?.text].filter(Boolean).join('：'))
      .filter(Boolean)
      .join('　　')
    : data.profile?.content?.settings?.announcement;

  if (!raw) {
    return null;
  }

  const line = String(raw).replace(/\s+/g, ' ').trim();
  if (line.length < 16) {
    return null;
  }

  // 内容复制一份接在后面：滚动到末尾时不会出现一段空白再接第一批字。
  return h('div.classboard-marquee',
    h('div.classboard-marquee-inner',
      h('span', line),
      h('span.classboard-marquee-gap', line)));
}

/** 中央：大时钟。 */
function renderTick(data) {
  const now = new Date();

  return h('div.classboard-tick',
    h('div.classboard-clock', { dataset: { cbClock: '' } },
      `${pad(now.getHours())}:${pad(now.getMinutes())}:${pad(now.getSeconds())}`),
    h('div.classboard-remain', { dataset: { cbRemain: '' } }, remainText(data.profile)));
}

/** 某一天要上的课：按星期挑课表，取上课节次。 */
function lessonsOfDay(profile, day) {
  if (!profile?.content) {
    return [];
  }

  const plans = profile.content.classPlans ?? [];
  const plan = plans.find((p) => (p.daysOfWeek ?? []).includes(day) && p.isEnabled !== false)
    ?? plans.find((p) => (p.daysOfWeek ?? []).includes(day));

  if (!plan) {
    return [];
  }

  const layout = (profile.content.timeLayouts ?? []).find((l) => l.id === plan.timeLayoutId);
  if (!layout) {
    return [];
  }

  const subjects = new Map((profile.content.subjects ?? []).map((s) => [s.id, s]));
  const classPoints = (layout.items ?? []).filter((i) => i.kind === 'class');

  return classPoints.map((point, index) => {
    const slot = (plan.slots ?? []).find((s) => s.index === index);
    const subject = slot?.subjectId ? subjects.get(slot.subjectId) : null;

    const startMin = minutesOf(point.startTime);
    let endMin = minutesOf(point.endTime);

    // 跨零点（如 23:50–00:20）：结束时间在数字上小于开始时间，其实落在第二天。
    // 不做 +24h 修正，这段会被算成负时长，且凌晨那半小时永远匹配不到「上课中」——
    // 寄宿学校 / 晚自习场景会稳定复现。
    const crossesMidnight = startMin !== null && endMin !== null && endMin <= startMin;
    if (crossesMidnight) {
      endMin += 24 * 60;
    }

    return {
      index,
      start: point.startTime,
      end: point.endTime,
      startMin,
      endMin,
      crossesMidnight,
      subject: subject?.name ?? '',
      teacher: subject?.teacherName ?? '',
    };
  });
}

/**
 * 今天（或下一个有课的日子）的课表。
 *
 * <p>
 * 周末 / 节假日当天没课时，往前找最近一个**有课**的星期，最多一周。
 * 看板空着比显示"下一上学日"更没用——学生周末也会看到这块屏。
 * </p>
 */
function todayLessons(profile) {
  const start = new Date().getDay();

  for (let offset = 0; offset < 7; offset += 1) {
    const day = (start + offset) % 7;
    const lessons = lessonsOfDay(profile, day);
    if (lessons.length > 0) {
      return { lessons, offset, day };
    }
  }

  return { lessons: [], offset: 0, day: start };
}

/** 「现在进行到哪一节」：返回当前节、下一节、以及两者之间的状态。 */
function currentState(profile) {
  const { lessons, offset, day } = todayLessons(profile);
  if (lessons.length === 0) {
    return { kind: 'empty', lessons };
  }

  // 不是今天：直接按「还没开始」处理，并在标题上说明是哪天。
  if (offset > 0) {
    return { kind: 'other-day', next: lessons[0], lessons, offset, day };
  }

  const now = new Date();
  const nowMin = now.getHours() * 60 + now.getMinutes() + now.getSeconds() / 60;

  // 跨零点的段：凌晨 00:10 要能与前一天 23:50 开始的那节对上，
  // 所以判断时把「现在」临时补一天再比较。
  const inRange = (lesson) => {
    const probe = lesson.crossesMidnight && nowMin < lesson.startMin
      ? nowMin + 24 * 60
      : nowMin;

    return lesson.startMin !== null && lesson.endMin !== null
      && probe >= lesson.startMin && probe < lesson.endMin;
  };

  const current = lessons.find(inRange);
  if (current) {
    return { kind: 'in-class', current, lessons };
  }

  const next = lessons.find((l) => l.startMin !== null && l.startMin > nowMin);
  if (next) {
    return { kind: 'before', next, lessons };
  }

  return { kind: 'after', lessons };
}

/** 时钟下面那行：有倒计时就给倒计时，否则给"今天已放学"。 */
function remainText(profile) {
  const state = currentState(profile);
  const now = new Date();
  const nowMin = now.getHours() * 60 + now.getMinutes() + now.getSeconds() / 60;

  if (state.kind === 'in-class') {
    // 跨零点时「现在」也要补一天，否则 00:10 对着 00:20 的结束会算出 1450 分钟。
    const probe = state.current.crossesMidnight && nowMin < state.current.startMin
      ? nowMin + 24 * 60
      : nowMin;

    const left = Math.max(0, Math.ceil(state.current.endMin - probe));
    return t('classBoard.leftMinutes', { minutes: left });
  }

  if (state.kind === 'before') {
    const left = Math.max(0, Math.ceil(state.next.startMin - nowMin));
    return t('classBoard.beforeMinutes', { minutes: left });
  }

  // 非今天：倒计时没有意义，直接说下一个上学日。
  if (state.kind === 'other-day') {
    return t('classBoard.otherDayHint', { day: WEEK_DAYS[state.day] });
  }

  return '';
}

/**
 * 右上角的样式配置：极小、半透明，老师找得到、学生注意不到。
 *
 * <p>
 * 直接改 localStorage 再回调重画，不刷新页面——大屏上刷新一次要黑屏半秒，
 * 调个字号不该有这种动静。
 * </p>
 */
function renderConfigButton(data, style, onChange) {
  const toggle = (key) => {
    style[key] = !style[key];
    saveStyle(style);
    onChange();
  };

  const pick = (key, value) => {
    style[key] = value;
    saveStyle(style);
    onChange();
  };

  // options 传「内部值 + 显示文案」成对，**不要**拿文案反推内部值：
  // 早先那样写，theme 被存成中文「浅色」、clock 存成「小」，
  // 于是 className 变成 theme-浅色、字号变量查不到 → 两个开关都没生效（实测字号没变）。
  const optionRow = (label, key, options) => h('div.classboard-config-row',
    h('span.classboard-config-label', label),
    h('div.classboard-config-values', ...options.map((opt) => h(
      'button.classboard-config-chip' + (style[key] === opt.value ? '.on' : ''),
      { type: 'button', onClick: () => pick(key, opt.value) },
      opt.label,
    ))));

  const panel = h('div.classboard-config', { hidden: true },
    h('div.classboard-config-title', t('classBoard.styleTitle')),

    // 档案切换：换了就直接跳 URL（会重新加载数据），不做局部替换——
    // 课表、公告、轮换周次全都变了，局部刷新反而容易留下上一份的残影。
    data.profiles?.length > 1
      ? h('div.classboard-config-row',
        h('span.classboard-config-label', t('classBoard.switchProfile')),
        h('select.classboard-config-select', {
          onChange: (event) => {
            window.location.hash = `#/classboard/${event.target.value}`;
          },
        }, ...data.profiles.map((item) => h('option', {
          value: item.id,
          selected: item.id === data.profile?.id,
        }, item.name))))
      : null,
    optionRow(t('classBoard.styleTheme'), 'theme', [
      { value: 'dark', label: t('classBoard.styleDark') },
      { value: 'light', label: t('classBoard.styleLight') },
    ]),
    h('div.classboard-config-row',
      h('span.classboard-config-label', t('classBoard.styleAccent')),
      h('div.classboard-config-values', ...Object.keys(ACCENTS).map((key) => h('button.classboard-config-dot', {
        type: 'button',
        title: key,
        style: { background: ACCENTS[key] },
        class: style.accent === key ? 'on' : '',
        onClick: () => pick('accent', key),
      })))),
    optionRow(t('classBoard.styleClock'), 'clock', [
      { value: 'large', label: t('classBoard.styleClockLarge') },
      { value: 'medium', label: t('classBoard.styleClockMedium') },
      { value: 'small', label: t('classBoard.styleClockSmall') },
    ]),
    h('div.classboard-config-row',
      h('div.classboard-config-values', ...[
        ['weather', t('classBoard.styleWeather')],
        ['notice', t('classBoard.styleNotice')],
        ['marquee', t('classBoard.styleMarquee')],
      ].map(([key, label]) => h('button.classboard-config-chip' + (style[key] ? '.on' : ''), {
        type: 'button',
        onClick: () => toggle(key),
      }, label)))));

  const gear = h('button.classboard-gear', {
    type: 'button',
    title: t('classBoard.styleTitle'),
  }, '⚙');

  gear.onclick = () => {
    panel.hidden = !panel.hidden;
  };

  // 面板开着时点空白处收起：大屏上没人会去找关闭按钮。
  panel.addEventListener('click', (event) => {
    if (event.target === panel) {
      panel.hidden = true;
    }
  });

  return h('div.classboard-config-host', gear, panel);
}

/** 主体：当前节 + 今日课表。 */
function renderMain(data, style) {
  const state = currentState(data.profile);

  const headline = (() => {
    if (state.kind === 'in-class') {
      return h('div.classboard-now',
        h('span.classboard-now-label', t('classBoard.inClass')),
        h('span.classboard-now-subject', state.current.subject || '—'),
        state.current.teacher ? h('span.classboard-now-teacher', state.current.teacher) : null);
    }

    if (state.kind === 'before') {
      return h('div.classboard-now',
        h('span.classboard-now-label', t('classBoard.nextClass')),
        h('span.classboard-now-subject', state.next.subject || '—'),
        h('span.classboard-now-time', `${state.next.start.slice(0, 5)}–${state.next.end.slice(0, 5)}`));
    }

    // 今天没课（周末 / 假期）：显示下一个上学日的第一节，看板不至于空着。
    if (state.kind === 'other-day') {
      return h('div.classboard-now',
        h('span.classboard-now-label', t('classBoard.otherDay', { day: WEEK_DAYS[state.day] })),
        h('span.classboard-now-subject', state.next.subject || '—'),
        h('span.classboard-now-time', `${state.next.start.slice(0, 5)}–${state.next.end.slice(0, 5)}`));
    }

    return h('div.classboard-now',
      h('span.classboard-now-label', state.kind === 'empty' ? t('classBoard.noLessonToday') : t('classBoard.finished')));
  })();

  const rows = (state.lessons ?? []).map((lesson, index) => {
    const active = state.current === lesson;
    const passed = state.kind !== 'before' && lesson.endMin <= (state.current?.startMin ?? 1e9) && !active;

    return h('div.classboard-row' + (active ? '.active' : '') + (passed ? '.passed' : ''),
      h('span.classboard-row-no', String(index + 1)),
      h('span.classboard-row-subject', lesson.subject || '—'),
      h('span.classboard-row-time', `${lesson.start.slice(0, 5)}–${lesson.end.slice(0, 5)}`));
  });

  return h('div.classboard-main',
    h('div.classboard-left', headline),
    h('div.classboard-list', ...rows),
    renderSide(data, style));
}

/** 右栏：天气与公告。没有数据就整块不渲染，不留空框。 */
function renderSide(data, style) {
  // 外观配置里关掉的模块直接不渲染，而不是留一个空卡片。
  const weather = style?.weather === false ? null : data.weather;
  const notice = style?.notice === false ? null : data.profile?.content?.settings?.announcement;

  // 预警单独也算"有内容"：天气取不到时，预警更应该显示出来。
  const hasAlerts = Array.isArray(data.weather?.alerts) && data.weather.alerts.length > 0;

  if (!weather && !notice && !hasAlerts) {
    return null;
  }

  // 预警卡片：滚动条只给"一句提醒"，正文要能读全。
  // 标题 + 等级 + 发布时间 + 正文都铺出来，学生/老师不用再去别处查。
  const alerts = Array.isArray(data.weather?.alerts) ? data.weather.alerts : [];

  return h('div.classboard-side',
    alerts.length > 0
      ? h('div.classboard-card.classboard-alert-card',
        h('div.classboard-card-title', t('classBoard.alert')),
        ...alerts.map((alert) => h('div.classboard-alert',
          h('div.classboard-alert-head',
            alert.level ? h('span.classboard-alert-level', alert.level) : null,
            h('span.classboard-alert-title', alert.title || ''),
            alert.issuedAt ? h('span.classboard-alert-time', alert.issuedAt) : null),
          alert.text ? h('div.classboard-alert-text', alert.text) : null)))
      : null,
    weather
      ? h('div.classboard-card',
        h('div.classboard-card-title', t('classBoard.weather')),
        // 用服务端拼好的 summary：天气代码到文案的映射服务端已有一份，
        // 前端再猜一遍会长出第二套说法（同一个 4° 两处显示不一样）。
        // 温度缺失时显示 "--" 而不是 0：
        // 数据异常时给出一个**具体的错误气温**，比承认没有数据更糟。
        h('div.classboard-weather-temp',
          typeof weather.temperature === 'number'
            ? `${Math.round(weather.temperature)}${weather.temperatureText ?? '°C'}`
            : '--'),
        h('div.classboard-weather-text', weather.summary ?? ''),
        h('div.classboard-weather-range', weather.source ?? ''))
      : null,
    notice
      ? h('div.classboard-card',
        h('div.classboard-card-title', t('classBoard.notice')),
        h('div.classboard-notice', notice))
      : null);
}
