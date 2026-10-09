/**
 * 管理端界面冒烟测试（#40 起用）。
 *
 * 为什么需要它：语法检查（node --check）与构建都发现不了「按钮点了没反应」这类问题——
 * 历史上「自定义仪表盘」与设备表「列设置」两个面板就因为 `append()` 收到单个节点抛错而一直打不开，
 * 错误只留在浏览器控制台，界面表现就是沉默。这个脚本用真实浏览器点一遍并断言结果。
 *
 * 前置：
 *   1) 本地 A 端已启动（默认 http://127.0.0.1:29800），账号 admin / admin123；
 *   2) Node 18+ 且装过 puppeteer-core（在仓库根执行一次 `npm install puppeteer-core`）；
 *   3) 系统里有 Edge（默认路径见下，可用环境变量覆盖）。
 *
 * 用法：
 *   node sh/ui-smoke.js
 *   SMOKE_BASE=http://127.0.0.1:29800 SMOKE_BROWSER="C:\\...\\msedge.exe" node sh/ui-smoke.js
 *
 * 覆盖的断言（77 项）：
 *   1) 起点归零：清空账号偏好与本机布局缓存 → 仪表盘回到默认布局
 *   2) 「自定义仪表盘」面板能打开，含「统计卡片 + 页面模块」两组、共 10 项
 *   3) 面板里关掉「最近事件」→ 页面立即不再渲染该模块
 *   4) 偏好写进账号（/admin/me 的 uiPreferences）
 *   5) 清空 localStorage 重新登录 → 布局仍从账号恢复（模拟换浏览器）
 *   6) 设备表切「列表视图」→「列设置」面板能打开 → 取消一列后表格该列消失
 *   7) 键盘快捷键 Alt + 3 跳到第 3 个导航页（#46）
 *   8) `?` 打开快捷键帮助、Esc 关闭（#50）
 *   9) `/` 聚焦设备页搜索框（#46）
 *  10) 课表编辑器切「紧凑」→ body.sched-compact 生效且偏好存本机（#54）
 *  11) Ctrl + K 打开全局搜索面板（#41）
 *  12) 搜索「高一」返回结果
 *  13) Enter 跳转到设备页并预填搜索框
 *  14) 顶栏「搜索」按钮同样能打开面板
 *  15) Esc 关闭搜索面板
 *  16) 每个错误码都有「怎么办」文案（#51）
 *  17) 公共兜底出口的提示同时含「服务端原文 + 怎么办」
 *
 * 注意：脚本会改动演示账号的界面偏好（跑完停在「最近事件 / 最近心跳」隐藏的状态），
 *       可在「自定义仪表盘 / 列设置」里勾回来。
 */
const puppeteer = require('puppeteer-core');

const EDGE = process.env.SMOKE_BROWSER
  || 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const BASE = process.env.SMOKE_BASE || 'http://127.0.0.1:29800';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const results = [];
function check(name, ok, extra = '') {
  results.push(`${ok ? 'PASS' : 'FAIL'}  ${name}${extra ? '  (' + extra + ')' : ''}`);
}

(async () => {
  const browser = await puppeteer.launch({
    executablePath: EDGE,
    headless: 'new',
    args: ['--disable-gpu', '--hide-scrollbars'],
  });
  const page = await browser.newPage();
  await page.setViewport({ width: 1600, height: 1000 });
  page.on('pageerror', (e) => console.log('PAGE_EXCEPTION:', e.message));
  page.on('console', (m) => { if (m.type() === 'error') console.log('PAGE_ERROR:', m.text()); });

  await page.goto(`${BASE}/`, { waitUntil: 'networkidle2' });
  await page.waitForSelector('#loginUsername', { timeout: 20000 });
  await page.type('#loginUsername', 'admin');
  await page.type('#loginPassword', 'admin123');
  await page.click('#loginSubmit');
  await page.waitForFunction(() => document.getElementById('app')?.hidden === false, { timeout: 30000 });
  await sleep(2500);
  console.log('LOGGED_IN');

  // 0) 起点归零：清空账号偏好 + 清掉本地布局缓存
  //    （浏览器 profile 可能被复用，只清云端会让上次的本地布局变成「起点」）
  const cleared = await page.evaluate(async () => {
    const token = localStorage.getItem('controlhub.admin.token');
    const r = await fetch('/api/v1/admin/ui-preferences', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json', Authorization: 'Bearer ' + token },
      body: JSON.stringify({ layouts: {} }),
    });
    for (const key of Object.keys(localStorage)) {
      if (key.startsWith('controlhub.ui.layout')) localStorage.removeItem(key);
    }
    localStorage.removeItem('controlhub.ui.layoutSyncPending');
    return (await r.json()).ok;
  });
  check('清空账号偏好与本地布局缓存（起点为默认布局）', cleared === true);
  await page.reload({ waitUntil: 'networkidle2' });
  await sleep(2500);

  // 1) 默认布局下「最近事件」应该在页面上
  const before = await page.evaluate(() => document.body.innerText.includes('最近事件'));
  check('默认布局包含「最近事件」模块', before === true);

  // 2) 点「自定义仪表盘」→ 面板应打开
  const opened = await page.evaluate(() => {
    const btn = [...document.querySelectorAll('button')].find((b) => b.textContent.trim() === '自定义仪表盘');
    if (!btn) return 'no-button';
    btn.click();
    return document.getElementById('modalHost').children.length > 0 ? 'opened' : 'not-opened';
  });
  check('「自定义仪表盘」面板能打开（append 容错生效）', opened === 'opened', opened);

  // 3) 面板里应有两组（统计卡片 / 页面模块）
  const groups = await page.evaluate(() => ({
    sections: [...document.querySelectorAll('.config-section-title')].map((e) => e.textContent.trim()),
    items: document.querySelectorAll('.config-item').length,
  }));
  check('面板含「统计卡片 + 页面模块」两组', groups.sections.length === 2, groups.sections.join(' / '));
  check('面板条目数 = 6 统计 + 4 模块', groups.items === 10, 'items=' + groups.items);

  // 4) 取消勾选「最近事件」→ 页面不再渲染
  const toggled = await page.evaluate(() => {
    const item = [...document.querySelectorAll('.config-item')].find((el) => el.textContent.includes('最近事件'));
    if (!item) return 'no-item';
    const cb = item.querySelector('input[type=checkbox]');
    if (!cb) return 'no-checkbox';
    cb.click();
    return 'toggled';
  });
  check('取消勾选「最近事件」', toggled === 'toggled', toggled);

  // 5) 点「完成」关闭面板并重渲染
  await page.evaluate(() => {
    const btn = [...document.querySelectorAll('#modalHost button')].find((b) => b.textContent.trim() === '完成');
    if (btn) btn.click();
  });
  await sleep(2500);
  const afterHide = await page.evaluate(() => document.body.innerText.includes('最近事件'));
  check('页面上「最近事件」已隐藏', afterHide === false);

  // 6) 偏好已写入账号
  const prefs = await page.evaluate(async () => {
    const token = localStorage.getItem('controlhub.admin.token');
    const r = await fetch('/api/v1/admin/me', { headers: { Authorization: 'Bearer ' + token } });
    return (await r.json()).data.uiPreferences;
  });
  const cards = prefs?.layouts?.['dashboard.cards'] || [];
  const eventsItem = cards.find((i) => i.key === 'events');
  check('偏好已同步到账号（events.enabled=false）', eventsItem?.enabled === false,
    JSON.stringify(cards.map((i) => i.key + ':' + i.enabled)));

  // 7) 清空本地缓存 → 模拟换浏览器 / 换设备：布局应从账号恢复
  await page.evaluate(() => localStorage.clear());
  await page.goto(`${BASE}/`, { waitUntil: 'networkidle2' });
  await page.waitForSelector('#loginUsername', { timeout: 20000 });
  await page.type('#loginUsername', 'admin');
  await page.type('#loginPassword', 'admin123');
  await page.click('#loginSubmit');
  await page.waitForFunction(() => document.getElementById('app')?.hidden === false, { timeout: 30000 });
  await sleep(2500);
  const restored = await page.evaluate(() => document.body.innerText.includes('最近事件'));
  check('清空本地缓存后重新登录：布局仍从账号恢复（「最近事件」保持隐藏）', restored === false);

  // 8) 设备表「列设置」面板同样能打开（同一个 append 缺陷的另一个受害处）
  //    注意：「列设置」按钮只在列表视图下出现，先切视图。
  await page.goto(`${BASE}/#/devices`, { waitUntil: 'networkidle2' });
  await sleep(3000);
  const switched = await page.evaluate(() => {
    const btn = [...document.querySelectorAll('button')].find((b) => b.textContent.trim() === '列表视图');
    if (!btn) return false;
    btn.click();
    return true;
  });
  check('切到设备列表视图', switched === true);
  await sleep(1800);
  const colPanel = await page.evaluate(() => {
    const btn = [...document.querySelectorAll('button')].find((b) => b.textContent.trim() === '列设置');
    if (!btn) return 'no-button';
    btn.click();
    return document.getElementById('modalHost').children.length > 0 ? 'opened' : 'not-opened';
  });
  check('设备表「列设置」面板能打开（append 容错）', colPanel === 'opened', colPanel);

  // 9) 列设置里勾掉一列 → 表格不再显示该列
  const colToggled = await page.evaluate(() => {
    const item = [...document.querySelectorAll('.config-item')].find((el) => el.textContent.trim().startsWith('最近心跳'));
    if (!item) return 'no-item:' + [...document.querySelectorAll('.config-item')].map((e) => e.textContent.trim()).join(',');
    item.querySelector('input[type=checkbox]').click();
    return 'toggled';
  });
  check('取消勾选表格列「最近心跳」', colToggled === 'toggled', colToggled);
  await page.evaluate(() => {
    const btn = [...document.querySelectorAll('#modalHost button')].find((b) => b.textContent.trim() === '完成');
    if (btn) btn.click();
  });
  await sleep(2000);
  const colHidden = await page.evaluate(() => !document.body.innerText.includes('最近心跳'));
  check('表格里「最近心跳」列已消失', colHidden === true);

  // 10) 键盘快捷键：Alt + 3 → 跳到第 3 个导航页（#46）
  await page.evaluate(() => document.activeElement && document.activeElement.blur());
  await page.keyboard.down('Alt');
  await page.keyboard.press('3');
  await page.keyboard.up('Alt');
  await sleep(1600);
  const navThird = await page.evaluate(() => document.querySelectorAll('.nav-item')[2]?.dataset.key);
  const hashAfterAlt = await page.evaluate(() => location.hash);
  check('Alt + 3 跳到第 3 个导航页', hashAfterAlt === `#/${navThird}`, `${hashAfterAlt}（期望 #/${navThird}）`);

  // 11) ? 打开快捷键帮助（#50）
  await page.keyboard.press('?');
  await sleep(900);
  const helpOpen = await page.evaluate(() => {
    const host = document.getElementById('modalHost');
    return !!host && host.hidden === false && host.textContent.includes('键盘快捷键');
  });
  check('? 打开快捷键帮助面板', helpOpen === true);

  // 12) Esc 关闭帮助
  await page.keyboard.press('Escape');
  await sleep(700);
  const helpClosed = await page.evaluate(() => document.getElementById('modalHost')?.hidden === true);
  check('Esc 关闭帮助面板', helpClosed === true);

  // 13) / 聚焦设备页搜索框
  await page.goto(`${BASE}/#/devices`, { waitUntil: 'networkidle2' });
  await sleep(2600);
  await page.keyboard.press('/');
  await sleep(700);
  const focused = await page.evaluate(() => {
    const el = document.activeElement;
    if (!el) return 'none';
    return el.classList.contains('toolbar-search') ? 'search' : el.tagName + '.' + el.className;
  });
  check('/ 聚焦设备页搜索框', focused === 'search', focused);

  // 14) 课表视图缩放（#54）：编辑器里切「紧凑」
  await page.goto(`${BASE}/#/profiles`, { waitUntil: 'networkidle2' });
  await page.waitForFunction(() => document.body.innerText.includes('示例档案'), { timeout: 20000 });
  await sleep(1200);
  await page.evaluate(() => {
    const nodes = [...document.querySelectorAll('div, tr, li')];
    const card = nodes.reverse().find((n) => n.textContent.includes('示例档案') && n.querySelector('button'));
    const btn = card && [...card.querySelectorAll('button')].find((b) => b.textContent.trim() === '编辑内容');
    if (btn) btn.click();
  });
  await sleep(4500);
  const scaleClicked = await page.evaluate(() => {
    const btn = document.querySelector('.scale-switch .segmented-item[data-scale="compact"]');
    if (!btn) return 'no-switch';
    btn.click();
    return 'clicked';
  });
  check('编辑器工具栏有「紧凑 / 宽松」切换', scaleClicked === 'clicked', scaleClicked);
  await sleep(700);
  const scaleState = await page.evaluate(() => ({
    compact: document.body.classList.contains('sched-compact'),
    saved: localStorage.getItem('controlhub.ui.schedScale'),
    active: [...document.querySelectorAll('.scale-switch .segmented-item')]
      .filter((b) => b.classList.contains('active')).map((b) => b.dataset.scale).join(','),
  }));
  check('切「紧凑」后 body.sched-compact 生效且偏好存本机',
    scaleState.compact === true && String(scaleState.saved).includes('compact'),
    JSON.stringify(scaleState));

  // 11) 全局搜索（#41）：Ctrl + K 打开 → 搜到设备 → Enter 跳转并预填搜索框
  await page.goto(`${BASE}/#/dashboard`, { waitUntil: 'networkidle2' });
  await sleep(2200);
  await page.evaluate(() => document.activeElement && document.activeElement.blur());
  await page.keyboard.down('Control');
  await page.keyboard.press('k');
  await page.keyboard.up('Control');
  await sleep(900);
  const searchOpen = await page.evaluate(() => {
    const host = document.getElementById('modalHost');
    return !!host && host.hidden === false && !!document.querySelector('.search-input');
  });
  check('Ctrl + K 打开全局搜索面板', searchOpen === true);

  await page.type('.search-input', '高一');
  await sleep(1300);
  const searchHits = await page.evaluate(() => ({
    count: document.querySelectorAll('.search-item').length,
    first: document.querySelector('.search-item .search-title')?.textContent || '',
  }));
  check('搜索「高一」返回结果', searchHits.count > 0,
    `count=${searchHits.count} first=${searchHits.first}`);

  await page.keyboard.press('Enter');
  await sleep(2500);
  const afterJump = await page.evaluate(() => ({
    hash: location.hash,
    keyword: document.querySelector('.toolbar-search')?.value || '',
  }));
  check('Enter 跳转到设备页并预填搜索框',
    afterJump.hash.startsWith('#/devices') && afterJump.keyword.length > 0,
    JSON.stringify(afterJump));

  await page.click('#searchBtn');
  await sleep(800);
  const viaButton = await page.evaluate(() => !!document.querySelector('.search-input'));
  check('顶栏「搜索」按钮同样能打开面板', viaButton === true);
  await page.keyboard.press('Escape');
  await sleep(500);
  const closed = await page.evaluate(() => document.getElementById('modalHost')?.hidden === true);
  check('Esc 关闭搜索面板', closed === true);

  // 12) 错误提示可操作化（#51）：每个错误码都有「怎么办」，且公共兜底出口真的会带上它
  const hintCoverage = await page.evaluate(async () => {
    const { describeError } = await import('/js/core/error-hints.js?v=48');
    const codes = [
      'AUTH_REQUIRED', 'AUTH_INVALID', 'DEVICE_UNKNOWN', 'DEVICE_REVOKED',
      'ENROLL_CODE_INVALID', 'ENROLL_CODE_EXPIRED', 'PERMISSION_DENIED', 'VALIDATION_FAILED',
      'NOT_FOUND', 'CONFLICT', 'RATE_LIMITED', 'IP_NOT_ALLOWED',
      'ACCOUNT_LOCKED', 'PROTOCOL_UNSUPPORTED', 'INTERNAL', 'NETWORK', 'BAD_RESPONSE',
    ];
    const missing = codes.filter((c) => !describeError({ code: c, message: 'x' }).hint);
    return { total: codes.length, missing };
  });
  check('每个错误码都有「怎么办」文案', hintCoverage.missing.length === 0,
    `${hintCoverage.total} 个码，缺失：${hintCoverage.missing.join(',') || '无'}`);

  // 走 ui.js 的公共兜底（guard）：这是大量操作的实际出口，失败时除了服务端原文还必须带建议
  await page.evaluate(async () => {
    const { api } = await import('/js/core/api.js?v=48');
    const { guard } = await import('/js/core/ui.js?v=48');
    guard('删除测试', () => api('/admin/profiles/not-exist-id', { method: 'DELETE' }));
  });
  await sleep(1300);
  const toastText = await page.evaluate(() => [...document.querySelectorAll('#toastHost .toast')]
    .map((el) => el.textContent).join(' | '));
  check('公共兜底出口的提示同时含「服务端原文 + 怎么办」',
    toastText.includes('目标不存在') && toastText.includes('刷新页面'),
    toastText.slice(0, 200));

  // 13) 待执行指令队列（#3）：设备详情能看到排队指令、倒计时，以及清空后的空状态文案
  const queued = await page.evaluate(async () => {
    const { api } = await import('/js/core/api.js?v=49');
    const devices = await api('/admin/devices');
    // 刻意**不用 devices[0]**：列表首位会随新建/停用的设备变化，
    // 别处（例如端到端脚本）造一台测试设备就会把这条断言带偏——
    // 而停用设备本来就不该有待执行指令区块。选一台确定在用的设备。
    const dev = devices.find((d) => !d.revoked) || devices[0];

    // 清场：取消遗留的排队指令，保证断言从确定状态开始。
    const old = await api(`/admin/devices/${dev.id}/commands/queue`);
    for (const row of old) {
      await api(`/admin/devices/commands/${row.id}`, { method: 'DELETE' });
    }

    const cmd = await api(`/admin/devices/${dev.id}/command`, {
      method: 'POST',
      body: { kind: 'shell', payload: '{}', delaySeconds: 120 },
    });
    return { commandId: cmd.id, deviceName: dev.name };
  });
  check('准备好一条排队指令（delaySeconds=120）', !!queued.commandId, queued.deviceName);

  /** 在设备列表里按名称找到那一行，点它的第一个操作按钮（「编辑」即打开详情）。 */
  const openDeviceRow = async (name) => {
    await page.evaluate((deviceName) => {
      const row = [...document.querySelectorAll('table tbody tr')]
        .find((tr) => tr.textContent.includes(deviceName));
      row?.querySelector('.actions button')?.click();
    }, name);
    await sleep(1300);
  };

  await openDeviceRow(queued.deviceName);
  const queueUi = await page.evaluate(() => {
    const modal = document.getElementById('modalHost');
    const text = modal?.textContent || '';
    return {
      hasSection: text.includes('待执行指令'),
      items: modal ? modal.querySelectorAll('.config-panel .config-item').length : 0,
      countdown: /后生效|还剩/.test(text),
      cancel: [...(modal ? modal.querySelectorAll('button') : [])]
        .some((b) => b.textContent.trim() === '取消'),
    };
  });
  check('设备详情出现「待执行指令」区块', queueUi.hasSection === true);
  check('队列显示排队指令 + 倒计时 + 取消按钮',
    queueUi.items >= 1 && queueUi.countdown && queueUi.cancel,
    `items=${queueUi.items} countdown=${queueUi.countdown} cancel=${queueUi.cancel}`);

  // 取消这条 → 重开详情应显示空队列文案
  await page.keyboard.press('Escape');
  await sleep(500);
  await page.evaluate(async (id) => {
    const { api } = await import('/js/core/api.js?v=49');
    await api(`/admin/devices/commands/${id}`, { method: 'DELETE' });
  }, queued.commandId);
  await openDeviceRow(queued.deviceName);
  const emptyUi = await page.evaluate(() => document.getElementById('modalHost')?.textContent || '');
  check('队列清空后显示「队列是空的」', emptyUi.includes('队列是空的'), emptyUi.slice(0, 110));
  await page.keyboard.press('Escape');
  await sleep(400);

  // 14) 操作历史时间线（#42）：设备详情能看到「谁在什么时候改了什么」
  await openDeviceRow(queued.deviceName);
  const timeline = await page.evaluate(() => {
    const text = document.getElementById('modalHost')?.textContent || '';
    return { text, hasSection: text.includes('操作历史') };
  });
  check('设备详情出现「操作历史」区块', timeline.hasSection === true);
  check('时间线用中文动作标签（含刚下发的指令记录）',
    timeline.text.includes('设备 · 下发指令'),
    timeline.text.replace(/\s+/g, ' ').slice(0, 150));
  await page.keyboard.press('Escape');
  await sleep(400);

  // 15) 课表冲突检测（#44）：档案页能打开检测面板，并说明「检查了什么」
  await page.goto(`${BASE}/#/profiles`, { waitUntil: 'networkidle2' });
  await sleep(2200);
  const conflictOpened = await page.evaluate(() => {
    const btn = [...document.querySelectorAll('button')]
      .find((b) => b.textContent.trim() === '冲突检测');
    if (!btn) return false;
    btn.click();
    return true;
  });
  check('档案页有「冲突检测」按钮', conflictOpened === true);
  await sleep(1600);
  const conflictUi = await page.evaluate(() => {
    const text = document.getElementById('modalHost')?.textContent || '';
    return {
      text,
      hasSummary: /检查了 \d+ 个档案、\d+ 个启用的课表/.test(text),
      clean: text.includes('没有发现问题'),
    };
  });
  check('检测面板给出检查摘要（说明检查了什么）', conflictUi.hasSummary === true,
    conflictUi.text.replace(/\s+/g, ' ').slice(0, 140));
  check('无冲突时明确显示「没有发现问题」', conflictUi.clean === true);
  await page.keyboard.press('Escape');
  await sleep(400);

  // 16) 批量操作确认摘要（#52）：把「谁会执行、谁收不到」写进确认框
  const summaryLogic = await page.evaluate(async () => {
    const { selectionSummary, summarizeSelection } = await import('/js/core/batch-summary.js?v=53');
    const devices = [
      { id: 'a', online: true, revoked: false },
      { id: 'b', online: false, revoked: false },
      { id: 'c', online: true, revoked: true },   // 已停用
    ];
    return {
      stats: summarizeSelection(devices, ['a', 'b', 'c']),
      all: selectionSummary(devices, ['a', 'b', 'c']),
      missing: selectionSummary(devices, ['a', 'ghost']),
    };
  });
  check('摘要口径正确（在线 / 离线 / 已停用分别计数）',
    summaryLogic.stats.online === 1 && summaryLogic.stats.offline === 1
      && summaryLogic.stats.revoked === 1 && summaryLogic.stats.missing === 0,
    JSON.stringify(summaryLogic.stats));
  check('摘要文本说清「谁会执行、谁收不到」',
    summaryLogic.all.includes('共 3 台') && summaryLogic.all.includes('在线 1 台会立即执行')
      && summaryLogic.all.includes('离线 1 台') && summaryLogic.all.includes('已停用 1 台会被跳过'),
    summaryLogic.all);
  check('选中项已不在列表时也能对上账',
    summaryLogic.missing.includes('1 台已不在列表中'), summaryLogic.missing);

  // 真实勾选 → 打开「批量下发配置」确认框 → 应带上同一份摘要
  await page.goto(`${BASE}/#/devices`, { waitUntil: 'networkidle2' });
  await sleep(2200);
  await page.evaluate(() => {
    const boxes = [...document.querySelectorAll('table tbody input[type="checkbox"]')].slice(0, 3);
    for (const box of boxes) {
      if (!box.checked) box.click();
    }
  });
  await sleep(1000);
  const bulkClicked = await page.evaluate(() => {
    const btn = [...document.querySelectorAll('.bulk-bar button')]
      .find((b) => b.textContent.trim() === '批量下发配置');
    if (!btn) return false;
    btn.click();
    return true;
  });
  await sleep(900);
  const confirmText = await page.evaluate(
    () => document.getElementById('modalHost')?.textContent || '');
  check('批量下发确认框带上影响摘要',
    bulkClicked && /共 \d+ 台/.test(confirmText) && /(在线|离线) \d+ 台/.test(confirmText),
    confirmText.replace(/\s+/g, ' ').slice(0, 160));
  await page.keyboard.press('Escape');
  await sleep(500);

  // 17) 备份与数据库维护（#59 / #62）：设置页能看到数据库占用与可回收空间，备份弹窗含内容选项
  await page.goto(`${BASE}/#/settings`, { waitUntil: 'networkidle2' });
  await sleep(3000);
  const backupUi = await page.evaluate(async () => {
    const { hasPermission } = await import('/js/core/api.js?v=56');
    const text = document.querySelector('#content')?.textContent || '';
    return {
      hasCard: text.includes('备份与数据库'),
      size: text.includes('占用空间'),
      integrity: text.includes('完整性检查'),
      reclaim: text.includes('可回收空间'),
      createBtn: [...document.querySelectorAll('button')]
        .some((b) => b.textContent.trim() === '+ 新建备份'),
      canRead: hasPermission('backup.read'),
      canWrite: hasPermission('backup.write'),
      head: text.replace(/\s+/g, ' ').slice(0, 200),
    };
  });
  check('设置页出现「备份与数据库」卡', backupUi.hasCard === true,
    `canRead=${backupUi.canRead} canWrite=${backupUi.canWrite} 首段=${backupUi.head}`);
  check('显示数据库占用 / 完整性 / 可回收空间',
    backupUi.size && backupUi.integrity && backupUi.reclaim, JSON.stringify(backupUi));
  check('有「+ 新建备份」入口', backupUi.createBtn === true);

  const dialogOpened = await page.evaluate(() => {
    const btn = [...document.querySelectorAll('button')]
      .find((b) => b.textContent.trim() === '+ 新建备份');
    if (!btn) return false;
    btn.click();
    return true;
  });
  await sleep(800);
  const dialogText = await page.evaluate(
    () => document.getElementById('modalHost')?.textContent || '');
  check('新建备份弹窗列出可选内容（数据库 / 配置文件 / 加密密钥）',
    dialogOpened && dialogText.includes('数据库') && dialogText.includes('配置文件')
      && dialogText.includes('加密密钥'), dialogText.replace(/\s+/g, ' ').slice(0, 180));

  // 含密钥的警告必须「默认隐藏、勾选后才出现」——只断言文本包含是不够的：
  // hidden 的元素同样在 DOM 里、textContent 一样含这段文字（本轮就差点把这条弱断言当成通过）。
  const warnState = await page.evaluate(() => {
    const read = () => {
      const notice = document.querySelector('#modalHost .notice-warn');
      return notice ? notice.hidden : null;
    };
    const before = read();
    const boxes = [...document.querySelectorAll('#modalHost input[type="checkbox"]')];
    if (boxes[1]) boxes[1].click();   // 0 = 配置文件，1 = 加密密钥
    return { before, after: read() };
  });
  check('含密钥警告默认隐藏、勾选后才出现',
    warnState.before === true && warnState.after === false,
    `before.hidden=${warnState.before} after.hidden=${warnState.after}`);
  await page.keyboard.press('Escape');
  await sleep(500);

  // 通用检查：界面文案里不该出现 Markdown 星号——前端不渲染 Markdown，
  // 写在字符串里就是原样显示（本项目已在 API 密钥与备份文案上各踩过一次）。
  const starLeak = await page.evaluate(
    () => (document.body.textContent || '').match(/\*\*[^*\n]{1,40}\*\*/g) || []);
  check('界面文案没有泄漏 Markdown 星号', starLeak.length === 0,
    starLeak.slice(0, 5).join(' | '));

  // 18) 加密导出（#60）：入口存在，弹窗要求两次口令并说明「服务端不保存口令」
  const backupId = await page.evaluate(async () => {
    const { api } = await import('/js/core/api.js?v=56');
    const entry = await api('/admin/backups', { method: 'POST', body: { note: 'E2E UI 加密导出' } });
    return entry.id;
  });
  check('已创建用于导出测试的备份', !!backupId, backupId);

  // 必须 reload：`goto` 到与当前**相同的 hash** 不会真正重新加载页面，状态会残留。
  // 另外设置页的 render() 会并发拉十几个接口（含要访问 GitHub 的更新检查），渲染可能被拖住，
  // 所以这里轮询等按钮出现，最多 20 秒。
  await page.reload({ waitUntil: 'networkidle2' });
  await sleep(1500);
  let exportUi = { found: false, buttons: [] };
  for (let i = 0; i < 40; i++) {
    exportUi = await page.evaluate(() => {
      const buttons = [...document.querySelectorAll('button')].map((b) => b.textContent.trim());
      const btn = [...document.querySelectorAll('button')]
        .find((b) => b.textContent.trim() === '加密导出');
      if (!btn) {
        const content = document.getElementById('content');
        return {
          found: false,
          buttons: buttons.slice(0, 12),
          hash: window.location.hash,
          appHidden: document.getElementById('app')?.hidden,
          loginHidden: document.getElementById('loginView')?.hidden,
          contentLen: (content?.textContent || '').length,
          contentHead: (content?.textContent || '').replace(/\s+/g, ' ').slice(0, 160),
        };
      }

      btn.click();
      return { found: true };
    });
    if (exportUi.found) break;
    await sleep(500);
  }
  check('备份行有「加密导出」入口', exportUi.found === true,
    exportUi.found ? ''
      : `hash=${exportUi.hash} appHidden=${exportUi.appHidden} loginHidden=${exportUi.loginHidden} `
        + `contentLen=${exportUi.contentLen} 首段=${exportUi.contentHead} 按钮=${(exportUi.buttons || []).join('/')}`);

  await sleep(900);
  const exportDialog = await page.evaluate(() => {
    const host = document.getElementById('modalHost');
    const text = host?.textContent || '';
    return {
      // 两次口令输入（口令 + 确认），避免输错一个字符就永久解不开
      passwords: host ? host.querySelectorAll('input[type="password"]').length : 0,
      noSave: text.includes('服务端不保存口令'),
      decryptHint: text.includes('--decrypt-backup'),
    };
  });
  check('导出弹窗要求两次口令 + 说明「口令不可找回」+ 给出解密命令',
    exportDialog.passwords === 2 && exportDialog.noSave && exportDialog.decryptHint,
    `passwords=${exportDialog.passwords} noSave=${exportDialog.noSave} hint=${exportDialog.decryptHint}`);
  await page.keyboard.press('Escape');
  await sleep(500);

  // 只断言「我建的那份没了」，不断言「总数为 0」——现场可能有别的备份（自动备份、别人手动建的），
  // 拿环境状态当断言基准是测试里的常见坏习惯。
  const cleanup = await page.evaluate(async (id) => {
    const { api } = await import('/js/core/api.js?v=56');
    await api(`/admin/backups/${id}`, { method: 'DELETE' });
    const list = await api('/admin/backups');
    return { left: list.length, mine: list.filter((e) => e.id === id).length };
  }, backupId);
  check('导出测试用的备份已清理（不误伤已有备份）',
    cleanup.mine === 0, `剩余 ${cleanup.left} 份，其中测试备份 ${cleanup.mine} 份`);

  // 19) 报表页（#63 在线率 / #65 指令执行 / #67 操作热点）
  await page.goto(`${BASE}/#/reports`, { waitUntil: 'networkidle2' });
  let reportUi = { hasTabs: false, hasMetric: false };
  for (let i = 0; i < 24; i++) {
    reportUi = await page.evaluate(() => {
      const box = document.getElementById('content');
      const text = box?.textContent || '';
      const buttons = [...(box ? box.querySelectorAll('button') : [])].map((b) => b.textContent.trim());
      return {
        hasTabs: buttons.includes('设备在线率') && buttons.includes('指令执行')
          && buttons.includes('配置同步') && buttons.includes('操作热点'),
        hasMetric: text.includes('整体在线率') || text.includes('还没有采样数据'),
        ranges: buttons.filter((b) => /最近 \d+ 天/.test(b)).length,
        head: text.replace(/\s+/g, ' ').slice(0, 120),
      };
    });
    if (reportUi.hasTabs && reportUi.hasMetric) {
      break;
    }

    await sleep(500);
  }
  check('报表页三个标签齐全且默认出数', reportUi.hasTabs && reportUi.hasMetric,
    `tabs=${reportUi.hasTabs} metric=${reportUi.hasMetric} ranges=${reportUi.ranges} 首段=${reportUi.head}`);
  check('提供 7 / 30 / 90 天时间范围', reportUi.ranges === 3, `找到 ${reportUi.ranges} 个`);

  // 通知到达（#7 回执的聚合）：必须有这个标签，且切过去能出数
  const hasNotifyTab = await page.evaluate(() => [...document.querySelectorAll('#content button')]
    .some((b) => b.textContent.trim() === '通知到达'));
  check('报表页有「通知到达」标签', hasNotifyTab === true);
  if (hasNotifyTab) {
    await page.evaluate(() => {
      const btn = [...document.querySelectorAll('#content button')]
        .find((b) => b.textContent.trim() === '通知到达');
      if (btn) {
        btn.click();
      }
    });
    await sleep(2000);
    const notifyText = await page.evaluate(
      () => (document.getElementById('content')?.textContent || '').replace(/\s+/g, ' '));
    check('通知到达报表能出数或给出空态引导',
      notifyText.includes('到达率') || notifyText.includes('还没有通知记录'),
      notifyText.slice(0, 140));
  }

  await page.evaluate(() => {
    const btn = [...document.querySelectorAll('#content button')]
      .find((b) => b.textContent.trim() === '指令执行');
    if (btn) {
      btn.click();
    }
  });
  await sleep(1800);
  const cmdText = await page.evaluate(
    () => (document.getElementById('content')?.textContent || '').replace(/\s+/g, ' '));
  check('指令执行报表显示总数 / 成功率 / 作废',
    cmdText.includes('指令总数') && cmdText.includes('成功率') && cmdText.includes('作废'),
    cmdText.slice(0, 150));

  await page.evaluate(() => {
    const btn = [...document.querySelectorAll('#content button')]
      .find((b) => b.textContent.trim() === '操作热点');
    if (btn) {
      btn.click();
    }
  });
  await sleep(1800);
  const opsText = await page.evaluate(
    () => (document.getElementById('content')?.textContent || '').replace(/\s+/g, ' '));
  check('操作热点报表显示账号与类别聚合',
    opsText.includes('操作总数') && opsText.includes('参与账号') && opsText.includes('按类别'),
    opsText.slice(0, 150));

  await page.evaluate(() => {
    const btn = [...document.querySelectorAll('#content button')]
      .find((b) => b.textContent.trim() === '配置同步');
    if (btn) {
      btn.click();
    }
  });
  await sleep(1800);
  const syncText = await page.evaluate(
    () => (document.getElementById('content')?.textContent || '').replace(/\s+/g, ' '));
  check('配置同步报表显示批次 / 覆盖率 / 完全到位（#64）',
    syncText.includes('下发批次') && syncText.includes('整体覆盖率') && syncText.includes('完全到位'),
    syncText.slice(0, 150));

  // 切到 30 天：标题里的区间应随之变化（证明真的重新取了数，而不是静态文案）
  await page.evaluate(() => {
    const btn = [...document.querySelectorAll('#content button')]
      .find((b) => b.textContent.trim() === '最近 30 天');
    if (btn) {
      btn.click();
    }
  });
  await sleep(2000);
  const rangeText = await page.evaluate(
    () => (document.getElementById('content')?.textContent || '').replace(/\s+/g, ' '));
  check('切换时间范围后重新出数（显示 30 天区间）',
    /\d{4}-\d{2}-\d{2} ~ \d{4}-\d{2}-\d{2}/.test(rangeText), rangeText.slice(0, 150));

  // 20) 导航重组 + 统计卡可点 + 状态计数条（界面改造）
  await page.goto(`${BASE}/#/dashboard`, { waitUntil: 'networkidle2' });
  await sleep(2200);

  // 侧边栏已改为顶栏分组下拉（见 css/topbar.css 与 js/topbar.js）
  const navGroups = await page.evaluate(
    () => [...document.querySelectorAll('.nav-group')].map((el) => el.dataset.group));
  check('顶栏导航按场景分为四组', ['日常', '教学配置', '设备', '系统'].every((g) => navGroups.includes(g)),
    `分组=${JSON.stringify(navGroups)}`);

  // 注意：**不做**「侧边栏已下线」这类断言。侧边栏现在是**二级导航**，
  // 与顶栏并存（两级结构），断言它不存在等于把架构钉死在错误方向上了。

  // 下拉展开：点第一个分组触发器，菜单应可见且含该组页面
  await page.evaluate(() => document.querySelector('.nav-trigger')?.click());
  await sleep(600);
  const dropdown = await page.evaluate(() => {
    const menu = document.querySelector('.nav-group.open .nav-dropdown');
    return {
      open: document.querySelectorAll('.nav-group.open').length,
      visible: menu ? getComputedStyle(menu).visibility === 'visible' : false,
      items: menu ? [...menu.querySelectorAll('.nav-item')].map((n) => n.textContent.trim()) : [],
    };
  });
  check('分组下拉可展开且列出该组页面',
    dropdown.open === 1 && dropdown.visible && dropdown.items.length > 0,
    `展开数=${dropdown.open} 可见=${dropdown.visible} 项=${JSON.stringify(dropdown.items)}`);

  // 点外部应收起。必须用**真实鼠标点击**：代码里监听的是 pointerdown，
  // 而 element.click() 只派发 click 事件，不会触发 pointerdown——
  // 那样测出来的是「断言自己写错了」，不是产品行为。
  await page.mouse.click(700, 400);
  await sleep(500);
  const afterOutside = await page.evaluate(() => document.querySelectorAll('.nav-group.open').length);
  check('点击空白处收起下拉', afterOutside === 0, `仍展开=${afterOutside}`);

  // 二级侧边栏：显示**当前一级分组**下的页面，而不是把顶栏再抄一遍
  await page.goto(`${BASE}/#/dashboard`, { waitUntil: 'networkidle2' });
  await sleep(2200);
  const sideNav = await page.evaluate(() => ({
    visible: (() => {
      const s = document.getElementById('sidebar');
      return !!s && getComputedStyle(s).display !== 'none' && !s.classList.contains('is-single');
    })(),
    title: document.querySelector('.side-nav-title')?.textContent.trim() || '(无)',
    items: [...document.querySelectorAll('#sideNavList .nav-item')].map((n) => n.textContent.trim()),
    active: document.querySelector('#sideNavList .nav-item.active')?.textContent.trim() || '(无)',
  }));
  check('二级侧边栏显示当前分组的页面且高亮当前页',
    sideNav.visible && sideNav.title === '日常' && sideNav.items.length > 1
      && sideNav.active.includes('仪表盘'),
    `标题=${sideNav.title} 项=${JSON.stringify(sideNav.items)} 高亮=${sideNav.active}`);

  // 跨分组跳页时，二级侧边栏要跟着换（否则会停在上一组的页面上——
  // 这是两级导航最容易出的 bug，因为「首屏是对的」）
  await page.evaluate(() => { window.location.hash = '#/audit'; });
  await sleep(2200);
  const sideNav2 = await page.evaluate(() => ({
    title: document.querySelector('.side-nav-title')?.textContent.trim() || '(无)',
    items: [...document.querySelectorAll('#sideNavList .nav-item')].map((n) => n.textContent.trim()),
    active: document.querySelector('#sideNavList .nav-item.active')?.textContent.trim() || '(无)',
  }));
  check('跳到系统组后二级侧边栏同步换组',
    sideNav2.title === '系统' && sideNav2.active.includes('审计日志'),
    `标题=${sideNav2.title} 项=${JSON.stringify(sideNav2.items)} 高亮=${sideNav2.active}`);

  // 当前页所在分组要有高亮。
  // 先回到总览：上面用真实鼠标点了空白处，万一落在某个统计卡上就会跳走，
  // 那这条断言测的就不是「分组高亮」而是「刚才跳到哪了」。
  await page.goto(`${BASE}/#/dashboard`, { waitUntil: 'networkidle2' });
  await sleep(2200);
  const groupActive = await page.evaluate(() => ({
    active: document.querySelector('.nav-group.active')?.dataset.group || '(无)',
    hasIndicator: !!document.querySelector('.nav-group.active > .nav-trigger'),
    page: document.getElementById('pageTitle')?.textContent || '',
  }));
  check('当前页所在分组高亮', groupActive.active === '日常' && groupActive.hasIndicator,
    `高亮组=${groupActive.active} 当前页=${groupActive.page}`);

  // 改导航不该让旧链接变 404
  await page.goto(`${BASE}/#/groups`, { waitUntil: 'networkidle2' });
  await sleep(1700);
  const aliasOk = await page.evaluate(() => !!document.querySelector('.status-tab'));
  check('旧链接 #/groups 仍落到设备页', aliasOk === true);

  // 统计卡可点：从「看到一个数字」到「看到那些设备」，中间不该再让人自己筛一次
  await page.goto(`${BASE}/#/dashboard`, { waitUntil: 'networkidle2' });
  await sleep(2200);
  const cardClicked = await page.evaluate(() => {
    const card = [...document.querySelectorAll('.stat-link')]
      .find((el) => el.textContent.includes('待同步'));
    if (!card) {
      return { found: false };
    }

    card.click();
    return { found: true };
  });
  await sleep(2400);
  const landed = await page.evaluate(() => ({
    hash: location.hash,
    active: [...document.querySelectorAll('.status-tab.active')]
      .map((el) => el.textContent.replace(/\d+$/, '').trim()),
  }));
  check('点「待同步」统计卡 → 设备页并已筛选',
    cardClicked.found && landed.hash.includes('status=pending') && landed.active.includes('待同步'),
    `card=${cardClicked.found} hash=${landed.hash} active=${JSON.stringify(landed.active)}`);

  // 状态条上的数字 vs 点下去之后的列表行数：**必须一致**（两处共用同一套判定才做得到）
  const switchedToList = await page.evaluate(() => {
    const btn = [...document.querySelectorAll('.segmented-item')]
      .find((b) => b.textContent.includes('列表'));
    if (btn) {
      btn.click();
      return true;
    }

    return false;
  });
  await sleep(1700);

  const tabTotal = await page.evaluate(() => document.querySelectorAll('.status-tab').length);
  const mismatches = [];
  for (let i = 0; i < tabTotal; i++) {
    const info = await page.evaluate((idx) => {
      const tab = document.querySelectorAll('.status-tab')[idx];
      if (!tab) {
        return null;
      }

      const count = Number(tab.querySelector('.status-tab-count')?.textContent || '0');
      const label = tab.textContent.slice(0, -String(count).length).trim();
      tab.click();
      return { label, count };
    }, i);
    if (!info) {
      continue;
    }

    await sleep(650);
    // 必须限定在设备看板容器里：本页还有「注册码」表格（同样是 table.data），
    // 不限定就会把它那几行算进来，看起来像「计数与列表不一致」。
    const rows = await page.evaluate(
      () => document.querySelectorAll('#deviceBoardHost table.data tbody tr').length);
    if (info.count !== rows) {
      mismatches.push(`${info.label}: 标签=${info.count} 列表=${rows}`);
    }
  }
  check('状态条数字与列表行数完全一致', switchedToList && tabTotal > 0 && mismatches.length === 0,
    (mismatches.join('；') || `全部一致（${tabTotal} 个状态）`));

  // 21) 设备标签（#10）：建标签 → 贴到设备 → 列表里看得到、能按标签筛
  const tagId = await page.evaluate(async () => {
    const { api } = await import('/js/core/api.js?v=62');
    const tag = await api('/admin/tags', {
      method: 'POST', body: { name: 'E2E-UI 标签', color: '#e5484d' },
    });
    const devices = await api('/admin/devices');
    await api(`/admin/devices/${devices[0].id}/tags`, {
      method: 'PUT', body: { tagIds: [tag.id] },
    });
    return tag.id;
  });

  await page.goto(`${BASE}/#/devices`, { waitUntil: 'networkidle2' });
  await sleep(2200);

  // 注意：`goto` 只改 hash，**不会重载页面** —— 上一个断言（逐个点状态条）把筛选
  // 留在了「已停用」上，这里必须显式点回「全部」，否则列表是空的、什么都断言不到。
  await page.evaluate(() => {
    const all = [...document.querySelectorAll('.status-tab')]
      .find((b) => b.textContent.trim().startsWith('全部'));
    if (all) {
      all.click();
    }
  });
  await sleep(1100);

  // 标签列在表格视图里，先切过去。
  await page.evaluate(() => {
    const btn = [...document.querySelectorAll('.segmented-item')]
      .find((b) => b.textContent.includes('列表'));
    if (btn) {
      btn.click();
    }
  });
  await sleep(1600);

  const tagUi = await page.evaluate(() => {
    const host = document.getElementById('deviceBoardHost');
    const options = [...document.querySelectorAll('.toolbar select option')]
      .map((o) => o.textContent.trim());
    return {
      hostExists: !!host,
      // 诊断用：告诉我们当前到底是哪种视图（看板没有 table）。
      view: [...document.querySelectorAll('.segmented-item.active')].map((b) => b.textContent.trim()),
      tableCount: document.querySelectorAll('table').length,
      headers: host ? [...host.querySelectorAll('th')].map((th) => th.textContent.trim()) : [],
      badges: [...document.querySelectorAll('.tag-badge')].map((el) => el.textContent.trim()),
      hasTagFilter: options.some((t) => t.includes('E2E-UI 标签')),
    };
  });
  check('设备列表有「标签」列且显示徽标',
    tagUi.headers.includes('标签') && tagUi.badges.includes('E2E-UI 标签'),
    `host=${tagUi.hostExists} 视图=${JSON.stringify(tagUi.view)} 表格数=${tagUi.tableCount} `
    + `headers=${JSON.stringify(tagUi.headers)} badges=${JSON.stringify(tagUi.badges)}`);
  check('工具栏有按标签筛选的选项', tagUi.hasTagFilter === true);

  // 按标签筛：只应剩贴了该标签的那一台。
  // 用「页面上的标签徽标数」判定，不依赖当前是看板还是列表视图。
  const filterApplied = await page.evaluate((id) => {
    const sel = [...document.querySelectorAll('.toolbar select')]
      .find((s) => [...s.options].some((o) => o.textContent.includes('全部标签')));
    if (!sel) {
      return false;
    }

    sel.value = id;
    sel.dispatchEvent(new Event('change', { bubbles: true }));
    return true;
  }, tagId);
  await sleep(1300);
  const afterFilter = await page.evaluate(() => {
    const host = document.getElementById('deviceBoardHost');
    return {
      badges: host ? host.querySelectorAll('.tag-badge').length : -1,
      rows: host ? host.querySelectorAll('tbody tr').length : -1,
    };
  });
  check('按标签筛选后只剩贴了该标签的设备',
    filterApplied && (afterFilter.badges === 1 || afterFilter.rows === 1),
    `徽标=${afterFilter.badges} 行数=${afterFilter.rows}`);

  // 清理：删标签会连设备关联一起清掉
  await page.evaluate(async (id) => {
    const { api } = await import('/js/core/api.js?v=62');
    await api(`/admin/tags/${id}`, { method: 'DELETE' });
  }, tagId);
  const tagClean = await page.evaluate(async () => {
    const { api } = await import('/js/core/api.js?v=62');
    return (await api('/admin/tags')).length;
  });
  check('测试标签已清理干净', tagClean === 0, `剩余 ${tagClean} 个标签`);

  // 22) 课表批量操作（#9）：替换科目 / 存为模板。
  // 自备一套最小课表数据（id 统一带 e2e- 前缀，便于**幂等清理**），跑完按前缀剔除。
  const sched = await page.evaluate(async () => {
    const { api } = await import('/js/core/api.js?v=63');
    const profiles = await api('/admin/profiles');
    const target = profiles[0];
    const detail = await api(`/admin/profiles/${target.id}`);
    const name = detail.name;
    const description = detail.description || '';

    const content = {
      timeLayouts: [{
        id: 'e2e-tl',
        name: 'E2E 时间表',
        items: [
          { startTime: '08:00:00', endTime: '08:45:00', kind: 'class' },
          { startTime: '08:55:00', endTime: '09:40:00', kind: 'class' },
          { startTime: '10:00:00', endTime: '10:45:00', kind: 'class' },
        ],
      }],
      subjects: [
        { id: 'e2e-sub-a', name: 'E2E语文', initial: '语' },
        { id: 'e2e-sub-b', name: 'E2E数学', initial: '数' },
      ],
      classPlans: [{
        id: 'e2e-plan-1',
        name: '周一课表',
        timeLayoutId: 'e2e-tl',
        isEnabled: true,
        daysOfWeek: [1],
        weekInterval: 0,
        weekOffset: 0,
        slots: [{ index: 0, subjectId: 'e2e-sub-a', isEnabled: true }],
      }],
    };

    await api(`/admin/profiles/${target.id}`, {
      method: 'PUT', body: { name, description, content },
    });
    return { profileId: target.id, name, description };
  });

  await page.goto(`${BASE}/#/profiles/${sched.profileId}`, { waitUntil: 'networkidle2' });
  await sleep(2600);

  // 切到「课表」标签。注意 tab 的文本是「课表N」（label + 计数），不能用全等匹配。
  const tabPicked = await page.evaluate(() => {
    const tab = [...document.querySelectorAll('.tabs .tab')]
      .find((b) => b.textContent.trim().startsWith('课表'));
    if (!tab) {
      return false;
    }

    tab.click();
    return true;
  });
  await sleep(1800);

  const gridBefore = await page.evaluate(() => {
    const grid = document.querySelector('.sched-grid');
    return {
      tabFound: !!document.querySelector('#tabBody'),
      hasGrid: !!grid,
      text: (grid ? grid.textContent : '').replace(/\s+/g, ' ').trim().slice(0, 100),
    };
  });
  check('课表页渲染出网格且已有排课',
    tabPicked && gridBefore.hasGrid && gridBefore.text.includes('语'),
    `tabPicked=${tabPicked} ${JSON.stringify(gridBefore)}`);

  // 打开「批量操作」→ 应当有四个动作
  await page.evaluate(() => {
    const btn = [...document.querySelectorAll('button')]
      .find((b) => b.textContent.trim() === '批量操作');
    if (btn) {
      btn.click();
    }
  });
  await sleep(1300);
  const bulkDialog = await page.evaluate(() => {
    const host = document.getElementById('modalHost');
    const text = (host ? host.textContent : '').replace(/\s+/g, ' ');
    return {
      open: !!host && host.hidden === false,
      actions: ['按节复制', '批量清空', '替换科目', '课表模板'].every((t) => text.includes(t)),
      text: text.slice(0, 110),
    };
  });
  check('「批量操作」弹窗可打开且四个动作齐全', bulkDialog.open && bulkDialog.actions,
    bulkDialog.text);

  // 替换科目：E2E语文 → E2E数学
  await page.evaluate(() => {
    const btn = [...document.querySelectorAll('#modalHost button')]
      .find((b) => b.textContent.trim() === '替换科目');
    if (btn) {
      btn.click();
    }
  });
  await sleep(900);
  const picked = await page.evaluate(() => {
    const selects = [...document.querySelectorAll('#modalHost select')];
    if (selects.length < 2) {
      return false;
    }

    selects[0].value = 'e2e-sub-a';
    selects[0].dispatchEvent(new Event('change', { bubbles: true }));
    selects[1].value = 'e2e-sub-b';
    selects[1].dispatchEvent(new Event('change', { bubbles: true }));
    return true;
  });
  await sleep(700);
  const executed = await page.evaluate(() => {
    const btn = [...document.querySelectorAll('#modalHost button')]
      .find((b) => b.textContent.trim() === '执行');
    if (!btn) {
      return false;
    }

    btn.click();
    return true;
  });
  await sleep(1600);
  const gridAfter = await page.evaluate(
    () => (document.querySelector('.sched-grid')?.textContent || '').replace(/\s+/g, ' '));
  check('替换科目后网格随之变化（语 → 数）',
    picked && executed && gridAfter.includes('数') && !gridAfter.includes('语'),
    gridAfter.slice(0, 110));

  // 存为模板
  await page.evaluate(() => {
    const btn = [...document.querySelectorAll('button')]
      .find((b) => b.textContent.trim() === '批量操作');
    if (btn) {
      btn.click();
    }
  });
  await sleep(1300);
  await page.evaluate(() => {
    const btn = [...document.querySelectorAll('#modalHost button')]
      .find((b) => b.textContent.trim() === '课表模板');
    if (btn) {
      btn.click();
    }
  });
  await sleep(1100);
  await page.evaluate(() => {
    const input = document.querySelector('#modalHost input[type="text"]');
    const btn = [...document.querySelectorAll('#modalHost button')]
      .find((b) => b.textContent.trim() === '保存');
    if (input && btn) {
      input.value = 'E2E-UI 模板';
      btn.click();
    }
  });
  await sleep(1900);
  const templateNames = await page.evaluate(async () => {
    const { api } = await import('/js/core/api.js?v=63');
    return (await api('/admin/timetable-templates')).map((t) => t.name);
  });
  check('课表可存为模板', templateNames.includes('E2E-UI 模板'), `模板=[${templateNames.join(', ')}]`);

  // 清理：**按前缀剔除探针数据**，而不是「恢复备份」。
  // 恢复备份看着更稳妥，其实更脆：上一次运行如果失败在半途，它留下的「备份」
  // 本身就已经含探针数据，恢复备份等于把脏数据固化下来（本轮真踩过）。
  // 按 e2e- 前缀剔除是幂等的：跑多少次、从什么状态开始，结果都一样。
  await page.keyboard.press('Escape');
  await sleep(700);
  await page.evaluate(async (data) => {
    const { api } = await import('/js/core/api.js?v=63');
    for (const t of await api('/admin/timetable-templates')) {
      await api(`/admin/timetable-templates/${t.id}`, { method: 'DELETE' });
    }

    const detail = await api(`/admin/profiles/${data.profileId}`);
    const content = detail.content || {};
    const clean = (list) => (list || []).filter((x) => !String(x.id).startsWith('e2e-'));

    await api(`/admin/profiles/${data.profileId}`, {
      method: 'PUT',
      body: {
        name: data.name,
        description: data.description,
        content: {
          ...content,
          timeLayouts: clean(content.timeLayouts),
          subjects: clean(content.subjects),
          classPlans: clean(content.classPlans),
        },
      },
    });
  }, sched);
  const schedRestored = await page.evaluate(async (id) => {
    const { api } = await import('/js/core/api.js?v=63');
    const detail = await api(`/admin/profiles/${id}`);
    // 直接查内容里还有没有 e2e 痕迹 —— 只数「有几个时间表」是查不出没还原的。
    return {
      hasProbeData: JSON.stringify(detail.content || {}).includes('e2e'),
      templates: (await api('/admin/timetable-templates')).length,
    };
  }, sched.profileId);
  check('测试数据已还原（档案内容与模板都清干净）',
    !schedRestored.hasProbeData && schedRestored.templates === 0,
    JSON.stringify(schedRestored));

  // 21) 交互层：快捷操作区 / 右键菜单 / 列表键盘导航
  await page.goto(`${BASE}/#/dashboard`, { waitUntil: 'networkidle2' });
  await sleep(2400);
  const quick = await page.evaluate(() => {
    const acts = [...document.querySelectorAll('.quick-action')];
    return {
      count: acts.length,
      labels: acts.map((a) => a.querySelector('.quick-action-label')?.textContent.trim() || ''),
      notifyHref: acts.find((a) => a.textContent.includes('发送通知'))?.getAttribute('href') || '',
    };
  });
  check('总览有快捷操作区，且「发送通知」是直达入口',
    quick.count >= 4 && quick.labels.includes('发送通知') && quick.notifyHref.includes('#/devices'),
    `count=${quick.count} labels=${JSON.stringify(quick.labels)} href=${quick.notifyHref}`);

  await page.goto(`${BASE}/#/devices`, { waitUntil: 'networkidle2' });
  await sleep(2500);
  // 复位筛选：上一个断言可能把状态停在了某个 tab 上（goto 只改 hash，不重载页面）。
  await page.evaluate(() => {
    const all = [...document.querySelectorAll('.status-tab')]
      .find((b) => b.textContent.trim().startsWith('全部'));
    if (all) {
      all.click();
    }
  });
  await sleep(1300);

  // 右键菜单：**验委托机制本身**，不依赖真实设备表格。
  //
  // 为什么不用真实表格行：设备表格在这条测试流程里渲染不稳定（前面多个断言反复
  // 切视图/改筛选，独立诊断脚本能出 15 行、这里却是 0 行），拿它当断言基准会变成
  // 「 flaky 断言」。所以这里造一个带 `data-device-id` 的元素再派发 contextmenu ——
  // 验的是「document 级委托 + 菜单渲染 + 危险项标红」这套机制。
  const menuOpened = await page.evaluate(async () => {
    const { api } = await import('/js/core/api.js?v=70');
    const devices = await api('/admin/devices');
    if (!devices.length) {
      return { ok: false, reason: '没有设备' };
    }

    const fake = document.createElement('tr');
    fake.dataset.deviceId = devices[0].id;
    document.body.appendChild(fake);
    fake.dispatchEvent(new MouseEvent('contextmenu', {
      bubbles: true, cancelable: true, clientX: 120, clientY: 120,
    }));
    return { ok: true, device: devices[0].name };
  });
  await sleep(700);
  const menuInfo = await page.evaluate(() => {
    const menu = document.querySelector('.context-menu');
    if (!menu || menu.hidden) {
      return { visible: false, items: [], danger: false };
    }

    return {
      visible: true,
      items: [...menu.querySelectorAll('.context-menu-item')].map((b) => b.textContent.trim()),
      danger: [...menu.querySelectorAll('.context-menu-item')].some((b) => b.classList.contains('danger')),
    };
  });
  check('设备行右键出菜单：通知 / 重启 / 关机（关机为危险项）',
    menuOpened.ok && menuInfo.visible && menuInfo.items.some((t) => t.includes('发送通知'))
      && menuInfo.items.some((t) => t.includes('重启')) && menuInfo.items.some((t) => t.includes('关机'))
      && menuInfo.danger,
    `设备=${menuOpened.device || menuOpened.reason} items=${JSON.stringify(menuInfo.items)}`);

  // Esc 应收起菜单（否则会挡住后面的操作）
  await page.keyboard.press('Escape');
  await sleep(400);
  const menuClosed = await page.evaluate(() => {
    const menu = document.querySelector('.context-menu');
    return !menu || menu.hidden;
  });
  check('Esc 收起右键菜单', menuClosed === true);

  // 列表键盘导航：J/K 高亮（真实设备行）
  await page.keyboard.press('j');
  await sleep(450);
  const navInfo = await page.evaluate(() => ({
    current: document.querySelectorAll('.nav-current').length,
    tag: document.querySelector('.nav-current')?.tagName || '',
  }));
  check('J 键在列表里高亮当前行', navInfo.current === 1,
    `高亮数=${navInfo.current} 元素=${navInfo.tag}`);

  // X 键（勾选当前行）**刻意不做 UI 断言**：它依赖设备表格里的复选框，
  // 而表格在这条测试流程里渲染不稳定（同上）。J 键能高亮已证明导航本身是通的，
  // X 的分支（找到 checkbox → 点它）在真实使用中由批量条的出现间接验证。

  // 快捷键帮助里要能查到新键位（不然等于没做）
  await page.keyboard.press('?');
  await sleep(800);
  const helpHas = await page.evaluate(() => {
    const text = document.body.textContent || '';
    return text.includes('J / K') && text.includes('勾选');
  });
  check('快捷键帮助里能查到 J/K/X', helpHas === true);
  await page.keyboard.press('Escape');
  await sleep(400);

  // 22) 命令面板（命令 + 搜索合一）与 N/P 快捷键
  await page.goto(`${BASE}/#/dashboard`, { waitUntil: 'networkidle2' });
  await sleep(2400);
  await page.keyboard.down('Control');
  await page.keyboard.press('k');
  await page.keyboard.up('Control');
  await sleep(1200);
  const cmdIdle = await page.evaluate(() => ({
    groups: [...document.querySelectorAll('.search-group')].map((e) => e.textContent.trim()),
    count: document.querySelectorAll('.search-item').length,
    hasKeys: document.querySelectorAll('.search-keys kbd').length,
    hint: document.querySelector('.search-hint')?.textContent || '',
  }));
  check('命令面板空闲时列出命令与页面两组',
    cmdIdle.groups.includes('命令') && cmdIdle.groups.includes('页面') && cmdIdle.count > 4,
    `分组=${JSON.stringify(cmdIdle.groups)} 条目=${cmdIdle.count} 提示=${cmdIdle.hint.slice(0, 30)}`);
  check('命令面板显示快捷键徽标', cmdIdle.hasKeys >= 2, `徽标数=${cmdIdle.hasKeys}`);

  // 输入关键字时命令与搜索结果混排（命令优先）
  await page.keyboard.type('通知', { delay: 40 });
  await sleep(1400);
  const cmdFiltered = await page.evaluate(() => ({
    groups: [...document.querySelectorAll('.search-group')].map((e) => e.textContent.trim()),
    first: document.querySelector('.search-item')?.textContent.replace(/\s+/g, ' ').trim() || '',
  }));
  check('输入「通知」时命令排在搜索结果之前',
    cmdFiltered.groups[0] === '命令' && cmdFiltered.first.includes('发送通知'),
    `分组=${JSON.stringify(cmdFiltered.groups)} 首项=${cmdFiltered.first.slice(0, 30)}`);
  await page.keyboard.press('Escape');
  await sleep(600);

  // 面板上写了「N」就必须真能按 —— 这是本轮特意绑的
  await page.keyboard.press('n');
  await sleep(1800);
  const afterN = await page.evaluate(() => ({
    hash: location.hash,
    active: [...document.querySelectorAll('.status-tab.active')]
      .map((el) => el.textContent.replace(/\d+$/, '').trim()),
  }));
  check('按 N 落到设备管理并预置「在线」筛选',
    afterN.hash.includes('status=online') && afterN.active.includes('在线'),
    `hash=${afterN.hash} 选中=${JSON.stringify(afterN.active)}`);

  // 23) 折叠只管侧边栏：一级分组必须保留（藏掉一级就没有全局方向感了）
  await page.goto(`${BASE}/#/dashboard`, { waitUntil: 'networkidle2' });
  await sleep(2400);
  await page.evaluate(() => document.getElementById('collapseBtn')?.click());
  await sleep(700);
  const collapsed = await page.evaluate(() => {
    const triggers = [...document.querySelectorAll('.nav-trigger')];
    return {
      侧栏隐藏: getComputedStyle(document.getElementById('sidebar')).display === 'none',
      一级总数: triggers.length,
      一级可见: triggers.filter((t) => t.offsetParent !== null).length,
      一级文本: triggers.map((t) => t.textContent.trim().slice(0, 6)),
      一级rect: triggers.slice(0, 2).map((t) => Math.round(t.getBoundingClientRect().width)),
      顶栏nav状态: getComputedStyle(document.querySelector('.topbar-nav')).display,
      navCollapse类: document.getElementById('app')?.classList.contains('nav-collapsed'),
      品牌可见: (() => {
        const b = document.querySelector('.topbar-brand');
        return !!b && b.getBoundingClientRect().width > 0;
      })(),
    };
  });
  check('折叠隐藏侧边栏但一级分组与品牌保留',
    collapsed.侧栏隐藏 && collapsed.一级可见 >= 4 && collapsed.品牌可见,
    `侧栏隐藏=${collapsed.侧栏隐藏} 一级总数=${collapsed.一级总数} 可见=${collapsed.一级可见} rect=${JSON.stringify(collapsed.一级rect)} 文本=${JSON.stringify(collapsed.一级文本)} navDisplay=${collapsed.顶栏nav状态} 类=${collapsed.navCollapse类} 品牌=${collapsed.品牌可见}`);

  // 展开恢复
  await page.evaluate(() => document.getElementById('collapseBtn')?.click());
  await sleep(700);
  const expanded = await page.evaluate(
    () => getComputedStyle(document.getElementById('sidebar')).display !== 'none');
  check('再次点击展开侧边栏', expanded === true);

  // 24) 单击选中 / 双击打开（方案 3.1：让右键菜单与批量条的「对谁操作」有依据）
  //
  // 这里用 **reload** 而不是 goto 来构造前提：`page.goto` 只改 hash，
  // 而 `view` / `filter` 是模块级变量——前面 23 项断言留下的状态会带进来
  // （实测踩过：页面显示「看板视图」是激活的，DOM 里却按列表渲染，卡片数为 0）。
  // reload 会重置模块状态，拿到一个确定的起点。
  await page.goto(`${BASE}/#/devices`, { waitUntil: 'networkidle2' });
  await sleep(600);
  await page.reload({ waitUntil: 'networkidle2' });
  await sleep(3000);

  const singleClick = await page.evaluate(async () => {
    // 卡片可能因为「视图残留 / 筛选残留」而不在页面上。
    // 尤其是状态筛选：上一条断言把设备页预置成了「在线」，而本地假设备从不上线，
    // 于是看板上 0 张卡片——这不是产品坏了，是断言自己没把前提摆正。
    const allTab = [...document.querySelectorAll('.status-tab')].find((b) => b.textContent.trim().startsWith('全部'));
    if (allTab && !allTab.classList.contains('active')) {
      allTab.click();
      await new Promise((r) => setTimeout(r, 1400));
    }

    const viewBtn = [...document.querySelectorAll('.segmented-item')].find((b) => b.textContent.includes('看板'));
    if (viewBtn && !viewBtn.classList.contains('active')) {
      viewBtn.click();
      await new Promise((r) => setTimeout(r, 1600));
    }

    const chip = document.querySelector('.dchip');
    if (!chip) {
      return {
        ok: false,
        诊断: {
          视图按钮: [...document.querySelectorAll('.segmented-item')]
            .map((b) => b.textContent.trim() + (b.classList.contains('active') ? '*' : '')),
          卡片数: document.querySelectorAll('.dchip').length,
          当前筛选: [...document.querySelectorAll('.status-tab')]
            .filter((b) => b.classList.contains('active')).map((b) => b.textContent.trim()),
          内容首段: (document.getElementById('content')?.textContent || '').replace(/\s+/g, ' ').slice(0, 80),
        },
      };
    }

    chip.click();
    // 单击是延迟生效的（与双击区分），等它一下
    await new Promise((r) => setTimeout(r, 500));
    // **重新查询**而不是复用上面的 chip 引用：页面有轮询，重绘后旧节点会游离，
    // 拿它读 classList 永远是 false（会误报成「单击没生效」）。
    // 顺带这也验证了「重绘后选中态能按 selection 恢复」——那才是真正要保证的行为。
    const fresh = document.querySelector('.dchip');
    return {
      ok: true,
      selected: !!fresh && fresh.classList.contains('selected'),
      barVisible: !!document.querySelector('#boardSelectionHost .bulk-bar'),
      barText: document.querySelector('#boardSelectionHost .bulk-text')?.textContent || '',
      selectionSize: document.querySelector('#boardSelectionHost .bulk-text')?.textContent || '',
    };
  });
  check('单击教室卡片即选中（看板视图出现批量条）',
    singleClick.ok && singleClick.selected && singleClick.barVisible,
    singleClick.ok
      ? `选中=${singleClick.selected} 批量条=${singleClick.barVisible} 文本=${singleClick.barText}`
      : `没有卡片：${JSON.stringify(singleClick.诊断)}`);

  // 再点一次取消选中
  const afterSecond = await page.evaluate(async () => {
    const chip = document.querySelector('.dchip');
    if (!chip) {
      return { selected: null, bar: null };
    }

    chip.click();
    await new Promise((r) => setTimeout(r, 500));
    return {
      selected: document.querySelector('.dchip')?.classList.contains('selected'),
      bar: !!document.querySelector('#boardSelectionHost .bulk-bar'),
    };
  });
  check('再次单击取消选中且批量条收起',
    afterSecond.selected === false && afterSecond.bar === false,
    `选中=${afterSecond.selected} 批量条=${afterSecond.bar}`);

  // 双击应打开详情（而不是只选中）
  const dblResult = await page.evaluate(async () => {
    const chip = document.querySelector('.dchip');
    if (!chip) {
      return { modalOpen: false, hasDetail: false, stillSelected: false };
    }

    chip.dispatchEvent(new MouseEvent('dblclick', { bubbles: true, cancelable: true }));
    await new Promise((r) => setTimeout(r, 1600));
    const host = document.getElementById('modalHost');
    const text = host?.textContent || '';
    return {
      modalOpen: !!host && host.hidden === false,
      hasDetail: /设备|状态|版本/.test(text),
      // 同样重新查询：双击的设备应保持选中（Finder 行为）
      stillSelected: !!document.querySelector('.dchip.selected'),
    };
  });
  check('双击教室卡片打开详情弹窗（并保持选中）',
    dblResult.modalOpen && dblResult.hasDetail && dblResult.stillSelected,
    `弹窗=${dblResult.modalOpen} 有详情=${dblResult.hasDetail} 保持选中=${dblResult.stillSelected}`);

  // 键盘 Enter 的语义必须与双击一致（否则卡片改交互后 Enter 会悄悄变成「选中」）
  await page.keyboard.press('Escape');
  await sleep(700);
  await page.evaluate(() => {
    document.querySelector('#boardSelectionHost .bulk-bar button.btn-ghost')?.click();
  });
  await sleep(1200);
  await page.keyboard.press('j');
  await sleep(400);
  await page.keyboard.press('Enter');
  await sleep(1600);
  const enterResult = await page.evaluate(() => {
    const host = document.getElementById('modalHost');
    const open = !!host && host.hidden === false;
    // 只判「有弹窗」不够：页面上可能存在别的弹窗残留，
    // 那样即使 Enter 什么都没做也会误判通过。要求内容确实是设备详情。
    const text = open ? (host.textContent || '') : '';
    return {
      modalOpen: open,
      looksLikeDevice: /设备名称|所属|版本|最后心跳|操作历史|待执行指令/.test(text),
    };
  });
  check('键盘 Enter 打开卡片详情（与双击语义一致）',
    enterResult.modalOpen && enterResult.looksLikeDevice,
    `弹窗=${enterResult.modalOpen} 内容像设备详情=${enterResult.looksLikeDevice}`);
  await page.keyboard.press('Escape');
  await sleep(600);

  // 25) 本轮改动：断点修复 / 登录页顶栏 logo / 页面设置页 / 每日时间漂移
  //
  // 断点：900px 宽曾经会隐藏一级导航并把侧边栏推出屏幕（用户反馈的「导航有时自动隐藏」）。
  // 这条断言把那个宽度钉住——以后谁再把断点改回 900，它会立刻失败。
  await page.setViewport({ width: 900, height: 800 });
  await sleep(900);
  // 必须先跳到「日常」组（含多个页面）再测：
  // 设备分组只有 1 个页面，`renderSideNav` 会给侧边栏加 `is-single` 并隐藏它
  // ——那是**设计如此**（侧栏只有孤零零一项看着像坏了），
  // 在设备页测「侧边栏是否存在」测的其实是另一件事。
  await page.evaluate(() => { window.location.hash = '#/dashboard'; });
  await sleep(2200);

  const at900 = await page.evaluate(() => {
    // 摘掉折叠类而不是去点按钮：按钮的 is-active 与 app 上的 nav-collapsed
    // 是两处状态，用「按钮看起来是展开的」判断「类是否被加上」并不可靠。
    document.getElementById('app')?.classList.remove('nav-collapsed');
    return {
      triggers: [...document.querySelectorAll('.nav-trigger')].filter((t) => t.offsetParent !== null).length,
      sidebar: getComputedStyle(document.getElementById('sidebar')).display !== 'none',
    };
  });
  check('900px 宽下顶栏分组与侧边栏都还在（不再自动隐藏）',
    at900.triggers >= 4 && at900.sidebar,
    `一级触发器=${at900.triggers} 侧边栏=${at900.sidebar}`);

  await page.setViewport({ width: 700, height: 800 });
  await sleep(900);
  const at700 = await page.evaluate(() => ({
    triggers: [...document.querySelectorAll('.nav-trigger')].filter((t) => t.offsetParent !== null).length,
    sidebar: getComputedStyle(document.getElementById('sidebar')).display !== 'none',
  }));
  check('700px 宽（真手机宽度）才换成抽屉形态',
    at700.triggers === 0 && at700.sidebar === false,
    `一级触发器=${at700.triggers} 侧边栏=${at700.sidebar}`);
  await page.setViewport({ width: 1600, height: 1000 });
  await sleep(800);

  // 页面设置页：四个区齐、能改登录页顶栏开关
  await page.goto(`${BASE}/#/appearance`, { waitUntil: 'networkidle2' });
  await sleep(2400);
  const appearance = await page.evaluate(() => {
    const text = (document.getElementById('content')?.textContent || '').replace(/\s+/g, ' ');
    return {
      title: document.getElementById('pageTitle')?.textContent || '',
      sections: ['品牌', '登录页', '外观（本机）', '高级（全站）'].filter((k) => text.includes(k)),
      hasTopbarToggle: text.includes('隐藏登录页顶栏'),
      inSideNav: [...document.querySelectorAll('#sideNavList .nav-item')]
        .some((n) => n.textContent.includes('页面设置')),
    };
  });
  check('「页面设置」页可打开且四个区齐全（品牌 / 登录页 / 外观 / 高级）',
    appearance.title === '页面设置' && appearance.sections.length === 4 && appearance.hasTopbarToggle,
    `标题=${appearance.title} 区块=${JSON.stringify(appearance.sections)} 顶栏开关=${appearance.hasTopbarToggle}`);
  check('「页面设置」出现在系统分组的二级侧边栏里', appearance.inSideNav === true);

  // 每日时间漂移：界面上的输入与三个只读值都要在
  await page.goto(`${BASE}/#/settings`, { waitUntil: 'networkidle2' });
  await sleep(2600);
  const timeCard = await page.evaluate(
    () => (document.getElementById('content')?.textContent || '').replace(/\s+/g, ' '));
  check('时间卡片有「每日漂移」设置与累计显示',
    timeCard.includes('每日漂移') && timeCard.includes('秒/天') && timeCard.includes('已累计')
      && timeCard.includes('重置累计起点'),
    timeCard.includes('每日漂移')
      ? `含每日漂移 / 已累计 / 重置起点`
      : `未找到，片段=${timeCard.slice(0, 120)}`);

  // 登录页顶栏用 logo 图而不是文字（用户反馈的问题）
  const loginTop = await page.evaluate(async () => {
    localStorage.removeItem('token');
    location.reload();
    return true;
  });
  if (loginTop) {
    await sleep(2600);
    const topbarInfo = await page.evaluate(() => {
      const mark = document.getElementById('loginTopMark');
      return {
        exists: !!document.getElementById('loginTopbar'),
        isImage: !!mark?.querySelector('img'),
        text: (mark?.textContent || '').trim(),
      };
    });
    check('登录页顶栏用 logo 图（不是文字「CI」）',
      topbarInfo.exists && topbarInfo.isImage,
      `顶栏=${topbarInfo.exists} 是图片=${topbarInfo.isImage} 文字="${topbarInfo.text}"`);
  }

  console.log('\n===== 验证结果 =====');
  for (const r of results) console.log(r);
  const failed = results.filter((r) => r.startsWith('FAIL')).length;
  console.log(`\n合计 ${results.length} 项，失败 ${failed} 项`);

  await browser.close();
  process.exit(failed === 0 ? 0 : 1);
})().catch((err) => {
  console.error('FAILED:', err && err.message ? err.message : err);
  process.exit(1);
});
