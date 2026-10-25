/**
 * 首次使用引导页（#126）。
 *
 * <p>
 * **实例级**的一次性引导：部署完第一次进管理台时出现，看完（或跳过）就不再出现。
 * 与「新手引导」（core/tour.js，账号级、在管理台里高亮讲解）是两件不同的事：
 * 这个解决"刚部署完面对一片空白不知从哪下手"，那个解决"进来了但不会用某个页面"。
 * </p>
 *
 * <p>
 * 设计上的几条硬约束：
 * </p>
 * <ul>
 *   <li>**不阻塞**：右上角随时能跳过；读取状态失败也一律放行进管理台。</li>
 *   <li>**数据驱动**：已经有设备 / 已有档案，对应步骤直接显示"已完成"，不必再走一遍。</li>
 *   <li>**不进侧边栏**：只在路由表里存在，靠首次访问或「页面设置 → 重新播放」进入。</li>
 * </ul>
 */

import { api, session } from '../core/api.js?v=144';
import { toastError } from '../core/errors.js?v=144';
import { h, clear, toast, copyText } from '../core/ui.js?v=144';
import { t } from '../core/i18n.js?v=144';

export const meta = {
  title: t('welcome.title'),
  subtitle: t('welcome.subtitle'),
};

/** 兼容两种返回形状：直接数组，或 { items } / { devices }。 */
function asList(value) {
  if (Array.isArray(value)) return value;
  if (Array.isArray(value?.items)) return value.items;
  return [];
}

/** 取当前实例的落地情况：有没有设备、有没有档案。 */
async function loadSummary() {
  const [info, profiles] = await Promise.all([
    api('/server/info').catch(() => null),
    api('/admin/profiles').catch(() => null),
  ]);

  const deviceCount = info?.deviceCount ?? 0;
  return {
    deviceCount,
    onlineCount: info?.onlineDeviceCount ?? 0,
    profileCount: asList(profiles).length,
  };
}

export async function render(container) {
  clear(container);

  const summary = await loadSummary();
  const state = { index: 0, summary };

  const stepsHost = h('div.welcome-body');
  const dotsHost = h('div.welcome-dots');
  // ⚠️ 必须在首次 paint() 之前声明：步骤内容里会用到它，晚一步就是 TDZ 报错（踩过）。
  const codeHost = h('div.welcome-code', { hidden: true });
  const prevBtn = h('button.btn.btn-sm', { type: 'button', onClick: () => go(-1) }, t('welcome.prev'));
  const nextBtn = h('button.btn.btn-primary.btn-sm', { type: 'button', onClick: () => go(1) }, t('welcome.next'));

  const page = h('div.welcome-page',
    h('div.welcome-card.glass',
      h('button.btn.btn-ghost.btn-sm.welcome-skip', {
        type: 'button',
        onClick: () => finish(),
      }, t('welcome.skip')),
      stepsHost,
      h('div.welcome-foot', prevBtn, dotsHost, nextBtn),
    ),
  );

  container.appendChild(page);
  paint();

  /** 步骤定义：每次刷新 summary 后重建（"已完成"状态要跟着变）。 */
  function buildSteps() {
    const { deviceCount, onlineCount, profileCount } = state.summary;
    return [
      {
        id: 'hello',
        title: t('welcome.step1Title'),
        subtitle: t('welcome.step1Subtitle'),
        body: () => h('div.welcome-hero',
          h('div.welcome-emoji', '🏫'),
          h('p.welcome-lead', t('welcome.step1Body')),
          h('p.welcome-note', t('welcome.step1Note')),
        ),
      },
      {
        id: 'devices',
        title: t('welcome.step2Title'),
        subtitle: t('welcome.step2Subtitle'),
        done: deviceCount > 0,
        body: () => (deviceCount > 0
          ? h('div.welcome-done', t('welcome.step2Done', { count: deviceCount, online: onlineCount }))
          : h('div',
            h('ol.welcome-list',
              h('li', t('welcome.step2Item1')),
              h('li', t('welcome.step2Item2')),
              h('li', t('welcome.step2Item3'))),
            h('div.welcome-actions',
              h('button.btn.btn-primary.btn-sm', {
                type: 'button',
                onClick: (event) => createEnrollCode(event.currentTarget),
              }, t('welcome.step2Action')),
              h('a.btn.btn-ghost.btn-sm', { href: '#/devices', style: { textDecoration: 'none' } },
                t('welcome.step2Go')),
            ),
            codeHost,
          )),
      },
      {
        id: 'profiles',
        title: t('welcome.step3Title'),
        subtitle: t('welcome.step3Subtitle'),
        done: profileCount > 0,
        body: () => (profileCount > 0
          ? h('div.welcome-done', t('welcome.step3Done', { count: profileCount }))
          : h('div',
            h('p.welcome-note', t('welcome.step3Body')),
            h('div.welcome-actions',
              h('button.btn.btn-primary.btn-sm', {
                type: 'button',
                onClick: (event) => createSampleProfile(event.currentTarget),
              }, t('welcome.step3Action')),
              h('a.btn.btn-ghost.btn-sm', { href: '#/profiles', style: { textDecoration: 'none' } },
                t('welcome.step3Go')),
            ),
          )),
      },
      {
        id: 'ready',
        title: t('welcome.step4Title'),
        subtitle: t('welcome.step4Subtitle'),
        body: () => h('div',
          h('div.welcome-features',
            feature('🔔', t('welcome.feature1'), t('welcome.feature1Desc')),
            feature('🔄', t('welcome.feature2'), t('welcome.feature2Desc')),
            feature('⌨️', t('welcome.feature3'), t('welcome.feature3Desc'))),
          h('p.welcome-note', t('welcome.step4Note')),
        ),
      },
    ];
  }

  function feature(emoji, label, desc) {
    return h('div.welcome-feature',
      h('span.welcome-feature-icon', emoji),
      h('span.welcome-feature-label', label),
      h('span.welcome-feature-desc', desc));
  }

  function paint() {
    const steps = buildSteps();
    const step = steps[state.index];
    stepsHost.replaceChildren(
      h('div.welcome-head',
        h('h2.welcome-title', step.title),
        h('p.welcome-sub', step.subtitle)),
      step.body(),
    );

    dotsHost.replaceChildren(...steps.map((item, index) => h('span.welcome-dot', {
      class: index === state.index ? 'active' : '',
      title: item.title,
    })));

    prevBtn.hidden = state.index === 0;
    nextBtn.textContent = state.index === steps.length - 1
      ? t('welcome.enterConsole')
      : t('welcome.next');
  }

  function go(delta) {
    const steps = buildSteps();
    if (delta > 0 && state.index === steps.length - 1) {
      finish();
      return;
    }

    state.index = Math.min(steps.length - 1, Math.max(0, state.index + delta));
    paint();
  }

  /** 看完 / 跳过：标记实例状态后进管理台。失败也放行——绝不能用引导页把人挡在外面。 */
  async function finish() {
    try {
      await api('/admin/welcome/complete', { method: 'POST' });
    } catch {
      // 标记失败不影响使用：下次登录会再看到一遍，比"进不去管理台"好得多。
    }

    window.location.hash = '#/dashboard';
  }

  async function createEnrollCode(button) {
    button.disabled = true;
    try {
      const created = await api('/admin/enroll-codes', {
        method: 'POST',
        body: { maxUses: 0, validHours: 0, note: t('welcome.codeNote') },
      });
      const code = created?.code || created?.id || '';
      codeHost.hidden = false;
      codeHost.replaceChildren(
        h('span.welcome-code-value', code),
        h('button.btn.btn-ghost.btn-sm', {
          type: 'button',
          onClick: () => copyText(code, t('welcome.codeCopied')),
        }, t('welcome.copy')),
      );
      toast('ok', t('welcome.codeCreated'));
    } catch (err) {
      // 没有设备写权限时这里会 403：如实说明，别让人以为按钮坏了。
      toastError(err, t('welcome.codeFailed'));
    } finally {
      button.disabled = false;
    }
  }

  async function createSampleProfile(button) {
    button.disabled = true;
    try {
      await api('/admin/profiles/sample', { method: 'POST' });
      toast('ok', t('welcome.profileCreated'));
      state.summary = await loadSummary();
      paint();
    } catch (err) {
      toastError(err, t('welcome.profileFailed'));
    } finally {
      button.disabled = false;
    }
  }
}

/**
 * 该不该把人送去引导页（实例级，一天只该发生一次）。
 *
 * <p>读取失败一律返回 false：**宁可少引导一次，也不能因为一次 500 把人挡在管理台外面**。</p>
 */
export async function shouldShowWelcome() {
  try {
    const status = await api('/admin/welcome/status');
    return status?.completed !== true;
  } catch {
    return false;
  }
}

/** 当前账号有没有权限看引导页（所有人都能看，这里只是为了和调用处对称）。 */
export function canOpenWelcome() {
  return Boolean(session?.me);
}
