/**
 * 宣传帖配图自动截图：登录本地 A 端，逐页截取（用于 docs/promo/images）。
 * 用系统 Edge + puppeteer-core（不下载浏览器），跑完自动退出。
 */
const path = require('path');
const puppeteer = require('puppeteer-core');

const EDGE = 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const BASE = 'http://127.0.0.1:29800';
const OUT = 'E:\\classisland集控\\ClassIsland.ControlHub\\docs\\promo\\images';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const SHOTS = [
  ['02-dashboard.png', '#/dashboard'],
  ['03-devices.png', '#/devices'],
  ['04-profiles.png', '#/profiles'],
  ['05-remote.png', '#/remote'],
  ['06-audit.png', '#/audit'],
  ['07-settings.png', '#/settings'],
];

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
  await page.waitForFunction(() => {
    const app = document.getElementById('app');
    return app && app.hidden === false;
  }, { timeout: 30000 });
  console.log('LOGGED_IN');
  await sleep(1500);

  for (const [file, hash] of SHOTS) {
    await page.goto(`${BASE}/${hash}`, { waitUntil: 'networkidle2' });
    await sleep(2400);
    await page.screenshot({ path: path.join(OUT, file) });
    console.log('SHOT', file);
  }

  await browser.close();
  console.log('DONE');
})().catch((err) => {
  console.error('FAILED:', err && err.message ? err.message : err);
  process.exit(1);
});
