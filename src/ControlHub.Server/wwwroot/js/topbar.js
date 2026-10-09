/**
 * 顶栏交互（下拉、抽屉、收起导航）。
 *
 * <para>与方案里的 `topbar.js` 有几处刻意的不同，都是为了不破坏现有行为：</para>
 * <list type="bullet">
 *   <item>**不自己写主题切换**。项目已有完整的主题逻辑（跟随系统 / 浅 / 深 + localStorage +
 *       品牌配置同步，见 core/prefs.js 与 app.js），再造一套会出现「两套主题状态打架」。
 *       本模块只负责「移动端抽屉」与「导航收起」。</item>
 *   <item>**不硬编码导航项**。方案里的 `initMobileMenu` 把导航 HTML 写死在 JS 里，
 *       那会导致「加了页面要改两个地方」，而且无权限的页面也会出现在抽屉里（信息泄露）。
 *       这里改成**克隆桌面端已渲染好的导航**——它已经过了权限过滤。</item>
 *   <item>**不监听 hashchange 去改导航**。项目有统一的 `route()`，在它里面同步高亮
 *       （见 app.js 的 syncActiveNavGroup），避免两处都在改。</item>
 * </list>
 */

/**
 * 是否处于「窄屏需要抽屉」的状态。与 topbar.css 的断点保持一致。
 * <para>720px（不是 900px）：900px 会在「笔记本窗口没开满」「高 DPI 缩放」
 * 这类常见情况下命中，把导航换成抽屉——用户看到的是「导航自己消失了」。</para>
 */
const NARROW_QUERY = window.matchMedia('(max-width: 720px)');

/**
 * 初始化顶栏交互。由 app.js 的 bindShellEvents 调用一次。
 * @param {object} handlers
 * @param {() => boolean} handlers.isCollapsed 读导航收起状态。
 * @param {(next: boolean) => void} handlers.setCollapsed 写导航收起状态。
 * @param {() => void} [handlers.onNavigate] 导航后回调（用于收起抽屉）。
 */
export function initTopbar(handlers = {}) {
  // 点空白处 / 按 Esc 收起所有下拉。
  // 用 pointerdown 而不是 click：等 click 时菜单已经跟着手指闪了一下。
  document.addEventListener('pointerdown', (event) => {
    if (!event.target.closest('.nav-group')) {
      document.querySelectorAll('.nav-group.open')
        .forEach((el) => el.classList.remove('open'));
    }
  });

  document.addEventListener('keydown', (event) => {
    if (event.key !== 'Escape') return;
    const drawer = document.querySelector('.nav-drawer');
    if (drawer) {
      drawer.remove();
      return;
    }

    document.querySelectorAll('.nav-group.open').forEach((el) => el.classList.remove('open'));
  });

  // 折叠按钮在顶栏左侧：窄屏时它变成「打开抽屉」，宽屏时是「收起导航」。
  const toggle = document.getElementById('collapseBtn');
  if (toggle) {
    toggle.addEventListener('click', () => {
      if (NARROW_QUERY.matches) {
        toggleDrawer();
        return;
      }

      const next = !handlers.isCollapsed?.();
      handlers.setCollapsed?.(next);
      syncToggleState(next);
    });

    // 初始状态：折叠是持久化的（localStorage），但按钮此前**没有反映它**——
    // 用户上次折叠过、这次打开页面看到导航是空的，会以为「导航自己没了」。
    syncToggleState(!!handlers.isCollapsed?.());
  }

  // 从窄屏拉回宽屏时，抽屉要自动消失——它盖住整屏，留着会让人以为页面坏了。
  NARROW_QUERY.addEventListener('change', (event) => {
    if (!event.matches) {
      document.querySelector('.nav-drawer')?.remove();
    }
  });
}

/**
 * 同步折叠按钮的外观与提示语。
 * <para>按钮必须**看得出当前状态**：折叠是记住的，如果按钮长得一直一样，
 * 用户下次打开会以为导航坏了；提示语也要写清「点下去会发生什么」。</para>
 */
function syncToggleState(collapsed) {
  const toggle = document.getElementById('collapseBtn');
  if (!toggle) {
    return;
  }

  toggle.classList.toggle('is-active', collapsed);
  toggle.setAttribute('aria-pressed', collapsed ? 'true' : 'false');
  toggle.title = collapsed ? '显示侧边导航' : '隐藏侧边导航';
  toggle.setAttribute('aria-label', toggle.title);
}

/** 打开 / 关闭移动端抽屉。 */
export function toggleDrawer() {
  const existing = document.querySelector('.nav-drawer');
  if (existing) {
    existing.remove();
    return;
  }

  // 克隆桌面端导航，而不是另写一份：
  // ① 权限过滤只做一次；② 加页面不用改这里；③ 两边永远不会不一致。
  const source = document.getElementById('navList');
  if (!source) return;

  const drawer = document.createElement('div');
  drawer.className = 'nav-drawer';
  drawer.setAttribute('role', 'dialog');
  drawer.setAttribute('aria-label', '导航菜单');

  for (const group of source.querySelectorAll('.nav-group')) {
    const groupBox = document.createElement('div');
    groupBox.className = 'nav-drawer-group';

    const title = document.createElement('div');
    title.className = 'nav-drawer-title';
    title.textContent = group.dataset.group || '';
    groupBox.appendChild(title);

    for (const item of group.querySelectorAll('.nav-item')) {
      // cloneNode **不复制事件监听器**，所以跳转逻辑必须在这里重新绑一次；
      // 目标 hash 从 dataset 读（renderNav 时写进去的），不要去反查 DOM。
      const hash = item.dataset.hash;
      const clone = item.cloneNode(true);
      clone.addEventListener('click', () => {
        // 点完立刻关抽屉：否则新页面被抽屉盖住，用户以为没跳转。
        drawer.remove();
        if (hash) {
          window.location.hash = hash;
        }
      });
      groupBox.appendChild(clone);
    }

    drawer.appendChild(groupBox);
  }

  // 点抽屉里的空白处也关掉（跟遮罩点击一个道理）。
  drawer.addEventListener('click', (event) => {
    if (event.target === drawer) {
      drawer.remove();
    }
  });

  document.body.appendChild(drawer);
}
