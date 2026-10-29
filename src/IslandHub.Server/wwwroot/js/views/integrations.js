/** 通知与集成视图（系统设置拆分而来）：Webhook 外部通知与 AI 辅助导入配置。 */

import { api, hasPermission } from '../core/api.js?v=157';
import { toastError } from '../core/errors.js?v=157';
import {
  h, clear, toast, modal, confirmDialog, field, select, guard, loadingBlock, hintIcon,
} from '../core/ui.js?v=157';
import { t } from '../core/i18n.js?v=157';

export const meta = {
  title: t('integrations.title'),
  subtitle: t('integrations.subtitle'),
};

const WEBHOOK_KINDS = [
  { value: 'wecom', label: t('integrations.kindWecom') },
  { value: 'dingtalk', label: t('integrations.kindDingTalk') },
  { value: 'feishu', label: t('integrations.kindFeishu') },
  { value: 'generic', label: t('integrations.kindCustom') },
];

// ⚠️ 这份清单是硬编码的（没有接口下发）。新增事件时要与
// `IslandHub.Protocol/Dtos/WebhookDtos.cs` 的 `WebhookEvents.All` **同时**加：
// 只在一边加 = 前端勾不上、或服务端保存时被过滤掉，都是静默失效。
const WEBHOOK_EVENTS = [
  { value: 'device.offline', label: t('integrations.eventOffline') },
  { value: 'device.online', label: t('integrations.eventOnline') },
  { value: 'device.error', label: t('integrations.eventApplyFailed') },
  { value: 'command.failed', label: t('integrations.eventCommandFailed') },
  { value: 'admin.login.failed', label: t('integrations.eventLoginFailed') },
  { value: 'reminder.fired', label: t('integrations.eventReminderFired') },
  { value: 'sync.retry', label: t('integrations.eventPushRetry') },
  { value: 'plugin.deployed', label: t('integrations.eventPluginPushed') },
  { value: 'backup.completed', label: t('integrations.eventBackupDone') },
];


/**
 * 通知与集成：Webhook 外部通知 + AI 辅助导入的接口配置。
 *
 * <para>两件事都是「把系统接到外部服务上」，放在一起便于统一检查密钥与连通性。</para>
 */
export async function render(container) {
  clear(container);
  container.appendChild(loadingBlock());

  const canSettings = hasPermission('settings.read');

  const [aiConfig, webhooks] = await Promise.all([
    canSettings ? api('/admin/ai/config') : Promise.resolve(null),
    canSettings ? api('/admin/webhooks') : Promise.resolve([]),
  ]);

  clear(container);
  container.appendChild(h('div',
    canSettings ? renderAiCard(aiConfig) : null,
    canSettings ? renderWebhooksCard(container, webhooks) : null,
    canSettings ? null : h('div.notice.notice-warn',
      h('span.notice-icon', '!'),
      h('div', t('integrations.noPermission'))),
  ));
}

function webhookKindLabel(kind) {
  return WEBHOOK_KINDS.find((k) => k.value === kind)?.label || kind;
}

function renderWebhooksCard(container, hooks) {
  const rows = hooks.map((hook) => h('tr',
    h('td',
      h('div.cell-main', hook.name),
      h('div.cell-sub', webhookKindLabel(hook.kind)),
    ),
    h('td', { style: { fontSize: '12px', maxWidth: '260px', overflowWrap: 'anywhere' } }, hook.url),
    h('td', { style: { fontSize: '12px' } },
      (hook.events || []).map((e) => WEBHOOK_EVENTS.find((x) => x.value === e)?.label || e).join('、') || '—'),
    h('td', hook.enabled
      ? h('span.badge', { style: { background: 'var(--ok-soft)', color: 'var(--ok)' } }, t('integrations.enabled'))
      : h('span.badge.badge-neutral', t('integrations.disabled'))),
    webhookDeliveryCell(hook),
    h('td.actions',
      h('button.btn.btn-sm', { type: 'button', onClick: () => testWebhook(hook) }, t('integrations.test')),
      h('button.btn.btn-sm', { type: 'button', onClick: () => openDeliveriesDialog(hook) }, t('integrations.deliveries')),
      h('button.btn.btn-sm', { type: 'button', onClick: () => openWebhookDialog(container, hook) }, t('integrations.edit')),
      h('button.btn.btn-sm.btn-danger', {
        type: 'button',
        onClick: async () => {
          if (!await confirmDialog(t('integrations.deleteTitle'), t('integrations.deleteConfirm', { name: hook.name }), t('integrations.delete'), true)) return;
          await api(`/admin/webhooks/${hook.id}`, { method: 'DELETE' });
          toast('ok', t('integrations.deleted'));
          await render(container);
        },
      }, t('integrations.delete')),
    ),
  ));

  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', t('integrations.webhookCard')),
        h('p.card-desc',
          t('integrations.webhookCardDesc')),
      ),
      h('button.btn.btn-primary.btn-sm', {
        type: 'button',
        onClick: () => openWebhookDialog(container, null),
        }, t('integrations.newWebhook')),
        ),
        // 「怎么用」直接写在界面上：管理员最常卡住的一步是「去哪里拿那个地址」。
        h('div.notice.notice-info', { style: { marginTop: '12px' } },
        h('span.notice-icon', 'i'),
        h('div',
        h('div', { style: { fontWeight: '600' } }, t('integrations.howToTitle')),
        h('div', t('integrations.step1')),
        h('div', { style: { marginLeft: '14px', color: 'var(--text-dim)' } },
          t('integrations.wecomHowTo')),
        h('div', { style: { marginLeft: '14px', color: 'var(--text-dim)' } },
          t('integrations.dingTalkHowTo')),
        h('div', { style: { marginLeft: '14px', color: 'var(--text-dim)' } },
          t('integrations.feishuHowTo')),
        h('div', t('integrations.step2')),
        h('div', t('integrations.step3')
          + t('integrations.step3b')),
        h('div', { style: { marginTop: '6px', color: 'var(--text-dim)' } },
          t('integrations.deliveryNote')),
        )),
        hooks.length === 0
      ? h('div.notice.notice-info', { style: { marginTop: '12px' } },
        h('span.notice-icon', 'i'),
        h('div', t('integrations.emptyHooks')))
      : h('div.table-wrap', { style: { marginTop: '12px' } },
        h('table.data',
          h('thead', h('tr',
            h('th', t('integrations.colName')),
            h('th', t('integrations.colUrl')),
            h('th', t('integrations.colEvents')),
            h('th', t('integrations.colState')),
            h('th', t('integrations.colLastDelivery')),
            h('th', { style: { textAlign: 'right' } }, t('integrations.colActions')),
          )),
          h('tbody', ...rows),
        ),
      ),
  );
}

async function testWebhook(hook) {
  try {
    const result = await api(`/admin/webhooks/${hook.id}/test`, { method: 'POST' });
    if (result.success) {
      toast('ok', t('integrations.testOk'), result.message || t('integrations.testOkDesc'));
    } else {
      // 失败原因来自服务端解析：HTTP 状态码之外，还包括接收端在 200 响应里返回的业务错误码
      // （机器人被停用、key 失效、被移出群等）——这正是「提示成功但群里没消息」的根源。
      toast('error', t('integrations.testFailed'), result.message);
    }
  } catch (err) {
    toastError(err, t('integrations.testFailedShort'));
  }
}

/**
 * 投递明细：每条通知「发出去了没有、对方回了什么、耗时多久、为什么被跳过」。
 * <para>这是排查 Webhook 最直接的入口——比只看「最近一次结果」有用得多。</para>
 */
async function openDeliveriesDialog(hook) {
  await guard(t('integrations.openDeliveries'), async () => openDeliveriesDialogInner(hook));
}

async function openDeliveriesDialogInner(hook) {
  let list = [];
  try {
    list = await api(`/admin/webhooks/${hook.id}/deliveries`);
  } catch (err) {
    toastError(err, t('integrations.deliveriesFailed'));
    return;
  }

  const color = (d) => (d.skipped ? 'var(--warn, #b26a00)'
    : (d.success ? 'var(--ok, #1a7f37)' : 'var(--danger)'));

  modal({
    title: t('integrations.deliveriesTitle', { name: hook.name }),
    width: 'wide',
    hideFooter: true,
    body: h('div',
      h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', t('integrations.deliveriesCount', { count: list.length })
          + t('integrations.deliveriesNote'))),
      list.length === 0
        ? h('div', { style: { padding: '10px 0', color: 'var(--text-faint)' } }, t('integrations.noDeliveries'))
        : h('div.cmd-list', { style: { marginTop: '12px' } }, ...list.map((d) => h('div.cmd-row',
          h('div.cmd-head',
            h('span.log-time', new Date(d.createdAt).toLocaleString('zh-CN', { hour12: false })),
            h('span.log-level', { style: { color: color(d) } },
              d.skipped ? t('integrations.statusSkipped') : (d.success ? t('integrations.statusOk') : t('integrations.statusFailed'))),
            h('span.cmd-kind', d.event),
            h('span.cmd-kind', `HTTP ${d.statusCode}`),
            h('span.cmd-kind', t('integrations.attempts', { count: d.attempts })),
            h('span.cmd-kind', `${d.durationMs} ms`),
          ),
          h('div.shot-note', d.title),
          d.error ? h('div.shot-note', { style: { color: 'var(--danger)' } }, d.error) : null,
          d.response
            ? h('div.shot-note', { style: { color: 'var(--text-dim)' } }, t('integrations.remoteResponse', { response: d.response }))
            : null,
        ))),
    ),
  });
}

/**
 * 「最近投递」单元格：把「最后一次发出去没有 / 为什么没发出去」直接摆在列表里，
 * 而不是只能去翻服务端日志。
 */
function webhookDeliveryCell(hook) {
  if (!hook.lastAttemptAt) {
    return h('td', { style: { fontSize: '12px', color: 'var(--text-faint)' } }, t('integrations.neverDelivered'));
  }

  const at = new Date(hook.lastAttemptAt).toLocaleString('zh-CN', { hour12: false });
  if (!hook.lastError) {
    return h('td', { style: { fontSize: '12px' } },
      h('div', { style: { color: 'var(--ok)' } }, t('integrations.okAt', { at })),
      h('div.cell-sub', `HTTP ${hook.lastStatusCode}`));
  }

  return h('td', { style: { fontSize: '12px', maxWidth: '250px' } },
    h('div', { style: { color: 'var(--danger)' } }, t('integrations.failedAt', { count: hook.failCount, at })),
    h('div.cell-sub', { title: hook.lastError, style: { overflowWrap: 'anywhere' } }, hook.lastError));
}

function openWebhookDialog(container, hook) {
  guard(t('integrations.openWebhookWindow'), () => openWebhookDialogInner(container, hook));
}

function openWebhookDialogInner(container, hook) {
  const isNew = !hook;
  const nameInput = h('input', { type: 'text', value: hook?.name || '', placeholder: t('integrations.namePlaceholder') });
  const kindSelect = select(WEBHOOK_KINDS, hook?.kind || 'wecom');
  const urlInput = h('input', { type: 'text', value: hook?.url || '', placeholder: t('integrations.urlPlaceholder') });
  // 密钥不再回传给前端（服务端只给「已设置」标记），因此这里始终从空开始：
  // 留空 = 保持原密钥，填了 = 覆盖。避免「改个名字就把加签密钥清掉」。
  const secretInput = h('input', {
    type: 'text',
    value: '',
    placeholder: hook?.secretUnavailable
      ? t('integrations.secretUndecryptable')
      : hook?.hasSecret ? t('integrations.secretSetKeep') : t('integrations.secretPlaceholder'),
  });
  let secretTouched = false;
  secretInput.addEventListener('input', () => { secretTouched = true; });

  const mentionChk = h('input', { type: 'checkbox' });
  mentionChk.checked = Boolean(hook?.mentionAll);
  const headersInput = h('textarea', {
    placeholder: '每行一条，例如：\nAuthorization: Bearer 你的令牌',
    style: { minHeight: '62px' },
  });
  headersInput.value = hook?.headers || '';
  const quietInput = h('input', {
    type: 'text',
    value: hook?.quietHours || '',
    placeholder: t('integrations.quietHoursPlaceholder'),
  });
  const timeoutInput = h('input', { type: 'number', value: String(hook?.timeoutSeconds ?? 8) });
  const retryInput = h('input', { type: 'number', value: String(hook?.maxRetries ?? 2) });
  const enabledChk = h('input', { type: 'checkbox' });
  enabledChk.checked = hook ? hook.enabled : true;

  const eventBoxes = WEBHOOK_EVENTS.map((event) => {
    const box = h('input', { type: 'checkbox' });
    box.checked = isNew ? true : (hook.events || []).includes(event.value);
    return { value: event.value, box, el: h('label.weekday-chip', box, h('span', event.label)) };
  });

  modal({
    title: isNew ? t('integrations.newWebhookTitle') : t('integrations.editTitle', { name: hook.name }),
    width: 'wide',
    body: h('div',
      h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', t('integrations.dialogDesc1')
          + t('integrations.dialogDesc2'))),
      h('div.form-row',
        field(t('integrations.colName'), nameInput),
        field(t('integrations.fieldKind'), kindSelect),
      ),
      field(t('integrations.fieldUrl'), urlInput),
      field(t('integrations.fieldSecret'), secretInput, t('integrations.fieldSecretHint')),
      field(h('span', t('integrations.colEvents'), hintIcon(
        t('integrations.eventsHint1')
        + t('integrations.eventsHint2')
        + t('integrations.eventsHint3')
        + t('integrations.dedupeHint'))), eventBoxes.length
        ? h('div.weekday-row', ...eventBoxes.map((e) => e.el))
        : h('span', { style: { color: 'var(--text-faint)' } }, t('integrations.eventsLoading'))),
      h('div.form-row',
        field(t('integrations.fieldTimeout'), timeoutInput, '1 ~ 60'),
        field(t('integrations.fieldRetries'), retryInput, t('integrations.fieldRetriesHint')),
      ),
      field(t('integrations.fieldQuietHours'), quietInput, t('integrations.fieldQuietHoursHint')),
      field(t('integrations.fieldHeaders'), headersInput, t('integrations.fieldHeadersHint')),
      h('label.checkbox-field', mentionChk, h('span', t('integrations.fieldMentionAll'))),
      h('label.checkbox-field', enabledChk, h('span', t('integrations.fieldEnabled'))),
    ),
    confirmText: isNew ? t('integrations.create') : t('integrations.save'),
    onConfirm: async () => {
      const body = {
        name: nameInput.value.trim(),
        kind: kindSelect.value,
        url: urlInput.value.trim(),
        // null = 不改动已保存的密钥（服务端据此保留原值）。
        secret: secretTouched ? secretInput.value.trim() : null,
        events: eventBoxes.filter((e) => e.box.checked).map((e) => e.value),
        enabled: enabledChk.checked,
        mentionAll: mentionChk.checked,
        headers: headersInput.value,
        quietHours: quietInput.value.trim(),
        timeoutSeconds: Number(timeoutInput.value) || 8,
        maxRetries: Number(retryInput.value) || 0,
      };

      try {
        if (isNew) {
          await api('/admin/webhooks', { method: 'POST', body });
        } else {
          await api(`/admin/webhooks/${hook.id}`, { method: 'PUT', body });
        }

        toast('ok', isNew ? t('integrations.created') : t('integrations.saved'));
        await render(container);
        return true;
      } catch (err) {
        toastError(err, t('integrations.saveFailed'));
        return false;
      }
    },
  });
}

/** AI 辅助导入的接口配置。接口密钥只回传掩码，提交时留空表示保持原值。 */
function renderAiCard(config) {
  const enabledInput = h('input', { type: 'checkbox' });
  enabledInput.checked = config.enabled !== false;

  const baseUrlInput = h('input', {
    type: 'text',
    value: config.baseUrl || '',
    placeholder: 'https://api.deepseek.com/v1',
  });
  const modelInput = h('input', {
    type: 'text',
    value: config.model || '',
    placeholder: 'deepseek-chat',
  });
  const apiKeyInput = h('input', {
    type: 'password',
    placeholder: keyPlaceholder(config),
  });
  const timeoutInput = h('input', {
    type: 'number', min: '10', max: '900', step: '10',
    value: String(config.timeoutSeconds || 180),
  });

  const status = h('div', { style: { fontSize: '12.5px', color: 'var(--text-faint)', marginTop: '10px' } },
    config.apiKeySet ? t('integrations.apiKeySaved') : t('integrations.apiKeyMissing'));

  const setStatus = (ok, text) => {
    status.textContent = text;
    status.style.color = ok ? 'var(--ok)' : 'var(--danger)';
  };

  const save = async (extra = {}) => {
    const saved = await api('/admin/ai/config', {
      method: 'PUT',
      body: {
        enabled: enabledInput.checked,
        baseUrl: baseUrlInput.value.trim(),
        model: modelInput.value.trim(),
        timeoutSeconds: Number(timeoutInput.value) || 180,
        apiKey: apiKeyInput.value,
        apiKeyClear: false,
        ...extra,
      },
    });
    apiKeyInput.value = '';
    apiKeyInput.placeholder = keyPlaceholder(saved);
    return saved;
  };

  const run = async (button, task) => {
    button.disabled = true;
    try {
      await task();
    } catch (err) {
      setStatus(false, err.message);
    } finally {
      button.disabled = false;
    }
  };

  const testBtn = h('button.btn.btn-sm', {
    type: 'button',
    onClick: () => run(testBtn, async () => {
      setStatus(true, t('integrations.testing'));
      await save();
      const result = await api('/admin/ai/test', { method: 'POST' });
      setStatus(result.ok, result.ok ? `${result.message}（${result.elapsedMs} ms）` : result.message);
    }),
  }, t('integrations.testConnection'));

  const clearBtn = h('button.btn.btn-sm', {
    type: 'button',
    onClick: () => run(clearBtn, async () => {
      if (!await confirmDialog(t('integrations.clearApiKeyTitle'), t('integrations.clearApiKeyConfirm'), t('integrations.clear'), true)) {
        return;
      }

      const saved = await save({ apiKey: '', apiKeyClear: true });
      apiKeyInput.placeholder = keyPlaceholder(saved);
      setStatus(true, t('integrations.apiKeyCleared'));
    }),
  }, t('integrations.clearKey'));

  const saveBtn = h('button.btn.btn-sm.btn-primary', {
    type: 'button',
    onClick: () => run(saveBtn, async () => {
      await save();
      setStatus(true, t('integrations.aiSaved'));
    }),
  }, t('integrations.saveAi'));

  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', t('integrations.aiCard')),
        h('p.card-desc', t('integrations.aiCardDesc'))),
    ),
    h('label.checkbox-field', enabledInput, t('integrations.aiEnable')),
    h('div.form-row',
      field(t('integrations.aiBaseUrl'), baseUrlInput, t('integrations.aiBaseUrlHint')),
      field(t('integrations.aiModel'), modelInput, t('integrations.aiModelHint')),
    ),
    h('div.form-row',
      field(t('integrations.aiApiKey'), apiKeyInput, t('integrations.aiApiKeyHint')),
      field(t('integrations.aiTimeout'), timeoutInput, t('integrations.aiTimeoutHint')),
    ),
    h('div.notice.notice-info',
      h('span.notice-icon', 'i'),
      h('div', t('integrations.aiPrivacy')
        + t('integrations.aiTestSaves')),
    ),
    h('div.card-actions', testBtn, clearBtn, saveBtn),
    status,
  );
}

function keyPlaceholder(config) {
  return config.apiKeySet ? t('integrations.aiKeyPlaceholderSaved', { hint: config.apiKeyHint }) : t('integrations.aiKeyPlaceholderLocal');
}