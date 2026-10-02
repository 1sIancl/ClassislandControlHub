/**
 * 新手引导：按步骤高亮侧边栏与关键元素，支持随时跳过。
 * 走完或跳过后由调用方（app.js）把「已完成」记到当前账号上。
 */

import { h, clear } from './ui.js?v=34';

/** 引导步骤。`target` 返回 null 时该步会被自动跳过（例如账号没有对应权限）。 */
const STEPS = [
  {
    title: '欢迎使用 ClassislandControlHub 集控',
    text: '这是一套面向教室大屏的集中管理系统。接下来用一分钟带你走一遍主要流程，随时可以点「跳过」。',
    target: () => document.querySelector('.brand'),
  },
  {
    title: '第一步：配置档案',
    text: '课表、作息时间表与科目都在这里维护。可以用「新建示例档案」一键生成标准作息，也可以从 CSES 文件或 AI 导入现成课表。',
    target: () => document.querySelector('[data-key="profiles"]'),
  },
  {
    title: '第二步：设备管理',
    text: '按「楼栋 → 楼层 → 教室」组织教室终端：先建楼栋，再往楼里加楼层，最后把教室设备拖进对应楼层。',
    target: () => document.querySelector('[data-key="devices"]'),
  },
  {
    title: '第三步：配置下发',
    text: '把配置档案绑定到设备、分组或全局默认，并查看「哪些教室还没取到最新配置」，可一键重新推送。',
    target: () => document.querySelector('[data-key="deploy"]'),
  },
  {
    title: '远程管理',
    text: '远程命令行、插件启停、外观下发与即时提醒都在这里；指令是一次性派发的，不会重复执行。',
    target: () => document.querySelector('[data-key="remote"]'),
  },
  {
    title: '定时提醒',
    text: '设置「每周一 8:00 提醒各班开晨会」这类定时任务，到点自动推送到教室大屏，可选语音播报。提醒按账号隔离，互不干扰。',
    target: () => document.querySelector('[data-key="reminders"]'),
  },
  {
    title: '系统设置',
    text: '在这里换密码、改品牌外观、校准时间偏移，以及（超级管理员）新建账号并按模块勾选权限、发放注册邀请码。',
    target: () => document.querySelector('[data-key="settings"]'),
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

  const steps = STEPS.filter((step) => step.target());
  if (steps.length === 0) {
    options.onFinish?.(true);
    return;
  }

  let index = 0;
  host.hidden = false;

  const spot = h('div.tour-spot');
  const card = h('div.tour-card');

  const finish = (skipped) => {
    window.removeEventListener('keydown', onKey, true);
    window.removeEventListener('resize', layout);
    clear(host);
    host.hidden = true;
    options.onFinish?.(skipped);
  };

  function paint() {
    const step = steps[index];
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
