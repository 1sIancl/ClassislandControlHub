/**
 * 应用入口：会话引导、导航渲染与哈希路由。
 */

import { api, session, saveToken, setSessionExpiredHandler, fetchServerInfo, hasPermission as can } from './core/api.js?v=142';
import { toastError } from './core/errors.js?v=142';
import { h, clear, toast, icon } from './core/ui.js?v=142';
import { t, applyStatic, mountLocalePicker } from './core/i18n.js?v=142';
import {
  initTheme,
  getSidebarCollapsed, setSidebarCollapsed,
  applyDensity,
  applyAppearance, setBrandingAccent,
  applyRemotePrefs, applySchedScale,
} from './core/prefs.js?v=142';
import { initShortcuts, shortcutHint, setPageLister } from './core/shortcuts.js?v=142';
import { initGlassHighlight } from './core/glass.js?v=142';
import { initTopbar } from './topbar.js?v=142';
import { openSearch } from './core/search.js?v=142';

// ── 应用启动早期：应用主题 / 外观 / 布局偏好（避免闪烁） ──
initTheme();
applyAppearance();
applyDensity();
applySchedScale();
setSidebarCollapsed(getSidebarCollapsed());


/**
 * 导航结构。新增页面时只需在此登记。
 * `perm` 为该页面所需的权限键，可用数组表示「任一满足」；不写表示只需登录。
 */
/**
 * TOTP 验证码倒计时（30 秒时间片）。
 *
 * <para>纯前端按本地时钟估算：从服务端拿到「现在是第几个时间片」有网络延迟，
 * 所以宁可让用户「以为还剩 10 秒、其实还剩 12 秒」，也不要提前说「已过期」
 * 让人白输一遍——后者会让人怀疑是自己验证码输错了。</para>
 */
function startTotpCountdown() {
  const box = document.getElementById('totpTimer');
  const remain = document.getElementById('totpRemain');
  if (!box || !remain) {
    return;
  }

  box.hidden = false;
  const tick = () => {
    const left = 30 - (Math.floor(Date.now() / 1000) % 30);
    remain.textContent = String(left);
    // 最后 5 秒转成警告色：这时输入也来得及，但该提醒了。
    box.classList.toggle('warning', left <= 5);
  };

  tick();
  clearInterval(startTotpCountdown.timer);
  startTotpCountdown.timer = setInterval(tick, 1000);
}

/**
 * 导航分组名 → 文案键（#48）。分组没有 key 字段，这里按中文原名映射一次；
 * 新增分组时记得同时加 `nav.group.*` 的条目。
 */
const NAV_GROUP_KEYS = {
  日常: 'nav.group.daily',
  教学配置: 'nav.group.teaching',
  设备: 'nav.group.devices',
  系统: 'nav.group.system',
};

const NAV = [
  {
    label: '日常',
    items: [
      { key: 'dashboard', label: '仪表盘', icon: 'dashboard', hash: '#/dashboard' },
  // 大屏看板（#113）：挂在值班室大屏上的只读总览；放得离仪表盘近，找起来顺。
  { key: 'board', label: '大屏看板', icon: 'monitor', hash: '#/board', perm: 'devices.read' },
      { key: 'deploy', label: '配置下发', icon: 'send', hash: '#/deploy', perm: 'deploy.write' },
      { key: 'remote', label: '远程管理', icon: 'send', hash: '#/remote', perm: 'remote.read' },
    ],
  },
  {
    label: '教学配置',
    items: [
      { key: 'profiles', label: '配置档案', icon: 'profiles', hash: '#/profiles', perm: 'profiles.read' },
      { key: 'reminders', label: '定时提醒', icon: 'bell', hash: '#/reminders', perm: 'reminders.read' },
      // 考试模式（#21）：一次性把选定教室切到考试档案，考完一键还原。
      // 权限沿用 devices.write——它做的正是「改一批设备的档案 + 下发」。
      { key: 'examMode', label: '考试模式', icon: 'bell', hash: '#/examMode', perm: 'devices.write' },
      // 走班制（#25）：按节次自动切换教室档案。
      { key: 'profileSlots', label: '走班制', icon: 'clock', hash: '#/profileSlots', perm: 'devices.write' },
      // 授时偏移调的是教室大屏的时钟，属于教学场景（原来挂在「系统维护」下，
      // 找它得先经过服务器维护，不合理）。接口权限仍是 settings.read。
      { key: 'timesync', label: '时间偏移', icon: 'clock', hash: '#/timesync', perm: 'settings.read' },
      // 快捷换课：读教室端实时课表（下节课 + 倒计时），并直接改教室端课表（换课 / 调休）。
      { key: 'lessons', label: '快捷换课', icon: 'list', hash: '#/lessons', perm: 'devices.read' },
    ],
  },
  {
    label: '设备',
    items: [
      { key: 'devices', label: '设备管理', icon: 'monitor', hash: '#/devices', perm: 'devices.read' },
      // 插件管理（#121）：从 ClassIsland 插件市场缓存 .cipx 再批量下发到教室端。
      // 教室端的一条轴——插件装没装、装的是哪一版，不用再跑到教室机上看。
      { key: 'plugins', label: '插件管理', icon: 'database', hash: '#/plugins', perm: 'plugins.read' },
    ],
  },
  {
    label: '系统',
    items: [
      { key: 'reports', label: '报表', icon: 'clock', hash: '#/reports', perm: 'audit.read' },
      { key: 'audit', label: '审计日志', icon: 'list', hash: '#/audit', perm: 'audit.read' },
      // 操作审批（#35）：危险操作（恢复备份等）由非超管发起时，在这里等超级管理员确认。
      // 不设 perm：提交人要看得到自己提交的申请（非超管在服务端只被允许看到自己的）。
      { key: 'approvals', label: '操作审批', icon: 'list', hash: '#/approvals' },
      { key: 'appearance', label: '页面设置', icon: 'auto', hash: '#/appearance', perm: ['settings.read', 'settings.write'] },
      // 原来的「系统设置」一页十四张卡，管理员每找一个开关都要滚很久；
      // 拆成四页之后每一页只讲一件事（账号 / 服务器 / 维护 / 集成）。
      { key: 'accounts', label: '账号与权限', icon: 'user', hash: '#/accounts', perm: ['accounts.read', 'settings.read'] },
      { key: 'server', label: '服务器信息', icon: 'database', hash: '#/server', perm: 'settings.read' },
      // 天气：全校一套，配好城市后教室端可读（`GET /client/weather`）。
      { key: 'weather', label: '天气', icon: 'cloud', hash: '#/weather', perm: 'settings.read' },
      { key: 'integrations', label: '通知与集成', icon: 'link', hash: '#/integrations', perm: 'settings.read' },
      { key: 'maintenance', label: '系统维护', icon: 'gear', hash: '#/maintenance', perm: ['settings.read', 'backup.read'] },
    ],
  },
];

// 文案抽资源（#48）：导航文案在模块加载时按 `nav.<key>` / `nav.group.*` 取一次。
// 这里就地改写 NAV（而不是把 label 换成键名），是为了让「键缺失」时还能退回数组里的中文——
// 翻译做一半时最多是某几项还是中文，不会出现空标签。
for (const group of NAV) {
  group.label = t(NAV_GROUP_KEYS[group.label] || group.label);
  for (const item of group.items) {
    item.label = t(`nav.${item.key}`);
  }
}

// 静态 HTML 上的文案（登录页、顶栏、账号菜单）与语言下拉框。
applyStatic();
mountLocalePicker(document.getElementById('localePicker'));

/**
 * 旧链接重定向：改过导航之后，别人收藏的旧地址不能变成 404。
 * <para>只做**同一目标**的别名，不做「功能搬迁」——搬迁该由用户重新养成习惯，
 * 而不是让一个地址长期指向另一个语义不同的页面。</para>
 */
const ROUTE_ALIASES = {
  // 「分组管理」早就并入了设备管理。
  groups: 'devices',
  // 「系统设置」已拆成四页：旧地址落到最常用的那一页（账号与权限），
  // 而不是给一个 404 —— 收藏夹里那条链接多半就是冲着改账号去的。
  settings: 'accounts',
};

/** 路由表：key → 视图模块加载器。 */
const ROUTES = {
  dashboard: () => import('./views/dashboard.js?v=142'),
  devices: () => import('./views/devices.js?v=142'),
  // 「分组管理」已并入设备管理，旧链接继续可用。
  groups: () => import('./views/devices.js?v=142'),
  profiles: () => import('./views/profiles.js?v=142'),
  profileEditor: () => import('./views/profileEditor.js?v=142'),
  deploy: () => import('./views/deploy.js?v=142'),
  remote: () => import('./views/remote.js?v=142'),
  reminders: () => import('./views/reminders.js?v=142'),
  examMode: () => import('./views/examMode.js?v=142'),
  weather: () => import('./views/weather.js?v=142'),
  profileSlots: () => import('./views/profileSlots.js?v=142'),
  audit: () => import('./views/audit.js?v=142'),
  reports: () => import('./views/reports.js?v=142'),
  appearance: () => import('./views/appearance.js?v=142'),
  // 「系统设置」拆成四页（原 settings.js 已按此拆分，见各文件头部说明）。
  accounts: () => import('./views/accounts.js?v=142'),
  server: () => import('./views/server.js?v=142'),
  integrations: () => import('./views/integrations.js?v=142'),
  maintenance: () => import('./views/maintenance.js?v=142'),
  board: () => import('./views/board.js?v=142'),
  approvals: () => import('./views/approvals.js?v=142'),
  timesync: () => import('./views/timesync.js?v=142'),
  lessons: () => import('./views/lessons.js?v=142'),
  plugins: () => import('./views/pluginMarket.js?v=142'),
  // 首次使用引导（#126）：**故意不进 NAV**——它只在部署后第一次进来时出现，
  // 以及「页面设置 → 重新播放引导」时进入；放进侧边栏反而会变成一个"日常页面"。
  welcome: () => import('./views/welcome.js?v=142'),
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
  // 报表读的就是历史数据，权限与审计一致（#63 / #65 / #67）。
  reports: 'audit.read',
  // 页面设置改的是全站品牌 / 登录页 / 外观：读要 settings.read，
  // 保存走 /admin/branding 时会再校验 settings.write。
  appearance: ['settings.read', 'settings.write'],
  // 拆出来的四页各自的最小权限：与导航里的 perm 保持一致，
  // 否则会出现「导航里有、直接敲地址却说没权限」这种自相矛盾。
  accounts: ['accounts.read', 'settings.read'],
  server: 'settings.read',
  integrations: 'settings.read',
  // 系统维护页里有备份卡，而备份权限独立于系统设置（backup.read）。
  maintenance: ['settings.read', 'backup.read'],
  // 授时接口要 settings.read（服务端 /admin/time-offset 的校验不随导航走）。
  timesync: 'settings.read',
  // 快捷换课读的是设备状态（devices.read）；换课 / 调休的下发要 remote.write，
  // 由接口层再校验一次（页面对只读账号也可打开，看得到但发不出去）。
  lessons: 'devices.read',
  // 插件管理：看市场与缓存要 plugins.read；同步 / 缓存 / 下发由接口层再校验 plugins.write，
  // 所以只读账号也能打开这一页看清单（看得到但发不出去）。
  plugins: 'plugins.read',
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
    showLogin(t('app.sessionExpired'));
  }
});

async function bootstrap() {
  await loadServerInfo();

  // 无论有没有本地保存的令牌，都先问一次 /admin/me：
  // 桌面外壳（本地控制台）下服务端凭回环信任直接放行，此时浏览器里并没有令牌。
  try {
    session.me = await api('/admin/me');
    await showApp();
    return;
  } catch {
    saveToken('');
  }

  showLogin();
}

async function loadServerInfo() {
  try {
    const info = await fetchServerInfo();
    session.serverInfo = info;
    session.revision = info.revision;
    applyBranding(info);
    setLoginServerState('online', info.serverName || t('app.connected'));
  } catch {
    setLoginServerState('offline', t('app.disconnected'));
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
  const siteName = branding.siteName || info.serverName || t('app.defaultSiteName');
  // 没配品牌图时用内置的 icon.png（applyBrandMark 的兜底）；logoText 只在图片加载不出来时才显示。
  const logoText = branding.logoText || 'IslandHub';

  document.title = t('app.titleWithSite', { site: siteName });
  const ids = ['brandServerName', 'loginTitle', 'brandTitle'];
  for (const id of ids) {
    const el = document.getElementById(id);
    if (el) el.textContent = siteName;
  }

  applyBrandMark('brandMark', logoText, branding.logoImage);
  applyBrandMark('loginBrandMark', logoText, branding.logoImage);
  // 登录页顶栏：之前只更新了登录卡片的品牌块，**漏了这一处**，
  // 于是它一直显示 index.html 里硬编码的「CI」——正是这次反馈的问题。
  applyBrandMark('loginTopMark', logoText, branding.logoImage);

  const topbar = document.getElementById('loginTopbar');
  if (topbar) {
    topbar.hidden = branding.loginTopbarHidden === true;
    const title = document.getElementById('loginTopName');
    if (title) {
      title.textContent = branding.loginTopbarTitle || siteName;
    }
  }

  // 登录页背景（可选）：学校自定义的背景图 + 淡化程度，保证表单区域可读。
  const root = document.documentElement;
  if (branding.loginBackground) {
    root.style.setProperty('--login-bg-image', `url("${branding.loginBackground}")`);
    const dim = Math.min(0.9, Math.max(0, (branding.loginBackgroundDim ?? 45) / 100));
    root.style.setProperty('--login-bg-dim', String(dim));
    // 左侧介绍区（标题 / 说明 / 功能列表）没有自己的底，文字是直接压在页面背景上的。
    // 没有壁纸时那点对比度来自默认渐变，够用；一旦换成照片，标题（还是渐变文字）、
    // 说明与功能标签会一起糊掉。所以配了壁纸就额外开一层衬底，见 app.css 的 .login-hero::before。
    root.style.setProperty('--login-hero-scrim', '1');
  } else {
    root.style.removeProperty('--login-bg-image');
    root.style.removeProperty('--login-bg-dim');
    root.style.removeProperty('--login-hero-scrim');
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

  // ── 登录页文案 ────────────────────────────────────────────────────
  const loginTitle = document.getElementById('loginTitle');
  if (loginTitle) {
    loginTitle.textContent = branding.loginTitle || siteName;
  }

  const loginSubtitle = document.getElementById('loginSubtitle');
  if (loginSubtitle) {
    loginSubtitle.textContent = branding.loginSubtitle || t('app.defaultLoginSubtitle');
  }

  // 描述段落：配了才替换；配成空串则整段收起（而不是留一行空白）。
  const loginDescription = document.getElementById('loginDescription');
  if (loginDescription && typeof branding.loginDescription === 'string'
      && branding.loginDescription.trim() !== '') {
    loginDescription.textContent = branding.loginDescription;
  }

  // 特性列表整块由配置驱动；没配就保留模板里的默认四条，而不是留一片空白。
  const features = document.getElementById('loginFeatures');
  if (features) {
    const list = (branding.loginFeatures || []).filter(Boolean);
    if (list.length > 0) {
      clear(features);
      for (const item of list) {
        features.appendChild(h('li', item));
      }
    }
  }

  const footer = document.getElementById('loginFooter');
  if (footer) {
    footer.textContent = branding.footerText || '';
    footer.hidden = !branding.footerText;
  }

  // ── 主题色：全站值交给 prefs 统一处理 ─────────────────────────────
  // 强调色有「全站（这里）」「本机（页面设置 → 外观）」两个来源，
  // 谁生效只在 prefs.applyAccent 里判断一次，这边只负责把全站值送过去。
  setBrandingAccent(branding.accentColor);

  // ── 玻璃强度 / 高光 / 登录页布局：加 data 属性，让 CSS 去响应 ─────────
  root.dataset.glass = branding.glassLevel || 'standard';
  root.dataset.shine = branding.enableShine === false ? 'off' : 'on';
  root.dataset.loginLayout = branding.loginLayout === 'centered' ? 'centered' : 'split';

  // ── 自定义 CSS ───────────────────────────────────────────────────
  // 外观需求千奇百怪，与其一个个加设置项，不如给一个受控的注入点
  //（长度上限与「变更进审计」在服务端做）。
  let customStyle = document.getElementById('customBrandingCss');
  if (branding.customCss) {
    if (!customStyle) {
      customStyle = document.createElement('style');
      customStyle.id = 'customBrandingCss';
      document.head.appendChild(customStyle);
    }

    customStyle.textContent = branding.customCss;
  } else if (customStyle) {
    customStyle.remove();
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
  if (isTotp) {
    startTotpCountdown();
  }
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
    login: [t('app.welcomeBack'), t('app.loginIntro')],
    register: [t('app.modeRegister'), t('app.modeRegisterHint')],
    apply: [t('app.modeApply'), t('app.modeApplyHint')],
    totp: [t('app.twoFactor'), t('app.twoFactorHint')],
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
    btn.setAttribute('aria-label', t('app.showPassword'));
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
      btn.setAttribute('aria-label', show ? t('app.hidePassword') : t('app.showPassword'));
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
      ? t('app.loginHintDefault', { hint: info.hint })
      : t('app.defaultAccountHint');
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
    toast('warn', t('app.changeInitialTitle'), t('app.changeInitialText'), 8000);
  }

  // 密码到期提醒（#28）：服务端在**登录应答**里直接给出可读提示（临近到期 / 已过期），
  // 与「初始密码」提示并列，进控制台就能看到；不阻止登录，提醒后由管理员自行安排时间。
  if (result.passwordWarning) {
    toast('warn', t('app.changePasswordNeeded'), result.passwordWarning, 12000);
  }
}

async function handleLogin(event) {
  event.preventDefault();

  const button = document.getElementById('loginSubmit');
  const errorBox = document.getElementById('loginError');
  const username = document.getElementById('loginUsername').value.trim();
  const password = document.getElementById('loginPassword').value;

  setButtonLoading(button, true, t('app.signingIn'));
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
      toast('info', t('app.needsTwoFactor'), t('app.needsTwoFactorText'));
      return;
    }

    await completeSignIn(result);
  } catch (err) {
    errorBox.textContent = err.message || t('app.loginFailed');
    errorBox.hidden = false;
  } finally {
    setButtonLoading(button, false, t('login.submit'));
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
    showLogin(t('app.verifyExpired'));
    return;
  }

  setButtonLoading(button, true, t('app.verifying'));
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
    errorBox.textContent = err.message || t('app.verifyFailed');
    errorBox.hidden = false;
  } finally {
    setButtonLoading(button, false, t('app.verifyAndSignIn'));
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

  setButtonLoading(button, true, t('app.registering'));
  errorBox.hidden = true;

  try {
    const result = await api('/admin/register', { method: 'POST', auth: false, body });
    document.getElementById('registerPassword').value = '';
    await completeSignIn(result);
    toast('ok', t('app.registered'), t('app.welcomeUser', { name: result.displayName || session.me?.username || t('app.newColleague') }));
  } catch (err) {
    errorBox.textContent = err.message || t('app.registerFailed');
    errorBox.hidden = false;
  } finally {
    setButtonLoading(button, false, t('app.registerAndSignIn'));
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

  setButtonLoading(button, true, t('app.submitting'));
  errorBox.hidden = true;

  try {
    await api('/admin/register-requests', { method: 'POST', auth: false, body });
    document.getElementById('applyPassword').value = '';
    document.getElementById('applyForm').hidden = true;
    document.getElementById('applyDone').hidden = false;
    toast('ok', t('app.requestSubmitted'), t('app.requestSubmittedHint'));
  } catch (err) {
    errorBox.textContent = err.message || t('app.submitFailed');
    errorBox.hidden = false;
  } finally {
    setButtonLoading(button, false, t('app.submitRequest'));
  }
}

// ────────────────────────────── 主界面 ──────────────────────────────

/**
 * 该不该把人送去引导页（#126）。
 *
 * <p>
 * 单独抽出来是为了**只有一处判断**：登录后与"已登录直接刷新"两条路径都走它。
 * 惰性 import 视图模块，避免把引导页塞进首屏包。
 * </p>
 */
async function shouldShowWelcome() {
  try {
    const { shouldShowWelcome: check } = await import('./views/welcome.js?v=142');
    return await check();
  } catch {
    // 视图模块加载失败（网络/缓存）时不要拦人。
    return false;
  }
}

async function showApp() {
  document.getElementById('loginView').hidden = true;
  document.getElementById('app').hidden = false;

  const me = session.me || {};

  // 先把账号里的布局偏好铺到本机（#40）：必须在下面路由渲染之前，
  // 否则会先闪一下默认布局再跳成自定义布局。未登录 / 无偏好时此调用是空操作。
  applyRemotePrefs(me.uiPreferences);

  document.getElementById('userName').textContent = me.displayName || me.username || t('app.adminRole');
  document.getElementById('userAvatar').textContent = (me.displayName || me.username || 'A').slice(0, 1).toUpperCase();

  renderNav();
  bindShellEvents();
  startPolling();

  // 首次使用引导（#126）：**实例级**判断，放在进管理台之前。
  // 读状态失败时 shouldShowWelcome() 返回 false——宁可少引导一次，也不能把人挡在外面。
  const alreadyWelcome = window.location.hash.startsWith('#/welcome');
  const needWelcome = !alreadyWelcome && await shouldShowWelcome();

  if (needWelcome) {
    window.location.hash = '#/welcome';
  } else if (!window.location.hash || window.location.hash === '#/') {
    window.location.hash = '#/dashboard';
  } else {
    await route();
  }

  // 新账号（或在设置里重置过引导的账号）第一次进来时放一遍新手引导，随时可跳过。
  if (me.onboardingDone === false) {
    const { startTour } = await import('./core/tour.js?v=142');
    startTour({
      onFinish: async (skipped) => {
        try {
          await api('/admin/onboarding', { method: 'POST' });
          session.me.onboardingDone = true;
        } catch { /* 记录失败也无妨，下次登录会再放一次 */ }
        if (!skipped) {
          toast('ok', t('app.tourDone'), t('app.tourDoneHint'));
        }
      },
    });
  }
}

/**
 * 渲染顶栏导航：每个分组是一个「触发器 + 下拉」。
 *
 * <para>下拉里的页面项**继续用 `.nav-item` 类名**——这不是偷懒，而是刻意的：
 * `visibleNavItems()`（Alt+N 跳页）、命令面板取页面清单、玻璃高光的事件委托
 * 全都按这个类名工作。换成 `.nav-page-item` 就要同步改三处，
 * 而三处各改一次正是「后来者漏改」这种 bug 的标准来源。</para>
 *
 * <para>分组标题不再单独占一行（`nav-group-label`），而是变成触发器按钮的文本：
 * 顶栏横向空间宝贵，一行放不下四组标题 + 全部页面。</para>
 */
function renderNav() {
  const nav = document.getElementById('navList');
  clear(nav);

  // 快捷键序号：只给「有权限、真的出现在导航里」的页面编号，与 Alt + 数字一一对应（#46）。
  // 序号统一由 pageHintMap() 算，顶栏下拉 / 二级侧栏 / 命令面板共用一份。
  const hints = pageHintMap();

  for (const group of NAV) {
    // 没有权限的页面直接不出现；整组都没权限时连这个分组都不出现。
    const items = group.items.filter((item) => can(item.perm));
    if (items.length === 0) continue;

    const dropdown = h('div.nav-dropdown');
    for (const item of items) {
      const hint = hints.get(item.key) || '';
      const iconEl = h('span.nav-icon');
      iconEl.appendChild(icon(item.icon, 16));
      dropdown.appendChild(h('button.nav-item', {
        type: 'button',
        title: hint ? `${item.label}（${hint}）` : item.label,
        dataset: { key: item.key, group: group.label, hash: item.hash },
        onClick: () => {
          // 点完就收起下拉：否则它会盖在刚打开的页面上。
          closeNavDropdowns();
          window.location.hash = item.hash;
        },
      },
        iconEl,
        h('span.nav-label', item.label),
        hint ? h('span.nav-hint', hint) : null,
      ));
    }

    // chevron 直接内联画，不走 icon()：ui.js 的图标表里没有下箭头，
    // 为一个 12px 的三角去扩图标表不划算（而且它只需要这一个形状）。
    const chevron = h('span.nav-chevron', {
      html: '<svg viewBox="0 0 12 12" width="12" height="12" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M2.5 4.5L6 8l3.5-3.5" /></svg>',
    });

    const trigger = h('button.nav-trigger', {
      type: 'button',
      'aria-haspopup': 'true',
      // 状态由 closeNavDropdowns / toggleNavDropdown 维护（#49）：读屏软件靠它说「这一组现在开着」。
      'aria-expanded': 'false',
      // 整组的页面用同一个 title：鼠标停住不动也能看到这组下有什么。
      title: items.map((i) => i.label).join(' · '),
      onClick: (event) => {
        event.stopPropagation();
        toggleNavDropdown(trigger.closest('.nav-group'));
      },
      // 键盘（#49 收尾）：下拉里的项是普通 button，能 Tab 进去；但「用方向键展开并进去」
      // 是菜单类控件的常规预期，没它键盘用户只能 Tab 一路绕过去。
      onKeyDown: (event) => {
        if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') {
          return;
        }

        event.preventDefault();
        const group = trigger.closest('.nav-group');
        closeNavDropdowns();
        group.classList.add('open');
        group.querySelector('.nav-trigger')?.setAttribute('aria-expanded', 'true');
        // 下拉是**过渡显示**的：刚加上 .open 时里面的项还没进入可聚焦状态，focus() 会被静默丢掉。
        // 所以等一帧后试，没成功再退避重试几次（约半秒，覆盖过渡时长）。
        const focusWithRetry = (el, attempts) => {
          if (!el) {
            return;
          }

          el.focus();
          if (document.activeElement === el || attempts <= 0) {
            return;
          }

          setTimeout(() => focusWithRetry(el, attempts - 1), 60);
        };

        requestAnimationFrame(() => {
          const items = [...group.querySelectorAll('.nav-item')];
          if (items.length > 0) {
            focusWithRetry(event.key === 'ArrowDown' ? items[0] : items[items.length - 1], 8);
          }
        });
      },
    }, group.label, chevron);

    nav.appendChild(h('div.nav-group', { dataset: { group: group.label } }, trigger, dropdown));
  }

  syncActiveNavGroup();
  renderSideNav();
}

/**
 * 渲染**二级**侧边栏：当前一级分组下的页面。
 *
 * <para>为什么二级只列「当前分组」而不是全部页面：侧边栏的价值是
 * 「在这个场景里还能去哪」，而不是把顶栏再抄一遍。全列出来等于两个导航做同一件事，
 * 用户还得判断该看哪个。</para>
 *
 * <para>侧边栏**始终渲染**（窄屏除外）。早先「分组里只有一个页面就把整条侧栏
 * 隐藏」的做法有两个后果：内容区宽度随页面跳变（设备管理页会突然变宽），
 * 以及页面标题、连接状态、版本号**跟着一起消失**——它们是环境信息，
 * 不该因为「这一组只有一个页面」就看不到。</para>
 */
function renderSideNav() {
  const host = document.getElementById('sideNavList');
  if (!host) return;

  clear(host);

  // 注意用 activeNavKey() 而不是「顶栏当前展开的分组」：通过旧链接别名、
  // 命令面板或档案编辑器这类子页面进来时，展开的组和当前页可能不是一个。
  const group = NAV.find((g) => g.items.some((i) => i.key === activeNavKey()));
  const list = (group?.items || []).filter((item) => can(item.perm));
  const hints = pageHintMap();

  host.appendChild(h('div.side-nav-title', group ? group.label : t('app.navigation')));

  for (const item of list) {
    const hint = hints.get(item.key) || '';
    const iconEl = h('span.nav-icon');
    iconEl.appendChild(icon(item.icon, 16));
    host.appendChild(h('button.nav-item', {
      type: 'button',
      // 悬停提示带上 Alt + 数字：快捷键帮助里写着「侧边栏每个页面也带快捷键提示」，
      // 而之前只有顶栏下拉带、侧边栏没有——那句话是空头承诺。
      title: hint ? `${item.label}（${hint}）` : item.label,
      dataset: { key: item.key, group: group ? group.label : '', hash: item.hash },
      onClick: () => {
        window.location.hash = item.hash;
      },
    },
      iconEl,
      h('span.nav-label', item.label),
      hint ? h('span.nav-hint', hint) : null,
    ));
  }

  syncSideNavActive();
}

/** ── 以下是二级导航（侧边栏）相关 ───────────────────────────────── */

/**
 * 不在导航里的**子页面**用它所属的导航页来高亮。
 *
 * <para>典型是配置档案编辑器 `#/profiles/{id}`：它的路由 key 是 `profileEditor`，
 * 而导航里只有 `profiles`。此前这里直接用 `runtime.currentKey` 去找所属分组，
 * 编辑档案时**顶栏分组与侧栏项会全部失去高亮**——用户以为导航坏了。</para>
 */
const PARENT_NAV_KEY = {
  profileEditor: 'profiles',
};

/** 当前页在导航里的「身份」：子页面归到它所属的导航页。 */
function activeNavKey() {
  return PARENT_NAV_KEY[runtime.currentKey] || runtime.currentKey;
}

/**
 * 按导航顺序给「有权限的页面」编号（跨分组连续，Alt + 数字按它跳页）。
 *
 * <para>顶栏下拉、二级侧栏、命令面板**共用这一份**编号。三处各编一次的话，
 * 只要有一处漏改就会「提示写着 Alt+7、按下去跳到别处」。</para>
 */
function indexedPages() {
  let index = 0;
  return NAV.flatMap((group) => group.items
    .filter((item) => can(item.perm))
    .map((item) => {
      index++;
      return {
        key: item.key,
        label: item.label,
        hash: item.hash,
        groupLabel: group.label,
        index,
        // 只有 1..9 有对应按键；第 10 个之后不显示提示（写了也按不出来）。
        hint: index <= 9 ? shortcutHint(index) : '',
      };
    }));
}

/** key → 「Alt + N」提示语，供顶栏下拉与侧栏共用。 */
function pageHintMap() {
  return new Map(indexedPages().map((page) => [page.key, page.hint]));
}

/** 二级侧边栏的高亮跟当前页走。 */
function syncSideNavActive() {
  const current = activeNavKey();
  document.querySelectorAll('#sideNavList .nav-item').forEach((el) => {
    el.classList.toggle('active', el.dataset.key === current);
  });
}

/**
 * 供命令面板使用的页面清单（已按权限过滤）。
 * <para>命令面板空闲时要列出「所有有权限的页面」，不能自己再过滤一遍 ——
 * 过滤规则只应该有一处（NAV + can()），两处各写一份迟早会不一致。</para>
 */
function navPagesForPalette() {
  // 顶栏下拉和侧边栏**都**渲染 .nav-item，按 DOM 查会拿两份、序号错位——
  // 所以 Alt+N 与命令面板都必须走这份清单，而不是 visibleNavItems()。
  return indexedPages().map((page) => ({
    key: page.key,
    label: page.label,
    hash: page.hash,
    groupLabel: page.groupLabel,
    keys: page.hint ? [page.hint] : [],
    index: page.index,
  }));
}

/** 收起所有分组下拉（点外部、Esc、选中页面后都会用到）。 */
function closeNavDropdowns() {
  document.querySelectorAll('.nav-group.open').forEach((el) => {
    el.classList.remove('open');
    el.querySelector('.nav-trigger')?.setAttribute('aria-expanded', 'false');
  });
}

// 分组下拉的键盘导航（#49 收尾）：Esc 收起并把焦点还给触发器；方向键在项之间移动。
// 用一个全局监听就够了——同一时刻最多只有一个分组是展开的（toggleNavDropdown 保证）。
document.addEventListener('keydown', (event) => {
  const open = document.querySelector('.nav-group.open');
  if (!open) {
    return;
  }

  if (event.key === 'Escape') {
    event.preventDefault();
    const trigger = open.querySelector('.nav-trigger');
    closeNavDropdowns();
    trigger?.focus();
    return;
  }

  if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') {
    return;
  }

  const items = [...open.querySelectorAll('.nav-item')];
  if (items.length === 0) {
    return;
  }

  event.preventDefault();
  const current = items.indexOf(document.activeElement);
  const next = event.key === 'ArrowDown'
    ? (current < 0 ? 0 : (current + 1) % items.length)
    : (current <= 0 ? items.length - 1 : current - 1);
  items[next].focus();
});

/** 切换某个分组的下拉：自己开着就收，开着别的就换成自己（同时只开一个）。 */
function toggleNavDropdown(group) {
  if (!group) return;
  const isOpen = group.classList.contains('open');
  closeNavDropdowns();
  if (!isOpen) {
    group.classList.add('open');
    group.querySelector('.nav-trigger')?.setAttribute('aria-expanded', 'true');
  }
}

/**
 * 给「当前页所在的分组」打上 active。
 *
 * <para>单独抽出来，是因为它有两个调用时机：首屏渲染完（renderNav 末尾）
 * 和每次路由切换后。后者漏掉的话，切换页面时顶栏的高亮会一直停在初始分组上——
 * 这是导航改造最容易出的 bug，因为它「第一次打开时是对的」。</para>
 */
function syncActiveNavGroup() {
  // 用 runtime.currentKey（经 activeNavKey 归到导航页）而不是从 DOM 反查：
  // 它是路由的唯一真相来源，页面还没渲染完时 DOM 上可能还没有对应的 .nav-item。
  const current = activeNavKey();
  if (!current) return;

  const groupLabel = NAV.find((g) => g.items.some((i) => i.key === current))?.label;
  document.querySelectorAll('.nav-group').forEach((el) => {
    el.classList.toggle('active', !!groupLabel && el.dataset.group === groupLabel);
  });
}

function bindShellEvents() {
  if (bindShellEvents.bound) return;
  bindShellEvents.bound = true;

  // 键盘快捷键：Ctrl + K 命令面板、Alt + 数字跳页、/ 聚焦搜索、? 打开帮助（#46 / #50 / #41）
  initShortcuts();
  // 把「可见页面清单」交给快捷键模块，供 Ctrl+K 的命令面板列页面用。
  setPageLister(navPagesForPalette);

  // 液态玻璃的鼠标跟随高光（事件委托，见 core/glass.js）。
  initGlassHighlight();
  document.getElementById('searchBtn').addEventListener('click', () => openSearch(navPagesForPalette()));

  document.getElementById('refreshBtn').addEventListener('click', () => route());

  // 导航折叠 / 展开 + 移动端抽屉（顶栏导航，见 core 的 topbar.js）。
  // 「折叠」在顶栏形态下的含义是**隐藏分组导航区**，只留品牌与右侧操作——
  // 沿用 prefs 里的 sidebar-collapsed 键，这样老用户升级后 remembered 状态直接生效。
  initTopbar({
    isCollapsed: getSidebarCollapsed,
    setCollapsed: (next) => {
      setSidebarCollapsed(next);
      document.getElementById('app')?.classList.toggle('nav-collapsed', next);
    },
  });
  // 恢复上次的折叠状态（initTopbar 只管事件，不负责初始状态）。
  if (getSidebarCollapsed()) {
    document.getElementById('app')?.classList.add('nav-collapsed');
  }

  // 主题 / 密度 / 字体 / 圆角 / 强调色**不在这里**：整套外观设置已挪到
  // 「系统 → 页面设置」（views/appearance.js）。顶栏只留全局动作（搜索、刷新、账号），
  // 这也顺带把顶栏右侧腾空，窄窗口不再互相挤压。
  const userBtn = document.getElementById('userBtn');
  const dropdown = document.getElementById('userDropdown');

  /**
   * 账号菜单的键盘支持（#49 收尾）。
   *
   * 屏幕阅读器 / 键盘用户面对的是一个 `aria-haspopup="menu"` 的按钮：
   * 打开方式除了点击还有 Enter / 空格 / 方向键，菜单里上下移动、Esc 关闭并把焦点还给按钮。
   * 「焦点归还」是键盘可用性的底线——菜单一关焦点若丢回文档开头，用户就得从头 Tab 一遍。
   */
  const userMenuItems = () => [...dropdown.querySelectorAll('[role="menuitem"]')];

  const setUserMenuOpen = (open) => {
    dropdown.hidden = !open;
    userBtn.setAttribute('aria-expanded', open ? 'true' : 'false');
  };

  const openUserMenu = (focusFirst = true) => {
    setUserMenuOpen(true);
    if (focusFirst) {
      userMenuItems()[0]?.focus();
    }
  };

  const closeUserMenu = (restoreFocus = false) => {
    setUserMenuOpen(false);
    if (restoreFocus) {
      userBtn.focus();
    }
  };

  userBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    if (dropdown.hidden) {
      // 鼠标点开时**不抢焦点**（否则第一次点击会同时触发按钮的激活，行为很怪）；
      // 键盘操作才把焦点移进菜单。
      openUserMenu(false);
    } else {
      closeUserMenu();
    }
  });

  // Enter / 空格由按钮自己触发 click（上面已处理）；方向键直接开菜单并进第一项。
  userBtn.addEventListener('keydown', (e) => {
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      const items = userMenuItems();
      if (items.length === 0) {
        return;
      }

      openUserMenu(false);
      (e.key === 'ArrowDown' ? items[0] : items[items.length - 1]).focus();
    }
  });

  dropdown.addEventListener('keydown', (e) => {
    const items = userMenuItems();
    if (items.length === 0) {
      return;
    }

    const current = items.indexOf(document.activeElement);
    if (e.key === 'Escape') {
      e.preventDefault();
      closeUserMenu(true);
      return;
    }

    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      const next = e.key === 'ArrowDown'
        ? (current < 0 ? 0 : (current + 1) % items.length)
        : (current <= 0 ? items.length - 1 : current - 1);
      items[next].focus();
      return;
    }

    if (e.key === 'Home') {
      e.preventDefault();
      items[0].focus();
    } else if (e.key === 'End') {
      e.preventDefault();
      items[items.length - 1].focus();
    }
  });

  document.addEventListener('click', () => closeUserMenu());

  dropdown.addEventListener('click', (e) => {
    const action = e.target.dataset?.action;
    closeUserMenu();
    if (action === 'logout') {
      api('/admin/logout', { method: 'POST' }).catch(() => {});
      saveToken('');
      session.me = null;
      stopPolling();
      showLogin(t('app.signedOut'));
    } else if (action === 'change-password') {
      window.location.hash = '#/accounts';
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

  // 旧地址经别名表兜底（如「分组管理」已并入设备管理）。
  const navKey = segments[0] || 'dashboard';
  const key = ROUTE_ALIASES[navKey] || navKey;
  return { key, module: ROUTES[key] || ROUTES.dashboard, params };
}

async function route() {
  const { key, module, params } = parseHash();
  runtime.currentKey = key;

  // 高亮用 activeNavKey()：档案编辑器（#/profiles/{id}）这类子页面要归到
  // 它所属的导航页上，否则进编辑器之后导航会整块失去高亮。
  const activeKey = activeNavKey();
  for (const button of document.querySelectorAll('.nav-item')) {
    button.classList.toggle('active', button.dataset.key === activeKey);
  }

  // 分组高亮跟着一起换（顶栏改造后新增）：上面那句只管下拉里的页面项，
  // 而「当前在哪个组」要看触发器上的主色与指示条。
  syncActiveNavGroup();
  // 二级侧边栏跟着当前页重新渲染：跨分组跳页时它要换成另一组的页面。
  renderSideNav();

  const content = document.getElementById('content');

  // 无权访问的页面直接给提示，不要先打一堆注定 403 的接口。
  if (!can(ROUTE_PERMS[key])) {
    const label = NAV.flatMap((g) => g.items).find((i) => i.key === key)?.label || t('app.thisPage');
    document.getElementById('pageTitle').textContent = label;
    document.getElementById('pageSubtitle').textContent = t('app.forbiddenTitle');
    clear(content);
    content.appendChild(h('div.card',
      h('div.notice.notice-warn', { style: { margin: 0 } },
        h('span.notice-icon', '!'),
        h('div',
          h('strong', t('app.forbiddenText')),
          h('div', t('app.forbiddenHint'))),
      ),
    ));
    return;
  }

  clear(content);
  content.appendChild(h('div', { style: { padding: '40px', textAlign: 'center', color: 'var(--text-faint)' } },
    h('span.spinner')));

  try {
    const view = await module();

    document.getElementById('pageTitle').textContent = view.meta?.title || t('app.defaultCenterName');
    document.getElementById('pageSubtitle').textContent = view.meta?.subtitle || '';

    await view.render(content, params);
    updateRevisionChip();
  } catch (err) {
    clear(content);
    content.appendChild(h('div.card',
      h('div.notice.notice-danger', { style: { margin: 0 } },
        h('span.notice-icon', '!'),
        h('div', h('strong', t('app.pageLoadFailed')), h('div', err.message || String(err))),
      ),
    ));
    toastError(err, t('app.loadFailed'));
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
    textEl.textContent = t('app.onlineCount', { online: info.onlineDeviceCount, total: info.deviceCount });
    document.getElementById('sidebarVersion').textContent =
      t('app.versionLine', { version: info.version, protocol: info.protocolVersion });
    updateRevisionChip();
  } catch {
    stateEl.className = 'conn-state offline';
    textEl.textContent = t('app.connectionLost');
  }
}

function updateRevisionChip() {
  const info = session.serverInfo;
  if (info) {
    document.getElementById('revisionChip').textContent = t('topbar.revision', { revision: info.revision });
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
