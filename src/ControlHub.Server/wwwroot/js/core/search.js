/**
 * 命令面板 + 全局搜索（#41 的面板，本轮升级为「命令 + 搜索」合一）。
 *
 * 交互：`Ctrl + K` 或点顶栏「搜索」打开 → 输入（防抖 220ms）→ `↑`/`↓` 选择、`Enter` 执行、`Esc` 关闭。
 *
 * <para>为什么把两者合在一个面板里，而不是「命令面板一套快捷键、搜索另一个」：
 * 用户不该记「找东西按哪个键、做事情按哪个键」。他输「通知」的时候，
 * 既可能想找那台装了通知插件的设备，也可能想直接发通知——面板把两种结果并排给出，
 * 让他按意图选，而不是按分类选。</para>
 *
 * <para>空闲时（没输入任何字）显示命令清单，而不是给一句「输入关键字开始搜索」：
 * 面板最大的价值是「不知道功能在哪」时也能用。</para>
 */

import { api } from './api.js?v=86';
import { h, clear, modal, icon } from './ui.js?v=86';

/** 类别标签：与后端 `SearchItemDto.Kind` 对应；后三个是本面板自己造的类别。 */
const KIND_LABELS = {
  device: '设备',
  group: '分组',
  profile: '档案',
  account: '账号',
  action: '命令',
  command: '页面',
  found: '搜索结果',
};

/**
 * 可执行命令（做事情）。
 *
 * <para>刻意**只有真正能立刻执行的**才放进来。「进远程管理看看」这种需要再选目标、
 * 再填参数的流程不算命令——放进来只会让人点了个寂寞。</para>
 *
 * <para>`keys` 显示在右侧，**必须与 core/shortcuts.js 里真实绑定的键位一致**：
 * 写一个没绑的快捷键比不写更糟（用户按了没反应，反而以为面板坏了）。</para>
 */
const ACTION_COMMANDS = [
  {
    id: 'act-notify',
    label: '发送通知',
    keys: ['N'],
    hint: '挑设备后批量推送文字或图片',
    // 落到设备管理并预置「在线」筛选：通知讲时效，离线的设备发过去也没人看。
    run: () => { window.location.hash = '#/devices?status=online'; },
  },
  {
    id: 'act-deploy',
    label: '下发配置',
    keys: ['P'],
    hint: '把配置档案推送到设备',
    run: () => { window.location.hash = '#/deploy'; },
  },
  {
    id: 'act-remote',
    label: '远程管理',
    keys: [],
    hint: '指令、插件、外观与诊断',
    run: () => { window.location.hash = '#/remote'; },
  },
  {
    id: 'act-reminders',
    label: '定时提醒',
    keys: [],
    hint: '按日期与周期自动推送到大屏',
    run: () => { window.location.hash = '#/reminders'; },
  },
];

/** 输入到发请求之间的等待（毫秒）：太短会让每敲一个字都打一次接口。 */
const DEBOUNCE_MS = 220;

/**
 * 打开命令面板（连按 Ctrl+K 不会叠出多个）。
 * @param {Array<{key: string, label: string, hash: string}>} [navItems]
 *   侧边栏导航项，由 app.js 传入。**从外面传进来**而不是在这里 import app.js：
 *   导航定义在 app.js 里，从这里反向依赖会绕成环。
 */
export function openSearch(navItems = []) {
  const host = document.getElementById('modalHost');

  // 已经有弹窗开着（多半就是搜索面板本身）就不再叠加：连按快捷键是最常见的误操作。
  if (host && host.hidden === false) {
    return;
  }

  const input = h('input.search-input', {
    type: 'search',
    placeholder: '搜设备 / 分组 / 档案 / 账号，或输入命令…',
    spellcheck: 'false',
    autocomplete: 'off',
  });

  const resultBox = h('div.search-results');
  const hint = h('p.search-hint');

  /** 归一化后的候选：{ type, label, sub, keys, run } —— 搜索结果与命令统一成同一种形状。 */
  let items = [];
  let active = 0;
  /** 请求序号：只认最后一次请求的结果，避免先发的慢请求覆盖后发的。 */
  let sequence = 0;
  let timer = null;

  const dialog = modal({
    title: '命令与搜索',
    width: 'wide',
    hideFooter: true,
    // 任何关闭路径（✕ / Esc / 点遮罩 / Enter 跳转）都要停掉待发的防抖请求。
    onClose: () => clearTimeout(timer),
    body: h('div.search-panel',
      h('div.search-box',
        h('span.search-box-icon', icon('search', 15)),
        input,
      ),
      resultBox,
      hint,
    ),
  });

  function jump(item) {
    if (!item) {
      return;
    }

    dialog.close();
    item.run();
  }

  function renderResults() {
    clear(resultBox);
    if (items.length === 0) {
      return;
    }

    let lastKind = null;
    items.forEach((item, index) => {
      // 同类的相邻项才插分组标题：命令与搜索结果交错时，按类型分段更好读。
      if (item.kind !== lastKind) {
        lastKind = item.kind;
        resultBox.appendChild(h('div.search-group', KIND_LABELS[item.kind] || item.kind));
      }

      resultBox.appendChild(h(`button.search-item${index === active ? '.active' : ''}`, {
        type: 'button',
        onClick: () => jump(item),
      },
        h('span.search-kind', item.badge || ''),
        h('span.search-text',
          h('span.search-title', item.label),
          item.sub ? h('span.search-sub', item.sub) : null,
        ),
        item.keys && item.keys.length
          ? h('span.search-keys', ...item.keys.map((k) => h('kbd', k)))
          : null,
      ));
    });
  }

  /** 命令清单（跳页 + 动作），不随关键字变化。 */
  function allCommands() {
    const pages = (navItems || []).map((item) => ({
      kind: 'command',
      badge: '页面',
      label: item.label,
      sub: item.groupLabel || '',
      keys: item.keys || [],
      run: () => { window.location.hash = item.hash; },
    }));


    const actions = ACTION_COMMANDS.map((c) => ({
      kind: 'action',
      badge: '命令',
      label: c.label,
      sub: c.hint,
      keys: c.keys,
      run: c.run,
    }));

    return [...actions, ...pages];
  }

  /** 命令的匹配：标签命中优先，其次是副标题（提示语）命中。 */
  function matchCommands(keyword) {
    const kw = keyword.toLowerCase();
    return allCommands()
      .map((c) => {
        const inLabel = c.label.toLowerCase().includes(kw);
        const inSub = (c.sub || '').toLowerCase().includes(kw);
        return { c, score: inLabel ? 0 : (inSub ? 1 : -1) };
      })
      .filter((x) => x.score >= 0)
      .sort((a, b) => a.score - b.score)
      .map((x) => x.c);
  }

  async function runSearch() {
    const keyword = input.value.trim();
    if (keyword.length === 0) {
      // 空闲态：给命令清单。这比「输入关键字开始搜索」有用得多——
      // 面板最该解决的问题是「不知道这个功能在哪」。
      items = allCommands();
      active = 0;
      renderResults();
      hint.textContent = `共 ${items.length} 条命令。↑↓ 选择，Enter 执行，Esc 关闭。`;
      return;
    }

    const mine = ++sequence;
    const commands = matchCommands(keyword);
    let results = [];
    try {
      const result = await api('/admin/search', { query: { q: keyword, limit: 20 } });
      if (mine !== sequence) {
        return;
      }

      results = (result?.items || []).map((item) => ({
        kind: 'found',
        badge: KIND_LABELS[item.kind] || item.kind,
        label: item.title,
        sub: item.subtitle,
        keys: [],
        run: () => { window.location.hash = item.hash; },
      }));
    } catch (err) {
      if (mine !== sequence) {
        return;
      }

      hint.textContent = `搜索失败：${err.message || '请求失败'}`;
    }

    // 命令排前面：输入框里的字是「我要做什么」的概率高于「我要找什么」。
    items = [...commands, ...results];
    active = 0;
    renderResults();
    if (items.length > 0) {
      hint.textContent = `找到 ${items.length} 条。↑↓ 选择，Enter 打开。`;
    } else if (keyword.length > 0) {
      hint.textContent = `没有找到与「${keyword}」相关的命令、设备、分组、档案或账号。`;
    }
  }

  input.addEventListener('input', () => {
    clearTimeout(timer);
    timer = setTimeout(runSearch, DEBOUNCE_MS);
  });

  input.addEventListener('keydown', (event) => {
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      if (items.length > 0) {
        active = (active + 1) % items.length;
        renderResults();
      }

      return;
    }

    if (event.key === 'ArrowUp') {
      event.preventDefault();
      if (items.length > 0) {
        active = (active - 1 + items.length) % items.length;
        renderResults();
      }

      return;
    }

    if (event.key === 'Enter') {
      event.preventDefault();
      jump(items[active]);
    }
  });

  setTimeout(() => input.focus(), 60);
  // 打开即渲染命令（不等输入，防抖只作用于后续输入）。
  runSearch();
}
