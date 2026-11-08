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

import { api } from '../core/api.js?v=172';
import { h, clear } from '../core/ui.js?v=172';
import { t } from '../core/i18n.js?v=172';

export const meta = {
  title: t('classBoard.title'),
  subtitle: t('classBoard.subtitle'),
};

/** 每 60 秒重取一次数据：课表与公告不该因为页面开着就不更新。 */
const REFRESH_MS = 60000;

let clockTimer = null;
let refreshTimer = null;

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
  const [profile, weather] = await Promise.all([
    api(`/admin/profiles/${profileId}`).catch(() => null),
    api('/admin/weather').catch(() => null),
  ]);

  return {
    profile,
    weather: weather?.available ? weather : null,
    at: new Date(),
  };
}

export async function render(container, params) {
  clear(container);
  stopTimers();

  // 没传档案 ID 就用默认档案：看板通常是"这间教室那台设备用的档案"。
  let profileId = params.profileId;
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

  const paint = () => {
    clear(host);

    // 逐个判空后再 append：这块是**直接** appendChild，不经过 h() 的子节点过滤，
    // 没有公告时 renderMarquee 返回 null，appendChild(null) 会直接抛
    // "parameter 1 is not of type 'Node'"，整页白屏。
    const blocks = [renderMarquee(data), renderTop(data), renderTick(data), renderMain(data)];
    for (const block of blocks) {
      if (block) {
        host.appendChild(block);
      }
    }
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
}

/** 顶部：日期、星期、档案名。 */
function renderTop(data) {
  const now = data.at;
  const name = data.profile?.name ?? '';

  return h('div.classboard-top',
    h('div.classboard-date',
      h('span.classboard-ymd', t('classBoard.date', {
        year: now.getFullYear(),
        month: now.getMonth() + 1,
        day: now.getDate(),
      })),
      h('span.classboard-week', WEEK_DAYS[now.getDay()])),
    name ? h('div.classboard-profile', name) : null);
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
  const raw = data.profile?.content?.settings?.announcement;
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
    return {
      index,
      start: point.startTime,
      end: point.endTime,
      startMin: minutesOf(point.startTime),
      endMin: minutesOf(point.endTime),
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

  const current = lessons.find((l) => nowMin >= l.startMin && nowMin < l.endMin);
  if (current) {
    return { kind: 'in-class', current, lessons };
  }

  const next = lessons.find((l) => l.startMin > nowMin);
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
    const left = Math.max(0, Math.ceil(state.current.endMin - nowMin));
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

/** 主体：当前节 + 今日课表。 */
function renderMain(data) {
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
    renderSide(data));
}

/** 右栏：天气与公告。没有数据就整块不渲染，不留空框。 */
function renderSide(data) {
  const weather = data.weather;
  const notice = data.profile?.content?.settings?.announcement;

  if (!weather && !notice) {
    return null;
  }

  return h('div.classboard-side',
    weather
      ? h('div.classboard-card',
        h('div.classboard-card-title', t('classBoard.weather')),
        // 用服务端拼好的 summary：天气代码到文案的映射服务端已有一份，
        // 前端再猜一遍会长出第二套说法（同一个 4° 两处显示不一样）。
        h('div.classboard-weather-temp',
          `${Math.round(weather.temperature ?? 0)}${weather.temperatureText ?? '°C'}`),
        h('div.classboard-weather-text', weather.summary ?? ''),
        h('div.classboard-weather-range', weather.source ?? ''))
      : null,
    notice
      ? h('div.classboard-card',
        h('div.classboard-card-title', t('classBoard.notice')),
        h('div.classboard-notice', notice))
      : null);
}
