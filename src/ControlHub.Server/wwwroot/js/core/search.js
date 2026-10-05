/**
 * 全局搜索面板（#41）。
 *
 * 交互：`Ctrl + K` 或点顶栏「搜索」打开 → 输入关键字（防抖 220ms）→ `↑`/`↓` 选择、`Enter` 打开、`Esc` 关闭。
 * 结果由服务端 `/admin/search` 聚合返回（设备 / 分组 / 档案 / 账号），每项自带跳转 hash，
 * 前端不需要知道各类实体怎么定位。
 *
 * 为什么做成面板而不是「顶栏一个搜索框」：搜索是跨页面的，面板里能同时看到四类结果并直接跳走；
 * 顶栏输入框会让人以为只搜当前页。
 */

import { api } from './api.js?v=57';
import { h, clear, modal, icon } from './ui.js?v=57';

/** 类别标签：与后端 `SearchItemDto.Kind` 对应。 */
const KIND_LABELS = {
  device: '设备',
  group: '分组',
  profile: '档案',
  account: '账号',
};

/** 输入到发请求之间的等待（毫秒）：太短会让每敲一个字都打一次接口。 */
const DEBOUNCE_MS = 220;

/** 打开全局搜索面板（连按 Ctrl+K 不会叠出多个）。 */
export function openSearch() {
  const host = document.getElementById('modalHost');

  // 已经有弹窗开着（多半就是搜索面板本身）就不再叠加：连按快捷键是最常见的误操作。
  if (host && host.hidden === false) {
    return;
  }

  const input = h('input.search-input', {
    type: 'search',
    placeholder: '搜索设备、分组、档案、账号…',
    spellcheck: 'false',
    autocomplete: 'off',
  });

  const resultBox = h('div.search-results');
  const hint = h('p.search-hint', '输入关键字开始搜索。↑↓ 选择，Enter 打开，Esc 关闭。');

  /** 当前结果与选中项。 */
  let items = [];
  let active = 0;
  /** 请求序号：只认最后一次请求的结果，避免先发的慢请求覆盖后发的。 */
  let sequence = 0;
  let timer = null;

  const dialog = modal({
    title: '全局搜索',
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
    window.location.hash = item.hash;
  }

  function renderResults() {
    clear(resultBox);
    items.forEach((item, index) => {
      resultBox.appendChild(h(`button.search-item${index === active ? '.active' : ''}`, {
        type: 'button',
        onClick: () => jump(item),
      },
        h('span.search-kind', KIND_LABELS[item.kind] || item.kind),
        h('span.search-text',
          h('span.search-title', item.title),
          item.subtitle ? h('span.search-sub', item.subtitle) : null,
        ),
      ));
    });
  }

  async function runSearch() {
    const keyword = input.value.trim();
    if (keyword.length === 0) {
      items = [];
      active = 0;
      clear(resultBox);
      hint.textContent = '输入关键字开始搜索。↑↓ 选择，Enter 打开，Esc 关闭。';
      return;
    }

    const mine = ++sequence;
    try {
      const result = await api('/admin/search', { query: { q: keyword, limit: 20 } });
      if (mine !== sequence) {
        return;
      }

      items = result?.items || [];
      active = 0;
      renderResults();
      hint.textContent = items.length > 0
        ? `共 ${items.length} 条结果。↑↓ 选择，Enter 打开。`
        : `没有找到与「${keyword}」相关的设备、分组、档案或账号。`;
    } catch (err) {
      if (mine !== sequence) {
        return;
      }

      items = [];
      clear(resultBox);
      // 搜索失败时说清原因，而不是留下一个空面板让人以为「没搜到」。
      hint.textContent = `搜索失败：${err.message || '请求失败'}`;
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
}
