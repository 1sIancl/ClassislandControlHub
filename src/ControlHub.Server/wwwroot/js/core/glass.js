/**
 * 液态玻璃的「鼠标跟随高光」。
 *
 * 做法上用**事件委托**（一个 document 级监听）而不是给每个元素绑 mousemove：
 * 设备页可能有几百张卡片，逐元素绑定会让移动指针时明显掉帧。
 * 委托版只在指针真正落在带高光的元素上时才做一次 getBoundingClientRect。
 *
 * 另外用 `pointermove` 而不是 `mouseover`/`mousemove`：前者对触控与笔同样有效，
 * 且 `passive: true` 明确告诉浏览器「不会 preventDefault」，滚动不会被它拖累。
 */

/** 当前高光元素：用来避免在同一个元素内移动时重复查询 DOM。 */
let current = null;

/** 高光选择器：只挑内容简单、不会有 sticky 子元素的容器（见 CSS 里的说明）。 */
const SELECTOR = '.glass-hi, .stat, .stat-link';

/**
 * 启用鼠标跟随高光。重复调用无害（只注册一次监听）。
 */
export function initGlassHighlight() {
  if (initGlassHighlight.started) {
    return;
  }

  initGlassHighlight.started = true;

  document.addEventListener('pointermove', (event) => {
    const element = event.target instanceof Element ? event.target.closest(SELECTOR) : null;

    if (element === current) {
      // 同一个元素内移动：直接更新，省掉一次 closest 之外的查询。
      if (element) {
        paint(element, event);
      }
      return;
    }

    current = element;
    if (element) {
      paint(element, event);
    }
  }, { passive: true });
}

function paint(element, event) {
  const rect = element.getBoundingClientRect();
  if (rect.width === 0 || rect.height === 0) {
    return;
  }

  element.style.setProperty('--mx', `${((event.clientX - rect.left) / rect.width) * 100}%`);
  element.style.setProperty('--my', `${((event.clientY - rect.top) / rect.height) * 100}%`);
}
