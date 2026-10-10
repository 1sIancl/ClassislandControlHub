/** 通知与集成视图（系统设置拆分而来）：Webhook 外部通知与 AI 辅助导入配置。 */

import { api, hasPermission } from '../core/api.js?v=90';
import { toastError } from '../core/errors.js?v=90';
import {
  h, clear, toast, modal, confirmDialog, field, select, guard, loadingBlock,
} from '../core/ui.js?v=90';

export const meta = {
  title: '通知与集成',
  subtitle: 'Webhook 外部通知与 AI 辅助导入',
};

const WEBHOOK_KINDS = [
  { value: 'wecom', label: '企业微信群机器人' },
  { value: 'dingtalk', label: '钉钉群机器人' },
  { value: 'feishu', label: '飞书群机器人' },
  { value: 'generic', label: '自定义端点（JSON）' },
];

const WEBHOOK_EVENTS = [
  { value: 'device.offline', label: '设备掉线' },
  { value: 'device.online', label: '设备恢复上线' },
  { value: 'device.error', label: '配置应用失败' },
  { value: 'command.failed', label: '远程指令失败' },
  { value: 'admin.login.failed', label: '登录失败（安全预警）' },
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
      h('div', '当前账号没有「系统设置」权限，看不到集成配置。')),
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
      ? h('span.badge', { style: { background: 'var(--ok-soft)', color: 'var(--ok)' } }, '已启用')
      : h('span.badge.badge-neutral', '已停用')),
    webhookDeliveryCell(hook),
    h('td.actions',
      h('button.btn.btn-sm', { type: 'button', onClick: () => testWebhook(hook) }, '测试'),
      h('button.btn.btn-sm', { type: 'button', onClick: () => openDeliveriesDialog(hook) }, '投递明细'),
      h('button.btn.btn-sm', { type: 'button', onClick: () => openWebhookDialog(container, hook) }, '编辑'),
      h('button.btn.btn-sm.btn-danger', {
        type: 'button',
        onClick: async () => {
          if (!await confirmDialog('删除 Webhook', `确定删除「${hook.name}」吗？`, '删除', true)) return;
          await api(`/admin/webhooks/${hook.id}`, { method: 'DELETE' });
          toast('ok', '已删除');
          await render(container);
        },
      }, '删除'),
    ),
  ));

  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', 'Webhook 外部通知'),
        h('p.card-desc',
          '设备掉线、配置应用失败、远程指令失败时，自动把消息推到企业微信 / 钉钉 / 飞书群，或你自己的服务。'),
      ),
      h('button.btn.btn-primary.btn-sm', {
        type: 'button',
        onClick: () => openWebhookDialog(container, null),
        }, '+ 新建 Webhook'),
        ),
        // 「怎么用」直接写在界面上：管理员最常卡住的一步是「去哪里拿那个地址」。
        h('div.notice.notice-info', { style: { marginTop: '12px' } },
        h('span.notice-icon', 'i'),
        h('div',
        h('div', { style: { fontWeight: '600' } }, '三步就能用起来'),
        h('div', '① 在群里添加「群机器人」并复制它的 Webhook 地址：'),
        h('div', { style: { marginLeft: '14px', color: 'var(--text-dim)' } },
          '企业微信：群聊 → 右上角「…」→ 群机器人 → 添加 → 复制 Webhook 地址'),
        h('div', { style: { marginLeft: '14px', color: 'var(--text-dim)' } },
          '钉钉：群设置 → 智能群助手 → 添加机器人 → 自定义 → 复制 Webhook 地址（安全设置选「加签」时把密钥一起填上）'),
        h('div', { style: { marginLeft: '14px', color: 'var(--text-dim)' } },
          '飞书：群设置 → 群机器人 → 添加机器人 → 自定义机器人 → 复制 Webhook 地址'),
        h('div', '② 点「+ 新建 Webhook」：填名称、选接收端类型、粘贴地址，勾选要推送的事件（至少一个）。'),
        h('div', '③ 先点「测试」：成功会显示耗时；失败会直接告诉你原因（含对方返回的错误码）。'
          + '确认真能收到消息后再打开「启用」。'),
        h('div', { style: { marginTop: '6px', color: 'var(--text-dim)' } },
          '真实推送的结果（含被静默/去重跳过）都在「投递明细」里逐条可查，不用去翻服务器日志。'),
        )),
        hooks.length === 0
      ? h('div.notice.notice-info', { style: { marginTop: '12px' } },
        h('span.notice-icon', 'i'),
        h('div', '还没有配置。建一个之后，教室里出问题会第一时间出现在群里，不用一直盯着管理界面。'))
      : h('div.table-wrap', { style: { marginTop: '12px' } },
        h('table.data',
          h('thead', h('tr',
            h('th', '名称'),
            h('th', '接收地址'),
            h('th', '订阅事件'),
            h('th', '状态'),
            h('th', '最近投递'),
            h('th', { style: { textAlign: 'right' } }, '操作'),
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
      toast('ok', '测试发送成功', result.message || '去目标群里看看有没有收到消息。');
    } else {
      // 失败原因来自服务端解析：HTTP 状态码之外，还包括接收端在 200 响应里返回的业务错误码
      // （机器人被停用、key 失效、被移出群等）——这正是「提示成功但群里没消息」的根源。
      toast('error', '测试发送失败', result.message);
    }
  } catch (err) {
    toastError(err, '测试失败');
  }
}

/**
 * 投递明细：每条通知「发出去了没有、对方回了什么、耗时多久、为什么被跳过」。
 * <para>这是排查 Webhook 最直接的入口——比只看「最近一次结果」有用得多。</para>
 */
async function openDeliveriesDialog(hook) {
  await guard('打开投递明细', async () => openDeliveriesDialogInner(hook));
}

async function openDeliveriesDialogInner(hook) {
  let list = [];
  try {
    list = await api(`/admin/webhooks/${hook.id}/deliveries`);
  } catch (err) {
    toastError(err, '读取投递明细失败');
    return;
  }

  const color = (d) => (d.skipped ? 'var(--warn, #b26a00)'
    : (d.success ? 'var(--ok, #1a7f37)' : 'var(--danger)'));

  modal({
    title: `投递明细 · ${hook.name}`,
    width: 'wide',
    hideFooter: true,
    body: h('div',
      h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', `最近 ${list.length} 条记录（每个 Webhook 最多保留 50 条）。`
          + '真实推送失败会自动重试；被跳过通常是静默时段或短时间重复触发。')),
      list.length === 0
        ? h('div', { style: { padding: '10px 0', color: 'var(--text-faint)' } }, '还没有投递记录。')
        : h('div.cmd-list', { style: { marginTop: '12px' } }, ...list.map((d) => h('div.cmd-row',
          h('div.cmd-head',
            h('span.log-time', new Date(d.createdAt).toLocaleString('zh-CN', { hour12: false })),
            h('span.log-level', { style: { color: color(d) } },
              d.skipped ? '已跳过' : (d.success ? '成功' : '失败')),
            h('span.cmd-kind', d.event),
            h('span.cmd-kind', `HTTP ${d.statusCode}`),
            h('span.cmd-kind', `尝试 ${d.attempts} 次`),
            h('span.cmd-kind', `${d.durationMs} ms`),
          ),
          h('div.shot-note', d.title),
          d.error ? h('div.shot-note', { style: { color: 'var(--danger)' } }, d.error) : null,
          d.response
            ? h('div.shot-note', { style: { color: 'var(--text-dim)' } }, `对方响应：${d.response}`)
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
    return h('td', { style: { fontSize: '12px', color: 'var(--text-faint)' } }, '尚未投递');
  }

  const at = new Date(hook.lastAttemptAt).toLocaleString('zh-CN', { hour12: false });
  if (!hook.lastError) {
    return h('td', { style: { fontSize: '12px' } },
      h('div', { style: { color: 'var(--ok)' } }, `成功 · ${at}`),
      h('div.cell-sub', `HTTP ${hook.lastStatusCode}`));
  }

  return h('td', { style: { fontSize: '12px', maxWidth: '250px' } },
    h('div', { style: { color: 'var(--danger)' } }, `失败 ${hook.failCount} 次 · ${at}`),
    h('div.cell-sub', { title: hook.lastError, style: { overflowWrap: 'anywhere' } }, hook.lastError));
}

function openWebhookDialog(container, hook) {
  guard('打开 Webhook 窗口', () => openWebhookDialogInner(container, hook));
}

function openWebhookDialogInner(container, hook) {
  const isNew = !hook;
  const nameInput = h('input', { type: 'text', value: hook?.name || '', placeholder: '例如：高一教师群' });
  const kindSelect = select(WEBHOOK_KINDS, hook?.kind || 'wecom');
  const urlInput = h('input', { type: 'text', value: hook?.url || '', placeholder: '群机器人的 Webhook 地址' });
  // 密钥不再回传给前端（服务端只给「已设置」标记），因此这里始终从空开始：
  // 留空 = 保持原密钥，填了 = 覆盖。避免「改个名字就把加签密钥清掉」。
  const secretInput = h('input', {
    type: 'text',
    value: '',
    placeholder: hook?.secretUnavailable
      ? '原密钥无法解密（加密密钥已更换），请重新填写'
      : hook?.hasSecret ? '已设置（留空保持不变）' : '钉钉加签密钥（其它类型留空）',
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
    placeholder: '例如 22:00-07:00（留空 = 不静默，支持跨夜）',
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
    title: isNew ? '新建 Webhook' : `编辑 Webhook · ${hook.name}`,
    width: 'wide',
    body: h('div',
      h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', '在群聊里添加「群机器人」，把它的 Webhook 地址填到这里即可；'
          + '钉钉若开启了「加签」模式，把加签密钥一并填上。')),
      h('div.form-row',
        field('名称', nameInput),
        field('接收端类型', kindSelect),
      ),
      field('Webhook 地址', urlInput),
      field('加签密钥（可选）', secretInput, '只有钉钉机器人在加签模式下需要。'),
      field('订阅事件', h('div.weekday-row', ...eventBoxes.map((e) => e.el)), '至少选一个。'),
      h('div.form-row',
        field('单次超时（秒）', timeoutInput, '1 ~ 60'),
        field('失败重试次数', retryInput, '0 ~ 3（间隔 1 / 5 / 15 秒）'),
      ),
      field('静默时段', quietInput, '该区间内不推送，避免半夜刷屏；留空表示不静默。'),
      field('自定义请求头', headersInput, '对接需要鉴权的自建端点时填写；# 开头为注释。'),
      h('label.checkbox-field', mentionChk, h('span', '推送时 @所有人（紧急通知用）')),
      h('label.checkbox-field', enabledChk, h('span', '启用')),
    ),
    confirmText: isNew ? '创建' : '保存',
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

        toast('ok', isNew ? '已创建' : '已保存');
        await render(container);
        return true;
      } catch (err) {
        toastError(err, '保存失败');
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
    config.apiKeySet ? '接口密钥已保存，可先「测试连接」确认可用。' : '尚未保存接口密钥。');

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
      setStatus(true, '正在测试…');
      await save();
      const result = await api('/admin/ai/test', { method: 'POST' });
      setStatus(result.ok, result.ok ? `${result.message}（${result.elapsedMs} ms）` : result.message);
    }),
  }, '测试连接');

  const clearBtn = h('button.btn.btn-sm', {
    type: 'button',
    onClick: () => run(clearBtn, async () => {
      if (!await confirmDialog('清除接口密钥', '确定清除已保存的接口密钥吗？', '清除', true)) {
        return;
      }

      const saved = await save({ apiKey: '', apiKeyClear: true });
      apiKeyInput.placeholder = keyPlaceholder(saved);
      setStatus(true, '接口密钥已清除。');
    }),
  }, '清除密钥');

  const saveBtn = h('button.btn.btn-sm.btn-primary', {
    type: 'button',
    onClick: () => run(saveBtn, async () => {
      await save();
      setStatus(true, 'AI 配置已保存。');
    }),
  }, '保存 AI 配置');

  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', 'AI 辅助导入课表'),
        h('p.card-desc', '把任意格式的课表文本（Excel 粘贴、网页复制、手工整理）交给大模型，自动整理成作息时间表、科目与课表。')),
    ),
    h('label.checkbox-field', enabledInput, '启用 AI 辅助导入'),
    h('div.form-row',
      field('接口地址', baseUrlInput, '任何兼容 OpenAI Chat Completions 的服务，填到 /v1 即可。'),
      field('模型名称', modelInput, '例如 deepseek-chat、qwen-plus、gpt-4o-mini。'),
    ),
    h('div.form-row',
      field('接口密钥', apiKeyInput, '仅保存在本机数据库，页面只回传掩码。'),
      field('超时（秒）', timeoutInput, '解析整周课表耗时较长，建议 120 秒以上。'),
    ),
    h('div.notice.notice-info',
      h('span.notice-icon', 'i'),
      h('div', '课表内容会发送到你填写的接口地址。若其中含教师姓名等信息，请使用可信服务或本地部署的模型。'
        + '「测试连接」会先保存当前配置。'),
    ),
    h('div.card-actions', testBtn, clearBtn, saveBtn),
    status,
  );
}

function keyPlaceholder(config) {
  return config.apiKeySet ? `已保存 ${config.apiKeyHint}，留空则不修改` : '本地部署的模型可留空';
}