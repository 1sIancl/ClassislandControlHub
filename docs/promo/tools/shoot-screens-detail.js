/** 重截 03-devices（分组改名后）与 04-profiles（进入档案编辑器的周课表网格）。 */
const path = require('path');
const puppeteer = require('puppeteer-core');

const EDGE = 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const BASE = 'http://127.0.0.1:29800';
const OUT = 'E:\\classisland集控\\ClassIsland.ControlHub\\docs\\promo\\images';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

(async () => {
  const browser = await puppeteer.launch({
    executablePath: EDGE,
    headless: 'new',
    args: ['--disable-gpu', '--hide-scrollbars'],
  });
  const page = await browser.newPage();
  await page.setViewport({ width: 1600, height: 1000 });
  page.on('pageerror', (e) => console.log('PAGE_EXCEPTION:', e.message));

  await page.goto(`${BASE}/`, { waitUntil: 'networkidle2' });
  await page.waitForSelector('#loginUsername', { timeout: 20000 });
  await page.type('#loginUsername', 'admin');
  await page.type('#loginPassword', 'admin123');
  await page.click('#loginSubmit');
  await page.waitForFunction(() => document.getElementById('app')?.hidden === false, { timeout: 30000 });
  console.log('LOGGED_IN');

  // 03 设备看板
  await page.goto(`${BASE}/#/devices`, { waitUntil: 'networkidle2' });
  await sleep(3200);
  await page.screenshot({ path: path.join(OUT, '03-devices.png') });
  console.log('SHOT 03-devices.png');

  // 04 配置档案 → 示例档案 → 编辑内容 → 课表
  await page.goto(`${BASE}/#/profiles`, { waitUntil: 'networkidle2' });
  await page.waitForFunction(() => document.body.innerText.includes('示例档案'), { timeout: 20000 });
  await sleep(1200);

  const clicked = await page.evaluate(() => {
    const nodes = [...document.querySelectorAll('div, tr, li')];
    const card = nodes.reverse().find((n) => n.textContent.includes('示例档案') && n.querySelector('button'));
    if (!card) return 'no-card';
    const btn = [...card.querySelectorAll('button')].find((b) => b.textContent.trim() === '编辑内容');
    if (!btn) return 'no-edit-btn';
    btn.click();
    return 'clicked';
  });
  console.log('EDIT_CLICK:', clicked);

  // 等编辑器渲染（固定等待更稳：编辑器是同步渲染 + 一次详情请求）
  await sleep(5000);
  const tabOk = await page.evaluate(() => {
    const tabs = [...document.querySelectorAll('button')]
      .filter((b) => /^(时间表|课表|科目|自定义设置)/.test(b.textContent.trim()));
    const tab = tabs.find((b) => b.textContent.trim().startsWith('课表'));
    if (!tab) return 'no-tab:' + tabs.map((b) => b.textContent.trim()).join(',');
    tab.click();
    return 'clicked';
  });
  console.log('TAB_CLICK:', tabOk);
  await sleep(3000);
  await page.screenshot({ path: path.join(OUT, '04-profiles.png') });
  console.log('SHOT 04-profiles.png');

  await browser.close();
  console.log('DONE');
})().catch((err) => {
  console.error('FAILED:', err && err.message ? err.message : err);
  process.exit(1);
});
