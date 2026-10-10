/**
 * 新手引导：按步骤跳到对应页面，并高亮页面上的关键元素，支持随时跳过。
 * 走完或跳过后由调用方（app.js）把「已完成」记到当前账号上。
 *
 * <para>⚠️ 每一处 target 都必须指向**当前真正可见**的元素。上一版是指向顶栏下拉里的
 * `.nav-item`（`[data-key="profiles"]` 等）：下拉收起时它们在 DOM 里但不可见，
 * 命中矩形是全 0，于是高亮框缩在左上角、卡片跑到屏幕外——看起来就是「引导坏了」。
 * 现在改为**先把页面切过去**，再高亮二级侧栏里那一项（它是可见的，也正好告诉用户
 * 「这个功能在侧栏的哪个位置」）。</para>
 */

import { h, clear } from './ui.js?v=90';

/** 二级侧栏里某一项（当前分组下才会渲染，所以必须先切页）。 */
const sideItem = (key) => document.querySelector(`#sideNavList .nav-item[data-key="${key}"]`);

/**
 * 引导步骤。
 *
 * <para>`route` 表示该步要先把界面切到哪一页；`target` 返回 null 时该步会被跳过
 * （最常见的原因是账号没有该模块的权限，侧栏里就不会有这一项）。</para>
 */
const STEPS = [
  {
    title: '欢迎使用 ClassislandControlHub 集控',
    text: '这是一套面向教室大屏的集中管理系统。接下来用一分钟带你走一遍主要流程，随时可以点「跳过」。',
    route: '#/dashboard',
    target: () => document.querySelector('.topbar-brand'),
  },
  {
    title: '配置档案',
    text: '课表、作息时间表与科目都在这里维护。可以用「新建示例档案」一键生成标准作息，也可以从 CSES 文件或 AI 导入现成课表。',
    route: '#/profiles',
    target: () => sideItem('profiles'),
  },
  {
    title: '设备管理',
    text: '按「楼栋 → 楼层 → 教室」组织教室终端：先建楼栋，再往楼里加楼层，最后把教室设备放进对应楼层。',
    route: '#/devices',
    target: () => sideItem('devices'),
  },
  {
    title: '配置下发',
    text: '把配置档案绑定到设备、分组或全局默认，并查看「哪些教室还没取到最新配置」，可一键重新推送。',
    route: '#/deploy',
    target: () => sideItem('deploy'),
  },
  {
    title: '远程管理',
    text: '远程命令行、插件启停、外观下发与即时提醒都在这里；指令是一次性派发的，不会重复执行。',
    route: '#/remote',
    target: () => sideItem('remote'),
  },
  {
    title: '定时提醒',
    text: '设置「每周一 8:00 提醒各班开晨会」这类定时任务，到点自动推送到教室大屏，可选语音播报。提醒按账号隔离，互不干扰。',
    route: '#/reminders',
    target: () => sideItem('reminders'),
  },
  {
    title: '时间偏移（授时）',
    text: '教室设备「每天快几秒」用固定偏移修不好，在这里填每日漂移，系统按天累计补偿后再下发给所有终端。',
    route: '#/timesync',
    target: () => sideItem('timesync'),
  },
  {
    title: '账号与权限',
    text: '换密码、开两步验证，以及（超级管理员）新建账号并按模块勾选权限、发放注册邀请码。',
    route: '#/accounts',
    target: () => sideItem('accounts'),
  },
  {
    title: '准备开始',
    text: '顶栏的「搜索」按钮就是命令面板（Ctrl + K），输入关键词可以直接跳到某个设备或某份档案；鼠标停在侧栏的页面上还能看到 Alt + 数字的跳页快捷键。',
    route: '#/dashboard',
    target: () => document.getElementById('searchBtn'),
  },
];

/**
 * 开始引导。
 * @param {{ onFinish?: (skipped: boolean) => void }} options 结束回调，`skipped` 表示是否被跳过。
 */
export function startTour(options = {}) {
  const host = document.getElementById('tourHost');
  if (!host) {
    options.onFinish?.(true);
    return;
  }

  // 刻意**不在开始前**按「目标是否存在」过滤步骤：二级侧栏只渲染当前分组的页面，
  // 别的分组的页面项此刻根本不在 DOM 里，那样过滤会把引导砍到只剩第一步。
  // 改成「走到哪一步、先切到那一页，再看目标在不在」，不在就跳过这一步
  // （下面 layout() 与 ensureRoute() 负责这件事）。
  const steps = STEPS;

  let index = 0;
  host.hidden = false;

  const spot = h('div.tour-spot');
  const card = h('div.tour-card');

  /**
   * 定位令牌：切页期间用户又点了「下一步」时，旧的那次定位不该再回来覆盖新位置。
   * 每次 paint 自增，等待循环里发现令牌变了就退出。
   */
  let navToken = 0;

  const finish = (skipped) => {
    navToken++;
    window.removeEventListener('keydown', onKey, true);
    window.removeEventListener('resize', layout);
    clear(host);
    host.hidden = true;
    options.onFinish?.(skipped);
  };

  /** 切到该步所属页面，并等目标元素出现（最多 1.6 秒）。 */
  async function ensureRoute(step, token) {
    const want = step.route;
    // 只比 hash 的路径部分：`#/devices?status=online` 也算在设备页。
    if (want && window.location.hash.split('?')[0] !== want) {
      window.location.hash = want;
    }

    const deadline = Date.now() + 1600;
    while (Date.now() < deadline) {
      if (token !== navToken) return;
      if (step.target()) return;
      await new Promise((r) => setTimeout(r, 80));
    }
  }

  function paintCard(step) {
    clear(card);
    card.append(
      h('div.tour-step', `第 ${index + 1} / ${steps.length} 步`),
      h('h3.tour-title', step.title),
      h('p.tour-text', step.text),
      h('div.tour-actions',
        h('button.btn.btn-sm', { type: 'button', onClick: () => finish(true) }, '跳过'),
        h('div.spacer'),
        index > 0
          ? h('button.btn.btn-sm', { type: 'button', onClick: () => { index--; paint(); } }, '上一步')
          : null,
        h('button.btn.btn-sm.btn-primary', {
          type: 'button',
          onClick: () => {
            if (index < steps.length - 1) {
              index++;
              paint();
            } else {
              finish(false);
            }
          },
        }, index < steps.length - 1 ? '下一步' : '开始使用'),
      ),
    );
  }

  async function paint() {
    const token = ++navToken;
    const step = steps[index];
    // 先把卡片画出来：切页要等一会儿，界面不能在这段时间里「什么都没发生」。
    paintCard(step);
    await ensureRoute(step, token);
    if (token !== navToken) return;
    layout();
  }

  /** 把高亮框与卡片对准当前步骤的目标元素。 */
  function layout() {
    const step = steps[index];
    const target = step.target();
    if (!target) {
      // 目标在引导过程中消失了（例如页面切换），直接跳到下一步。
      if (index < steps.length - 1) {
        index++;
        paint();
      } else {
        finish(true);
      }
      return;
    }

    const rect = target.getBoundingClientRect();
    const pad = 6;
    spot.style.left = `${rect.left - pad}px`;
    spot.style.top = `${rect.top - pad}px`;
    spot.style.width = `${rect.width + pad * 2}px`;
    spot.style.height = `${rect.height + pad * 2}px`;

    // 卡片贴在目标右侧；右侧空间不够就放到下方。
    const cardWidth = 340;
    const cardHeight = 200;
    let left = rect.right + 16;
    let top = rect.top;
    if (left + cardWidth > window.innerWidth - 12) {
      left = Math.max(12, Math.min(rect.left, window.innerWidth - cardWidth - 12));
      top = rect.bottom + 14;
    }
    if (top + cardHeight > window.innerHeight - 12) {
      top = Math.max(12, window.innerHeight - cardHeight - 12);
    }

    card.style.left = `${left}px`;
    card.style.top = `${top}px`;
  }

  function onKey(event) {
    if (event.key === 'Escape') {
      event.preventDefault();
      finish(true);
    } else if (event.key === 'ArrowRight' || event.key === 'Enter') {
      event.preventDefault();
      if (index < steps.length - 1) {
        index++;
        paint();
      } else {
        finish(false);
      }
    } else if (event.key === 'ArrowLeft' && index > 0) {
      event.preventDefault();
      index--;
      paint();
    }
  }

  clear(host);
  host.append(spot, card);
  paint();

  window.addEventListener('keydown', onKey, true);
  window.addEventListener('resize', layout);
}
