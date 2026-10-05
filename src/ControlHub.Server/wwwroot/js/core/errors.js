/**
 * 错误提示可操作化（#51）——界面层入口。
 *
 * 现状问题：页面上大量 `toast('error', '保存失败', err.message)` 只把服务端那句话转述一遍——
 * 管理员看到「当前账号没有此操作所需的权限」时仍然不知道**该找谁、去哪里勾**。
 *
 * 做法：错误码 → 「怎么办」的映射放在 `error-hints.js`（零依赖），这里负责把它接到界面上。
 * 服务端原文（message）照旧保留——它是给排查用的细节，不该被替换掉，但也不该是管理员唯一能看到的东西。
 *
 * 本模块只被视图层使用；`ui.js` 的通用兜底走 `error-hints.js`，避免循环依赖。
 */

import { h, toast } from './ui.js?v=57';
import { describeError, formatErrorText, hasErrorHint } from './error-hints.js?v=57';

/**
 * 弹一个带「怎么办」的错误提示。
 * @param {any} err 错误对象。
 * @param {string} [title] 标题，例如「下发失败」。省略时用「操作失败」。
 */
export function toastError(err, title = '操作失败') {
  // 带建议的多留一会儿：读完一句话再判断要做什么，默认的 3.6 秒不够。
  toast('error', title, formatErrorText(err), hasErrorHint(err) ? 8000 : 4200);
}

/**
 * 页面内的错误块：比 toast 持久，适合「这一块内容没加载出来」。
 * @param {any} err 错误对象。
 * @param {{title?:string, onRetry?:Function}} [options] 标题与「重试」按钮回调。
 */
export function errorBlock(err, options = {}) {
  const info = describeError(err);

  return h('div.notice.notice-danger',
    h('span.notice-icon', '!'),
    h('div', { style: { minWidth: 0 } },
      h('div', { style: { fontWeight: '600' } }, options.title || '加载失败'),
      info.message ? h('div', { style: { marginTop: '4px' } }, info.message) : null,
      info.hint
        ? h('div', { style: { marginTop: '4px', color: 'var(--text-faint)' } }, `怎么办：${info.hint}`)
        : null,
      options.onRetry
        ? h('button.btn.btn-sm', {
          type: 'button',
          style: { marginTop: '8px' },
          onClick: options.onRetry,
        }, '重试')
        : null,
    ),
  );
}
