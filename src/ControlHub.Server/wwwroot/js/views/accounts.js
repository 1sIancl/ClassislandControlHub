/**
 * 账号与权限视图（系统设置拆分而来）。
 *
 * <para>注意：卡片里的 `render(container)` 指的是**本文件的 render**——
 * 页面自己的刷新入口。所以每张卡片都与它的页面放在同一个文件里，
 * 而不是抽成公共组件（那样就得给每张卡传一个「刷新回调」，反而更绕）。</para>
 */

import { api, session, hasPermission } from '../core/api.js?v=90';
import { toastError } from '../core/errors.js?v=90';
import {
  h, clear, formatDateTime, toast, loadingBlock, field, modal, copyText,
  confirmDialog, kvRow,
} from '../core/ui.js?v=90';

export const meta = {
  title: '账号与权限',
  subtitle: '账号安全、权限、邀请码与接入密钥',
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
  { key: 'devices.read', label: '设备只读（设备清单、状态、日志）' },
  { key: 'profiles.read', label: '配置档案只读' },
  { key: 'audit.read', label: '审计日志只读' },
  { key: 'remote.read', label: '远程信息只读（指令历史、诊断记录）' },
];

const API_KEY_WRITE_SCOPES = [
  { key: 'remote.write', label: '远程下发（指令、通知、外观）' },
  { key: 'deploy.write', label: '触发配置下发' },
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

  const [me, accounts, permissions, permissionPresets, registerCodes, registerRequests, registration, apiKeys] =
    await Promise.all([
      api('/admin/me'),
      canAccounts ? api('/admin/accounts') : Promise.resolve([]),
      canAccounts ? api('/admin/permissions') : Promise.resolve([]),
      canAccounts ? api('/admin/permission-presets') : Promise.resolve([]),
      canAccounts ? api('/admin/register-codes') : Promise.resolve([]),
      canAccounts ? api('/admin/register-requests') : Promise.resolve([]),
      api('/admin/registration', { auth: false }).catch(() => ({ enabled: false })),
      canAccounts ? api('/admin/api-keys') : Promise.resolve([]),
    ]);

  session.me = me;

  clear(container);
  container.appendChild(h('div',
    // 初始密码提醒放在最上面：它要的是「马上做点什么」，不是「知道了」。
    me.mustChangePassword
      ? h('div.notice.notice-warn',
      h('span.notice-icon', '!'),
      h('div', '当前账号仍在使用初始密码，请立即在下方「账号安全」中修改。'))
      : null,
    renderAccountCard(me),
    canAccounts ? renderAccountsCard(container, accounts, me, permissions, permissionPresets) : null,
    canAccounts ? renderRegisterCodesCard(container, registerCodes, registration) : null,
    canAccounts ? renderRegisterRequestsCard(container, registerRequests, registration) : null,
    canAccounts ? renderApiKeysCard(container, apiKeys) : null,
    canAccounts ? null : h('div.notice.notice-warn',
      h('span.notice-icon', '!'),
      h('div', '当前账号没有「账号管理」权限，这里只显示与自己账号安全相关的内容。')),
  ));
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
      kvRow('显示名称', me.displayName || me.username),
      kvRow('角色', isAdmin ? '超级管理员（全部权限）' : '自定义权限'),
      kvRow('权限', isAdmin ? '全部模块' : `已勾选 ${granted} 项`),
      kvRow('两步验证', me.totpEnabled
        ? h('span.badge', { style: { background: 'var(--ok-soft)', color: 'var(--ok)' } }, '已开启 TOTP')
        : h('span', { style: { color: 'var(--text-faint)' } }, '未开启')),
      kvRow('登录有效期至', formatDateTime(me.expiresAt)),
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

  const { startTour } = await import('../core/tour.js?v=90');
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