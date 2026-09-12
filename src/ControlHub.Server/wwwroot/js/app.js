/**
 * 应用入口：会话引导、导航渲染与哈希路由。
 */

import { api, session, saveToken, setSessionExpiredHandler, fetchServerInfo } from './core/api.js';
import { h, clear, toast } from './core/ui.js';

/** 导航结构。新增页面时只需在此登记。 */
const NAV = [
  {
    label: '概览',
    items: [
      { key: 'dashboard', label: '仪表盘', icon: '◫', hash: '#/dashboard' },
    ],
  },
  {
    label: '配置管理',
    items: [
      { key: 'profiles', label: '配置档案', icon: '▤', hash: '#/profiles' },
      { key: 'devices', label: '设备管理', icon: '▣', hash: '#/devices' },
      { key: 'groups', label: '分组管理', icon: '❐', hash: '#/groups' },
    ],
  },
  {
    label: '下发管理',
    items: [
      { key: 'deploy', label: '配置下发', icon: '⇅', hash: '#/deploy' },
    ],
  },
  {
    label: '系统',
    items: [
      { key: 'audit', label: '审计日志', icon: '☰', hash: '#/audit' },
      { key: 'settings', label: '系统设置', icon: '⚙', hash: '#/settings' },
    ],
  },
];

/** 路由表：key → 视图模块加载器。 */
const ROUTES = {
  dashboard: () => import('./views/dashboard.js'),
  devices: () => import('./views/devices.js'),
  groups: () => import('./views/groups.js'),
  profiles: () => import('./views/profiles.js'),
  profileEditor: () => import('./views/profileEditor.js'),
  deploy: () => import('./views/deploy.js'),
  audit: () => import('./views/audit.js'),
  settings: () => import('./views/settings.js'),
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

    document.getElementById('brandServerName').textContent = info.serverName || 'ClassIsland 集控';
    document.getElementById('loginServerName').textContent = info.serverName || 'ClassIsland 集控服务器';
    document.title = `${info.serverName || '集控系统'} · 管理后台`;
  } catch {
    document.getElementById('loginServerName').textContent = '无法连接到服务器';
  }
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
      const button = h('button.nav-item', {
        type: 'button',
        dataset: { key: item.key },
        onClick: () => {
          window.location.hash = item.hash;
        },
      },
        h('span.nav-icon', item.icon),
        h('span', item.label),
      );
      nav.appendChild(button);
    }
  }
}

function bindShellEvents() {
  if (bindShellEvents.bound) return;
  bindShellEvents.bound = true;

  document.getElementById('refreshBtn').addEventListener('click', () => route());

  const userBtn = document.getElementById('userBtn');
  const dropdown = document.getElementById('userDropdown');
  userBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    dropdown.hidden = !dropdown.hidden;
  });

  document.addEventListener('click', () => {
    dropdown.hidden = true;
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
