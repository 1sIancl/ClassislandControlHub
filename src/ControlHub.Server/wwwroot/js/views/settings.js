/**
 * 系统设置视图：服务器信息、账号安全与部署提示。
 */

import { api, session, hasPermission, fetchBlob } from '../core/api.js?v=79';
import { toastError } from '../core/errors.js?v=79';
import {
  h, clear, formatDateTime, formatDuration, toast, loadingBlock,
  field, modal, copyText, confirmDialog, select, guard,
} from '../core/ui.js?v=79';

export const meta = {
  title: '系统设置',
  subtitle: '账号与权限、服务器信息与部署说明',
};

export async function render(container) {
  clear(container);
  container.appendChild(loadingBlock());

  // 按权限取数：没有权限的接口干脆不请求，否则整页会被 403 打断。
  const canSettings = hasPermission('settings.read');
  const canAccounts = hasPermission('accounts.read');

  const [info, me, accounts, permissions, permissionPresets, registerCodes, registerRequests, registration, timeOffset, updateState, aiConfig, webhooks, apiKeys] =
    await Promise.all([
      api('/server/info', { auth: false }),
      api('/admin/me'),
      canAccounts ? api('/admin/accounts') : Promise.resolve([]),
      canAccounts ? api('/admin/permissions') : Promise.resolve([]),
      canAccounts ? api('/admin/permission-presets') : Promise.resolve([]),
      canAccounts ? api('/admin/register-codes') : Promise.resolve([]),
      canAccounts ? api('/admin/register-requests') : Promise.resolve([]),
      api('/admin/registration', { auth: false }).catch(() => ({ enabled: false })),
      canSettings ? api('/admin/time-offset') : Promise.resolve(null),
      canSettings ? api('/admin/update/state') : Promise.resolve(null),
      canSettings ? api('/admin/ai/config') : Promise.resolve(null),
      canSettings ? api('/admin/webhooks') : Promise.resolve([]),
      canAccounts ? api('/admin/api-keys') : Promise.resolve([]),
    ]);

  session.serverInfo = info;
  session.me = me;

  clear(container);
  container.appendChild(h('div',
    me.mustChangePassword
      ? h('div.notice.notice-warn',
        h('span.notice-icon', '!'),
        h('div', '当前账号仍在使用初始密码，请立即在下方「账号安全」中修改。'))
      : null,
    h('div.grid-2',
      renderServerCard(info),
      renderAccountCard(me),
    ),
    canAccounts ? renderAccountsCard(container, accounts, me, permissions, permissionPresets) : null,
    canAccounts ? renderRegisterCodesCard(container, registerCodes, registration) : null,
    canAccounts ? renderRegisterRequestsCard(container, registerRequests, registration) : null,
    canAccounts ? renderApiKeysCard(container, apiKeys) : null,
    canSettings ? renderBrandingCard(info) : null,
    canSettings ? renderTimeCard(timeOffset) : null,
    canSettings ? renderAiCard(aiConfig) : null,
    canSettings ? renderWebhooksCard(container, webhooks) : null,
    canSettings ? renderUpdateCard(updateState) : null,
    // 备份与数据库维护（#59 / #62）：权限独立于系统设置，有 backup.read 就可见。
    hasPermission('backup.read') ? renderBackupCard(container) : null,
    renderShellCard(),
    renderDeployCard(info),
  ));
}

function renderServerCard(info) {
  const baseUrl = window.location.origin;

  return h('div.card',
    h('div.card-head',
      h('div',
        h('h3', '服务器信息'),
        h('p.card-desc', info.serverName || '集控服务器'),
      ),
    ),
    h('div', { style: { display: 'grid', gap: '10px', fontSize: '13px' } },
      row('管理界面地址', baseUrl, true),
      row('服务版本', info.version),
      row('通信协议', `v${info.protocolVersion}`),
      row('当前配置版本', `#${info.revision}`),
      row('运行时长', formatDuration(info.uptimeSeconds)),
      row('启动时间', formatDateTime(info.startedAt)),
      row('监听端口', `HTTP ${info.httpPort}`),
      row('局域网发现', info.discoveryEnabled ? `已开启（UDP ${info.discoveryPort}）` : '已关闭'),
      row('设备注册', info.requiresEnrollCode ? '需要注册码' : '无需注册码'),
      row('设备统计', `共 ${info.deviceCount} 台，在线 ${info.onlineDeviceCount} 台，待同步 ${info.pendingDeviceCount} 台`),
      row('数据目录', info.dataDirectory, true),
      row('敏感数据加密', formatSecrets(info.secretsEncrypted)),
      row('加密密钥文件', info.secretsEncrypted?.keyFile || '—', true),
    ),
    h('div.card-actions', { style: { marginTop: '14px' } },
      h('button.btn.btn-sm', {
        type: 'button',
        onClick: () => copyText(baseUrl, '访问地址已复制'),
      }, '复制访问地址'),
    ),
  );
}

/**
 * 静态敏感数据加密状态（#36）：注册码 / 邀请码 / Webhook 密钥分别有多少条已加密。
 * 有一条解不开就明确报警——「以为加密了其实没有」正是这个功能要防的。
 */
function formatSecrets(secrets) {
  if (!secrets) return '—';
  const parts = [
    `注册码 ${secrets.enrollCodes.encrypted}/${secrets.enrollCodes.total}`,
    `邀请码 ${secrets.registerCodes.encrypted}/${secrets.registerCodes.total}`,
    `Webhook 密钥 ${secrets.webhooks.encrypted}/${secrets.webhooks.total}`,
  ];
  const unavailable = (secrets.enrollCodes.unavailable || 0)
    + (secrets.registerCodes.unavailable || 0)
    + (secrets.webhooks.unavailable || 0);
  if (unavailable > 0) {
    return `${parts.join('，')} 已加密；${unavailable} 条无法解密（密钥文件已更换或丢失），需重新生成 / 重新填写`;
  }
  return `${parts.join('，')} 已加密`;
}

function row(label, value, mono = false) {
  return h('div', { style: { display: 'flex', justifyContent: 'space-between', gap: '16px', alignItems: 'baseline' } },
    h('span', { style: { color: 'var(--text-dim)', flex: 'none' } }, label),
    h('span', {
      style: {
        textAlign: 'right',
        wordBreak: 'break-all',
        fontFamily: mono ? 'var(--mono)' : 'inherit',
        fontSize: mono ? '12px' : 'inherit',
      },
    }, String(value ?? '—')),
  );
}

function renderAccountCard(me) {
  const isAdmin = me.role === 'admin';
  const granted = (me.permissions || []).length;

  return h('div.card',
    h('div.card-head',
      h('div',
        h('h3', '账号安全'),
        h('p.card-desc', `当前登录账号：${me.username}`),
      ),
    ),
    h('div', { style: { display: 'grid', gap: '10px', fontSize: '13px', marginBottom: '16px' } },
      row('显示名称', me.displayName || me.username),
      row('角色', isAdmin ? '超级管理员（全部权限）' : '自定义权限'),
      row('权限', isAdmin ? '全部模块' : `已勾选 ${granted} 项`),
      row('两步验证', me.totpEnabled
        ? h('span.badge', { style: { background: 'var(--ok-soft)', color: 'var(--ok)' } }, '已开启 TOTP')
        : h('span', { style: { color: 'var(--text-faint)' } }, '未开启')),
      row('登录有效期至', formatDateTime(me.expiresAt)),
    ),
    h('div.card-actions',
      h('button.btn.btn-primary.btn-sm', {
        type: 'button',
        onClick: () => openChangePasswordDialog(),
      }, '修改密码'),
      h('button.btn.btn-sm', {
        type: 'button',
        onClick: () => openTotpDialog(me),
      }, me.totpEnabled ? '关闭两步验证' : '开启两步验证'),
      h('button.btn.btn-sm', {
        type: 'button',
        title: '重新播放新手引导',
        onClick: () => replayOnboarding(),
      }, '重新观看引导'),
      h('button.btn.btn-sm', {
        type: 'button',
        onClick: async () => {
          await api('/admin/logout', { method: 'POST' });
          window.location.reload();
        },
      }, '退出登录'),
    ),
  );
}

/** 重置引导状态并立即播放一遍。 */
async function replayOnboarding() {
  try {
    await api('/admin/onboarding', { method: 'POST', query: { done: false } });
  } catch (err) {
    toastError(err, '操作失败');
    return;
  }

  const { startTour } = await import('../core/tour.js?v=42');
  startTour({
    onFinish: async (skipped) => {
      if (!skipped) {
        await api('/admin/onboarding', { method: 'POST' });
      }
    },
  });
}

/** 两步验证：开启（生成密钥 → 录入验证器 → 输入验证码确认）或关闭（用密码确认）。 */
function openTotpDialog(me) {
  if (me.totpEnabled) {
    const passwordInput = h('input', { type: 'password', autocomplete: 'current-password' });
    modal({
      title: '关闭两步验证',
      body: h('div',
        h('div.notice.notice-warn',
          h('span.notice-icon', '!'),
          h('div', '关闭后仅凭密码即可登录，建议只在验证器丢失时临时关闭，并尽快重新绑定。')),
        field('当前密码', passwordInput, '用于确认是本人在操作。')),
      confirmText: '关闭两步验证',
      danger: true,
      onConfirm: async () => {
        try {
          await api('/admin/totp/disable', { method: 'POST', body: { password: passwordInput.value } });
          toast('ok', '已关闭两步验证');
          await render(document.getElementById('content'));
          return true;
        } catch (err) {
          toastError(err, '关闭失败');
          return false;
        }
      },
    });
    return;
  }

  api('/admin/totp/setup', { method: 'POST' }).then((setup) => {
    const codeInput = h('input', { type: 'text', inputmode: 'numeric', maxlength: '6', placeholder: '6 位验证码' });
    const secretBox = h('code',
      { style: { fontFamily: 'var(--mono)', fontSize: '14px', letterSpacing: '1px' } }, setup.secret);
    const urlArea = h('textarea', { readOnly: true, style: { minHeight: '64px' } });
    urlArea.value = setup.otpAuthUrl;

    modal({
      title: '开启两步验证',
      width: 'wide',
      body: h('div',
        h('div.notice.notice-info',
          h('span.notice-icon', 'i'),
          h('div', '在验证器 App（Google / Microsoft Authenticator、1Password 等）里手动添加账号，'
            + '或用 otpauth 链接导入；然后输入它显示的 6 位验证码完成绑定。')),
        field('密钥（手动录入）',
          h('div', { style: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' } },
            secretBox,
            h('button.btn.btn-sm', { type: 'button', onClick: () => copyText(setup.secret, '密钥已复制') }, '复制密钥')),
          '建议把密钥单独记一份：验证器丢失时，它是恢复访问的唯一凭据。'),
        field('otpauth 链接（可选）', urlArea, '可复制到验证器生成二维码。'),
        field('验证码', codeInput, '输入验证器当前显示的 6 位数字。')),
      confirmText: '启用',
      onConfirm: async () => {
        try {
          await api('/admin/totp/enable', { method: 'POST', body: { code: codeInput.value.trim() } });
          toast('ok', '已开启两步验证', '下次登录需要额外输入验证码。');
          await render(document.getElementById('content'));
          return true;
        } catch (err) {
          toastError(err, '启用失败');
          return false;
        }
      },
    });
  }).catch((err) => toastError(err, '生成密钥失败'));
}

function openChangePasswordDialog() {
  const current = h('input', { type: 'password', autocomplete: 'current-password' });
  const next = h('input', { type: 'password', autocomplete: 'new-password' });
  const confirm = h('input', { type: 'password', autocomplete: 'new-password' });

  modal({
    title: '修改密码',
    width: 'wide',
    body: h('div',
      field('原密码', current),
      field('新密码', next, '至少 8 位，需同时包含字母与数字。'),
      field('确认新密码', confirm),
      h('div.notice.notice-warn',
        h('span.notice-icon', '!'),
        h('div', '修改成功后当前登录状态会失效，需要使用新密码重新登录。')),
    ),
    confirmText: '修改',
    onConfirm: async () => {
      if (next.value !== confirm.value) {
        toast('warn', '两次输入的新密码不一致');
        return false;
      }

      await api('/admin/password', {
        method: 'POST',
        body: { currentPassword: current.value, newPassword: next.value },
      });

      toast('ok', '密码已修改', '正在返回登录页…');
      setTimeout(() => window.location.reload(), 1200);
      return true;
    },
  });
}

function renderAccountsCard(container, accounts, me, permissions, presets) {
  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', `账号与权限（${accounts.length}）`),
        h('p.card-desc',
          '按模块勾选每个账号能做什么。设备、档案、分组等学校数据是共享的，'
          + '只有账号本身与个人提醒按用户隔离。'),
      ),
      h('button.btn.btn-primary.btn-sm', {
        type: 'button',
        onClick: () => openAccountEditor(container, null, permissions, presets),
      }, '+ 新建账号'),
    ),
    h('div.table-wrap',
      h('table.data',
        h('thead', h('tr',
          h('th', '账号'),
          h('th', '角色'),
          h('th', '权限'),
          h('th', '创建时间'),
          h('th', { style: { textAlign: 'right' } }, '操作'),
        )),
        h('tbody', ...accounts.map((account) => h('tr',
          h('td',
            h('div.cell-main', account.username),
            h('div.cell-sub', account.displayName || '—'),
            account.mustChangePassword
              ? h('div.cell-sub', { style: { color: 'var(--warn)' } }, '仍使用初始密码')
              : null,
          ),
          h('td', account.role === 'admin'
            ? h('span.badge.badge-accent', '超级管理员')
            : h('span.badge.badge-neutral', '自定义')),
          h('td', account.role === 'admin'
            ? h('span', { style: { color: 'var(--text-dim)' } }, '全部模块')
            : h('span', `已勾选 ${(account.permissions || []).length} 项`)),
          h('td', { style: { fontSize: '12px', color: 'var(--text-faint)' } }, formatDateTime(account.createdAt)),
          h('td.actions',
            h('button.btn.btn-sm', {
              type: 'button',
              onClick: () => openAccountEditor(container, account, permissions, presets),
            }, '编辑'),
            ' ',
            h('button.btn.btn-sm', {
              type: 'button',
              onClick: () => openResetPasswordDialog(account),
            }, '重置密码'),
            ' ',
            account.id === me.id
              ? null
              : h('button.btn.btn-sm.btn-danger', {
                type: 'button',
                onClick: () => removeAccount(container, account),
              }, '删除'),
          ),
        ))),
      ),
    ),
  );
}

/** 与服务端 PermissionKeys.DefaultForNewUser 保持一致：新建账号时的推荐权限。 */
const DEFAULT_PERMISSIONS = [
  'profiles.read', 'profiles.write',
  'devices.read', 'devices.write',
  'deploy.write',
  'remote.read', 'remote.write',
  'reminders.read', 'reminders.write',
  'audit.read',
];

/**
 * 模块权限勾选表。
 * @returns {{ table: HTMLElement, selected: Set<string>, refresh: () => void }}
 */
function createPermissionTable(permissions, initial, isAdminRole) {
  const selected = new Set(initial);
  const table = h('table.perm-table',
    h('thead', h('tr', h('th', '模块'), h('th.center', '查看'), h('th.center', '修改'))));
  const body = h('tbody');
  table.appendChild(body);

  let adminMode = isAdminRole;

  function paint() {
    clear(body);
    table.classList.toggle('disabled', adminMode);

    for (const module of permissions) {
      const make = (key) => {
        if (!key) {
          return h('span', { style: { color: 'var(--text-faint)' } }, '—');
        }

        const box = h('input', { type: 'checkbox' });
        box.checked = adminMode || selected.has(key);
        box.disabled = adminMode;
        box.addEventListener('change', () => {
          if (box.checked) {
            selected.add(key);
            // 「能改就能看」：勾上修改时把对应的查看也补上，避免出现自相矛盾的状态。
            if (key.endsWith('.write') && module.readKey) {
              selected.add(module.readKey);
              syncCheckbox(module.readKey, true);
            }
          } else {
            selected.delete(key);
            if (key.endsWith('.read') && module.writeKey) {
              selected.delete(module.writeKey);
              syncCheckbox(module.writeKey, false);
            }
          }
        });
        return box;
      };

      body.appendChild(h('tr',
        h('td',
          h('div.perm-name', module.label),
          h('div.perm-desc', module.description)),
        h('td.center', make(module.readKey)),
        h('td.center', make(module.writeKey)),
      ));
    }
  }

  /** 联动勾选时直接改对应复选框，不整表重绘，避免丢焦点。 */
  function syncCheckbox(key, value) {
    const boxes = body.querySelectorAll('input[type="checkbox"]');
    const keys = [];
    for (const module of permissions) {
      if (module.readKey) keys.push(module.readKey);
      if (module.writeKey) keys.push(module.writeKey);
    }

    const index = keys.indexOf(key);
    if (index >= 0 && boxes[index]) {
      boxes[index].checked = value;
    }
  }

  paint();
  return {
    table,
    selected,
    setAdminMode: (value) => {
      adminMode = value;
      paint();
    },
    /** 一键套用角色模板：整体替换勾选状态后重绘。 */
    setSelected: (keys) => {
      selected.clear();
      for (const key of (keys || [])) {
        selected.add(key);
      }
      paint();
    },
  };
}

function openAccountEditor(container, account, permissions, presets) {
  const isNew = !account;
  const isAdminAccount = account?.role === 'admin';
  const canGrantAdmin = session.me?.role === 'admin';

  const usernameInput = h('input', {
    type: 'text',
    value: account?.username || '',
    placeholder: '3~32 位，字母 / 数字 / . _ -',
  });
  usernameInput.disabled = !isNew;

  const displayInput = h('input', {
    type: 'text',
    value: account?.displayName || '',
    placeholder: '留空则使用用户名',
  });

  const passwordInput = h('input', {
    type: 'password',
    autocomplete: 'new-password',
    placeholder: '留空则由系统生成，并在创建后显示一次',
  });
  if (!isNew) {
    passwordInput.disabled = true;
  }

  const adminChk = h('input', { type: 'checkbox' });
  adminChk.checked = isAdminAccount;
  adminChk.disabled = !canGrantAdmin;

  const perm = createPermissionTable(permissions, isNew ? DEFAULT_PERMISSIONS : (account?.permissions || []), isAdminAccount);

  // 角色模板：一键套用岗位权限组合（套用后仍可逐个微调）
  const presetRow = h('div.preset-row');
  if ((presets || []).length > 0) {
    presetRow.appendChild(h('span.preset-label', '快速套用角色'));
    for (const preset of presets) {
      presetRow.appendChild(h('button.btn.btn-sm.preset-btn', {
        type: 'button',
        title: preset.description,
        onClick: () => {
          adminChk.checked = false;
          perm.setAdminMode(false);
          perm.setSelected(preset.permissions);
          toast('ok', `已套用「${preset.label}」`, preset.description);
        },
      }, preset.label));
    }
  } else {
    presetRow.hidden = true;
  }

  if (canGrantAdmin) {
    adminChk.addEventListener('change', () => perm.setAdminMode(adminChk.checked));
  }

  modal({
    title: isNew ? '新建账号' : `编辑账号 · ${account.username}`,
    width: 'wide',
    body: h('div',
      h('div.form-row',
        field('用户名', usernameInput, isNew ? '创建后不可修改。' : '用户名不可修改。'),
        field('显示名称', displayInput),
      ),
      isNew ? field('初始密码', passwordInput) : null,
      canGrantAdmin
        ? h('label.checkbox-field', adminChk,
          h('span', '设为超级管理员'),
          h('span', { style: { color: 'var(--text-faint)', fontSize: '12px' } }, '（拥有全部权限，且不可被裁剪）'))
        : null,
      presetRow,
      h('div', { style: { marginTop: '6px' } },
        h('div', { style: { fontSize: '12.5px', color: 'var(--text-dim)', marginBottom: '8px' } }, '模块权限'),
        perm.table,
        h('div.notice.notice-info', { style: { marginTop: '12px' } },
          h('span.notice-icon', 'i'),
          h('div', '「修改」包含新增 / 编辑 / 删除；勾选「修改」会自动带上对应的「查看」。'
            + '权限变更后，该账号需要重新登录才会生效。')),
      ),
    ),
    confirmText: isNew ? '创建' : '保存',
    onConfirm: async () => {
      const body = {
        username: usernameInput.value.trim(),
        displayName: displayInput.value.trim(),
        role: adminChk.checked ? 'admin' : 'custom',
        permissions: adminChk.checked ? [] : [...perm.selected],
        password: passwordInput.value,
      };

      if (isNew && !body.username) {
        toast('warn', '请填写用户名');
        return false;
      }

      let created = null;
      try {
        if (isNew) {
          created = await api('/admin/accounts', { method: 'POST', body });
        } else {
          await api(`/admin/accounts/${account.id}`, { method: 'PUT', body });
          toast('ok', '已保存');
        }
      } catch (err) {
        toastError(err, '保存失败');
        return false;
      }

      await render(container);

      if (created?.generatedPassword) {
        modal({
          title: '账号已创建',
          width: 'wide',
          hideFooter: true,
          body: h('div',
            h('p', { style: { marginTop: 0, color: 'var(--text-dim)' } },
              '系统生成的初始密码只显示这一次，请立即转交并提醒对方首次登录后修改：'),
            h('div.copy-row',
              h('div.code-block', created.generatedPassword),
              h('button.btn', {
                type: 'button',
                onClick: () => copyText(created.generatedPassword, '密码已复制'),
              }, '复制'),
            ),
          ),
        });
      } else if (isNew) {
        toast('ok', '账号已创建');
      }

      return true;
    },
  });
}

async function removeAccount(container, account) {
  if (!await confirmDialog('删除账号',
    `删除「${account.username}」会同时清掉它的登录会话与个人提醒，且不可恢复。确定继续吗？`, '删除', true)) {
    return;
  }

  try {
    await api(`/admin/accounts/${account.id}`, { method: 'DELETE' });
    toast('ok', '已删除');
    await render(container);
  } catch (err) {
    toastError(err, '删除失败');
  }
}

// ────────────────────────────── 注册申请（需审批） ──────────────────────────────

function renderRegisterRequestsCard(container, requests, registration) {
  const toggle = h('input', { type: 'checkbox' });
  toggle.checked = Boolean(registration?.approvalEnabled);
  toggle.addEventListener('change', async () => {
    toggle.disabled = true;
    try {
      await api('/admin/registration-approval', { method: 'PUT', query: { enabled: toggle.checked } });
      toast('ok', toggle.checked ? '已开启自助注册申请' : '已关闭自助注册申请');
    } catch (err) {
      toggle.checked = !toggle.checked;
      toastError(err, '操作失败');
    } finally {
      toggle.disabled = false;
    }
  });

  const pending = requests.filter((req) => req.status === 'pending');
  const handled = requests.filter((req) => req.status !== 'pending');

  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', '注册申请（需审批）'),
        h('p.card-desc',
          '开启后登录页会出现「申请账号」入口：用户填表提交，你在这里批准或拒绝，'
          + '批准之后账号才会真正建出来（用户密码由申请人自己设置）。'),
      ),
      pending.length > 0
        ? h('span.badge', { style: { background: 'var(--warn-soft)', color: 'var(--warn)' } }, `待审批 ${pending.length}`)
        : null,
    ),
    h('label.checkbox-field', toggle,
      h('span', '允许提交自助注册申请'),
      h('span', { style: { color: 'var(--text-faint)', fontSize: '12px' } }, '（不会自动建号，必须由管理员批准）')),
    pending.length === 0
      ? h('div.notice.notice-info', { style: { marginTop: '12px' } },
        h('span.notice-icon', 'i'),
        h('div', '当前没有待审批的申请。'))
      : h('div.table-wrap', { style: { marginTop: '12px' } },
        h('table.data',
          h('thead', h('tr',
            h('th', '用户名'),
            h('th', '显示名称'),
            h('th', '申请说明'),
            h('th', '提交时间'),
            h('th', { style: { textAlign: 'right' } }, '操作'),
          )),
          h('tbody', ...pending.map((req) => h('tr',
            h('td', h('code', { style: { fontWeight: '600' } }, req.username)),
            h('td', req.displayName || h('span', { style: { color: 'var(--text-faint)' } }, '—')),
            h('td', { style: { maxWidth: '260px', whiteSpace: 'pre-wrap', fontSize: '12px' } },
              req.note || h('span', { style: { color: 'var(--text-faint)' } }, '—')),
            h('td', { style: { fontSize: '12px' } }, formatDateTime(req.createdAt)),
            h('td.actions',
              h('button.btn.btn-primary.btn-sm', {
                type: 'button',
                onClick: () => openApproveDialog(container, req),
              }, '批准'),
              h('button.btn.btn-sm', {
                type: 'button',
                onClick: () => openRejectDialog(container, req),
              }, '拒绝'),
            ),
          ))),
        ),
      ),
    handled.length > 0 ? renderHandledRequests(container, handled) : null,
  );
}

/** 已处理的申请折叠展示，保留追溯信息。 */
function renderHandledRequests(container, handled) {
  return h('details', { style: { marginTop: '14px' } },
    h('summary', { style: { cursor: 'pointer', fontSize: '12.5px', color: 'var(--text-dim)' } },
      `已处理的申请（${handled.length}）`),
    h('div.table-wrap', { style: { marginTop: '10px' } },
      h('table.data',
        h('thead', h('tr',
          h('th', '用户名'),
          h('th', '结果'),
          h('th', '审批人'),
          h('th', '审批时间'),
          h('th', '拒绝理由'),
          h('th', { style: { textAlign: 'right' } }, '操作'),
        )),
        h('tbody', ...handled.map((req) => h('tr',
          h('td', req.username),
          h('td', req.status === 'approved'
            ? h('span.badge', { style: { background: 'var(--ok-soft)', color: 'var(--ok)' } }, '已批准')
            : h('span.badge', { style: { background: 'var(--danger-soft)', color: 'var(--danger)' } }, '已拒绝')),
          h('td', req.reviewedBy || '—'),
          h('td', { style: { fontSize: '12px' } }, req.reviewedAt ? formatDateTime(req.reviewedAt) : '—'),
          h('td', { style: { fontSize: '12px' } }, req.reason || '—'),
          h('td.actions',
            h('button.btn.btn-ghost.btn-sm', {
              type: 'button',
              onClick: async () => {
                if (!await confirmDialog('删除记录', `确定删除 ${req.username} 的申请记录吗？已建出的账号不受影响。`, '删除', true)) return;
                await api(`/admin/register-requests/${req.id}`, { method: 'DELETE' });
                toast('ok', '已删除');
                await render(container);
              },
            }, '删除'),
          ),
        ))),
      ),
    ),
  );
}

/** 批准申请：勾选该账号获得的权限，确认后立即建号。 */
function openApproveDialog(container, req) {
  const perm = createPermissionTable([], DEFAULT_PERMISSIONS, false);
  const catalog = h('div');
  let table = null;

  api('/admin/permissions').then((modules) => {
    table = createPermissionTable(modules, DEFAULT_PERMISSIONS, false);
    clear(catalog);
    catalog.appendChild(table.table);
  }).catch((err) => {
    clear(catalog);
    catalog.appendChild(h('div.notice.notice-danger', h('span.notice-icon', '!'), h('div', err.message)));
  });

  modal({
    title: `批准 ${req.username}`,
    width: 'wide',
    body: h('div',
      h('div.notice.notice-info', { style: { marginBottom: '12px' } },
        h('span.notice-icon', 'i'),
        h('div', `批准后立即创建账号「${req.username}」，密码为该申请人自行设置的密码，角色为「自定义权限」。`)),
      h('div', { style: { fontSize: '12.5px', color: 'var(--text-dim)', marginBottom: '8px' } }, '批准后获得的权限'),
      catalog,
    ),
    confirmText: '批准并建号',
    onConfirm: async () => {
      const permissions = [...(table ? table.selected : perm.selected)];
      if (permissions.length === 0) {
        toast('warn', '请至少勾选一项权限');
        return false;
      }

      await api(`/admin/register-requests/${req.id}/approve`, { method: 'POST', body: { permissions } });
      toast('ok', '已批准', `账号 ${req.username} 已创建。`);
      await render(container);
    },
  });
}

/** 拒绝申请：可填写理由，仅记录在管理端。 */
function openRejectDialog(container, req) {
  const reasonInput = h('input', { type: 'text', placeholder: '例如：请使用学校统一账号，或先联系管理员说明身份' });

  modal({
    title: `拒绝 ${req.username}`,
    body: h('div',
      field('拒绝理由', reasonInput, '只记录在管理端，不会自动通知申请人。'),
    ),
    confirmText: '拒绝申请',
    onConfirm: async () => {
      await api(`/admin/register-requests/${req.id}/reject`, {
        method: 'POST',
        body: { reason: reasonInput.value.trim() },
      });
      toast('ok', '已拒绝');
      await render(container);
    },
  });
}

// ────────────────────────────── 注册邀请码 ──────────────────────────────

function renderRegisterCodesCard(container, codes, registration) {
  const toggle = h('input', { type: 'checkbox' });
  toggle.checked = Boolean(registration?.enabled);
  toggle.addEventListener('change', async () => {
    toggle.disabled = true;
    try {
      await api('/admin/registration', { method: 'PUT', query: { enabled: toggle.checked } });
      toast('ok', toggle.checked ? '已开启自助注册' : '已关闭自助注册');
    } catch (err) {
      toggle.checked = !toggle.checked;
      toastError(err, '操作失败');
    } finally {
      toggle.disabled = false;
    }
  });

  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', '注册邀请码'),
        h('p.card-desc',
          '默认关闭自助注册：A 端是校内控制台，开放注册意味着任何拿到地址的人都有可能进入管理界面。'),
      ),
      h('button.btn.btn-primary.btn-sm', {
        type: 'button',
        onClick: () => openCreateRegisterCodeDialog(container),
      }, '+ 生成邀请码'),
    ),
    h('label.checkbox-field', toggle,
      h('span', '允许凭邀请码自助注册'),
      h('span', { style: { color: 'var(--text-faint)', fontSize: '12px' } }, '（关闭时注册接口一律拒绝）')),
    codes.length === 0
      ? h('div.notice.notice-info', { style: { marginTop: '12px' } },
        h('span.notice-icon', 'i'),
        h('div', '还没有邀请码。生成一个并把码发给新同事，对方即可在登录页自助注册。'))
      : h('div.table-wrap', { style: { marginTop: '12px' } },
        h('table.data',
          h('thead', h('tr',
            h('th', '邀请码'),
            h('th', '备注'),
            h('th', '注册后权限'),
            h('th', '剩余次数'),
            h('th', '有效期'),
            h('th', { style: { textAlign: 'right' } }, '操作'),
          )),
          h('tbody', ...codes.map((code) => h('tr',
            h('td',
              h('div', { style: { display: 'flex', alignItems: 'center', gap: '8px' } },
                code.available === false
                  ? h('div', { style: { display: 'flex', alignItems: 'center', gap: '8px' } },
                    h('span.badge.badge-danger', '不可用'),
                    h('span', { style: { color: 'var(--text-faint)', fontSize: '12px' } },
                      `加密密钥已更换，${code.reference}（请重新生成）`),
                  )
                  : [
                    h('code', { style: { fontSize: '14px', letterSpacing: '1.5px', fontWeight: '700' } }, code.code),
                    h('button.btn.btn-ghost.btn-sm', {
                      type: 'button',
                      onClick: () => copyText(code.code, '邀请码已复制'),
                    }, '复制'),
                  ],
              ),
            ),
            h('td', code.note || h('span', { style: { color: 'var(--text-faint)' } }, '—')),
            h('td', `已勾选 ${(code.permissions || []).length} 项`),
            h('td', code.remainingUses < 0 ? '不限' : `剩余 ${code.remainingUses} / ${code.maxUses}`),
            h('td', code.expiresAt
              ? h('span', { style: { fontSize: '12px' } }, formatDateTime(code.expiresAt))
              : h('span.badge.badge-ok', '长期有效')),
            h('td.actions',
              h('button.btn.btn-sm.btn-danger', {
                type: 'button',
                onClick: async () => {
                  // 明文不可用时用指纹引用操作该行（#36）：界面拿不到邀请码本体，但删除仍然可用。
                  const target = code.available === false ? code.reference : code.code;
                  const label = code.available === false ? code.reference : code.code;
                  if (!await confirmDialog('删除邀请码', `确定删除邀请码 ${label} 吗？已注册的账号不受影响。`, '删除', true)) return;
                  await api(`/admin/register-codes/${encodeURIComponent(target)}`, { method: 'DELETE' });
                  toast('ok', '已删除');
                  await render(container);
                },
              }, '删除'),
            ),
          ))),
        ),
      ),
  );
}

function openCreateRegisterCodeDialog(container) {
  const noteInput = h('input', { type: 'text', placeholder: '例如：给张老师的账号' });
  const maxUsesInput = h('input', { type: 'number', min: '0', value: '1' });
  const hoursInput = h('input', { type: 'number', min: '0', value: '72' });

  // 与新建账号共用同一张权限表，保证口径一致。
  const perm = createPermissionTable([], DEFAULT_PERMISSIONS, false);
  const catalog = h('div');
  let table = null;

  api('/admin/permissions').then((modules) => {
    table = createPermissionTable(modules, DEFAULT_PERMISSIONS, false);
    clear(catalog);
    catalog.appendChild(table.table);
  }).catch((err) => {
    clear(catalog);
    catalog.appendChild(h('div.notice.notice-danger', h('span.notice-icon', '!'), h('div', err.message)));
  });

  modal({
    title: '生成注册邀请码',
    width: 'wide',
    body: h('div',
      field('备注', noteInput, '仅用于管理端识别，不会展示给注册者。'),
      h('div.form-row',
        field('可注册次数', maxUsesInput, '填 0 表示不限次数。'),
        field('有效小时数', hoursInput, '填 0 表示长期有效。'),
      ),
      h('div', { style: { marginTop: '6px' } },
        h('div', { style: { fontSize: '12.5px', color: 'var(--text-dim)', marginBottom: '8px' } }, '注册后获得的权限'),
        catalog,
        h('div.notice.notice-info', { style: { marginTop: '12px' } },
          h('span.notice-icon', 'i'),
          h('div', '只能勾选你自己拥有的权限；注册出来的账号一律是「自定义权限」角色。')),
      ),
    ),
    confirmText: '生成',
    onConfirm: async () => {
      const body = {
        note: noteInput.value.trim(),
        permissions: [...(table ? table.selected : perm.selected)],
        maxUses: Number(maxUsesInput.value) || 0,
        validHours: Number(hoursInput.value) || 0,
      };

      if (body.permissions.length === 0) {
        toast('warn', '请至少勾选一项权限');
        return false;
      }

      let created = null;
      try {
        created = await api('/admin/register-codes', { method: 'POST', body });
      } catch (err) {
        toastError(err, '生成失败');
        return false;
      }

      await render(container);
      modal({
        title: '邀请码已生成',
        width: 'wide',
        hideFooter: true,
        body: h('div',
          h('p', { style: { marginTop: 0, color: 'var(--text-dim)' } },
            '把下面的邀请码发给对方，对方可在登录页「使用邀请码注册」完成注册：'),
          h('div.copy-row',
            h('div.code-block', created.code),
            h('button.btn', { type: 'button', onClick: () => copyText(created.code, '邀请码已复制') }, '复制'),
          ),
        ),
      });
      return true;
    },
  });
}

function openResetPasswordDialog(account) {
  const next = h('input', { type: 'password', autocomplete: 'new-password' });

  modal({
    title: `重置密码 · ${account.username}`,
    width: 'wide',
    body: h('div',
      field('新密码', next, '至少 8 位，需同时包含字母与数字。'),
      h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', '重置后该账号的所有登录状态会失效，下次登录时会被要求修改密码。')),
    ),
    confirmText: '重置',
    danger: true,
    onConfirm: async () => {
      await api(`/admin/accounts/${account.id}/reset-password`, {
        method: 'POST',
        body: { currentPassword: '', newPassword: next.value },
      });
      toast('ok', '密码已重置');
      return true;
    },
  });
}

// ────────────────────────────── API 密钥（开放 API 接入） ──────────────────────────────

/** 可签发的权限（默认只读）：密钥不该拿它去做管理动作，需要写权限时应有意识地勾选。 */
const API_KEY_READ_SCOPES = [
  { key: 'devices.read', label: '设备只读（设备清单、状态、日志）' },
  { key: 'profiles.read', label: '配置档案只读' },
  { key: 'audit.read', label: '审计日志只读' },
  { key: 'remote.read', label: '远程信息只读（指令历史、诊断记录）' },
];

const API_KEY_WRITE_SCOPES = [
  { key: 'remote.write', label: '远程下发（指令、通知、外观）' },
  { key: 'deploy.write', label: '触发配置下发' },
];

function renderApiKeysCard(container, keys) {
  const canWrite = hasPermission('accounts.write');
  const active = keys.filter((k) => k.active);

  const rows = keys.map((key) => h('tr',
    h('td',
      h('div.cell-main', key.name),
      h('div.cell-sub', `${key.prefix}…　由 ${key.createdBy || '—'} 签发${key.note ? `　${key.note}` : ''}`),
    ),
    h('td', { style: { fontSize: '12px', maxWidth: '260px', overflowWrap: 'anywhere' } },
      (key.permissions || []).join('、') || '—'),
    h('td', { style: { fontSize: '12px' } },
      key.active
        ? h('span.badge', { style: { background: 'var(--ok-soft)', color: 'var(--ok)' } }, '可用')
        : h('span.badge.badge-neutral', key.revoked ? '已撤销' : '已过期'),
      h('div.cell-sub', key.expiresAt ? `至 ${formatDateTime(key.expiresAt)}` : '长期有效'),
    ),
    h('td', { style: { fontSize: '12px' } }, key.lastUsedAt ? formatDateTime(key.lastUsedAt) : '从未使用'),
    h('td.actions',
      canWrite && key.active
        ? h('button.btn.btn-sm.btn-danger', {
          type: 'button',
          onClick: async () => {
            if (!await confirmDialog('撤销密钥',
              `撤销后「${key.name}」立刻失效，正在使用它的脚本会收到 401。确定撤销吗？`, '撤销', true)) return;
            try {
              await api(`/admin/api-keys/${key.id}`, { method: 'DELETE' });
              toast('ok', '已撤销');
              await render(container);
            } catch (err) {
              toastError(err, '撤销失败');
            }
          },
        }, '撤销')
        : null,
    ),
  ));

  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', `API 密钥（${active.length} 把可用）`),
        h('p.card-desc',
          '给监控脚本、第三方系统用的凭据：可只给只读权限、可设期限、可随时撤销，不必再共用管理员账号或密码。'),
      ),
      canWrite
        ? h('button.btn.btn-primary.btn-sm', {
          type: 'button',
          onClick: () => openApiKeyDialog(container),
        }, '+ 签发密钥')
        : null,
    ),
    keys.length === 0
      ? h('div.notice.notice-info', { style: { marginTop: '12px' } },
        h('span.notice-icon', 'i'),
        h('div', '还没有密钥。要让脚本读设备状态或拉审计日志，建议签发一把只读密钥，而不是直接用管理员账号。'))
      : h('div.table-wrap', { style: { marginTop: '12px' } },
        h('table.table',
          h('thead', h('tr',
            h('th', '名称'), h('th', '权限'), h('th', '状态'), h('th', '最近使用'), h('th', ''))),
          h('tbody', ...rows))),
  );
}

/** 签发密钥：选权限（默认只读）、设期限；明文只在返回时显示一次。 */
function openApiKeyDialog(container) {
  const nameInput = h('input', { type: 'text', placeholder: '例如：机房监控脚本 / 教务导出工具' });
  const daysInput = h('input', { type: 'number', min: '0', max: '3650', value: '90' });
  const noteInput = h('input', { type: 'text', placeholder: '谁在用、用来做什么（便于将来判断该不该撤销）' });

  const boxes = [...API_KEY_READ_SCOPES, ...API_KEY_WRITE_SCOPES].map((scope) => ({
    ...scope,
    box: h('input', { type: 'checkbox', checked: scope.key.endsWith('.read') }),
  }));

  modal({
    title: '签发 API 密钥',
    width: 'wide',
    confirmText: '签发',
    body: h('div',
      h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', '密钥明文只会显示这一次（服务端只存哈希），丢了只能重新签发。'
          + '建议只勾只读权限；出于安全考虑，「密钥管理」权限不允许授予密钥，避免一把密钥无限自我复制。')),
      field('名称', nameInput, '必填：写清是谁在用，否则将来无法判断该不该撤销。'),
      field('有效天数', daysInput, '填 0 表示长期有效；建议给脚本设一个期限，到期自动失效。'),
      h('div', { style: { marginTop: '10px' } },
        h('div', { style: { fontSize: '12.5px', marginBottom: '6px' } }, '权限（默认只勾只读）'),
        h('div', { style: { display: 'grid', gap: '4px' } },
          ...boxes.map(({ box, label }) => h('label.checkbox-field', box, label)))),
      field('备注', noteInput, '可选：便于交接与日后排查。'),
    ),
    onConfirm: async () => {
      const permissions = boxes.filter((b) => b.box.checked).map((b) => b.key);
      try {
        const result = await api('/admin/api-keys', {
          method: 'POST',
          body: {
            name: nameInput.value.trim(),
            permissions,
            expiresInDays: Number(daysInput.value || 0),
            note: noteInput.value.trim(),
          },
        });
        showApiKeySecret(result);
        await render(container);
        return true;
      } catch (err) {
        toastError(err, '签发失败');
        return false;
      }
    },
  });
}

/** 明文展示（一次性）：给出密钥与一句可直接粘贴的用法示例。 */
function showApiKeySecret(result) {
  const secretBox = h('input', {
    readOnly: true,
    value: result.secret,
    style: { fontFamily: 'var(--mono)', fontSize: '13px' },
  });
  const sampleBox = h('textarea', {
    readOnly: true,
    rows: '2',
    style: { width: '100%', fontFamily: 'var(--mono)', fontSize: '12px' },
  });
  sampleBox.value = `curl -H "Authorization: ApiKey ${result.secret}" `
    + `${(session.serverInfo && session.serverInfo.publicBaseUrl) || 'http://<服务器>:29800'}/api/v1/admin/devices`;

  modal({
    title: '密钥已签发（请立刻保存）',
    width: 'wide',
    confirmText: '我已保存',
    body: h('div',
      h('div.notice.notice-warn',
        h('span.notice-icon', '!'),
        h('div', '这是唯一一次显示明文的机会。关闭后无法再查看，只能重新签发。')),
      field('密钥', secretBox, `名称：${result.key.name}`),
      h('div', { style: { marginTop: '8px' } },
        h('button.btn.btn-sm', {
          type: 'button',
          onClick: async () => {
            try {
              await navigator.clipboard.writeText(result.secret);
              toast('ok', '已复制密钥');
            } catch {
              toast('warn', '复制失败', '请手动选中输入框内容复制。');
            }
          },
        }, '复制密钥')),
      field('用法示例', sampleBox, '也可以改用 X-Api-Key 请求头。'),
    ),
  });
}

// ────────────────────────────── Webhook 外部通知 ──────────────────────────────

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

// ────────────────────────────── 备份与数据库维护（#59 / #62） ──────────────────────────────

/** 字节数写成可读文本。 */
function formatBytes(bytes) {
  const n = Number(bytes) || 0;
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  if (n < 1024 * 1024 * 1024) return `${(n / 1024 / 1024).toFixed(1)} MB`;
  return `${(n / 1024 / 1024 / 1024).toFixed(2)} GB`;
}

const BACKUP_TYPE_LABELS = { manual: '手动', auto: '自动', update: '更新前' };

/**
 * 备份与数据库维护卡。
 *
 * 为什么把这两件事放一起：它们共用同一个前提——「数据在磁盘上会越长越大，而备份是唯一的退路」。
 * 备份**默认不含加密密钥**（密钥是凭据），所以列表里逐条标注「含 / 不含密钥」，
 * 免得有人以为随便哪份备份都能拿去换机器。
 */
function renderBackupCard(container) {
  const listBox = h('div');
  const healthBox = h('div');

  const canWrite = hasPermission('backup.write');

  const card = h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', '备份与数据库'),
        h('p.card-desc',
          '备份把数据目录（数据库、配置文件）复制到 data/Backups 下；'
          + '加密密钥默认不包含在内——它是凭据，含密钥的备份等于能接管全部注册码。'),
      ),
      canWrite
        ? h('button.btn.btn-primary.btn-sm', {
          type: 'button',
          onClick: () => openCreateBackupDialog(container),
        }, '+ 新建备份')
        : null,
    ),
    healthBox,
    listBox,
  );

  const load = async () => {
    await Promise.all([loadHealth(), loadList()]);
  };

  async function loadHealth() {
    clear(healthBox);
    healthBox.appendChild(h('p.card-desc', '正在检查数据库…'));
    try {
      const health = await api('/admin/database/health');
      clear(healthBox);
      healthBox.appendChild(renderHealth(health, load));
    } catch (err) {
      clear(healthBox);
      healthBox.appendChild(h('p.card-desc', `${err.message || '检查失败'}`));
    }
  }

  function renderHealth(health, reload) {
    const reclaimable = health.reclaimableBytes || 0;
    return h('div', { style: { marginBottom: '16px' } },
      h('div.config-section-title', '数据库'),
      h('div', { style: { display: 'grid', gap: '6px', fontSize: '13px' } },
        row('占用空间', `${formatBytes(health.totalBytes)}（主库 ${formatBytes(health.databaseBytes)}`
          + `，WAL ${formatBytes(health.walBytes)}，SHM ${formatBytes(health.sharedMemoryBytes)}）`),
        row('完整性检查', health.integrityOk ? '正常' : `异常：${health.integrityMessage}`),
        row('可回收空间', reclaimable > 0
          ? `${formatBytes(reclaimable)}（${health.freePageCount} 个空闲页）`
          : '没有可回收的空闲页'),
      ),
      health.integrityOk
        ? null
        : h('div.notice.notice-danger', { style: { marginTop: '10px' } },
          h('span.notice-icon', '!'),
          h('div', '完整性检查未通过。请立刻做一次备份，并把这份数据库交给技术人员确认。')),
      reclaimable > 0
        ? h('p.card-desc', { style: { marginTop: '8px' } },
          'SQLite 删除数据后不会自动归还磁盘空间，整理（VACUUM）会重写整库并回收这部分空间；'
          + `执行期间需要额外约 ${formatBytes(health.databaseBytes)} 的磁盘可用空间。`)
        : null,
      canWrite
        ? h('button.btn.btn-sm', {
          type: 'button',
          style: { marginTop: '10px' },
          onClick: async () => {
            if (!await confirmDialog('整理数据库',
              `将重写整个数据库并回收空间${reclaimable > 0 ? `（预计回收约 ${formatBytes(reclaimable)}）` : ''}。\n`
              + '过程中服务端会短暂变慢，建议避开使用高峰。确定继续吗？', '开始整理')) {
              return;
            }

            try {
              const result = await api('/admin/database/vacuum', { method: 'POST' });
              toast('ok', '整理完成',
                `${formatBytes(result.beforeBytes)} → ${formatBytes(result.afterBytes)}`
                + `（回收 ${formatBytes(result.reclaimedBytes)}，耗时 ${result.durationMs} ms）`);
              await reload();
            } catch (err) {
              toastError(err, '整理失败');
            }
          },
        }, '整理数据库（VACUUM）')
        : null,
    );
  }

  async function loadList() {
    clear(listBox);
    listBox.appendChild(h('p.card-desc', '正在读取备份列表…'));
    try {
      const entries = await api('/admin/backups');
      clear(listBox);
      listBox.appendChild(renderList(entries, load));
    } catch (err) {
      clear(listBox);
      listBox.appendChild(h('p.card-desc', err.message || '读取失败'));
    }
  }

  function renderList(entries, reload) {
    const title = h('div.config-section-title', `备份（${entries.length}）`);
    if (entries.length === 0) {
      return h('div', title,
        h('p.card-desc', { style: { margin: 0 } },
          '还没有备份。升级、改数据之前建议先建一份——出问题能整库退回。'));
    }

    return h('div', title,
      h('div.config-panel', ...entries.map((entry) => h('div.config-item',
        h('div', { style: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 } },
          h('span', { style: { fontWeight: '600', fontSize: '13px' } },
            `${BACKUP_TYPE_LABELS[entry.type] || entry.type} · ${formatDateTime(entry.createdAt)}`),
          h('span', { style: { color: 'var(--text-faint)', fontSize: '12px' } },
            `${formatBytes(entry.sizeBytes)}`
            + `${entry.fileCount ? `，${entry.fileCount} 个文件` : ''}`
            + `${entry.note ? `　${entry.note}` : ''}`),
        ),
        h('span', { style: { marginLeft: 'auto', flex: 'none', display: 'flex', alignItems: 'center', gap: '8px' } },
          entry.includesSecretsKey
            ? h('span.badge.badge-danger', { title: '含加密密钥：这份备份可以接管全部凭据，切勿外发' }, '含密钥')
            : h('span.badge.badge-neutral', { title: '不含加密密钥：换机器恢复时注册码会失效，需要另找 secrets.key' }, '不含密钥'),
          h('button.btn.btn-sm', { type: 'button', onClick: () => downloadBackup(entry.id) }, '下载'),
          canWrite
            ? h('button.btn.btn-sm', {
              type: 'button',
              title: '导出为加密文件（换机器 / 放网盘时用）',
              onClick: () => openEncryptedExportDialog(entry),
            }, '加密导出')
            : null,
          canWrite
            ? h('button.btn.btn-sm', {
              type: 'button',
              onClick: async () => {
                if (!await confirmDialog('从备份恢复',
                  `将用「${entry.id}」覆盖当前数据库，并需要重启服务才生效。\n`
                  + '恢复前会自动为当前数据做一次保护性备份。确定继续吗？', '恢复', true)) {
                  return;
                }

                try {
                  await api(`/admin/backups/${encodeURIComponent(entry.id)}/restore`, { method: 'POST' });
                  toast('ok', '已恢复', '需要重启服务端才能生效。', 8000);
                  await reload();
                } catch (err) {
                  toastError(err, '恢复失败');
                }
              },
            }, '恢复')
            : null,
          canWrite
            ? h('button.btn.btn-sm.btn-danger', {
              type: 'button',
              onClick: async () => {
                if (!await confirmDialog('删除备份', `确定删除备份「${entry.id}」吗？`, '删除', true)) {
                  return;
                }

                try {
                  await api(`/admin/backups/${encodeURIComponent(entry.id)}`, { method: 'DELETE' });
                  toast('ok', '已删除');
                  await reload();
                } catch (err) {
                  toastError(err, '删除失败');
                }
              },
            }, '删除')
            : null,
        ),
      ))),
    );
  }

  load();
  return card;
}

/** 下载备份（走带鉴权的取回方式，避免直链 401）。 */
async function downloadBackup(id) {
  try {
    const blob = await fetchBlob(`/admin/backups/${encodeURIComponent(id)}/download`);
    const url = URL.createObjectURL(blob);
    const link = h('a', { href: url, download: `${id}.zip` });
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 4000);
  } catch (err) {
    toastError(err, '下载失败');
  }
}

/**
 * 加密导出（#60）：口令走请求体（不进 URL / 访问日志），服务端**不保存**它——
 * 忘了口令这份备份就解不开，所以界面必须把这句话说在前面，并要求二次输入。
 */
function openEncryptedExportDialog(entry) {
  const p1 = h('input', { type: 'password', placeholder: '至少 6 位' });
  const p2 = h('input', { type: 'password', placeholder: '再输一次' });
  const hint = h('p.card-desc', { style: { margin: '8px 0 0' } }, '');

  const fail = (text) => {
    hint.textContent = text;
    hint.style.color = 'var(--danger)';
  };

  modal({
    title: `加密导出 · ${entry.id}`,
    width: 'wide',
    body: h('div',
      h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', '导出的是加密文件（.zip.enc），必须用口令才能解开。'
          + '服务端不保存口令——忘记口令这份备份就作废了。')),
      field('口令', p1, '请记到安全的地方；建议与其他密码分开保管。'),
      field('确认口令', p2),
      hint,
      h('p.card-desc', { style: { marginTop: '10px' } },
        '解密命令：ControlHub.Server --decrypt-backup <加密文件> <输出.zip>（会提示输入口令）。'),
    ),
    confirmText: '导出',
    onConfirm: async () => {
      if (p1.value.length < 6) {
        fail('口令至少 6 位。');
        return false;
      }

      if (p1.value !== p2.value) {
        fail('两次输入的口令不一致。');
        return false;
      }

      try {
        const blob = await fetchBlob(`/admin/backups/${encodeURIComponent(entry.id)}/export`, {
          method: 'POST',
          body: { passphrase: p1.value },
        });

        const url = URL.createObjectURL(blob);
        const link = h('a', { href: url, download: `${entry.id}.zip.enc` });
        document.body.appendChild(link);
        link.click();
        link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 4000);

        toast('ok', '已导出加密备份', '请把口令记在安全的地方：丢了就解不开这份备份。', 8000);
        return true;
      } catch (err) {
        fail(err.message || '导出失败。');
        return false;
      }
    },
  });
}

/** 新建备份对话框：可选内容默认「数据库 + 配置文件」，密钥要显式勾选并看到警告。 */
function openCreateBackupDialog(container) {
  const noteInput = h('input', { type: 'text', placeholder: '例如：升级 1.3.5 前' });
  const configChk = h('input', { type: 'checkbox' });
  configChk.checked = true;
  const keyChk = h('input', { type: 'checkbox' });
  const tokenChk = h('input', { type: 'checkbox' });

  const keyWarning = h('div.notice.notice-warn', { hidden: true },
    h('span.notice-icon', '!'),
    h('div', '含密钥的备份等同于「服务器凭据全套」：谁拿到它，谁就能解开全部注册码与 Webhook 密钥。'
      + '只在换机器迁移时勾选，不要外发、不要放公共云盘。'));
  keyChk.addEventListener('change', () => {
    keyWarning.hidden = !keyChk.checked;
  });

  modal({
    title: '新建备份',
    width: 'wide',
    body: h('div',
      field('备注', noteInput, '只在备份列表里显示，方便以后认出这份备份是用来干什么的。'),
      h('div', { style: { marginTop: '12px' } },
        h('div.config-section-title', '备份内容'),
        h('div.config-panel',
          h('div.config-item',
            h('span', '数据库'), h('span.badge.badge-neutral', { style: { marginLeft: 'auto' } }, '必需')),
          h('label.config-item', configChk, h('span', '配置文件（*.json）')),
          h('label.config-item', keyChk, h('span', '加密密钥（secrets.key）')),
          h('label.config-item', tokenChk, h('span', '本机免登录令牌（local-shell.token）')),
        )),
      h('div', { style: { marginTop: '10px' } }, keyWarning),
    ),
    confirmText: '创建',
    onConfirm: async () => {
      try {
        await api('/admin/backups', {
          method: 'POST',
          body: {
            note: noteInput.value.trim(),
            options: {
              includeConfigFiles: configChk.checked,
              includeSecretsKey: keyChk.checked,
              includeLocalShellToken: tokenChk.checked,
            },
          },
        });
        toast('ok', '已创建备份');
        await render(container);
        return true;
      } catch (err) {
        toastError(err, '创建失败');
        return false;
      }
    },
  });
}

function renderBrandingCard(info) {
  const branding = info.branding || {};
  const siteNameInput = h('input', { type: 'text', value: branding.siteName || '', placeholder: 'ClassislandControlHub 集控系统' });
  const logoTextInput = h('input', { type: 'text', value: branding.logoText || '', placeholder: 'CI' });
  const logoImageInput = h('textarea', { placeholder: '粘贴图片 data URL 或图片地址，留空显示 Logo 文字' });
  logoImageInput.value = branding.logoImage || '';
  const faviconInput = h('textarea', { placeholder: '粘贴图片 data URL，留空使用默认图标' });
  faviconInput.value = branding.favicon || '';

  // 登录页背景：支持图片地址或本地上传（转 data URL），并可调淡化程度
  const bgInput = h('textarea', { placeholder: '登录页背景图：粘贴图片地址或 data URL，留空使用默认渐变背景' });
  bgInput.value = branding.loginBackground || '';

  const bgDim = branding.loginBackgroundDim ?? 45;
  const bgDimLabel = h('span', { style: { fontSize: '12px', color: 'var(--text-faint)' } }, `淡化 ${bgDim}%`);
  const bgDimInput = h('input', { type: 'range', min: '0', max: '90', step: '5', value: String(bgDim) });
  bgDimInput.addEventListener('input', () => {
    bgDimLabel.textContent = `淡化 ${bgDimInput.value}%`;
  });

  // ── 登录页文案：标题 / 副标题 / 描述 / 特性列表 / 页脚 ──
  const loginTitleInput = h('input', {
    type: 'text', value: branding.loginTitle || '', placeholder: '留空使用站点名称',
  });
  const loginSubtitleInput = h('input', {
    type: 'text', value: branding.loginSubtitle || '', placeholder: 'ClassIsland 机房集控平台',
  });
  const loginDescInput = h('textarea', { rows: '2', placeholder: '留空则隐藏这一整段' });
  loginDescInput.value = branding.loginDescription || '';

  const featuresInput = h('textarea', {
    rows: '4',
    placeholder: '一行一条，最多 8 条。留空则保留默认的四条特性。',
  });
  featuresInput.value = (branding.loginFeatures || []).join('\n');

  const footerInput = h('input', {
    type: 'text', value: branding.footerText || '', placeholder: '例如：智教联盟 · 教务处（留空不显示）',
  });

  // ── 主题色 / 玻璃 / 布局 / 自定义 CSS ──
  const accentInput = h('input', { type: 'color', value: branding.accentColor || '#1e90ff' });
  const accentDefault = h('input', { type: 'checkbox' });
  accentDefault.checked = !branding.accentColor;
  accentInput.disabled = accentDefault.checked;
  accentDefault.addEventListener('change', () => {
    accentInput.disabled = accentDefault.checked;
  });

  const glassSelect = select([
    { value: 'subtle', label: '轻（低配机 / 投影更稳）' },
    { value: 'standard', label: '标准（推荐）' },
    { value: 'strong', label: '强（质感更强，更吃性能）' },
  ], branding.glassLevel || 'standard', () => {});

  const shineCheckbox = h('input', { type: 'checkbox' });
  shineCheckbox.checked = branding.enableShine !== false;

  const layoutSelect = select([
    { value: 'split', label: '左右分栏（左品牌 / 右表单）' },
    { value: 'centered', label: '单列居中（只有一张登录卡）' },
  ], branding.loginLayout || 'split', () => {});

  const customCssInput = h('textarea', {
    rows: '6',
    placeholder: '例如：.hero-desc { font-size: 15px; }\n留空表示不用。改动会记进审计日志。',
  });
  customCssInput.value = branding.customCss || '';

  const bgFileInput = h('input', { type: 'file', accept: 'image/*', style: { display: 'none' } });
  bgFileInput.addEventListener('change', () => {
    const file = bgFileInput.files && bgFileInput.files[0];
    if (!file) return;
    if (file.size > 300 * 1024) {
      toast('warn', '图片太大', '请选择 300KB 以内的图片（背景图会随配置一起保存）。');
      bgFileInput.value = '';
      return;
    }

    const reader = new FileReader();
    reader.onload = () => {
      bgInput.value = String(reader.result || '');
      toast('ok', '已载入图片', '点「保存并应用」后生效。');
    };
    reader.readAsDataURL(file);
  });

  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', '品牌个性化'),
        h('p.card-desc', '替换校名（站点名称）、Logo、浏览器图标与登录页背景，登录页与主界面会同步生效。'),
      ),
    ),
    h('div.form-row',
      field('站点名称', siteNameInput, '显示在登录页标题、浏览器标题与侧边栏。'),
      field('Logo 文字', logoTextInput, '显示在品牌标识方块中，留空使用默认「CI」。'),
    ),
    field('Logo 图片（可选）', logoImageInput, '填写图片地址或 data URL，会替代 Logo 文字。'),
    field('浏览器图标（可选）', faviconInput, '填写图片 data URL，会替换浏览器标签页图标。'),
    field('登录页背景图（可选）',
      h('div',
        bgInput,
        h('div', { style: { display: 'flex', alignItems: 'center', gap: '10px', marginTop: '8px', flexWrap: 'wrap' } },
          h('button.btn.btn-sm', { type: 'button', onClick: () => bgFileInput.click() }, '选择本地图片'),
          bgFileInput,
          bgDimLabel,
          bgDimInput)),
      '支持图片地址或本地上传（≤300KB，会转成 data URL 随配置保存）；淡化程度越高背景越淡、文字越清晰。'),

    h('div.card-subhead', '登录页文案'),
    field('登录页大标题', loginTitleInput, '留空使用站点名称。'),
    h('div.form-row',
      field('副标题', loginSubtitleInput, '跟在标题旁边的一行短说明。'),
      field('页脚', footerInput, '例如「智教联盟 · 教务处」；留空则整块不显示。'),
    ),
    field('描述段落', loginDescInput, '登录页标题下方那段介绍；留空则隐藏。'),
    field('特性列表', featuresInput, '一行一条（最多 8 条）。留空则保留默认的四条。'),

    h('div.card-subhead', '外观与材质'),
    h('div.form-row',
      field('主题色', h('div', { style: { display: 'flex', alignItems: 'center', gap: '10px' } },
        accentInput,
        h('label', { style: { display: 'flex', alignItems: 'center', gap: '6px', fontSize: '12.5px' } },
          accentDefault, '用默认蓝')),
      '会同时改变按钮、选中态与图表强调色（hover / 浅底自动派生）。'),
      field('玻璃强度', glassSelect, '「轻」在低配机与投影上更稳，「强」质感更明显但更吃性能。'),
    ),
    h('div.form-row',
      field('登录页布局', layoutSelect, '分栏适合宽屏展示品牌；居中适合只想要一张干净的登录卡。'),
      field('鼠标高光', h('label', {
        style: { display: 'flex', alignItems: 'center', gap: '6px', fontSize: '13px' },
      }, shineCheckbox, '统计卡片跟随鼠标的高光'), '关掉可省一点 GPU，投屏场景建议关。'),
    ),
    field('自定义 CSS', customCssInput,
      '追加到管理界面与登录页（上限 20000 字符）。这是「高度自定义」的兜底口子，'
      + '外观需求千奇百怪，与其一个个加设置项，不如给一个受控的注入点。改动会记进审计日志。'),

    h('div.card-actions',
      h('button.btn.btn-primary.btn-sm', {
        type: 'button',
        onClick: async () => {
          await api('/admin/branding', {
            method: 'PUT',
            body: {
              siteName: siteNameInput.value.trim(),
              logoText: logoTextInput.value.trim(),
              logoImage: logoImageInput.value.trim(),
              favicon: faviconInput.value.trim(),
              loginBackground: bgInput.value.trim(),
              loginBackgroundDim: Number(bgDimInput.value) || 0,

              // 登录页文案
              loginTitle: loginTitleInput.value.trim(),
              loginSubtitle: loginSubtitleInput.value.trim(),
              loginDescription: loginDescInput.value.trim(),
              // 一行一条；空行丢掉。服务端还会再限一次（最多 8 条、每条 40 字）。
              loginFeatures: featuresInput.value
                .split('\n').map((x) => x.trim()).filter(Boolean),
              footerText: footerInput.value.trim(),

              // 外观与材质
              accentColor: accentDefault.checked ? '' : accentInput.value,
              glassLevel: glassSelect.value,
              enableShine: shineCheckbox.checked,
              loginLayout: layoutSelect.value,
              customCss: customCssInput.value,
            },
          });
          toast('ok', '已保存', '品牌设置已生效，正在刷新页面…');
          setTimeout(() => window.location.reload(), 800);
        },
      }, '保存并应用'),
    ),
  );
}

function renderTimeCard(timeOffset) {
  const offsetInput = h('input', {
    type: 'number', step: '1', value: timeOffset.offsetSeconds ?? 0, placeholder: '0',
  });

  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', '时间偏移（授时）'),
        h('p.card-desc', '设定后，服务器会在 NTP 授时基础上叠加该偏移，再下发给所有教室终端。'),
      ),
    ),
    h('div', { style: { display: 'grid', gap: '10px', fontSize: '13px', marginBottom: '14px' } },
      row('手动偏移', `${timeOffset.offsetSeconds ?? 0} 秒`),
      row('NTP 校正偏移', `${timeOffset.ntpOffsetSeconds ?? 0} 秒`),
      row('当前授时时间', formatDateTime(timeOffset.serverTime)),
      row('授时状态', timeOffset.lastSyncStatus || '—'),
    ),
    field('时间偏移（秒，正值 = 整体提前）', offsetInput,
      '例如填 180 让所有终端快 3 分钟；填 -60 慢 1 分钟。范围 ±86400 秒。'),
    h('div.card-actions',
      h('button.btn.btn-primary.btn-sm', {
        type: 'button',
        onClick: async () => {
          const seconds = Number(offsetInput.value);
          if (Number.isNaN(seconds)) {
            toast('warn', '请输入有效数字');
            return;
          }
          await api('/admin/time-offset', { method: 'PUT', body: { offsetSeconds: seconds } });
          toast('ok', '已保存', '时间偏移已生效，将叠加到后续授时中。');
        },
      }, '保存偏移'),
    ),
  );
}

function renderUpdateCard(update) {
  const statusText = h('div', { style: { fontSize: '13px', color: 'var(--text-dim)' } }, update.status || '尚未检查更新。');
  const versionText = h('div', { style: { fontSize: '13px', color: 'var(--text-dim)' } },
    `当前版本 v${update.currentVersion || '—'}` + (update.latestVersion ? ` · 最新 v${update.latestVersion}` : ''));

  const checkBtn = h('button.btn.btn-sm', {
    type: 'button',
    onClick: async () => {
      checkBtn.disabled = true;
      checkBtn.textContent = '检查中…';
      try {
        const r = await api('/admin/update/check');
        toast('ok', r.status || '检查完成');
        await render(document.getElementById('content'));
      } catch (e) {
        toastError(e, '检查失败');
        checkBtn.disabled = false;
        checkBtn.textContent = '检查更新';
      }
    },
  }, '检查更新');

  const applyBtn = h('button.btn.btn-primary.btn-sm', {
    type: 'button',
    onClick: async () => {
      const ok = await confirmDialog('应用更新',
        `将下载并安装新版本 v${update.latestVersion}，原有数据会保留，完成后服务自动重启、管理界面短暂不可用。确定继续吗？`,
        '更新并重启');
      if (!ok) return;
      applyBtn.disabled = true;
      applyBtn.textContent = '更新中…';
      try {
        const r = await api('/admin/update/apply', { method: 'POST' });
        toast('ok', '更新已启动', r.status || '');
      } catch (e) {
        toastError(e, '更新失败');
        applyBtn.disabled = false;
        applyBtn.textContent = '立即更新';
      }
    },
  }, '立即更新');

  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', '自动更新'),
        h('p.card-desc', '从 GitHub Release 检查并安装新版本，更新保留原有数据、不影响已接入的设备。'),
      ),
    ),
    h('div', { style: { display: 'grid', gap: '8px', marginBottom: '14px' } },
      versionText,
      statusText,
      update.hasUpdate && update.releaseNotes
        ? h('div', {
          style: {
            fontSize: '12px', color: 'var(--text-faint)', whiteSpace: 'pre-wrap',
            maxHeight: '120px', overflowY: 'auto', background: 'var(--bg-panel-2)',
            padding: '8px 10px', borderRadius: 'var(--radius-sm)',
          },
        }, update.releaseNotes)
        : null,
    ),
    h('div.card-actions',
      checkBtn,
      update.hasUpdate ? applyBtn : null,
    ),
  );
}

/**
 * 本地外壳（桌面端）专用卡片：只在被外壳内嵌时出现。
 * 页面通过 WebMessage 通道请求外壳执行动作（见 src/ControlHub.Shell 的 HandleShellMessage）。
 */
function renderShellCard() {
  const webview = window.chrome?.webview;
  if (!webview) {
    return null;
  }

  const post = (action) => webview.postMessage(JSON.stringify({ action }));
  const actionButton = (action, label, primary) => h(primary ? 'button.btn.btn-primary.btn-sm' : 'button.btn.btn-sm', {
    type: 'button',
    onClick: () => {
      post(action);
      toast('ok', '已通知本地外壳', `${label} 指令已发送。`);
    },
  }, label);

  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', '本地外壳'),
        h('p.card-desc', '当前界面运行在 ClassislandControlHub 桌面外壳里，可以在这里直接控制 A 端进程。'),
      ),
    ),
    h('div.toolbar',
      actionButton('restart', '重启 A 端', true),
      actionButton('open-browser', '在浏览器打开'),
      h('div.spacer'),
      h('button.btn.btn-sm', {
        type: 'button',
        onClick: async () => {
          if (!await confirmDialog('停止 A 端', '将停止由外壳启动的 A 端进程，本页面会随即断开。确定继续吗？', '停止')) return;
          post('stop');
          toast('ok', '已通知本地外壳', 'A 端正在停止。');
        },
      }, '停止 A 端'),
    ),
    h('div.notice.notice-info', { style: { marginTop: '10px' } },
      h('span.notice-icon', 'i'),
      h('div', '「停止 A 端」只对由外壳启动的进程有效；A 端作为 Windows 服务运行、或外壳连的是远程服务器时不会生效。')),
  );
}

function renderDeployCard(info) {
  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', '部署与接入说明'),
        h('p.card-desc', '同一套服务端既可作为校内本地服务器运行，也可部署到公网服务器以网页访问。'),
      ),
    ),
    h('div', { style: { display: 'grid', gap: '14px', fontSize: '13px', lineHeight: '1.85' } },
      block('教室终端如何接入',
        h('ol', { style: { margin: 0, paddingLeft: '20px' } },
          h('li', '在「设备管理」中生成一个注册码。'),
          h('li', '在教室电脑上打开 ClassIsland，从插件市场安装「集控客户端」插件（或放入 .cipx 插件包）。'),
          h('li', '打开插件的设置页面，点击「自动发现服务器」；若不在同一网段，则手动填写服务器地址。'),
          h('li', '填入注册码并保存，设备即会出现在本页的设备列表中。'),
        )),
      block('内网本地运行',
        h('div', '直接运行 ControlHub.Server.exe 即可。程序会监听所有网卡的 '
          + `${info.httpPort} 端口，同一局域网内的终端经 http://本机IP:${info.httpPort} 即可访问。`
          + '局域网自动发现使用 UDP ' + info.discoveryPort + ' 端口，请确保防火墙放行。')),
      block('部署到服务器',
        h('div', '把发布目录上传到服务器运行，并通过 Nginx 等反向代理提供 HTTPS。'
          + '反代需要额外转发 WebSocket/长轮询所需的 HTTP/1.1 连接，并在配置中填入对外公布地址 '
          + '（appsettings.json 的 PublicBaseUrl），这样局域网发现与客户端提示中显示的地址才正确。')),
      block('数据与备份',
        h('div', '所有数据存放在数据目录下的 controlhub.db（SQLite）。'
          + `当前数据目录：${info.dataDirectory}。备份时复制该文件即可；建议在低峰期执行。`)),
    ),
  );
}

function block(title, content) {
  return h('div', { style: { padding: '13px 15px', background: 'var(--bg-panel-2)', borderRadius: 'var(--radius-sm)' } },
    h('div', { style: { fontWeight: '600', marginBottom: '6px' } }, title),
    h('div', { style: { color: 'var(--text-dim)', fontSize: '12.5px' } }, content),
  );
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
