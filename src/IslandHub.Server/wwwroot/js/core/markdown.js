/**
 * 极小的 Markdown 子集渲染（#121 后续：插件详情里看作者的自述文件）。
 *
 * <para>**为什么自己写**：这里只需要看懂插件 README 的常规结构（标题、列表、代码块、链接、粗体），
 * 引一个 markdown 库要背一整棵依赖树，而这个前端一直是零依赖的。</para>
 *
 * <para><b>安全是第一位</b>：README 是**第三方内容**，来自插件作者的仓库。所以顺序永远是
 * 「先把所有文本转义，再在转义后的结果上套结构」——README 里就算写了 `&lt;script&gt;`，
 * 也只会显示成文字。链接只放行 http / https（挡掉 `javascript:` 这类），并统一带
 * `rel="noopener noreferrer"`、`target="_blank"`。</para>
 */

import { t } from './i18n.js?v=157';

/** HTML 转义（所有进入 innerHTML 的文本都必须先过这里）。 */
function escapeHtml(text) {
  return text
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}

/** 行内元素：先取代码段（其中的 `**` 不该再被当语法），再处理粗体与链接。 */
function inlineHtml(escaped) {
  const parts = escaped.split('`');
  let html = '';
  for (let i = 0; i < parts.length; i++) {
    if (i % 2 === 1) {
      html += `<code>${parts[i]}</code>`;
      continue;
    }

    let text = parts[i];

    // 图片一律降级成链接（#121 后续）：README 里的截图是 GitHub 外链，
    // 管理端浏览器直连多半加载不出来——留一堆碎图不如给一个「点开看」。
    // 这里处理的是**转义后**的文本，所以标签长得像 `&lt;img src="..."&gt;`。
    text = text.replace(/!\[([^\]]*)\]\(([^)\s]+)\)/g, (match, alt, url) =>
      /^https?:\/\//i.test(url.replaceAll('&amp;', '&'))
        ? t('md.imageLink', { url, label: alt || t('md.view') })
        : (alt || t('md.image')));
    text = text.replace(/&lt;img[^&]*?src="([^"]+)"[\s\S]*?&gt;/gi, (match, url) =>
      /^https?:\/\//i.test(url.replaceAll('&amp;', '&'))
        ? t('md.imageLink', { url, label: t('md.view') })
        : t('md.image'));

    text = text.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
    text = text.replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, (match, label, url) => {
      // 转义后的 URL 里 & 已经变成 &amp;，这里还原一下再判断协议。
      const raw = url.replaceAll('&amp;', '&');
      return /^https?:\/\//i.test(raw)
        ? `<a href="${url}" target="_blank" rel="noopener noreferrer">${label}</a>`
        : label;
    });
    html += text;
  }

  return html;
}

/**
 * 管理端「富文本预览」（#11）：输入时实时渲染提醒 / 通知的正文。
 *
 * <p>
 * 为什么单独做一个函数而不是让调用方自己拼：**安全规则必须和最终渲染完全一致**。
 * 预览要是比真渲染"更宽松"，用户就会以为自己能写的东西其实发不出去；
 * 反过来"更严格"则会让人反复试。所以两者共用同一个 <see cref="renderMarkdown"/>。
 * </p>
 *
 * @param {() => string} getText 取当前输入文本。
 * @returns {{ element: HTMLElement, refresh: () => void }} 预览容器与手动刷新函数。
 */
export function createRichPreview(getText) {
  const host = document.createElement('div');
  host.className = 'md-preview';
  // 标签靠 CSS ::before 显示（不占 DOM 文本，读屏与截图都不会重复念一遍）。
  host.dataset.label = t('md.previewLabel');
  host.hidden = true;

  const refresh = () => {
    const text = typeof getText === 'function' ? (getText() || '') : '';
    // 空内容不显示空壳（会让人以为"预览坏了"）。
    host.hidden = text.trim().length === 0;
    host.replaceChildren(renderMarkdown(text));
  };

  refresh();
  return { element: host, refresh };
}

/**
 * 把一段 Markdown 渲染成一个元素（放进弹窗里用）。
 * @param {string} text Markdown 原文。
 * @returns {HTMLElement} 渲染结果容器。
 */
export function renderMarkdown(text) {
  const host = document.createElement('div');
  host.className = 'md-body';
  if (typeof text !== 'string' || text.trim().length === 0) {
    return host;
  }

  const lines = text.replaceAll('\r\n', '\n').replaceAll('\r', '\n').split('\n');
  const out = [];
  let index = 0;
  let paragraph = [];

  const flushParagraph = () => {
    if (paragraph.length > 0) {
      out.push(`<p>${inlineHtml(escapeHtml(paragraph.join(' ')))}</p>`);
      paragraph = [];
    }
  };

  while (index < lines.length) {
    const line = lines[index];

    // 围栏代码块：原样保留缩进与换行（只转义，不解析语法）。
    if (/^\s*```/.test(line)) {
      flushParagraph();
      const code = [];
      index++;
      while (index < lines.length && !/^\s*```/.test(lines[index])) {
        code.push(lines[index]);
        index++;
      }

      index++;
      out.push(`<pre><code>${escapeHtml(code.join('\n'))}</code></pre>`);
      continue;
    }

    // 标题（# 到 ######）。
    const heading = /^(#{1,6})\s+(.*)$/.exec(line);
    if (heading) {
      flushParagraph();
      const level = Math.min(6, heading[1].length + 2);   // README 的 # 从 h3 起，别盖过大标题
      out.push(`<h${level}>${inlineHtml(escapeHtml(heading[2].trim()))}</h${level}>`);
      index++;
      continue;
    }

    // 分隔线。
    if (/^\s*([-*_])\s*\1\s*\1[\s\1]*$/.test(line)) {
      flushParagraph();
      out.push('<hr>');
      index++;
      continue;
    }

    // 无序列表。
    if (/^\s*[-*+]\s+/.test(line)) {
      flushParagraph();
      const items = [];
      while (index < lines.length && /^\s*[-*+]\s+/.test(lines[index])) {
        items.push(lines[index].replace(/^\s*[-*+]\s+/, ''));
        index++;
      }

      out.push(`<ul>${items.map((t) => `<li>${inlineHtml(escapeHtml(t))}</li>`).join('')}</ul>`);
      continue;
    }

    // 有序列表。
    if (/^\s*\d+[.)]\s+/.test(line)) {
      flushParagraph();
      const items = [];
      while (index < lines.length && /^\s*\d+[.)]\s+/.test(lines[index])) {
        items.push(lines[index].replace(/^\s*\d+[.)]\s+/, ''));
        index++;
      }

      out.push(`<ol>${items.map((t) => `<li>${inlineHtml(escapeHtml(t))}</li>`).join('')}</ol>`);
      continue;
    }

    // 引用。
    if (/^\s*>\s?/.test(line)) {
      flushParagraph();
      const quoted = [];
      while (index < lines.length && /^\s*>\s?/.test(lines[index])) {
        quoted.push(lines[index].replace(/^\s*>\s?/, ''));
        index++;
      }

      out.push(`<blockquote>${inlineHtml(escapeHtml(quoted.join(' ')))}</blockquote>`);
      continue;
    }

    // 空行 = 段落结束。
    if (line.trim().length === 0) {
      flushParagraph();
      index++;
      continue;
    }

    paragraph.push(line.trim());
    index++;
  }

  flushParagraph();
  // 上面拼出来的每一段都已逐块转义，这里只是在套自己生成的标签。
  host.innerHTML = out.join('\n');
  return host;
}
