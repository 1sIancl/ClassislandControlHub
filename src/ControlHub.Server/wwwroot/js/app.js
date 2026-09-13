/**
 * 应用入口：会话引导、导航渲染与哈希路由。
 */

import { api, session, saveToken, setSessionExpiredHandler, fetchServerInfo } from './core/api.js?v=12';
import { h, clear, toast } from './core/ui.js?v=12';
import {
  initTheme, getTheme, applyTheme, THEMES,
  getSidebarCollapsed, setSidebarCollapsed,
  getDensity, setDensity, applyDensity, DENSITIES,
  applyAppearance, getAccent, setAccent, ACCENTS,
  getFont, setFont, FONTS,
  getRadius, setRadius, RADII,
} from './core/prefs.js?v=12';

// ── 应用启动早期：应用主题 / 外观 / 布局偏好（避免闪烁） ──
initTheme();
applyAppearance();
applyDensity();
setSidebarCollapsed(getSidebarCollapsed());

/** 导航图标（内联 SVG，描边风格，跟随文字颜色）。 */
const ICONS = {
  dashboard: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="7" height="7" rx="1.6"/><rect x="14" y="3" width="7" height="7" rx="1.6"/><rect x="14" y="14" width="7" height="7" rx="1.6"/><rect x="3" y="14" width="7" height="7" rx="1.6"/></svg>',
  profiles: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6"/><path d="M8 13h8M8 17h5"/></svg>',
  devices: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="3" width="20" height="14" rx="2"/><path d="M8 21h8M12 17v4"/></svg>',
  groups: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/></svg>',
  deploy: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M22 2 11 13"/><path d="M22 2 15 22l-4-9-9-4z"/></svg>',
  audit: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M8 6h13M8 12h13M8 18h13"/><path d="M3 6h.01M3 12h.01M3 18h.01"/></svg>',
  settings: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09a1.65 1.65 0 0 0-1-1.51 1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09a1.65 1.65 0 0 0 1.51-1 1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33h.01a1.65 1.65 0 0 0 1-1.51V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51h.01a1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82v.01a1.65 1.65 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"/></svg>',
};

/** 导航结构。新增页面时只需在此登记。 */
const NAV = [
  {
    label: '概览',
    items: [
      { key: 'dashboard', label: '仪表盘', icon: 'dashboard', hash: '#/dashboard' },
    ],
  },
  {
    label: '配置管理',
    items: [
      { key: 'profiles', label: '配置档案', icon: 'profiles', hash: '#/profiles' },
      { key: 'devices', label: '设备管理', icon: 'devices', hash: '#/devices' },
      { key: 'groups', label: '分组管理', icon: 'groups', hash: '#/groups' },
    ],
  },
  {
    label: '下发管理',
    items: [
      { key: 'deploy', label: '配置下发', icon: 'deploy', hash: '#/deploy' },
      { key: 'remote', label: '远程管理', icon: 'deploy', hash: '#/remote' },
    ],
  },
  {
    label: '系统',
    items: [
      { key: 'audit', label: '审计日志', icon: 'audit', hash: '#/audit' },
      { key: 'settings', label: '系统设置', icon: 'settings', hash: '#/settings' },
    ],
  },
];

/** 路由表：key → 视图模块加载器。 */
const ROUTES = {
  dashboard: () => import('./views/dashboard.js?v=12'),
  devices: () => import('./views/devices.js?v=12'),
  groups: () => import('./views/groups.js?v=12'),
  profiles: () => import('./views/profiles.js?v=12'),
  profileEditor: () => import('./views/profileEditor.js?v=12'),
  deploy: () => import('./views/deploy.js?v=12'),
  remote: () => import('./views/remote.js?v=12'),
  audit: () => import('./views/audit.js?v=12'),
  settings: () => import('./views/settings.js?v=12'),
};

/** 运行状态。 */
const runtime = {
  currentKey: '',
  refreshTimer: null,
};

// ────────────────────────────── 会话 ──────────────────────────────

setSessionExpiredHandler(() => {
  if (document.getElementById('app')?.hidden === false) {
    stopPolling();
    showLogin('登录状态已失效，请重新登录。');
  }
});

async function bootstrap() {
  await loadServerInfo();

  if (session.token) {
    try {
      session.me = await api('/admin/me');
      await showApp();
      return;
    } catch {
      saveToken('');
    }
  }

  showLogin();
}

async function loadServerInfo() {
  try {
    const info = await fetchServerInfo();
    session.serverInfo = info;
    session.revision = info.revision;
    applyBranding(info);
    setLoginServerState('online', info.serverName || '已连接服务器');
  } catch {
    setLoginServerState('offline', '无法连接到服务器');
  }
}

/** 更新登录页的服务器状态指示（online / offline）。 */
function setLoginServerState(state, text) {
  const box = document.getElementById('loginServerName');
  const textEl = document.getElementById('loginServerText');
  if (textEl) textEl.textContent = text;
  if (box) box.className = `login-server ${state}`;
}

/** 应用站点品牌个性化（名称 / Logo / 图标）。 */
function applyBranding(info) {
  const branding = info.branding || {};
  const siteName = branding.siteName || info.serverName || 'ClassislandControlHub 集控系统';
  const logoText = branding.logoText || 'CI';

  document.title = `${siteName} · 管理后台`;
  const ids = ['brandServerName', 'loginTitle', 'brandTitle'];
  for (const id of ids) {
    const el = document.getElementById(id);
    if (el) el.textContent = siteName;
  }

  applyBrandMark('brandMark', logoText, branding.logoImage);
  applyBrandMark('loginBrandMark', logoText, branding.logoImage);

  if (branding.favicon) {
    let link = document.querySelector('link[rel="icon"]');
    if (!link) {
      link = document.createElement('link');
      link.rel = 'icon';
      document.head.appendChild(link);
    }
    link.href = branding.favicon;
  }
}

/** 更新品牌标识方块：有自定义图片用图片，否则默认使用 icon.png。 */
function applyBrandMark(id, logoText, logoImage) {
  const mark = document.getElementById(id);
  if (!mark) return;
  mark.textContent = '';
  const src = logoImage || 'icon.png';
  const img = document.createElement('img');
  img.src = src;
  img.style.width = '100%';
  img.style.height = '100%';
  img.style.objectFit = 'contain';
  img.onerror = () => {
    mark.textContent = '';
    mark.textContent = logoText.slice(0, 2).toUpperCase();
  };
  mark.appendChild(img);
}

// ────────────────────────────── 登录 ──────────────────────────────

function showLogin(message) {
  document.getElementById('app').hidden = true;
  const loginView = document.getElementById('loginView');
  loginView.hidden = false;

  const errorBox = document.getElementById('loginError');
  if (message) {
    errorBox.textContent = message;
    errorBox.hidden = false;
  } else {
    errorBox.hidden = true;
  }

  document.getElementById('loginUsername').focus();
}

async function handleLogin(event) {
  event.preventDefault();

  const button = document.getElementById('loginSubmit');
  const errorBox = document.getElementById('loginError');
  const username = document.getElementById('loginUsername').value.trim();
  const password = document.getElementById('loginPassword').value;

  button.disabled = true;
  button.textContent = '登录中…';
  errorBox.hidden = true;

  try {
    const result = await api('/admin/login', {
      method: 'POST',
      auth: false,
      body: { username, password },
    });

    saveToken(result.token);
    session.me = result;
    document.getElementById('loginPassword').value = '';
    await showApp();

    if (result.role === 'admin') {
      const me = await api('/admin/me');
      if (me.mustChangePassword) {
        toast('warn', '请修改初始密码', '当前账号仍在使用初始密码，建议立即在「系统设置」中修改。', 8000);
      }
    }
  } catch (err) {
    errorBox.textContent = err.message || '登录失败';
    errorBox.hidden = false;
  } finally {
    button.disabled = false;
    button.textContent = '登录';
  }
}

// ────────────────────────────── 主界面 ──────────────────────────────

async function showApp() {
  document.getElementById('loginView').hidden = true;
  document.getElementById('app').hidden = false;

  const me = session.me || {};
  document.getElementById('userName').textContent = me.displayName || me.username || '管理员';
  document.getElementById('userAvatar').textContent = (me.displayName || me.username || 'A').slice(0, 1).toUpperCase();

  renderNav();
  bindShellEvents();
  startPolling();

  if (!window.location.hash || window.location.hash === '#/') {
    window.location.hash = '#/dashboard';
  } else {
    await route();
  }
}

function renderNav() {
  const nav = document.getElementById('navList');
  clear(nav);

  for (const group of NAV) {
    nav.appendChild(h('div.nav-group-label', group.label));
    for (const item of group.items) {
      const iconEl = h('span.nav-icon');
      iconEl.innerHTML = ICONS[item.icon] || '';
      const button = h('button.nav-item', {
        type: 'button',
        dataset: { key: item.key },
        onClick: () => {
          window.location.hash = item.hash;
        },
      },
        iconEl,
        h('span.nav-label', item.label),
      );
      nav.appendChild(button);
    }
  }
}

function bindShellEvents() {
  if (bindShellEvents.bound) return;
  bindShellEvents.bound = true;

  document.getElementById('refreshBtn').addEventListener('click', () => route());

  // 侧边栏折叠 / 展开
  document.getElementById('collapseBtn').addEventListener('click', () => {
    setSidebarCollapsed(!getSidebarCollapsed());
  });

  // 主题 + 密度（外观）下拉
  const themeBtn = document.getElementById('themeBtn');
  const themeDropdown = document.getElementById('themeDropdown');
  renderAppearanceMenu();
  themeBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    themeDropdown.hidden = !themeDropdown.hidden;
  });
  themeDropdown.addEventListener('click', (e) => {
    const btn = e.target.closest('button');
    if (!btn) return;
    const { action, value } = btn.dataset;
    if (action === 'theme') {
      applyTheme(value);
    } else if (action === 'density') {
      setDensity(value);
    } else if (action === 'accent') {
      setAccent(value);
    } else if (action === 'font') {
      setFont(value);
    } else if (action === 'radius') {
      setRadius(value);
    }
    renderAppearanceMenu();
    themeDropdown.hidden = true;
  });

  const userBtn = document.getElementById('userBtn');
  const dropdown = document.getElementById('userDropdown');
  userBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    dropdown.hidden = !dropdown.hidden;
  });

  document.addEventListener('click', () => {
    dropdown.hidden = true;
    themeDropdown.hidden = true;
  });

  dropdown.addEventListener('click', (e) => {
    const action = e.target.dataset?.action;
    dropdown.hidden = true;
    if (action === 'logout') {
      api('/admin/logout', { method: 'POST' }).catch(() => {});
      saveToken('');
      session.me = null;
      stopPolling();
      showLogin('已退出登录。');
    } else if (action === 'change-password') {
      window.location.hash = '#/settings';
    }
  });

  window.addEventListener('hashchange', () => route());
}

/** 渲染「外观」下拉：主题 + 强调色 + 字体 + 圆角 + 密度。 */
function renderAppearanceMenu() {
  const dropdown = document.getElementById('themeDropdown');
  const themeBtn = document.getElementById('themeBtn');
  const currentTheme = getTheme();
  const currentDensity = getDensity();
  const currentAccent = getAccent();
  const currentFont = getFont();
  const currentRadius = getRadius();
  clear(dropdown);

  // 主题按钮图标随当前主题变化
  const resolved = document.documentElement.dataset.theme;
  themeBtn.textContent = resolved === 'light' ? '☀' : '☾';

  dropdown.appendChild(h('div.dropdown-section', '主题'));
  for (const t of THEMES) {
    dropdown.appendChild(h('button', { dataset: { action: 'theme', value: t.key } },
      h('span', { class: `theme-swatch ${t.key}` }, t.icon),
      h('span', t.label),
      currentTheme === t.key ? h('span.menu-check', '✓') : null,
    ));
  }

  dropdown.appendChild(h('div.menu-sep'));
  dropdown.appendChild(h('div.dropdown-section', '强调色'));
  for (const a of ACCENTS) {
    dropdown.appendChild(h('button', { dataset: { action: 'accent', value: a.value } },
      h('span.accent-dot', { style: { background: a.value } }),
      h('span', a.label),
      currentAccent === a.value ? h('span.menu-check', '✓') : null,
    ));
  }

  dropdown.appendChild(h('div.menu-sep'));
  dropdown.appendChild(h('div.dropdown-section', '字体'));
  for (const f of FONTS) {
    dropdown.appendChild(h('button', { dataset: { action: 'font', value: f.value } },
      h('span', { style: { width: '18px', textAlign: 'center', flex: 'none', opacity: 0.8 } }, 'A'),
      h('span', f.label),
      currentFont === f.value ? h('span.menu-check', '✓') : null,
    ));
  }

  dropdown.appendChild(h('div.menu-sep'));
  dropdown.appendChild(h('div.dropdown-section', '圆角'));
  for (const r of RADII) {
    dropdown.appendChild(h('button', { dataset: { action: 'radius', value: r.key } },
      h('span', { style: { width: '18px', textAlign: 'center', flex: 'none', opacity: 0.8 } }, '▢'),
      h('span', r.label),
      currentRadius === r.key ? h('span.menu-check', '✓') : null,
    ));
  }

  dropdown.appendChild(h('div.menu-sep'));
  dropdown.appendChild(h('div.dropdown-section', '密度'));
  for (const d of DENSITIES) {
    dropdown.appendChild(h('button', { dataset: { action: 'density', value: d.key } },
      h('span', { style: { width: '18px', textAlign: 'center', flex: 'none', opacity: 0.8 } }, '▤'),
      h('span', d.label),
      currentDensity === d.key ? h('span.menu-check', '✓') : null,
    ));
  }
}

// ────────────────────────────── 路由 ──────────────────────────────

function parseHash() {
  const raw = window.location.hash.replace(/^#\/?/, '');
  const [pathPart, queryPart = ''] = raw.split('?');
  const segments = pathPart.split('/').filter(Boolean);
  const params = {};
  for (const [key, value] of new URLSearchParams(queryPart)) {
    params[key] = value;
  }

  if (segments[0] === 'profiles' && segments[1]) {
    return { key: 'profileEditor', module: ROUTES.profileEditor, params: { ...params, id: segments[1] } };
  }

  const key = segments[0] || 'dashboard';
  return { key, module: ROUTES[key] || ROUTES.dashboard, params };
}

async function route() {
  const { key, module, params } = parseHash();
  runtime.currentKey = key;

  for (const button of document.querySelectorAll('.nav-item')) {
    button.classList.toggle('active', button.dataset.key === key);
  }

  const content = document.getElementById('content');
  clear(content);
  content.appendChild(h('div', { style: { padding: '40px', textAlign: 'center', color: 'var(--text-faint)' } },
    h('span.spinner')));

  try {
    const view = await module();

    document.getElementById('pageTitle').textContent = view.meta?.title || '集控中心';
    document.getElementById('pageSubtitle').textContent = view.meta?.subtitle || '';

    await view.render(content, params);
    updateRevisionChip();
  } catch (err) {
    clear(content);
    content.appendChild(h('div.card',
      h('div.notice.notice-danger', { style: { margin: 0 } },
        h('span.notice-icon', '!'),
        h('div', h('strong', '页面加载失败'), h('div', err.message || String(err))),
      ),
    ));
    toast('error', '加载失败', err.message || String(err));
  }
}

// ────────────────────────────── 状态轮询 ──────────────────────────────

function startPolling() {
  stopPolling();
  refreshConnectionState();
  runtime.refreshTimer = setInterval(refreshConnectionState, 30000);
}

function stopPolling() {
  if (runtime.refreshTimer) {
    clearInterval(runtime.refreshTimer);
    runtime.refreshTimer = null;
  }
}

async function refreshConnectionState() {
  const stateEl = document.getElementById('connState');
  const textEl = stateEl.querySelector('.conn-text');

  try {
    const info = await fetchServerInfo();
    session.serverInfo = info;
    session.revision = info.revision;

    stateEl.className = 'conn-state online';
    textEl.textContent = `在线 · ${info.onlineDeviceCount}/${info.deviceCount} 台`;
    document.getElementById('sidebarVersion').textContent =
      `v${info.version} · 协议 ${info.protocolVersion}`;
    updateRevisionChip();
  } catch {
    stateEl.className = 'conn-state offline';
    textEl.textContent = '与服务器断开连接';
  }
}

function updateRevisionChip() {
  const info = session.serverInfo;
  if (info) {
    document.getElementById('revisionChip').textContent = `版本 #${info.revision}`;
  }
}

// ────────────────────────────── 启动 ──────────────────────────────

document.getElementById('loginForm').addEventListener('submit', handleLogin);

// 登录页也展示在线状态，方便确认服务器是否可达。
fetchServerInfo()
  .then((info) => {
    session.serverInfo = info;
  })
  .catch(() => {});

bootstrap();
