/**
 * 天气管理页：配置城市 → 服务端定时抓取 → 教室端读取。
 *
 * <para>
 * 设计取舍：全校**一套**天气（校园集控，不是天气 App）；数据源免 Key（Open-Meteo），
 * 内网抓不到时**保留上一次数据**并把原因写在页面上——教室里看到旧一点的天气，也好过突然空白。
 * </para>
 */
import { api } from '../core/api.js?v=142';
import { toastError } from '../core/errors.js?v=142';
import { h, clear, toast, loadingBlock, formatDateTime, field } from '../core/ui.js?v=142';
import { t } from '../core/i18n.js?v=142';

export const meta = {
  title: t('weather.title'),
  subtitle: t('weather.subtitle'),
};

/** WMO 天气代码 → 文案键（只分十几类，够显示图标与一句话）。 */
function describeCode(code) {
  if (code === 0) return t('weather.code.clear');
  if (code === 1) return t('weather.code.mostlyClear');
  if (code === 2) return t('weather.code.partlyCloudy');
  if (code === 3) return t('weather.code.overcast');
  if (code === 45 || code === 48) return t('weather.code.fog');
  if (code === 51 || code === 53 || code === 55) return t('weather.code.drizzle');
  if (code === 56 || code === 57 || code === 66 || code === 67) return t('weather.code.freezing');
  if (code === 61 || code === 63 || code === 65) return t('weather.code.rain');
  if (code === 71 || code === 73 || code === 75 || code === 77) return t('weather.code.snow');
  if (code === 80 || code === 81 || code === 82) return t('weather.code.showers');
  if (code === 85 || code === 86) return t('weather.code.snowShowers');
  if (code === 95 || code === 96 || code === 99) return t('weather.code.thunder');
  return t('weather.code.unknown');
}

/** 天气图标（纯装饰，用 emoji 最省事且不占资源）。 */
function iconFor(code) {
  if (code === 0) return '☀️';
  if (code === 1 || code === 2) return '⛅';
  if (code === 3) return '☁️';
  if (code === 45 || code === 48) return '🌫️';
  if ([51, 53, 55, 56, 57, 61, 63, 65, 66, 67, 80, 81, 82].includes(code)) return '🌧️';
  if ([71, 73, 75, 77, 85, 86].includes(code)) return '❄️';
  if ([95, 96, 99].includes(code)) return '⛈️';
  return '🌡️';
}

export async function render(container) {
  clear(container);
  container.appendChild(loadingBlock());

  let state;
  try {
    state = await api('/admin/weather');
  } catch (err) {
    clear(container);
    container.appendChild(h('div.notice.notice-warn',
      h('span.notice-icon', '!'),
      h('div', t('weather.loadFailed', { message: err.message || String(err) }))));
    return;
  }

  const repaint = () => render(container);

  clear(container);
  container.appendChild(h('div.weather-page',
    renderCurrentCard(state, repaint),
    renderConfigCard(state, repaint),
    renderHintCard(),
  ));
}

/** 当前天气：大号温度 + 一句话 + 刷新按钮；抓取失败时把原因写在下面。 */
function renderCurrentCard(state, repaint) {
  const config = state.config || {};
  const hasData = !!state.hasData;
  const temp = hasData ? `${Math.round(state.temperature)}${state.temperatureText || '°C'}` : '—';

  const refreshBtn = h('button.btn.btn-sm', {
    type: 'button',
    disabled: !config.enabled,
    onClick: async () => {
      refreshBtn.disabled = true;
      refreshBtn.textContent = t('weather.refreshing');
      try {
        const next = await api('/admin/weather/refresh', { method: 'POST' });
        if (next.error) {
          toast('warn', t('weather.refreshFailed'), next.error, 8000);
        } else {
          toast('ok', t('weather.refreshed'), next.summary || '');
        }

        repaint();
      } catch (err) {
        toastError(err, t('weather.refreshFailed'));
        refreshBtn.disabled = false;
        refreshBtn.textContent = t('weather.refreshNow');
      }
    },
  }, t('weather.refreshNow'));

  return h('div.card',
    h('div.card-head',
      h('div',
        h('h3', t('weather.current')),
        h('p.card-desc', config.enabled
          ? (config.city
            ? t('weather.currentDesc', { city: config.city })
            : t('weather.setCityHint'))
          : t('weather.disabledHint')),
      ),
      h('div.card-actions', refreshBtn),
    ),
    h('div.card-body',
      h('div', { style: { display: 'flex', alignItems: 'center', gap: '18px', flexWrap: 'wrap' } },
        h('div', { style: { fontSize: '46px', lineHeight: '1' } }, hasData ? iconFor(state.weatherCode) : '🌡️'),
        h('div',
          h('div', { style: { fontSize: '34px', fontWeight: '700', lineHeight: '1.1' } }, temp),
          h('div', { style: { fontSize: '13px', color: 'var(--text-dim)' } },
            hasData ? describeCode(state.weatherCode) : t('weather.noData')),
        ),
        h('div', { style: { fontSize: '12.5px', color: 'var(--text-faint)', lineHeight: '1.8' } },
          hasData && state.humidity != null ? h('div', t('weather.humidity', { value: state.humidity })) : null,
          hasData && state.windSpeed != null ? h('div', t('weather.wind', { value: Math.round(state.windSpeed) })) : null,
          hasData && state.observedAt ? h('div', t('weather.observedAt', { at: state.observedAt })) : null,
          state.updatedAt ? h('div', t('weather.fetchedAt', { at: formatDateTime(state.updatedAt) })) : null,
          h('div', t('weather.source', { source: state.source || 'Open-Meteo' })),
        ),
      ),
      state.error
        ? h('div.notice.notice-warn', { style: { marginTop: '14px' } },
          h('span.notice-icon', '!'),
          h('div', t('weather.errorNotice', { message: state.error })))
        : null,
    ));
}

/** 配置：启用 / 城市 / 刷新间隔 / 单位。 */
function renderConfigCard(state, repaint) {
  const config = state.config || {};

  const enabled = h('input', { type: 'checkbox', checked: !!config.enabled });
  const city = h('input', {
    type: 'text',
    value: config.city || '',
    placeholder: t('weather.cityPlaceholder'),
  });
  const interval = h('select',
    ...[10, 15, 30, 60, 120].map((minutes) => {
      const option = h('option', { value: String(minutes) }, t('weather.everyMinutes', { minutes }));
      if ((config.refreshMinutes || 30) === minutes) option.selected = true;
      return option;
    }));
  const unit = h('select',
    ...[['celsius', t('weather.unitC')], ['fahrenheit', t('weather.unitF')]].map(([value, label]) => {
      const option = h('option', { value }, label);
      if ((config.temperatureUnit || 'celsius') === value) option.selected = true;
      return option;
    }));

  const saveBtn = h('button.btn.btn-primary.btn-sm', {
    type: 'button',
    onClick: async () => {
      saveBtn.disabled = true;
      saveBtn.textContent = t('weather.saving');
      try {
        const next = await api('/admin/weather', {
          method: 'PUT',
          body: {
            enabled: enabled.checked,
            city: city.value.trim(),
            latitude: config.latitude || 0,
            longitude: config.longitude || 0,
            refreshMinutes: Number(interval.value),
            temperatureUnit: unit.value,
          },
        });
        // 保存时会顺手抓一次：成功就直接说天气，失败把原因带出来（不要假装保存成功就万事大吉）。
        if (next.config?.enabled && next.error) {
          toast('warn', t('weather.savedButFetchFailed'), next.error, 9000);
        } else if (next.config?.enabled) {
          toast('ok', t('weather.saved'), next.summary || '');
        } else {
          toast('ok', t('weather.saved'), t('weather.savedDisabled'));
        }

        repaint();
      } catch (err) {
        toastError(err, t('weather.saveFailed'));
        saveBtn.disabled = false;
        saveBtn.textContent = t('weather.save');
      }
    },
  }, t('weather.save'));

  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', t('weather.configTitle')),
        h('p.card-desc', t('weather.configDesc')),
      ),
    ),
    h('div.card-body',
      h('div.form-grid',
        field(t('weather.enabled'), enabled, t('weather.enabledHint')),
        field(t('weather.city'), city, t('weather.cityHint')),
        field(t('weather.interval'), interval, t('weather.intervalHint')),
        field(t('weather.unit'), unit),
      ),
      config.latitude
        ? h('div', { style: { fontSize: '12px', color: 'var(--text-faint)', marginTop: '8px' } },
          t('weather.coords', { lat: config.latitude.toFixed(4), lon: config.longitude.toFixed(4) }))
        : null,
      h('div.card-actions', saveBtn),
    ));
}

/** 教室端怎么取这份天气（给运维看的，不占主要位置）。 */
function renderHintCard() {
  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', t('weather.clientTitle')),
        h('p.card-desc', t('weather.clientDesc')),
      ),
    ),
    h('div.card-body',
      h('div', { style: { fontSize: '12.5px', color: 'var(--text-dim)', lineHeight: '1.9' } },
        h('div', t('weather.clientEndpoint')),
        h('div', t('weather.clientAvailable')),
        h('div', t('weather.clientSummary')),
      ),
    ));
}
