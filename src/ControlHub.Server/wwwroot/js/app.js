/**
 * 应用入口：会话引导、导航渲染与哈希路由。
 */

import { api, session, saveToken, setSessionExpiredHandler, fetchServerInfo, hasPermission as can } from './core/api.js?v=30';
import { h, clear, toast, icon } from './core/ui.js?v=30';
import {
  initTheme, getTheme, applyTheme, THEMES,
  getSidebarCollapsed, setSidebarCollapsed,
  getDensity, setDensity, applyDensity, DENSITIES,
  applyAppearance, getAccent, setAccent, ACCENTS,
  getFont, setFont, FONTS,
  getRadius, setRadius, RADII,
} from './core/prefs.js?v=30';

// ── 应用启动早期：应用主题 / 外观 / 布局偏好（避免闪烁） ──
initTheme();
applyAppearance();
applyDensity();
setSidebarCollapsed(getSidebarCollapsed());


/**
 * 导航结构。新增页面时只需在此登记。
 * `perm` 为该页面所需的权限键，可用数组表示「任一满足」；不写表示只需登录。
 */
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
      { key: 'profiles', label: '配置档案', icon: 'profiles', hash: '#/profiles', perm: 'profiles.read' },
      { key: 'devices', label: '设备管理', icon: 'monitor', hash: '#/devices', perm: 'devices.read' },
    ],
  },
  {
    label: '下发管理',
    items: [
      { key: 'deploy', label: '配置下发', icon: 'send', hash: '#/deploy', perm: 'deploy.write' },
      { key: 'remote', label: '远程管理', icon: 'send', hash: '#/remote', perm: 'remote.read' },
      { key: 'reminders', label: '定时提醒', icon: 'bell', hash: '#/reminders', perm: 'reminders.read' },
    ],
  },
  {
    label: '系统',
    items: [
      { key: 'audit', label: '审计日志', icon: 'list', hash: '#/audit', perm: 'audit.read' },
      { key: 'settings', label: '系统设置', icon: 'gear', hash: '#/settings', perm: ['settings.read', 'accounts.read'] },
    ],
  },
];

/** 路由表：key → 视图模块加载器。 */
const ROUTES = {
  dashboard: () => import('./views/dashboard.js?v=30'),
  devices: () => import('./views/devices.js?v=30'),
  // 「分组管理」已并入设备管理，旧链接继续可用。
  groups: () => import('./views/devices.js?v=30'),
  profiles: () => import('./views/profiles.js?v=30'),
  profileEditor: () => import('./views/profileEditor.js?v=30'),
  deploy: () => import('./views/deploy.js?v=30'),
  remote: () => import('./views/remote.js?v=30'),
  reminders: () => import('./views/reminders.js?v=30'),
  audit: () => import('./views/audit.js?v=30'),
  settings: () => import('./views/settings.js?v=30'),
};

/** 各页面所需权限：直接敲 hash 进无权页面时给出明确提示，而不是让接口先报 403。 */
const ROUTE_PERMS = {
  profiles: 'profiles.read',
  profileEditor: 'profiles.read',
  devices: 'devices.read',
  groups: 'devices.read',
  deploy: 'deploy.write',
  remote: 'remote.read',
  reminders: 'reminders.read',
  audit: 'audit.read',
  settings: ['settings.read', 'accounts.read'],
};

/** 运行状态。 */
const runtime = {
  currentKey: '',
  refreshTimer: null,
  /** 服务端是否开放了凭邀请码自助注册（决定登录页是否显示该入口）。 */
  registrationEnabled: false,
  /** 服务端是否开放了自助注册申请（提交后需管理员审批）。 */
  approvalEnabled: false,
  /** 两步验证的半程票据（登录第一步返回，验证码通过后作废）。 */
  pendingTotp: '',
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

  // 登录页背景（可选）：学校自定义的背景图 + 淡化程度，保证表单区域可读。
  const root = document.documentElement;
  if (branding.loginBackground) {
    root.style.setProperty('--login-bg-image', `url("${branding.loginBackground}")`);
    const dim = Math.min(0.9, Math.max(0, (branding.loginBackgroundDim ?? 45) / 100));
    root.style.setProperty('--login-bg-dim', String(dim));
  } else {
    root.style.removeProperty('--login-bg-image');
    root.style.removeProperty('--login-bg-dim');
  }

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

  switchLoginMode('login');
  refreshRegistrationEntry();
  document.getElementById('loginUsername').focus();
}

/**
 * 在「登录」「凭邀请码注册」「自助申请（需审批）」三张表单之间切换。
 * @param {'login'|'register'|'apply'} mode
 */
function switchLoginMode(mode) {
  const isLogin = mode === 'login';
  const isApply = mode === 'apply';
  const isTotp = mode === 'totp';

  document.getElementById('loginForm').hidden = !isLogin;
  document.getElementById('registerForm').hidden = mode !== 'register';
  document.getElementById('applyForm').hidden = !isApply;
  document.getElementById('applyDone').hidden = true;
  const totpForm = document.getElementById('totpForm');
  if (totpForm) totpForm.hidden = !isTotp;

  // 两个注册入口只在登录页展示，且各自受服务端开关控制。
  const toRegister = document.getElementById('switchToRegister');
  const toApply = document.getElementById('switchToApply');
  const toLogin = document.getElementById('switchToLogin');
  toRegister.hidden = !isLogin || !runtime.registrationEnabled;
  toApply.hidden = !isLogin || !runtime.approvalEnabled;
  // 两步验证进行中不给「返回登录」：半程票据还在，切回去只会让人困惑。
  toLogin.hidden = isLogin || isTotp;

  const switchBox = document.getElementById('loginSwitch');
  if (switchBox) switchBox.hidden = toRegister.hidden && toApply.hidden && toLogin.hidden;

  document.getElementById('loginError').hidden = true;
  document.getElementById('registerError').hidden = true;
  document.getElementById('applyError').hidden = true;
  const totpError = document.getElementById('totpError');
  if (totpError) totpError.hidden = true;

  // 标题与副文案跟随当前表单，避免多张表单共用一个标题造成误解。
  const titles = {
    login: ['欢迎回来', '登录以继续管理您的集控服务'],
    register: ['创建账号', '凭管理员发放的邀请码即可自助注册'],
    apply: ['申请账号', '提交后由管理员审批，通过即可登录'],
    totp: ['两步验证', '请输入验证器 App 显示的 6 位验证码'],
  };
  const [title, subtitle] = titles[mode] || titles.login;
  const heading = document.getElementById('loginHeading');
  const sub = document.getElementById('loginSubtitle');
  if (heading) heading.textContent = title;
  if (sub) sub.textContent = subtitle;

  // 切走时收起已揭示的密码，避免明文停留在屏幕上。
  resetPasswordFields();

  const firstId = isTotp
    ? 'totpCode'
    : (isApply ? 'applyUsername' : (mode === 'register' ? 'registerCode' : 'loginUsername'));
  document.getElementById(firstId)?.focus();
}

/** 提交按钮的忙碌态：禁用 + aria-busy + 按钮内联转圈。 */
function setButtonLoading(button, loading, label) {
  if (!button) return;
  button.disabled = loading;
  button.classList.toggle('is-loading', loading);
  button.setAttribute('aria-busy', loading ? 'true' : 'false');
  if (label) button.textContent = label;
}

/** 收起已揭示的密码输入框（切换表单或提交后调用）。 */
function resetPasswordFields() {
  for (const id of ['loginPassword', 'registerPassword', 'applyPassword']) {
    const input = document.getElementById(id);
    if (input) input.type = 'password';
  }

  for (const btn of document.querySelectorAll('.pw-toggle')) {
    btn.classList.remove('is-on');
    btn.setAttribute('aria-pressed', 'false');
    btn.setAttribute('aria-label', '显示密码');
  }
}

/** 密码可见切换：移动端输入长密码时尤其有用，同时给出 aria 状态。 */
function bindPasswordToggles() {
  for (const btn of document.querySelectorAll('[data-pw-toggle]')) {
    btn.addEventListener('click', () => {
      const input = document.getElementById(btn.dataset.pwToggle);
      if (!input) return;

      const show = input.type === 'password';
      input.type = show ? 'text' : 'password';
      btn.classList.toggle('is-on', show);
      btn.setAttribute('aria-pressed', show ? 'true' : 'false');
      btn.setAttribute('aria-label', show ? '隐藏密码' : '显示密码');
      input.focus();
    });
  }
}

/** 询问服务端开放了哪些注册方式；关闭的入口不显示，避免误导。 */
async function refreshRegistrationEntry() {
  try {
    const info = await api('/admin/registration', { auth: false });
    runtime.registrationEnabled = Boolean(info.enabled);
    runtime.approvalEnabled = Boolean(info.approvalEnabled);
    document.getElementById('loginFootHint').innerHTML = info.hint
      ? `${info.hint}；首次部署默认账号 <code>admin</code>。`
      : '首次部署默认账号 <code>admin</code>，登录后请立即修改密码。';
  } catch {
    runtime.registrationEnabled = false;
    runtime.approvalEnabled = false;
  }

  // 两个注册入口的显隐统一交给 switchLoginMode，避免两处逻辑打架。
  switchLoginMode('login');
}

/** 登录 / 注册成功后的统一收尾：补全权限与引导状态后再进主界面。 */
async function completeSignIn(result) {
  saveToken(result.token);
  session.me = result;

  // 登录响应里只有最小信息，权限集合与引导状态要单独取，导航过滤依赖它。
  try {
    session.me = { ...result, ...(await api('/admin/me')) };
  } catch { /* 取不到就退回最小信息，导航会保守地少显示几项 */ }

  await showApp();

  if (session.me.mustChangePassword) {
    toast('warn', '请修改初始密码', '当前账号仍在使用初始密码，建议立即在「系统设置」中修改。', 8000);
  }
}

async function handleLogin(event) {
  event.preventDefault();

  const button = document.getElementById('loginSubmit');
  const errorBox = document.getElementById('loginError');
  const username = document.getElementById('loginUsername').value.trim();
  const password = document.getElementById('loginPassword').value;

  setButtonLoading(button, true, '登录中…');
  errorBox.hidden = true;

  try {
    const result = await api('/admin/login', {
      method: 'POST',
      auth: false,
      body: { username, password },
    });

    document.getElementById('loginPassword').value = '';

    // 账号开启了两步验证：先拿到半程票据，切到验证码表单继续。
    if (result.needTotp) {
      runtime.pendingTotp = result.totpTicket || '';
      switchLoginMode('totp');
      toast('info', '需要两步验证', '请输入验证器 App 里的 6 位验证码。');
      return;
    }

    await completeSignIn(result);
  } catch (err) {
    errorBox.textContent = err.message || '登录失败';
    errorBox.hidden = false;
  } finally {
    setButtonLoading(button, false, '登录');
  }
}

/** 两步验证第二步：用半程票据 + 6 位验证码换取正式会话。 */
async function handleTotp(event) {
  event.preventDefault();

  const button = document.getElementById('totpSubmit');
  const errorBox = document.getElementById('totpError');
  const code = document.getElementById('totpCode').value.trim();

  if (!runtime.pendingTotp) {
    switchLoginMode('login');
    showLogin('验证已超时，请重新登录。');
    return;
  }

  setButtonLoading(button, true, '验证中…');
  errorBox.hidden = true;

  try {
    const result = await api('/admin/login/totp', {
      method: 'POST',
      auth: false,
      body: { ticket: runtime.pendingTotp, code },
    });

    runtime.pendingTotp = '';
    document.getElementById('totpCode').value = '';
    await completeSignIn(result);
  } catch (err) {
    errorBox.textContent = err.message || '验证失败';
    errorBox.hidden = false;
  } finally {
    setButtonLoading(button, false, '验证并登录');
  }
}

/** 凭邀请码自助注册，注册成功后直接登录。 */
async function handleRegister(event) {
  event.preventDefault();

  const button = document.getElementById('registerSubmit');
  const errorBox = document.getElementById('registerError');
  const body = {
    code: document.getElementById('registerCode').value.trim(),
    username: document.getElementById('registerUsername').value.trim(),
    displayName: document.getElementById('registerDisplayName').value.trim(),
    password: document.getElementById('registerPassword').value,
  };

  setButtonLoading(button, true, '注册中…');
  errorBox.hidden = true;

  try {
    const result = await api('/admin/register', { method: 'POST', auth: false, body });
    document.getElementById('registerPassword').value = '';
    await completeSignIn(result);
    toast('ok', '注册成功', `欢迎，${result.displayName || session.me?.username || '新同事'}。`);
  } catch (err) {
    errorBox.textContent = err.message || '注册失败';
    errorBox.hidden = false;
  } finally {
    setButtonLoading(button, false, '注册并登录');
  }
}

/** 提交自助注册申请：不建号，等管理员在「系统设置 → 注册申请」中批准。 */
async function handleApply(event) {
  event.preventDefault();

  const button = document.getElementById('applySubmit');
  const errorBox = document.getElementById('applyError');
  const body = {
    username: document.getElementById('applyUsername').value.trim(),
    displayName: document.getElementById('applyDisplayName').value.trim(),
    note: document.getElementById('applyNote').value.trim(),
    password: document.getElementById('applyPassword').value,
  };

  setButtonLoading(button, true, '提交中…');
  errorBox.hidden = true;

  try {
    await api('/admin/register-requests', { method: 'POST', auth: false, body });
    document.getElementById('applyPassword').value = '';
    document.getElementById('applyForm').hidden = true;
    document.getElementById('applyDone').hidden = false;
    toast('ok', '申请已提交', '管理员审批通过后即可登录。');
  } catch (err) {
    errorBox.textContent = err.message || '提交失败';
    errorBox.hidden = false;
  } finally {
    setButtonLoading(button, false, '提交申请');
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

  // 新账号（或在设置里重置过引导的账号）第一次进来时放一遍新手引导，随时可跳过。
  if (me.onboardingDone === false) {
    const { startTour } = await import('./core/tour.js?v=30');
    startTour({
      onFinish: async (skipped) => {
        try {
          await api('/admin/onboarding', { method: 'POST' });
          session.me.onboardingDone = true;
        } catch { /* 记录失败也无妨，下次登录会再放一次 */ }
        if (!skipped) {
          toast('ok', '引导已完成', '之后可以在「系统设置」里重新观看。');
        }
      },
    });
  }
}

function renderNav() {
  const nav = document.getElementById('navList');
  clear(nav);

  for (const group of NAV) {
    // 没有权限的页面直接不出现；整组都没权限时连分组标题也省掉。
    const items = group.items.filter((item) => can(item.perm));
    if (items.length === 0) continue;

    nav.appendChild(h('div.nav-group-label', group.label));
    for (const item of items) {
      const iconEl = h('span.nav-icon');
      iconEl.appendChild(icon(item.icon, 17));
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

/** 渲染「外观」下拉：主题、强调色、字体、圆角、密度。 */
function renderAppearanceMenu() {
  const dropdown = document.getElementById('themeDropdown');
  const themeBtn = document.getElementById('themeBtn');

  clear(dropdown);
  clear(themeBtn);
  themeBtn.appendChild(icon(document.documentElement.dataset.theme === 'light' ? 'sun' : 'moon', 16));

  const addItem = (label, active, dataset, marker) => dropdown.appendChild(h('button', { dataset },
    h('span.menu-marker', marker),
    h('span', label),
    active ? h('span.menu-check', icon('check', 13)) : null,
  ));

  const addGroup = (title) => {
    dropdown.appendChild(h('div.menu-sep'));
    dropdown.appendChild(h('div.dropdown-section', title));
  };

  dropdown.appendChild(h('div.dropdown-section', '主题'));
  for (const t of THEMES) {
    addItem(t.label, getTheme() === t.key, { action: 'theme', value: t.key }, icon(t.icon, 15));
  }

  addGroup('强调色');
  for (const a of ACCENTS) {
    addItem(a.label, getAccent() === a.value, { action: 'accent', value: a.value },
      h('span.accent-dot', { style: { background: a.value } }));
  }

  addGroup('字体');
  for (const f of FONTS) {
    addItem(f.label, getFont() === f.value, { action: 'font', value: f.value }, 'A');
  }

  addGroup('圆角');
  for (const r of RADII) {
    addItem(r.label, getRadius() === r.key, { action: 'radius', value: r.key }, icon('square', 14));
  }

  addGroup('密度');
  for (const d of DENSITIES) {
    addItem(d.label, getDensity() === d.key, { action: 'density', value: d.key }, icon('rows', 14));
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

  // 「分组管理」已并入设备管理。
  const key = segments[0] === 'groups' ? 'devices' : (segments[0] || 'dashboard');
  return { key, module: ROUTES[key] || ROUTES.dashboard, params };
}

async function route() {
  const { key, module, params } = parseHash();
  runtime.currentKey = key;

  for (const button of document.querySelectorAll('.nav-item')) {
    button.classList.toggle('active', button.dataset.key === key);
  }

  const content = document.getElementById('content');

  // 无权访问的页面直接给提示，不要先打一堆注定 403 的接口。
  if (!can(ROUTE_PERMS[key])) {
    const label = NAV.flatMap((g) => g.items).find((i) => i.key === key)?.label || '该页面';
    document.getElementById('pageTitle').textContent = label;
    document.getElementById('pageSubtitle').textContent = '无权访问';
    clear(content);
    content.appendChild(h('div.card',
      h('div.notice.notice-warn', { style: { margin: 0 } },
        h('span.notice-icon', '!'),
        h('div',
          h('strong', '当前账号没有访问该页面的权限'),
          h('div', `请联系超级管理员在「系统设置 → 账号管理」中为你的账号勾选相应权限。`)),
      ),
    ));
    return;
  }

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
document.getElementById('registerForm').addEventListener('submit', handleRegister);
document.getElementById('applyForm').addEventListener('submit', handleApply);
document.getElementById('totpForm').addEventListener('submit', handleTotp);
document.getElementById('switchToRegister').addEventListener('click', () => switchLoginMode('register'));
document.getElementById('switchToApply').addEventListener('click', () => switchLoginMode('apply'));
document.getElementById('switchToLogin').addEventListener('click', () => switchLoginMode('login'));
document.getElementById('applyBackToLogin').addEventListener('click', () => switchLoginMode('login'));

// 密码可见切换（登录 / 注册两处）
bindPasswordToggles();

// 登录页也展示在线状态，方便确认服务器是否可达。
fetchServerInfo()
  .then((info) => {
    session.serverInfo = info;
  })
  .catch(() => {});

bootstrap();