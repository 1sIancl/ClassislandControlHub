/**
 * 大屏看板（路线图 #113）：给值班室 / 教务办公室那一块大屏用的**一屏总览**。
 *
 * 与仪表盘的分工：仪表盘是「我自己用的操作台」，看板是「挂在墙上给人看的」——
 * 所以它只放大字、不做操作入口（避免值守的人误点），并且：
 *   · 每 30 秒自动刷新（大屏没人会去点刷新）；
 *   · 带「看板模式」：隐藏侧栏与顶栏，整屏都留给数据；
 *   · 页面切走自动停表（本仓的视图没有 unmount 钩子，靠 `container.isConnected` 自清）。
 */
import { api, hasPermission } from '../core/api.js?v=150';
import { toastError } from '../core/errors.js?v=150';
import { h, clear, formatDateTime } from '../core/ui.js?v=150';
import { t } from '../core/i18n.js?v=150';

export const meta = {
  title: t('board.title'),
  subtitle: t('board.subtitle'),
};

const REFRESH_MS = 30000;
let refreshTimer = null;
let clockTimer = null;

/** 载入看板需要的全部数据（并发；某个接口没权限就少一块，不影响其它）。 */
async function loadData() {
  const [info, devices, lessons, audit, weather] = await Promise.all([
    api('/server/info').catch(() => null),
    api('/admin/devices').catch(() => []),
    api('/admin/lessons/live').catch(() => null),
    hasPermission('audit.read') ? api('/admin/audit?limit=8').catch(() => null) : Promise.resolve(null),
    // 天气（新功能）：没启用 / 没数据 / 没权限时一律当作没有，大屏上不显示——绝不能因此让整页空掉。
    api('/admin/weather').catch(() => null),
  ]);

  return {
    info,
    devices: devices || [],
    lessons: lessons?.devices || [],
    audit: Array.isArray(audit) ? audit : (audit?.items || []),
    weather: weather?.available ? weather : null,
    at: new Date(),
  };
}

export async function render(container) {
  clear(container);
  stopTimers();
  container.appendChild(h('div.board-loading', t('board.loading')));

  let data;
  try {
    data = await loadData();
  } catch (e) {
    clear(container);
    container.appendChild(h('div.notice.notice-warn', h('span.notice-icon', '!'),
      h('div', t('board.loadFailed', { message: e.message }))));
    return;
  }

  const host = h('div.board');
  clear(container);
  container.appendChild(host);

  /** 重新取数并重画（「立即刷新」按钮与自动刷新都用它）。 */
  const reload = async () => {
    data = await loadData();
    paint();
  };

  const paint = () => {
    clear(host);
    host.appendChild(renderHeader(data, reload));
    host.appendChild(renderKpis(data));
    host.appendChild(h('div.board-columns',
      renderAttention(data),
      renderLessons(data)));
    host.appendChild(renderEvents(data));
  };

  paint();

  // 大时钟：只更新那一个节点，不整页重绘（否则每秒钟重建整棵 DOM 会闪）。
  clockTimer = setInterval(() => {
    if (!container.isConnected) {
      stopTimers();
      return;
    }

    const node = host.querySelector('[data-clock]');
    if (node) {
      node.textContent = new Date().toLocaleTimeString('zh-CN', { hour12: false });
    }
  }, 1000);

  refreshTimer = setInterval(async () => {
    if (!container.isConnected) {
      stopTimers();
      return;
    }

    try {
      data = await loadData();
      paint();
    } catch {
      /* 自动刷新失败就保持上一屏：大屏上跳一个错误框比数据旧一会儿更难看 */
    }
  }, REFRESH_MS);
}

function stopTimers() {
  if (refreshTimer) {
    clearInterval(refreshTimer);
    refreshTimer = null;
  }

  if (clockTimer) {
    clearInterval(clockTimer);
    clockTimer = null;
  }
}

function renderHeader(data, reload) {
  return h('div.board-head',
    h('div.board-brand',
      h('span.board-server', data.info?.serverName || t('board.serverFallback')),
      h('span.board-version', t('board.uptime', {
        version: data.info?.version || '—',
        uptime: formatUptime(data.info?.uptimeSeconds),
      }))),
    // 天气贴在时钟左边：值班的人最常看的就是「几点」和「今天什么天」。
    data.weather
      ? h('div.board-weather', { title: t('board.weatherTitle') },
        h('span.board-weather-text', data.weather.summary || ''))
      : null,
    h('div.board-clock', h('span', { 'data-clock': '' }, new Date().toLocaleTimeString('zh-CN', { hour12: false }))),
    h('div.board-actions',
      h('span.board-refresh', t('board.refreshHint', { seconds: REFRESH_MS / 1000, at: formatDateTime(data.at) })),
      // ⚠️ 刷新必须回调到 render 里的那个 `data`：在被调函数里给**同名形参**赋值
      //    不会影响外层的 `data`，表现是「点了立即刷新，画面还是旧的」。
      h('button.btn.btn-sm', {
        type: 'button',
        onClick: async () => {
          try {
            await reload();
          } catch (e) {
            toastError(e, t('board.refreshFailed'));
          }
        },
      }, t('board.refreshNow')),
      h('button.btn.btn-sm', {
        type: 'button',
        onClick: (event) => {
          const on = document.body.classList.toggle('board-full');
          event.target.textContent = on ? t('board.exitFullMode') : t('board.fullMode');
        },
      }, t('board.fullMode'))));
}

function renderKpis(data) {
  const total = data.info?.deviceCount ?? data.devices.length;
  const online = data.info?.onlineDeviceCount ?? data.devices.filter((d) => d.online).length;
  const pending = data.info?.pendingDeviceCount ?? 0;
  const offline = Math.max(0, total - online);

  const tile = (label, value, tone) => h('div', { class: `board-tile tone-${tone}` },
    h('div.board-tile-value', String(value)),
    h('div.board-tile-label', label));

  return h('div.board-kpis',
    tile(t('board.total'), total, 'plain'),
    tile(t('board.online'), online, 'ok'),
    tile(t('board.offline'), offline, offline > 0 ? 'warn' : 'ok'),
    tile(t('board.pending'), pending, pending > 0 ? 'warn' : 'ok'));
}

function renderAttention(data) {
  const rows = data.devices
    .filter((d) => !d.online || d.revoked)
    .sort((a, b) => String(a.name).localeCompare(String(b.name), 'zh-CN'))
    .slice(0, 12);

  return h('section.board-panel',
    h('h3.board-panel-title', t('board.attention', { count: rows.length })),
    rows.length === 0
      ? h('div.board-empty', t('board.allOnline'))
      : h('ul.board-list', ...rows.map((d) => h('li',
        h('span.board-name', d.revoked ? t('board.revoked', { name: d.name }) : d.name),
        h('span.board-meta', d.offlineReason || d.lastError
          || (d.lastSeenAt ? t('board.lastSeen', { at: formatDateTime(d.lastSeenAt) }) : t('board.neverOnline'))),
      ))));
}

function renderLessons(data) {
  const rows = [...data.lessons]
    .sort((a, b) => String(a.deviceName || a.deviceId).localeCompare(String(b.deviceName || b.deviceId), 'zh-CN'))
    .slice(0, 12);

  const describe = (lesson) => {
    if (!lesson) return t('board.noLessonState');
    const parts = [];
    if (lesson.currentSubject) parts.push(t('board.inClass', { subject: lesson.currentSubject }));
    if (lesson.nextSubject) {
      parts.push(t('board.nextClass', {
        subject: lesson.nextSubject,
        time: lesson.nextStartAt ? String(lesson.nextStartAt).slice(11, 16) : '',
      }).trim());
    }

    if (parts.length === 0) parts.push(lesson.classPlanName || t('board.breakTime'));
    if (lesson.rotationWeeks >= 2 && lesson.rotationWeek >= 1) {
      parts.push(t('board.weekOf', { week: lesson.rotationWeek, weeks: lesson.rotationWeeks }));
    }

    return parts.join(' · ');
  };

  return h('section.board-panel',
    h('h3.board-panel-title', t('board.lessonsPanel', { count: data.lessons.length })),
    rows.length === 0
      ? h('div.board-empty', t('board.noLessons'))
      : h('ul.board-list', ...rows.map((l) => h('li',
        h('span.board-name', l.deviceName || l.deviceId),
        h('span.board-meta', describe(l)),
      ))));
}

function renderEvents(data) {
  if (!data.audit || data.audit.length === 0) {
    return h('section.board-panel',
      h('h3.board-panel-title', t('board.events')),
      h('div.board-empty', hasPermission('audit.read') ? t('board.noEvents') : t('board.noAuditPermission')));
  }

  return h('section.board-panel',
    h('h3.board-panel-title', t('board.events')),
    h('ul.board-list', ...data.audit.slice(0, 8).map((a) => h('li',
      h('span.board-name', a.action || ''),
      h('span.board-meta', `${formatDateTime(a.createdAt)} · ${a.username || ''} · ${truncate(a.detail || a.target || '', 60)}`),
    ))));
}

function formatUptime(seconds) {
  if (!seconds && seconds !== 0) return '—';
  const days = Math.floor(seconds / 86400);
  const hours = Math.floor((seconds % 86400) / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  // 复用全站统一的时长文案（`ui.duration*`），别在这儿再维护一份中文与英文。
  return days > 0
    ? t('ui.durationDays', { days, hours })
    : (hours > 0
      ? t('ui.durationHours', { hours, minutes })
      : t('ui.durationMinutes', { minutes, seconds: 0 }));
}

function truncate(text, max) {
  const value = String(text || '');
  return value.length <= max ? value : `${value.slice(0, max)}…`;
}
