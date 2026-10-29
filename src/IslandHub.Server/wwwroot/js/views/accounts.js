/**
 * 账号与权限视图（系统设置拆分而来）。
 *
 * <para>注意：卡片里的 `render(container)` 指的是**本文件的 render**——
 * 页面自己的刷新入口。所以每张卡片都与它的页面放在同一个文件里，
 * 而不是抽成公共组件（那样就得给每张卡传一个「刷新回调」，反而更绕）。</para>
 */

import { api, session, hasPermission } from '../core/api.js?v=158';
import { toastError } from '../core/errors.js?v=158';
import {
  h, clear, formatDateTime, toast, loadingBlock, field, modal, copyText,
  confirmDialog, kvRow,
} from '../core/ui.js?v=158';
import { t } from './../core/i18n.js?v=158';

export const meta = {
  title: t('accounts.title'),
  subtitle: t('accounts.subtitle'),
};

/** 与服务端 PermissionKeys.DefaultForNewUser 保持一致：新建账号时的推荐权限。 */
const DEFAULT_PERMISSIONS = [
  'profiles.read', 'profiles.write',
  'devices.read', 'devices.write',
  'deploy.write',
  'remote.read', 'remote.write',
  'reminders.read', 'reminders.write',
  'audit.read',
];

/** 可签发的权限（默认只读）：密钥不该拿它去做管理动作，需要写权限时应有意识地勾选。 */
const API_KEY_READ_SCOPES = [
  { key: 'devices.read', label: t('accounts.permDevicesRead') },
  { key: 'profiles.read', label: t('accounts.permProfilesRead') },
  { key: 'audit.read', label: t('accounts.permAuditRead') },
  { key: 'remote.read', label: t('accounts.permRemoteRead') },
];

const API_KEY_WRITE_SCOPES = [
  { key: 'remote.write', label: t('accounts.permRemoteWrite') },
  { key: 'deploy.write', label: t('accounts.permDeploy') },
];


/**
 * 账号与权限：当前账号安全、账号列表与权限、邀请码、注册申请、API 密钥。
 *
 * <para>这一页从原来的「系统设置」里拆出来。原页面把账号、服务器信息、Webhook、备份、
 * 品牌外观全堆在一屏里（14 张卡），管理员每次都要滚很久才能找到要改的那一项；
 * 拆开之后每一页只讲一件事，二级侧边栏也终于有内容可列。</para>
 */
export async function render(container) {
  clear(container);
  container.appendChild(loadingBlock());

  // 按权限取数：没有权限的接口干脆不请求，否则整页会被 403 打断。
  const canAccounts = hasPermission('accounts.read');

  const [me, accounts, permissions, permissionPresets, registerCodes, registerRequests, registration, apiKeys, policy] =
    await Promise.all([
      api('/admin/me'),
      canAccounts ? api('/admin/accounts') : Promise.resolve([]),
      canAccounts ? api('/admin/permissions') : Promise.resolve([]),
      canAccounts ? api('/admin/permission-presets') : Promise.resolve([]),
      canAccounts ? api('/admin/register-codes') : Promise.resolve([]),
      canAccounts ? api('/admin/register-requests') : Promise.resolve([]),
      api('/admin/registration', { auth: false }).catch(() => ({ enabled: false })),
      canAccounts ? api('/admin/api-keys') : Promise.resolve([]),
      // 密码策略（#28）：强度要求与有效期都从服务端读，别在前端写死「至少 8 位」。
      api('/admin/password-policy').catch(() => null),
    ]);

  session.me = me;

  clear(container);
  container.appendChild(h('div',
    // 初始密码提醒放在最上面：它要的是「马上做点什么」，不是「知道了」。
    me.mustChangePassword
      ? h('div.notice.notice-warn',
      h('span.notice-icon', '!'),
      h('div', t('accounts.initialPasswordWarning')))
      : null,
    renderAccountCard(me, policy),
    canAccounts ? renderAccountsCard(container, accounts, me, permissions, permissionPresets, policy) : null,
    canAccounts ? renderRegisterCodesCard(container, registerCodes, registration) : null,
    canAccounts ? renderRegisterRequestsCard(container, registerRequests, registration) : null,
    canAccounts ? renderApiKeysCard(container, apiKeys) : null,
    canAccounts ? null : h('div.notice.notice-warn',
      h('span.notice-icon', '!'),
      h('div', t('accounts.noAccountPermission'))),
  ));
}

/**
 * 密码强度要求文案（#28）：按服务端策略拼，改了配置前端自动跟着变。
 */
function strengthHint(policy) {
  const min = policy?.minLength ?? 8;
  const parts = [];
  if (min > 0) {
    parts.push(t('accounts.atLeastChars', { count: min }));
  }

  if (policy?.requireMixedChars !== false) {
    parts.push(t('accounts.lettersAndDigits'));
  }

  return parts.length ? `${parts.join('，')}。` : t('accounts.noLengthRule');
}

/** 密码已用天数；<c>null</c> 表示账号没有改密时间（老库升级上来的，以创建时间兜底）。 */
function passwordAgeDays(account) {
  const baseline = account?.passwordChangedAt || account?.createdAt;
  if (!baseline) {
    return null;
  }

  const days = Math.floor((Date.now() - new Date(baseline).getTime()) / 86400000);
  return Number.isFinite(days) ? Math.max(0, days) : null;
}

/** 账号列表里「密码已用 N 天」那一行；超过有效期用警告色（#28）。 */
function passwordAgeLine(account, policy) {
  const days = passwordAgeDays(account);
  if (days === null) {
    return null;
  }

  const expiry = policy?.expiryDays ?? 0;
  const overdue = expiry > 0 && days >= expiry;
  return h('div.cell-sub', overdue ? { style: { color: 'var(--warn)' } } : null,
    overdue ? t('accounts.passwordAgeOverdue', { days, expiry }) : t('accounts.passwordAge', { days }));
}

function renderAccountCard(me, policy) {
  const isAdmin = me.role === 'admin';
  const granted = (me.permissions || []).length;

  return h('div.card',
    h('div.card-head',
      h('div',
        h('h3', t('accounts.security')),
        h('p.card-desc', t('accounts.currentAccount', { name: me.username })),
      ),
    ),
    h('div', { style: { display: 'grid', gap: '10px', fontSize: '13px', marginBottom: '16px' } },
      kvRow(t('accounts.displayName'), me.displayName || me.username),
      kvRow(t('accounts.role'), isAdmin ? t('accounts.roleSuperAdmin') : t('accounts.roleCustom')),
      kvRow(t('accounts.permissions'), isAdmin ? t('accounts.allModules') : t('accounts.grantedCount', { count: granted })),
      kvRow(t('accounts.twoFactor'), me.totpEnabled
        ? h('span.badge', { style: { background: 'var(--ok-soft)', color: 'var(--ok)' } }, t('accounts.totpOn'))
        : h('span', { style: { color: 'var(--text-faint)' } }, t('accounts.totpOff'))),
      kvRow(t('accounts.sessionExpires'), formatDateTime(me.expiresAt)),
    ),
    h('div.card-actions',
      h('button.btn.btn-primary.btn-sm', {
        type: 'button',
        onClick: () => openChangePasswordDialog(policy),
      }, t('accounts.changePassword')),
      h('button.btn.btn-sm', {
        type: 'button',
        onClick: () => openTotpDialog(me),
      }, me.totpEnabled ? t('accounts.disableTotp') : t('accounts.enableTotp')),
      h('button.btn.btn-sm', {
        type: 'button',
        title: t('accounts.replayTour'),
        onClick: () => replayOnboarding(),
      }, t('accounts.replayTourButton')),
      h('button.btn.btn-sm', {
        type: 'button',
        onClick: async () => {
          await api('/admin/logout', { method: 'POST' });
          window.location.reload();
        },
      }, t('accounts.signOut')),
    ),
  );
}

/** 重置引导状态并立即播放一遍。 */
async function replayOnboarding() {
  try {
    await api('/admin/onboarding', { method: 'POST', query: { done: false } });
  } catch (err) {
    toastError(err, t('accounts.actionFailed'));
    return;
  }

  const { startTour } = await import('../core/tour.js?v=158');
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
      title: t('accounts.disableTotp'),
      body: h('div',
        h('div.notice.notice-warn',
          h('span.notice-icon', '!'),
          h('div', t('accounts.disableTotpHint'))),
        field(t('accounts.currentPassword'), passwordInput, t('accounts.currentPasswordHint'))),
      confirmText: t('accounts.disableTotp'),
      danger: true,
      onConfirm: async () => {
        try {
          await api('/admin/totp/disable', { method: 'POST', body: { password: passwordInput.value } });
          toast('ok', t('accounts.totpDisabled'));
          await render(document.getElementById('content'));
          return true;
        } catch (err) {
          toastError(err, t('accounts.disableFailed'));
          return false;
        }
      },
    });
    return;
  }

  api('/admin/totp/setup', { method: 'POST' }).then((setup) => {
    const codeInput = h('input', { type: 'text', inputmode: 'numeric', maxlength: '6', placeholder: t('accounts.sixDigitCode') });
    const secretBox = h('code',
      { style: { fontFamily: 'var(--mono)', fontSize: '14px', letterSpacing: '1px' } }, setup.secret);
    const urlArea = h('textarea', { readOnly: true, style: { minHeight: '64px' } });
    urlArea.value = setup.otpAuthUrl;

    modal({
      title: t('accounts.enableTotp'),
      width: 'wide',
      body: h('div',
        h('div.notice.notice-info',
          h('span.notice-icon', 'i'),
          h('div', t('accounts.totpSetupLead')
            + t('accounts.totpSetupBody'))),
        field(t('accounts.totpSecret'),
          h('div', { style: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' } },
            secretBox,
            h('button.btn.btn-sm', { type: 'button', onClick: () => copyText(setup.secret, t('accounts.secretCopied')) }, t('accounts.copySecret'))),
          t('accounts.secretHint')),
        field(t('accounts.otpauthLink'), urlArea, t('accounts.otpauthHint')),
        field(t('accounts.code'), codeInput, t('accounts.codeHint'))),
      confirmText: t('accounts.enable'),
      onConfirm: async () => {
        try {
          await api('/admin/totp/enable', { method: 'POST', body: { code: codeInput.value.trim() } });
          toast('ok', t('accounts.totpEnabled'), t('accounts.totpEnabledHint'));
          await render(document.getElementById('content'));
          return true;
        } catch (err) {
          toastError(err, t('accounts.enableFailed'));
          return false;
        }
      },
    });
  }).catch((err) => toastError(err, t('accounts.secretFailed')));
}

function openChangePasswordDialog(policy) {
  const current = h('input', { type: 'password', autocomplete: 'current-password' });
  const next = h('input', { type: 'password', autocomplete: 'new-password' });
  const confirm = h('input', { type: 'password', autocomplete: 'new-password' });

  modal({
    title: t('accounts.changePassword'),
    width: 'wide',
    body: h('div',
      field(t('accounts.oldPassword'), current),
      field(t('accounts.newPassword'), next, strengthHint(policy)),
      field(t('accounts.confirmPassword'), confirm),
      h('div.notice.notice-warn',
        h('span.notice-icon', '!'),
        h('div', t('accounts.passwordChangeHint'))),
    ),
    confirmText: t('accounts.change'),
    onConfirm: async () => {
      if (next.value !== confirm.value) {
        toast('warn', t('accounts.passwordMismatch'));
        return false;
      }

      await api('/admin/password', {
        method: 'POST',
        body: { currentPassword: current.value, newPassword: next.value },
      });

      toast('ok', t('accounts.passwordChanged'), t('accounts.returningToLogin'));
      setTimeout(() => window.location.reload(), 1200);
      return true;
    },
  });
}

function renderAccountsCard(container, accounts, me, permissions, presets, policy) {
  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', t('accounts.listTitle', { count: accounts.length })),
        h('p.card-desc',
          t('accounts.listLead')
          + t('accounts.listTail')),
      ),
      h('button.btn.btn-primary.btn-sm', {
        type: 'button',
        onClick: () => openAccountEditor(container, null, permissions, presets),
      }, t('accounts.newAccount')),
    ),
    h('div.table-wrap',
      h('table.data',
        h('thead', h('tr',
          h('th', t('accounts.account')),
          h('th', t('accounts.role')),
          h('th', t('accounts.permissions')),
          h('th', t('accounts.createdAt')),
          h('th', { style: { textAlign: 'right' } }, t('accounts.actions')),
        )),
        h('tbody', ...accounts.map((account) => h('tr',
          h('td',
            h('div.cell-main', account.username),
            h('div.cell-sub', account.displayName || '—'),
            account.mustChangePassword
              ? h('div.cell-sub', { style: { color: 'var(--warn)' } }, t('accounts.stillInitialPassword'))
              : passwordAgeLine(account, policy),
          ),
          h('td', account.role === 'admin'
            ? h('span.badge.badge-accent', t('accounts.superAdmin'))
            : h('span.badge.badge-neutral', t('accounts.custom'))),
          h('td', account.role === 'admin'
            ? h('span', { style: { color: 'var(--text-dim)' } }, t('accounts.allModules'))
            : h('span', t('accounts.grantedCount', { count: (account.permissions || []).length }))),
          h('td', { style: { fontSize: '12px', color: 'var(--text-faint)' } }, formatDateTime(account.createdAt)),
          h('td.actions',
            h('button.btn.btn-sm', {
              type: 'button',
              onClick: () => openAccountEditor(container, account, permissions, presets),
            }, t('accounts.edit')),
            ' ',
            h('button.btn.btn-sm', {
              type: 'button',
              onClick: () => openResetPasswordDialog(account, policy),
            }, t('accounts.resetPassword')),
            ' ',
            account.id === me.id
              ? null
              : h('button.btn.btn-sm.btn-danger', {
                type: 'button',
                onClick: () => removeAccount(container, account),
              }, t('accounts.delete')),
          ),
        ))),
      ),
    ),
  );
}

/**
 * 模块权限勾选表。
 * @returns {{ table: HTMLElement, selected: Set<string>, refresh: () => void }}
 */
function createPermissionTable(permissions, initial, isAdminRole) {
  const selected = new Set(initial);
  const table = h('table.perm-table',
    h('thead', h('tr', h('th', t('accounts.module')), h('th.center', t('accounts.view')), h('th.center', t('accounts.change')))));
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
    placeholder: t('accounts.usernameRule'),
  });
  usernameInput.disabled = !isNew;

  const displayInput = h('input', {
    type: 'text',
    value: account?.displayName || '',
    placeholder: t('accounts.displayNameHint'),
  });

  const passwordInput = h('input', {
    type: 'password',
    autocomplete: 'new-password',
    placeholder: t('accounts.initialPasswordHint'),
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
    presetRow.appendChild(h('span.preset-label', t('accounts.applyPreset')));
    for (const preset of presets) {
      presetRow.appendChild(h('button.btn.btn-sm.preset-btn', {
        type: 'button',
        title: preset.description,
        onClick: () => {
          adminChk.checked = false;
          perm.setAdminMode(false);
          perm.setSelected(preset.permissions);
          toast('ok', t('accounts.presetApplied', { label: preset.label }), preset.description);
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
    title: isNew ? t('accounts.createAccount') : t('accounts.editAccount', { name: account.username }),
    width: 'wide',
    body: h('div',
      h('div.form-row',
        field(t('accounts.username'), usernameInput, isNew ? t('accounts.usernameImmutable') : t('accounts.usernameImmutableShort')),
        field(t('accounts.displayName'), displayInput),
      ),
      isNew ? field(t('accounts.initialPassword'), passwordInput) : null,
      canGrantAdmin
        ? h('label.checkbox-field', adminChk,
          h('span', t('accounts.makeSuperAdmin')),
          h('span', { style: { color: 'var(--text-faint)', fontSize: '12px' } }, t('accounts.superAdminNote')))
        : null,
      presetRow,
      h('div', { style: { marginTop: '6px' } },
        h('div', { style: { fontSize: '12.5px', color: 'var(--text-dim)', marginBottom: '8px' } }, t('accounts.modulePermissions')),
        perm.table,
        h('div.notice.notice-info', { style: { marginTop: '12px' } },
          h('span.notice-icon', 'i'),
          h('div', t('accounts.modulePermissionsHint')
            + t('accounts.permissionChangeHint'))),
      ),
    ),
    confirmText: isNew ? t('accounts.create') : t('accounts.save'),
    onConfirm: async () => {
      const body = {
        username: usernameInput.value.trim(),
        displayName: displayInput.value.trim(),
        role: adminChk.checked ? 'admin' : 'custom',
        permissions: adminChk.checked ? [] : [...perm.selected],
        password: passwordInput.value,
      };

      if (isNew && !body.username) {
        toast('warn', t('accounts.needUsername'));
        return false;
      }

      let created = null;
      try {
        if (isNew) {
          created = await api('/admin/accounts', { method: 'POST', body });
        } else {
          await api(`/admin/accounts/${account.id}`, { method: 'PUT', body });
          toast('ok', t('accounts.saved'));
        }
      } catch (err) {
        toastError(err, t('accounts.saveFailed'));
        return false;
      }

      await render(container);

      if (created?.generatedPassword) {
        modal({
          title: t('accounts.accountCreated'),
          width: 'wide',
          hideFooter: true,
          body: h('div',
            h('p', { style: { marginTop: 0, color: 'var(--text-dim)' } },
              t('accounts.generatedPasswordHint')),
            h('div.copy-row',
              h('div.code-block', created.generatedPassword),
              h('button.btn', {
                type: 'button',
                onClick: () => copyText(created.generatedPassword, t('accounts.passwordCopied')),
              }, t('accounts.copy')),
            ),
          ),
        });
      } else if (isNew) {
        toast('ok', t('accounts.accountCreated'));
      }

      return true;
    },
  });
}

async function removeAccount(container, account) {
  if (!await confirmDialog(t('accounts.deleteAccount'),
    t('accounts.deleteConfirm', { name: account.username }), t('accounts.delete'), true)) {
    return;
  }

  try {
    await api(`/admin/accounts/${account.id}`, { method: 'DELETE' });
    toast('ok', t('accounts.deleted'));
    await render(container);
  } catch (err) {
    toastError(err, t('accounts.deleteFailed'));
  }
}

function renderRegisterRequestsCard(container, requests, registration) {
  const toggle = h('input', { type: 'checkbox' });
  toggle.checked = Boolean(registration?.approvalEnabled);
  toggle.addEventListener('change', async () => {
    toggle.disabled = true;
    try {
      await api('/admin/registration-approval', { method: 'PUT', query: { enabled: toggle.checked } });
      toast('ok', toggle.checked ? t('accounts.registrationRequestOn') : t('accounts.registrationRequestOff'));
    } catch (err) {
      toggle.checked = !toggle.checked;
      toastError(err, t('accounts.actionFailed'));
    } finally {
      toggle.disabled = false;
    }
  });

  const pending = requests.filter((req) => req.status === 'pending');
  const handled = requests.filter((req) => req.status !== 'pending');

  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', t('accounts.registrationRequest')),
        h('p.card-desc',
          t('accounts.requestLead')
          + t('accounts.requestTail')),
      ),
      pending.length > 0
        ? h('span.badge', { style: { background: 'var(--warn-soft)', color: 'var(--warn)' } }, t('accounts.pendingApproval', { count: pending.length }))
        : null,
    ),
    h('label.checkbox-field', toggle,
      h('span', t('accounts.allowRequests')),
      h('span', { style: { color: 'var(--text-faint)', fontSize: '12px' } }, t('accounts.allowRequestsNote'))),
    pending.length === 0
      ? h('div.notice.notice-info', { style: { marginTop: '12px' } },
        h('span.notice-icon', 'i'),
        h('div', t('accounts.noPending')))
      : h('div.table-wrap', { style: { marginTop: '12px' } },
        h('table.data',
          h('thead', h('tr',
            h('th', t('accounts.username')),
            h('th', t('accounts.displayName')),
            h('th', t('accounts.requestNote')),
            h('th', t('accounts.submittedAt')),
            h('th', { style: { textAlign: 'right' } }, t('accounts.actions')),
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
              }, t('accounts.approve')),
              h('button.btn.btn-sm', {
                type: 'button',
                onClick: () => openRejectDialog(container, req),
              }, t('accounts.reject')),
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
      t('accounts.handledTitle', { count: handled.length })),
    h('div.table-wrap', { style: { marginTop: '10px' } },
      h('table.data',
        h('thead', h('tr',
          h('th', t('accounts.username')),
          h('th', t('accounts.result')),
          h('th', t('accounts.reviewer')),
          h('th', t('accounts.reviewedAt')),
          h('th', t('accounts.rejectReason')),
          h('th', { style: { textAlign: 'right' } }, t('accounts.actions')),
        )),
        h('tbody', ...handled.map((req) => h('tr',
          h('td', req.username),
          h('td', req.status === 'approved'
            ? h('span.badge', { style: { background: 'var(--ok-soft)', color: 'var(--ok)' } }, t('accounts.approved'))
            : h('span.badge', { style: { background: 'var(--danger-soft)', color: 'var(--danger)' } }, t('accounts.rejected'))),
          h('td', req.reviewedBy || '—'),
          h('td', { style: { fontSize: '12px' } }, req.reviewedAt ? formatDateTime(req.reviewedAt) : '—'),
          h('td', { style: { fontSize: '12px' } }, req.reason || '—'),
          h('td.actions',
            h('button.btn.btn-ghost.btn-sm', {
              type: 'button',
              onClick: async () => {
                if (!await confirmDialog(t('accounts.deleteRequest'), t('accounts.deleteRequestConfirm', { name: req.username }), t('accounts.delete'), true)) return;
                await api(`/admin/register-requests/${req.id}`, { method: 'DELETE' });
                toast('ok', t('accounts.deleted'));
                await render(container);
              },
            }, t('accounts.delete')),
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
    title: t('accounts.approveTitle', { name: req.username }),
    width: 'wide',
    body: h('div',
      h('div.notice.notice-info', { style: { marginBottom: '12px' } },
        h('span.notice-icon', 'i'),
        h('div', t('accounts.approveHint', { name: req.username }))),
      h('div', { style: { fontSize: '12.5px', color: 'var(--text-dim)', marginBottom: '8px' } }, t('accounts.approvePermissions')),
      catalog,
    ),
    confirmText: t('accounts.approveAndCreate'),
    onConfirm: async () => {
      const permissions = [...(table ? table.selected : perm.selected)];
      if (permissions.length === 0) {
        toast('warn', t('accounts.needOnePermission'));
        return false;
      }

      await api(`/admin/register-requests/${req.id}/approve`, { method: 'POST', body: { permissions } });
      toast('ok', t('accounts.approved'), t('accounts.accountCreatedFor', { name: req.username }));
      await render(container);
    },
  });
}

/** 拒绝申请：可填写理由，仅记录在管理端。 */
function openRejectDialog(container, req) {
  const reasonInput = h('input', { type: 'text', placeholder: t('accounts.rejectReasonPlaceholder') });

  modal({
    title: t('accounts.rejectTitle', { name: req.username }),
    body: h('div',
      field(t('accounts.rejectReason'), reasonInput, t('accounts.rejectHint')),
    ),
    confirmText: t('accounts.rejectRequest'),
    onConfirm: async () => {
      await api(`/admin/register-requests/${req.id}/reject`, {
        method: 'POST',
        body: { reason: reasonInput.value.trim() },
      });
      toast('ok', t('accounts.rejected'));
      await render(container);
    },
  });
}

function renderRegisterCodesCard(container, codes, registration) {
  const toggle = h('input', { type: 'checkbox' });
  toggle.checked = Boolean(registration?.enabled);
  toggle.addEventListener('change', async () => {
    toggle.disabled = true;
    try {
      await api('/admin/registration', { method: 'PUT', query: { enabled: toggle.checked } });
      toast('ok', toggle.checked ? t('accounts.selfRegistrationOn') : t('accounts.selfRegistrationOff'));
    } catch (err) {
      toggle.checked = !toggle.checked;
      toastError(err, t('accounts.actionFailed'));
    } finally {
      toggle.disabled = false;
    }
  });

  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', t('accounts.inviteCodes')),
        h('p.card-desc',
          t('accounts.inviteLead')),
      ),
      h('button.btn.btn-primary.btn-sm', {
        type: 'button',
        onClick: () => openCreateRegisterCodeDialog(container),
      }, t('accounts.newInvite')),
    ),
    h('label.checkbox-field', toggle,
      h('span', t('accounts.allowInvite')),
      h('span', { style: { color: 'var(--text-faint)', fontSize: '12px' } }, t('accounts.allowInviteNote'))),
    codes.length === 0
      ? h('div.notice.notice-info', { style: { marginTop: '12px' } },
        h('span.notice-icon', 'i'),
        h('div', t('accounts.noInvites')))
      : h('div.table-wrap', { style: { marginTop: '12px' } },
        h('table.data',
          h('thead', h('tr',
            h('th', t('accounts.inviteCode')),
            h('th', t('accounts.note')),
            h('th', t('accounts.invitePermissions')),
            h('th', t('accounts.remainingUses')),
            h('th', t('accounts.validity')),
            h('th', { style: { textAlign: 'right' } }, t('accounts.actions')),
          )),
          h('tbody', ...codes.map((code) => h('tr',
            h('td',
              h('div', { style: { display: 'flex', alignItems: 'center', gap: '8px' } },
                code.available === false
                  ? h('div', { style: { display: 'flex', alignItems: 'center', gap: '8px' } },
                    h('span.badge.badge-danger', t('accounts.unusable')),
                    h('span', { style: { color: 'var(--text-faint)', fontSize: '12px' } },
                      t('accounts.codeUnreadable', { ref: code.reference })),
                  )
                  : [
                    h('code', { style: { fontSize: '14px', letterSpacing: '1.5px', fontWeight: '700' } }, code.code),
                    h('button.btn.btn-ghost.btn-sm', {
                      type: 'button',
                      onClick: () => copyText(code.code, t('accounts.inviteCopied')),
                    }, t('accounts.copy')),
                  ],
              ),
            ),
            h('td', code.note || h('span', { style: { color: 'var(--text-faint)' } }, '—')),
            h('td', t('accounts.grantedCount', { count: (code.permissions || []).length })),
            h('td', code.remainingUses < 0 ? t('accounts.unlimited') : t('accounts.remainingOfMax', { left: code.remainingUses, max: code.maxUses })),
            h('td', code.expiresAt
              ? h('span', { style: { fontSize: '12px' } }, formatDateTime(code.expiresAt))
              : h('span.badge.badge-ok', t('accounts.neverExpires'))),
            h('td.actions',
              h('button.btn.btn-sm.btn-danger', {
                type: 'button',
                onClick: async () => {
                  // 明文不可用时用指纹引用操作该行（#36）：界面拿不到邀请码本体，但删除仍然可用。
                  const target = code.available === false ? code.reference : code.code;
                  const label = code.available === false ? code.reference : code.code;
                  if (!await confirmDialog(t('accounts.deleteInvite'), t('accounts.deleteInviteConfirm', { label }), t('accounts.delete'), true)) return;
                  await api(`/admin/register-codes/${encodeURIComponent(target)}`, { method: 'DELETE' });
                  toast('ok', t('accounts.deleted'));
                  await render(container);
                },
              }, t('accounts.delete')),
            ),
          ))),
        ),
      ),
  );
}

function openCreateRegisterCodeDialog(container) {
  const noteInput = h('input', { type: 'text', placeholder: t('accounts.inviteNotePlaceholder') });
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
    title: t('accounts.generateInvite'),
    width: 'wide',
    body: h('div',
      field(t('accounts.note'), noteInput, t('accounts.inviteNoteHint')),
      h('div.form-row',
        field(t('accounts.maxUses'), maxUsesInput, t('accounts.maxUsesHint')),
        field(t('accounts.validHours'), hoursInput, t('accounts.validHoursHint')),
      ),
      h('div', { style: { marginTop: '6px' } },
        h('div', { style: { fontSize: '12.5px', color: 'var(--text-dim)', marginBottom: '8px' } }, t('accounts.invitePermissionHint2')),
        catalog,
        h('div.notice.notice-info', { style: { marginTop: '12px' } },
          h('span.notice-icon', 'i'),
          h('div', t('accounts.invitePermissionHint'))),
      ),
    ),
    confirmText: t('accounts.generate'),
    onConfirm: async () => {
      const body = {
        note: noteInput.value.trim(),
        permissions: [...(table ? table.selected : perm.selected)],
        maxUses: Number(maxUsesInput.value) || 0,
        validHours: Number(hoursInput.value) || 0,
      };

      if (body.permissions.length === 0) {
        toast('warn', t('accounts.needOnePermission'));
        return false;
      }

      let created = null;
      try {
        created = await api('/admin/register-codes', { method: 'POST', body });
      } catch (err) {
        toastError(err, t('accounts.generateFailed'));
        return false;
      }

      await render(container);
      modal({
        title: t('accounts.inviteGenerated'),
        width: 'wide',
        hideFooter: true,
        body: h('div',
          h('p', { style: { marginTop: 0, color: 'var(--text-dim)' } },
            t('accounts.inviteGeneratedHint')),
          h('div.copy-row',
            h('div.code-block', created.code),
            h('button.btn', { type: 'button', onClick: () => copyText(created.code, t('accounts.inviteCopied')) }, t('accounts.copy')),
          ),
        ),
      });
      return true;
    },
  });
}

function openResetPasswordDialog(account, policy) {
  const next = h('input', { type: 'password', autocomplete: 'new-password' });

  modal({
    title: t('accounts.resetPasswordTitle', { name: account.username }),
    width: 'wide',
    body: h('div',
      field(t('accounts.newPassword'), next, strengthHint(policy)),
      h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', t('accounts.resetPasswordHint'))),
    ),
    confirmText: t('accounts.reset'),
    danger: true,
    onConfirm: async () => {
      await api(`/admin/accounts/${account.id}/reset-password`, {
        method: 'POST',
        body: { currentPassword: '', newPassword: next.value },
      });
      toast('ok', t('accounts.passwordReset'));
      return true;
    },
  });
}

function renderApiKeysCard(container, keys) {
  const canWrite = hasPermission('accounts.write');
  const active = keys.filter((k) => k.active);

  const rows = keys.map((key) => h('tr',
    h('td',
      h('div.cell-main', key.name),
      h('div.cell-sub', t('accounts.keyMeta', {
        prefix: key.prefix,
        by: key.createdBy || t('accounts.dash'),
        note: key.note ? t('accounts.keyNoteSuffix', { note: key.note }) : '',
      })),
    ),
    h('td', { style: { fontSize: '12px', maxWidth: '260px', overflowWrap: 'anywhere' } },
      (key.permissions || []).join('、') || '—'),
    h('td', { style: { fontSize: '12px' } },
      key.active
        ? h('span.badge', { style: { background: 'var(--ok-soft)', color: 'var(--ok)' } }, t('accounts.active'))
        : h('span.badge.badge-neutral', key.revoked ? t('accounts.revoked') : t('accounts.expired')),
      h('div.cell-sub', key.expiresAt ? t('accounts.validUntil', { time: formatDateTime(key.expiresAt) }) : t('accounts.neverExpires')),
    ),
    h('td', { style: { fontSize: '12px' } }, key.lastUsedAt ? formatDateTime(key.lastUsedAt) : t('accounts.neverUsed')),
    h('td.actions',
      canWrite && key.active
        ? h('button.btn.btn-sm.btn-danger', {
          type: 'button',
          onClick: async () => {
            if (!await confirmDialog(t('accounts.revokeKey'),
              t('accounts.revokeConfirm', { name: key.name }), t('accounts.revoke'), true)) return;
            try {
              await api(`/admin/api-keys/${key.id}`, { method: 'DELETE' });
              toast('ok', t('accounts.revoked'));
              await render(container);
            } catch (err) {
              toastError(err, t('accounts.revokeFailed'));
            }
          },
        }, t('accounts.revoke'))
        : null,
    ),
  ));

  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', t('accounts.apiKeysTitle', { count: active.length })),
        h('p.card-desc',
          t('accounts.apiKeysLead')),
      ),
      canWrite
        ? h('button.btn.btn-primary.btn-sm', {
          type: 'button',
          onClick: () => openApiKeyDialog(container),
        }, t('accounts.issueKey'))
        : null,
    ),
    keys.length === 0
      ? h('div.notice.notice-info', { style: { marginTop: '12px' } },
        h('span.notice-icon', 'i'),
        h('div', t('accounts.noKeys')))
      : h('div.table-wrap', { style: { marginTop: '12px' } },
        h('table.table',
          h('thead', h('tr',
            h('th', t('accounts.name')), h('th', t('accounts.permissions')), h('th', t('accounts.status')), h('th', t('accounts.lastUsed')), h('th', ''))),
          h('tbody', ...rows))),
  );
}

/** 签发密钥：选权限（默认只读）、设期限；明文只在返回时显示一次。 */
function openApiKeyDialog(container) {
  const nameInput = h('input', { type: 'text', placeholder: t('accounts.keyNamePlaceholder') });
  const daysInput = h('input', { type: 'number', min: '0', max: '3650', value: '90' });
  const noteInput = h('input', { type: 'text', placeholder: t('accounts.keyNoteHint') });

  const boxes = [...API_KEY_READ_SCOPES, ...API_KEY_WRITE_SCOPES].map((scope) => ({
    ...scope,
    box: h('input', { type: 'checkbox', checked: scope.key.endsWith('.read') }),
  }));

  modal({
    title: t('accounts.issueKeyTitle'),
    width: 'wide',
    confirmText: t('accounts.issue'),
    body: h('div',
      h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', t('accounts.keyPlainHint')
          + t('accounts.keyPermissionHint'))),
      field(t('accounts.name'), nameInput, t('accounts.keyNameRequired')),
      field(t('accounts.validDays'), daysInput, t('accounts.validDaysHint')),
      h('div', { style: { marginTop: '10px' } },
        h('div', { style: { fontSize: '12.5px', marginBottom: '6px' } }, t('accounts.keyPermissions')),
        h('div', { style: { display: 'grid', gap: '4px' } },
          ...boxes.map(({ box, label }) => h('label.checkbox-field', box, label)))),
      field(t('accounts.note'), noteInput, t('accounts.keyNoteOptional')),
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
        toastError(err, t('accounts.issueFailed'));
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
    + `${(session.serverInfo && session.serverInfo.publicBaseUrl) || t('accounts.defaultBaseUrl')}/api/v1/admin/devices`;

  modal({
    title: t('accounts.keyIssued'),
    width: 'wide',
    confirmText: t('accounts.savedIt'),
    body: h('div',
      h('div.notice.notice-warn',
        h('span.notice-icon', '!'),
        h('div', t('accounts.keyIssuedHint'))),
      field(t('accounts.key'), secretBox, t('accounts.keyNameLabel', { name: result.key.name })),
      h('div', { style: { marginTop: '8px' } },
        h('button.btn.btn-sm', {
          type: 'button',
          onClick: async () => {
            try {
              await navigator.clipboard.writeText(result.secret);
              toast('ok', t('accounts.keyCopied'));
            } catch {
              toast('warn', t('accounts.copyFailed'), t('accounts.copyManually'));
            }
          },
        }, t('accounts.copySecret'))),
      field(t('accounts.usageExample'), sampleBox, t('accounts.usageHint')),
    ),
  });
}