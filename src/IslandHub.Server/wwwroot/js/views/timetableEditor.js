/**
 * 课表编辑页（拆分步骤 2+3，见 docs/timetable-editor-split.md）。
 *
 * <p>
 * 路由是 `#/timetables/{档案}`。**独立模块**，不再借用档案编辑器：
 * 屏幕上只有「返回 / 档案名 / 保存」一行 + 课表本身，
 * 没有档案头、没有页签栏、没有「使用的时间表」区块。
 * </p>
 *
 * <p>
 * 渲染与交互仍然复用 `profileEditor.mountClassPlans`——**代码只有一份**。
 * 把 schedCell 那五套逻辑（渲染 / 点击 / 双击 / 拖拽 / 右键）复制一份到新文件，
 * 短期看是"独立了"，长期必然改一处忘一处。
 * </p>
 */
import { mountClassPlans } from './profileEditor.js?v=190';
import { t } from '../core/i18n.js?v=190';
import { clear } from '../core/ui.js?v=190';

export const meta = {
  title: t('timetables.title'),
  subtitle: t('profiles.editor.tabTimetable'),
};

export async function render(container, params) {
  clear(container);

  // 路由层已经保证带 profileId（`#/timetables/{档案}` 才会匹配到这里），
  // 所以不做"没传 ID"的兜底提示——那种分支永远走不到，只会多一个没人维护的文案键。
  await mountClassPlans(container, params.profileId);
}
