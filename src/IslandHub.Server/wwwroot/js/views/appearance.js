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

import { api } from '../core/api.js?v=137';
import {
  h, clear, field, select, loadingBlock, toast, confirmDialog,
} from '../core/ui.js?v=137';
import {
  THEMES, DENSITIES, ACCENTS, FONTS, RADII,
  getTheme, applyTheme, getDensity, setDensity,
  getAccent, setAccent, getFont, setFont, getRadius, setRadius,
} from '../core/prefs.js?v=137';
import { t } from '../core/i18n.js?v=137';

export const meta = {
  title: t('appearance.title'),
  subtitle: t('appearance.subtitle'),
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

  const ok = await confirmDialog(t('appearance.brokenImagesTitle'),
    t('appearance.imagesFailed', { list: broken.join('、') })
    + t('appearance.brokenImagesHint'),
    t('appearance.saveAnyway'));
  return !ok;
}

// ────────────────────────────── 图片字段 ──────────────────────────────

/**
 * 上传一张本地图片，返回服务端给出的站内地址。
 *
 * <para>有了它就不用再自己找图床：外部图床的防盗链、限速、校内网打不开，
 * 三件事最终都会表现为「壁纸不好用」。服务端按**文件头**判定类型并限制大小，
 * 伪装成 .png 的其它文件会被拒（否则同源发回 HTML 就是 XSS）。</para>
 */
async function uploadImage(file) {
  const form = new FormData();
  form.append('file', file, file.name);
  return api('/admin/branding/image', { method: 'POST', body: form });
}

/**
 * 「图片」输入项：地址框 + 选择本地图片 + 缩略图 + 清除。
 *
 * <para>这三样放在一起，是因为它们其实是同一个动作的三个阶段：选图 → 看有没有选对 → 改主意清掉。
 * 以前只有一个地址框，管理员存完只能回登录页刷新一次才知道行不行。</para>
 *
 * @param {string} label 字段名。
 * @param {string} initial 初始地址。
 * @param {string} desc 说明文字。
 * @param {{onChange?: (value: string) => void, previewHeight?: string}} [options]
 * @returns {{node: HTMLElement, input: HTMLInputElement, setValue: (value: string) => void}}
 */
function imageField(label, initial, desc, options = {}) {
  const input = h('input', {
    type: 'text',
    value: initial || '',
    placeholder: t('appearance.imagePlaceholder'),
  });

  const file = h('input', { type: 'file', accept: 'image/*', style: { display: 'none' } });
  const preview = h('div.image-field-preview');
  const status = h('span.image-field-status');

  const pickBtn = h('button.btn.btn-sm', { type: 'button', onClick: () => file.click() }, t('appearance.pickLocalImage'));
  const clearBtn = h('button.btn.btn-sm', {
    type: 'button',
    onClick: () => {
      input.value = '';
      paint();
      options.onChange?.('');
    },
  }, t('appearance.clear'));

  function paint() {
    const url = input.value.trim();
    clear(preview);
    status.textContent = '';

    if (!url) {
      preview.hidden = true;
      return;
    }

    preview.hidden = false;
    const img = h('img', {
      alt: t('appearance.preview'),
      src: url,
      style: { maxHeight: options.previewHeight || '72px' },
    });
    img.addEventListener('error', () => {
      // 「打不开」是最需要当场知道的事：地址写错、要登录、图床防盗链，现象全一样。
      clear(preview);
      status.textContent = t('appearance.imageBroken');
      status.classList.add('is-bad');
    });
    preview.appendChild(img);
  }

  input.addEventListener('input', () => {
    paint();
    options.onChange?.(input.value.trim());
  });

  file.addEventListener('change', async () => {
    const picked = file.files && file.files[0];
    file.value = '';
    if (!picked) return;

    status.classList.remove('is-bad');
    status.textContent = t('appearance.uploading');
    pickBtn.disabled = true;
    try {
      const saved = await uploadImage(picked);
      input.value = saved.url;
      paint();
      status.textContent = t('appearance.uploadedSize', { size: Math.round(saved.size / 1024) });
      options.onChange?.(saved.url);
    } catch (err) {
      status.classList.add('is-bad');
      status.textContent = err.message || t('appearance.uploadFailed');
    } finally {
      pickBtn.disabled = false;
    }
  });

  paint();

  return {
    node: h('div.image-field',
      field(label, input, desc),
      h('div.image-field-actions', pickBtn, file, clearBtn, status),
      preview,
    ),
    input,
    setValue: (value) => {
      input.value = value || '';
      paint();
    },
  };
}

/**
 * 登录页的实时预览（缩小的示意）。
 *
 * <para>它是**示意**，不是像素级还原**：只保留真正会被壁纸影响的三样东西——
 * 背景图（cover）、淡化层、左侧品牌区那块可读性衬底。这三样正是「壁纸好不好用」的判断依据，
 * 剩下的（顶栏、输入框、按钮）在真实登录页里看就行。</para>
 */
function loginPreview() {
  const bg = h('div.login-preview-bg');
  const dim = h('div.login-preview-dim');
  const scrim = h('div.login-preview-scrim');
  const heroTitle = h('span.login-preview-title');
  const heroLine = h('span.login-preview-line');
  const hero = h('div.login-preview-hero', heroTitle, heroLine);
  const card = h('div.login-preview-card',
    h('span.login-preview-field'),
    h('span.login-preview-field'),
    h('span.login-preview-btn'));

  const node = h('div.login-preview', bg, dim, scrim, hero, card);

  return {
    node,
    update({ background, dim: dimValue, layout, title }) {
      // 没有壁纸时交给 CSS 画默认渐变，这里必须显式清掉，否则上一张图会留着。
      bg.style.backgroundImage = background ? `url("${background}")` : '';
      dim.style.opacity = String(Math.min(0.9, Math.max(0, Number(dimValue) || 0) / 100));
      scrim.style.opacity = background ? '1' : '0';
      heroTitle.textContent = title || t('appearance.siteName');
      node.dataset.layout = layout === 'centered' ? 'centered' : 'split';
    },
  };
}

// ────────────────────────────── 品牌（全站） ──────────────────────────────

function renderBrandCard(branding) {
  const siteInput = h('input', { type: 'text', value: branding.siteName || '' });
  const logoImage = imageField(t('appearance.logoImage'), branding.logoImage,
    t('appearance.logoImageHint'),
    { previewHeight: '48px' });
  const logoTextInput = h('input', { type: 'text', value: branding.logoText || '', placeholder: 'CI' });
  const favicon = imageField(t('appearance.favicon'), branding.favicon, t('appearance.faviconHint'),
    { previewHeight: '32px' });

  return h('div.card',
    h('h3', t('appearance.brandCard')),
    scopeNote(t('appearance.brandCardDesc')),
    h('div.form-row',
      field(t('appearance.siteName'), siteInput, t('appearance.siteNameHint')),
      field(t('appearance.logoText'), logoTextInput, t('appearance.logoTextHint')),
    ),
    logoImage.node,
    favicon.node,
    h('div.card-actions',
      h('button.btn.btn-primary', {
        type: 'button',
        onClick: async () => {
          const stop = await confirmBrokenImages([
            [t('appearance.logoImage'), logoImage.input.value.trim()],
            [t('appearance.favicon'), favicon.input.value.trim()],
          ]);
          if (stop) return;

          await saveBranding({
            siteName: siteInput.value.trim(),
            logoText: logoTextInput.value.trim(),
            logoImage: logoImage.input.value.trim(),
            favicon: favicon.input.value.trim(),
          });
        },
      }, t('appearance.saveBrand'))),
  );
}

// ────────────────────────────── 登录页（全站） ────────────────────────────

function renderLoginCard(branding) {
  const layoutSelect = select([
    { value: 'split', label: t('appearance.layoutSplit') },
    { value: 'centered', label: t('appearance.layoutCentered') },
  ], branding.loginLayout || 'split', () => paintPreview());

  const titleInput = h('input', { type: 'text', value: branding.loginTitle || '', placeholder: t('appearance.emptyUsesSiteName') });
  const subtitleInput = h('input', { type: 'text', value: branding.loginSubtitle || '' });
  const descInput = h('textarea', { rows: 2 }, branding.loginDescription || '');
  const featuresInput = h('textarea', {
    rows: 4,
    placeholder: t('appearance.onePerLineMax8'),
  }, (branding.loginFeatures || []).join('\n'));
  const footerInput = h('input', { type: 'text', value: branding.footerText || '', placeholder: t('appearance.footerPlaceholder') });

  const topbarHiddenChk = h('input', { type: 'checkbox' });
  topbarHiddenChk.checked = branding.loginTopbarHidden === true;
  const topbarTitleInput = h('input', {
    type: 'text',
    value: branding.loginTopbarTitle || '',
    placeholder: t('appearance.emptyUsesSiteName'),
  });

  const preview = loginPreview();

  /**
   * 把当前填的壁纸 / 淡化 / 布局同步到预览上。
   * <para>以前要调好「淡化程度」必须：保存 → 退出登录 → 刷新 → 看 → 再回来改，
   * 来回三四次才敢确定。壁纸越亮越需要淡化，这两个参数本来就得**一起**看。</para>
   */
  function paintPreview() {
    preview.update({
      background: bg.input.value.trim(),
      dim: Number(dimInput.value) || 0,
      layout: layoutSelect.value,
      title: titleInput.value.trim() || branding.siteName || t('appearance.siteName'),
    });
  }

  const bg = imageField(t('appearance.backgroundImage'), branding.loginBackground,
    t('appearance.backgroundImageHint'),
    { previewHeight: '110px', onChange: () => paintPreview() });
  const dimInput = h('input', {
    type: 'number', min: '0', max: '90', value: String(branding.loginBackgroundDim ?? 45),
  });
  dimInput.addEventListener('input', paintPreview);
  titleInput.addEventListener('input', paintPreview);
  paintPreview();

  return h('div.card',
    h('h3', t('appearance.loginCard')),
    scopeNote(t('appearance.loginCardDesc')),
    field(t('appearance.layout'), layoutSelect),
    h('div.form-row',
      field(t('appearance.heading'), titleInput),
      field(t('appearance.subheading'), subtitleInput, t('appearance.subheadingHint')),
    ),
    field(t('appearance.description'), descInput, t('appearance.descriptionHint')),
    field(t('appearance.features'), featuresInput, t('appearance.featuresHint')),
    field(t('appearance.footerText'), footerInput),

    h('div.config-section-title', { style: { marginTop: '14px' } }, t('appearance.topbar')),
    h('label.checkbox-field',
      topbarHiddenChk,
      h('span', t('appearance.hideTopbar')),
      h('span', { style: { color: 'var(--text-faint)', fontSize: '12px' } },
        t('appearance.hideTopbarHint'))),
    field(t('appearance.topbarText'), topbarTitleInput, t('appearance.topbarTextHint')),

    h('div.config-section-title', { style: { marginTop: '14px' } }, t('appearance.background')),
    bg.node,
    field(t('appearance.dimLevel'), dimInput, t('appearance.dimLevelHint')),
    h('p.card-desc', { style: { margin: '6px 0 8px' } },
      t('appearance.livePreviewHint')),
    preview.node,

    h('div.card-actions',
      h('button.btn.btn-primary', {
        type: 'button',
        onClick: async () => {
          const stop = await confirmBrokenImages([[t('appearance.loginBackground'), bg.input.value.trim()]]);
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
            loginBackground: bg.input.value.trim(),
            loginBackgroundDim: Math.min(90, Math.max(0, Number(dimInput.value) || 45)),
          });
        },
      }, t('appearance.saveLogin'))),
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
    [{ value: '', label: t('appearance.followSite') }, ...ACCENTS.map((a) => ({ value: a.value, label: a.label }))],
    getAccent(), (v) => setAccent(v));

  return h('div.card',
    h('h3', t('appearance.localCard')),
    scopeNote(t('appearance.localCardDesc1')
      + t('appearance.localCardDesc2')),
    h('div.form-row',
      field(t('appearance.theme'), themeSelect, t('appearance.themeHint')),
      field(t('appearance.density'), densitySelect, t('appearance.densityHint')),
    ),
    h('div.form-row',
      field(t('appearance.accent'), accentSelect, t('appearance.accentHint')),
      field(t('appearance.font'), fontSelect, t('appearance.fontHint')),
    ),
    field(t('appearance.radius'), radiusSelect, t('appearance.radiusHint')),
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
    { value: 'subtle', label: t('appearance.glassLight') },
    { value: 'standard', label: t('appearance.glassStandard') },
    { value: 'strong', label: t('appearance.glassStrong') },
  ], branding.glassLevel || 'standard', () => { });
  const shineChk = h('input', { type: 'checkbox' });
  shineChk.checked = branding.enableShine !== false;
  const cssInput = h('textarea', { rows: 6, placeholder: t('appearance.cssPlaceholder') }, branding.customCss || '');

  return h('div.card',
    h('h3', t('appearance.advancedCard')),
    scopeNote(t('appearance.advancedCardDesc')),
    h('label.checkbox-field',
      accentDefaultChk,
      h('span', t('appearance.useDefaultAccent')),
      h('span', { style: { color: 'var(--text-faint)', fontSize: '12px' } }, t('appearance.useDefaultAccentHint'))),
    field(t('appearance.siteAccent'), accentInput, t('appearance.siteAccentHint')),
    field(t('appearance.glassLevel'), glassSelect, t('appearance.glassLevelHint')),
    h('label.checkbox-field',
      shineChk,
      h('span', t('appearance.enableShine')),
      h('span', { style: { color: 'var(--text-faint)', fontSize: '12px' } }, t('appearance.enableShineHint'))),
    field(t('appearance.customCss'), cssInput,
      t('appearance.customCssHint')),
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
      }, t('appearance.saveAdvanced'))),

    h('div.config-section-title', { style: { marginTop: '16px' } }, t('appearance.moreCard')),
    h('p.card-desc', t('appearance.moreCardDesc')),
    h('div.card-actions',
      h('button.btn.btn-sm', {
        type: 'button',
        onClick: () => { window.location.hash = '#/dashboard'; },
      }, t('appearance.dashboardLayout')),
      h('button.btn.btn-sm', {
        type: 'button',
        onClick: () => { window.location.hash = '#/devices'; },
      }, t('appearance.deviceColumns')),
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
  toast('ok', t('appearance.saved'), t('appearance.savedDesc'));
  // 让界面上的即时类改动（主题色等）马上能看到：重载最稳，
  // 因为品牌设置同时作用于 CSS 变量、登录页与 favicon，逐处同步反而容易漏。
  setTimeout(() => window.location.reload(), 900);
}
