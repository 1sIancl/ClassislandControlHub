/**
 * 系统设置视图：服务器信息、账号安全与部署提示。
 */

import { api, session, hasPermission } from '../core/api.js?v=29';
import {
  h, clear, formatDateTime, formatDuration, toast, loadingBlock,
  field, modal, copyText, confirmDialog,
} from '../core/ui.js?v=29';

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

  const [info, me, accounts, permissions, permissionPresets, registerCodes, registerRequests, registration, timeOffset, updateState, aiConfig] =
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
    canSettings ? renderBrandingCard(info) : null,
    canSettings ? renderTimeCard(timeOffset) : null,
    canSettings ? renderAiCard(aiConfig) : null,
    canSettings ? renderUpdateCard(updateState) : null,
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
    ),
    h('div.card-actions', { style: { marginTop: '14px' } },
      h('button.btn.btn-sm', {
        type: 'button',
        onClick: () => copyText(baseUrl, '访问地址已复制'),
      }, '复制访问地址'),
    ),
  );
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
      row('登录有效期至', formatDateTime(me.expiresAt)),
    ),
    h('div.card-actions',
      h('button.btn.btn-primary.btn-sm', {
        type: 'button',
        onClick: () => openChangePasswordDialog(),
      }, '修改密码'),
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
    toast('error', '操作失败', err.message);
    return;
  }

  const { startTour } = await import('../core/tour.js?v=29');
  startTour({
    onFinish: async (skipped) => {
      if (!skipped) {
        await api('/admin/onboarding', { method: 'POST' });
      }
    },
  });
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
        toast('error', '保存失败', err.message);
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
    toast('error', '删除失败', err.message);
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
      toast('error', '操作失败', err.message);
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
      toast('error', '操作失败', err.message);
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
                h('code', { style: { fontSize: '14px', letterSpacing: '1.5px', fontWeight: '700' } }, code.code),
                h('button.btn.btn-ghost.btn-sm', {
                  type: 'button',
                  onClick: () => copyText(code.code, '邀请码已复制'),
                }, '复制'),
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
                  if (!await confirmDialog('删除邀请码', `确定删除邀请码 ${code.code} 吗？已注册的账号不受影响。`, '删除', true)) return;
                  await api(`/admin/register-codes/${encodeURIComponent(code.code)}`, { method: 'DELETE' });
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
        toast('error', '生成失败', err.message);
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
        toast('error', '检查失败', e.message);
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
        toast('error', '更新失败', e.message);
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
