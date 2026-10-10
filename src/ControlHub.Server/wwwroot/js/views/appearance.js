/**
 * 页面设置（品牌 / 登录页 / 外观 / 高级）。
 *
 * <para>为什么单独开一页，而不是散在各处（原来品牌在外观设置里、仪表盘模块在仪表盘右上角、
 * 设备列在设备页）：这些都是「把这套系统改成我们学校的样子」的同一类事情，
 * 分散之后管理员每次都要先想「那个开关在哪个页面来着」。</para>
 *
 * <para>两类配置在这里**刻意分开标注**，因为它们的生效范围完全不同：</para>
 * <list type="bullet">
 *   <item>**全站**（品牌 / 登录页 / 主题色 / 玻璃 / 自定义 CSS）：存服务端，所有人看到的一样。</item>
 *   <item>**本机**（主题明暗 / 密度 / 字体 / 圆角 / 强调色）：只存这台电脑的浏览器，
 *       办公室电脑与教室大屏对它们的偏好往往不同，同步过去反而是打扰。</item>
 * </list>
 *
 * <para>顶栏原来的「外观」下拉（主题 / 强调色 / 字体 / 圆角 / 密度）已整组搬到
 * 本页的「外观（本机）」。</para>
 */

import { api } from '../core/api.js?v=90';
import {
  h, clear, field, select, loadingBlock, toast, confirmDialog,
} from '../core/ui.js?v=90';
import {
  THEMES, DENSITIES, ACCENTS, FONTS, RADII,
  getTheme, applyTheme, getDensity, setDensity,
  getAccent, setAccent, getFont, setFont, getRadius, setRadius,
} from '../core/prefs.js?v=90';

export const meta = {
  title: '页面设置',
  subtitle: '品牌 / 登录页 / 外观',
};

export async function render(container) {
  clear(container);
  container.appendChild(loadingBlock());

  const branding = await api('/admin/branding');

  clear(container);
  container.appendChild(h('div',
    renderBrandCard(branding),
    renderLoginCard(branding),
    renderAppearanceCard(),
    renderAdvancedCard(branding),
  ));
}

/** 一行说明文字（用来标注生效范围这类容易混淆的信息）。 */
function scopeNote(text) {
  return h('p.card-desc', { style: { margin: '0 0 10px' } }, text);
}

/**
 * 试加载一张图片，返回它到底能不能显示出来。
 *
 * <para>为什么要做这件事：品牌图片（Logo / 浏览器图标 / 登录页背景）填的是**外部地址**，
 * 从输入框里根本看不出对错。地址写错、图床要登录、图床禁外链，现象都一样——
 * 「填了 URL，刷新后还是老样子」。以前只能靠管理员自己怀疑人生；现在保存前先试一次，
 * 失败就问一句「这张图打不开，还要保存吗」，并说清常见原因。</para>
 *
 * <para>注意：外链图片还受页面 CSP 的 <c>img-src</c> 约束（服务端已放开 https/http）。
 * 这个探测走的是同一套规则，所以它失败 = 页面上同样显示不出来。</para>
 */
function probeImage(url) {
  return new Promise((resolve) => {
    if (!url) {
      resolve(true);
      return;
    }

    const img = new Image();
    // 图床慢的时候不要让人一直等：5 秒够不够都算失败（失败也允许保存）。
    const timer = setTimeout(() => {
      img.src = '';
      resolve(false);
    }, 5000);
    img.onload = () => { clearTimeout(timer); resolve(true); };
    img.onerror = () => { clearTimeout(timer); resolve(false); };
    img.src = url;
  });
}

/**
 * 保存前检查若干图片地址；都打得开就返回 false（不用再问），
 * 有打不开的就把名字列出来问一句「还存吗」。
 */
async function confirmBrokenImages(entries) {
  const broken = [];
  for (const [label, url] of entries) {
    if (!await probeImage(url)) broken.push(label);
  }
  if (broken.length === 0) return false;

  const ok = await confirmDialog('这些图片打不开',
    `${broken.join('、')} 加载失败，保存后界面上仍会显示成文字或默认图标。`
    + '常见原因：地址写错、图床需要登录、图床禁止外链（防盗链）。仍要保存吗？',
    '仍然保存');
  return !ok;
}

// ────────────────────────────── 品牌（全站） ──────────────────────────────

function renderBrandCard(branding) {
  const siteInput = h('input', { type: 'text', value: branding.siteName || '' });
  const logoImageInput = h('input', {
    type: 'text',
    value: branding.logoImage || '',
    placeholder: 'https://…/logo.png 或 data:image/png;base64,…',
  });
  const logoTextInput = h('input', { type: 'text', value: branding.logoText || '', placeholder: 'CI' });
  const faviconInput = h('input', { type: 'text', value: branding.favicon || '', placeholder: '留空使用默认图标' });

  return h('div.card',
    h('h3', '品牌'),
    scopeNote('这些设置**全站生效**（含登录页与管理界面），保存后刷新可见。'),
    h('div.form-row',
      field('站点名称', siteInput, '显示在浏览器标题与界面各处。'),
      field('Logo 文字', logoTextInput, '没有上传图片时，用这几个字作为标识。'),
    ),
    field('Logo 图片', logoImageInput,
      '填图片地址或 data URL。**填了就用图片**（登录页顶栏、登录卡片、站内顶栏都会换）。'),
    field('浏览器标签图标', faviconInput, '小尺寸图（32×32 左右）最合适。'),
    h('div.card-actions',
      h('button.btn.btn-primary', {
        type: 'button',
        onClick: async () => {
          const stop = await confirmBrokenImages([
            ['Logo 图片', logoImageInput.value.trim()],
            ['浏览器标签图标', faviconInput.value.trim()],
          ]);
          if (stop) return;

          await saveBranding({
            siteName: siteInput.value.trim(),
            logoText: logoTextInput.value.trim(),
            logoImage: logoImageInput.value.trim(),
            favicon: faviconInput.value.trim(),
          });
        },
      }, '保存品牌')),
  );
}

// ────────────────────────────── 登录页（全站） ────────────────────────────

function renderLoginCard(branding) {
  const layoutSelect = select([
    { value: 'split', label: '左右分栏（左品牌 / 右表单）' },
    { value: 'centered', label: '单列居中（只要一张登录卡）' },
  ], branding.loginLayout || 'split', () => { });

  const titleInput = h('input', { type: 'text', value: branding.loginTitle || '', placeholder: '留空使用站点名称' });
  const subtitleInput = h('input', { type: 'text', value: branding.loginSubtitle || '' });
  const descInput = h('textarea', { rows: 2 }, branding.loginDescription || '');
  const featuresInput = h('textarea', {
    rows: 4,
    placeholder: '每行一条，最多 8 条',
  }, (branding.loginFeatures || []).join('\n'));
  const footerInput = h('input', { type: 'text', value: branding.footerText || '', placeholder: '例如：智教联盟 · 教务处' });

  const topbarHiddenChk = h('input', { type: 'checkbox' });
  topbarHiddenChk.checked = branding.loginTopbarHidden === true;
  const topbarTitleInput = h('input', {
    type: 'text',
    value: branding.loginTopbarTitle || '',
    placeholder: '留空使用站点名称',
  });

  const bgInput = h('input', { type: 'text', value: branding.loginBackground || '', placeholder: '图片地址或 data URL' });
  const dimInput = h('input', {
    type: 'number', min: '0', max: '90', value: String(branding.loginBackgroundDim ?? 45),
  });

  return h('div.card',
    h('h3', '登录页'),
    scopeNote('登录页是别人看到的第一屏；这一节同样**全站生效**。'),
    field('布局', layoutSelect),
    h('div.form-row',
      field('大标题', titleInput),
      field('副标题', subtitleInput, '短句，跟在标题右侧/下方。'),
    ),
    field('描述段落', descInput, '介绍这套系统做什么，留空则隐藏整段。'),
    field('特性列表', featuresInput, '每行一条（如「一处配置，全网生效」）。'),
    field('页脚文字', footerInput),

    h('div.config-section-title', { style: { marginTop: '14px' } }, '顶栏'),
    h('label.checkbox-field',
      topbarHiddenChk,
      h('span', '隐藏登录页顶栏'),
      h('span', { style: { color: 'var(--text-faint)', fontSize: '12px' } },
        '（整页嵌入学校门户时可选）')),
    field('顶栏文案', topbarTitleInput, '留空使用站点名称；这里可以写完整校名。'),

    h('div.config-section-title', { style: { marginTop: '14px' } }, '背景'),
    field('背景图', bgInput, '留空使用默认的蓝紫渐变。'),
    field('淡化程度', dimInput, '0~90，数值越大背景越淡（保证表单可读）。'),

    h('div.card-actions',
      h('button.btn.btn-primary', {
        type: 'button',
        onClick: async () => {
          const stop = await confirmBrokenImages([['登录页背景图', bgInput.value.trim()]]);
          if (stop) return;

          await saveBranding({
            loginLayout: layoutSelect.value,
            loginTitle: titleInput.value.trim(),
            loginSubtitle: subtitleInput.value.trim(),
            loginDescription: descInput.value.trim(),
            loginFeatures: featuresInput.value.split('\n').map((s) => s.trim()).filter(Boolean).slice(0, 8),
            footerText: footerInput.value.trim(),
            loginTopbarHidden: topbarHiddenChk.checked,
            loginTopbarTitle: topbarTitleInput.value.trim(),
            loginBackground: bgInput.value.trim(),
            loginBackgroundDim: Math.min(90, Math.max(0, Number(dimInput.value) || 45)),
          });
        },
      }, '保存登录页设置')),
  );
}

// ────────────────────────────── 外观（本机） ──────────────────────────────

function renderAppearanceCard() {
  const themeSelect = select(
    THEMES.map((t) => ({ value: t.key, label: t.label })), getTheme(), (v) => applyTheme(v));
  const densitySelect = select(
    DENSITIES.map((d) => ({ value: d.key, label: d.label })), getDensity(), (v) => setDensity(v));
  const fontSelect = select(
    FONTS.map((f) => ({ value: f.value, label: f.label })), getFont(), (v) => setFont(v));
  const radiusSelect = select(
    RADII.map((r) => ({ value: r.key, label: r.label })), getRadius(), (v) => setRadius(v));
  // 强调色的第一项是「跟随全站」：它不是「不设置」，而是明确地把控制权交回
  // 全站主题色（见 prefs.applyAccent 的取值顺序：本机优先、全站兜底）。
  const accentSelect = select(
    [{ value: '', label: '跟随全站设置' }, ...ACCENTS.map((a) => ({ value: a.value, label: a.label }))],
    getAccent(), (v) => setAccent(v));

  return h('div.card',
    h('h3', '外观（本机）'),
    scopeNote('只影响**这台电脑的浏览器**。办公室电脑与教室大屏的偏好常常不同，'
      + '所以这类设置不跟随账号同步——各调各的更省事。'),
    h('div.form-row',
      field('明暗主题', themeSelect, '「跟随系统」会随操作系统的深色模式切换。'),
      field('界面密度', densitySelect, '紧凑模式一屏能多看几行。'),
    ),
    h('div.form-row',
      field('强调色', accentSelect, '「跟随全站设置」表示用下面「高级」里配的那个颜色；选了具体颜色则只改这台电脑。'),
      field('字体', fontSelect, '换一套中文字体有时比调整字号更管用。'),
    ),
    field('圆角', radiusSelect, '直角看起来更硬朗；圆润在大屏上观感更柔和。'),
  );
}

// ────────────────────────────── 高级（全站） ──────────────────────────────

function renderAdvancedCard(branding) {
  // 主题色用取色器 + 「用默认」勾选（与系统设置里那份表单保持一致的模式）：
  // 纯文本框要求管理员手写十六进制，错一位就静默不生效。
  const accentInput = h('input', { type: 'color', value: branding.accentColor || '#1e90ff' });
  const accentDefaultChk = h('input', { type: 'checkbox' });
  accentDefaultChk.checked = !branding.accentColor;
  accentInput.disabled = accentDefaultChk.checked;
  accentDefaultChk.addEventListener('change', () => {
    accentInput.disabled = accentDefaultChk.checked;
  });

  const glassSelect = select([
    { value: 'subtle', label: '轻（内容优先，低配机友好）' },
    { value: 'standard', label: '标准' },
    { value: 'strong', label: '强（质感优先）' },
  ], branding.glassLevel || 'standard', () => { });
  const shineChk = h('input', { type: 'checkbox' });
  shineChk.checked = branding.enableShine !== false;
  const cssInput = h('textarea', { rows: 6, placeholder: '/* 追加的 CSS */' }, branding.customCss || '');

  return h('div.card',
    h('h3', '高级（全站）'),
    scopeNote('改动会影响**所有人**。这两项是「高度自定义」的兜底口子，不确定时保持默认。'),
    h('label.checkbox-field',
      accentDefaultChk,
      h('span', '主题色用默认蓝'),
      h('span', { style: { color: 'var(--text-faint)', fontSize: '12px' } }, '（取消勾选后可自选颜色）')),
    field('全站主题色', accentInput, '所有人的默认强调色；个人可在上面的「外观（本机）」里改成只对自己生效的颜色。'),
    field('玻璃材质强度', glassSelect, '低配机或投影场景建议打「轻」。'),
    h('label.checkbox-field',
      shineChk,
      h('span', '启用鼠标跟随高光'),
      h('span', { style: { color: 'var(--text-faint)', fontSize: '12px' } }, '（关掉可省一点显卡开销）')),
    field('自定义 CSS', cssInput,
      '追加到管理界面与登录页（上限 20000 字符）。改坏了可以清空这里恢复。'),
    h('div.card-actions',
      h('button.btn.btn-primary', {
        type: 'button',
        onClick: async () => {
          // 勾了「用默认蓝」就提交空串——服务端空值即「不覆盖」。
          const accent = accentDefaultChk.checked ? '' : accentInput.value.trim();
          await saveBranding({
            accentColor: accent,
            glassLevel: glassSelect.value,
            enableShine: shineChk.checked,
            customCss: cssInput.value,
          });
        },
      }, '保存高级设置')),

    h('div.config-section-title', { style: { marginTop: '16px' } }, '更多自定义'),
    h('p.card-desc', '下面两项与具体页面相关，在各自页面里调整更顺手（这里给个直达入口）：'),
    h('div.card-actions',
      h('button.btn.btn-sm', {
        type: 'button',
        onClick: () => { window.location.hash = '#/dashboard'; },
      }, '仪表盘模块显隐与排序'),
      h('button.btn.btn-sm', {
        type: 'button',
        onClick: () => { window.location.hash = '#/devices'; },
      }, '设备表格列显隐'),
    ),
  );
}

/**
 * 保存品牌设置。
 *
 * <para>**先读后合再写**：表单只编辑了一部分字段，直接 PUT 会把没展示的字段（比如别处设置的
 * 玻璃强度）清成默认值。合并当前值再提交，才不会「改 A 把 B 改没了」。</para>
 */
async function saveBranding(patch) {
  const current = await api('/admin/branding');
  await api('/admin/branding', { method: 'PUT', body: { ...current, ...patch } });
  toast('ok', '已保存', '刷新页面后可见（外观类改动需要重载才完全生效）。');
  // 让界面上的即时类改动（主题色等）马上能看到：重载最稳，
  // 因为品牌设置同时作用于 CSS 变量、登录页与 favicon，逐处同步反而容易漏。
  setTimeout(() => window.location.reload(), 900);
}
