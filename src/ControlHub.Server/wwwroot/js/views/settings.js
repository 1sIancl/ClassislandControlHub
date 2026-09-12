/**
 * 系统设置视图：服务器信息、账号安全与部署提示。
 */

import { api, session } from '../core/api.js?v=7';
import {
  h, clear, formatDateTime, formatDuration, toast, loadingBlock,
  field, modal, copyText,
} from '../core/ui.js?v=7';

export const meta = {
  title: '系统设置',
  subtitle: '服务器信息、账号安全与部署说明',
};

export async function render(container) {
  clear(container);
  container.appendChild(loadingBlock());

  const [info, me, accounts, timeOffset] = await Promise.all([
    api('/server/info', { auth: false }),
    api('/admin/me'),
    api('/admin/accounts'),
    api('/admin/time-offset'),
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
    renderAccountsCard(accounts, me),
    renderBrandingCard(info),
    renderTimeCard(timeOffset),
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
  return h('div.card',
    h('div.card-head',
      h('div',
        h('h3', '账号安全'),
        h('p.card-desc', `当前登录账号：${me.username}`),
      ),
    ),
    h('div', { style: { display: 'grid', gap: '10px', fontSize: '13px', marginBottom: '16px' } },
      row('显示名称', me.displayName || me.username),
      row('角色', me.role === 'admin' ? '管理员' : me.role),
      row('登录有效期至', formatDateTime(me.expiresAt)),
    ),
    h('div.card-actions',
      h('button.btn.btn-primary.btn-sm', {
        type: 'button',
        onClick: () => openChangePasswordDialog(),
      }, '修改密码'),
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

function renderAccountsCard(accounts, me) {
  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', `管理员账号（${accounts.length}）`),
        h('p.card-desc', '账号信息由服务端配置文件初始化，可在此重置密码。'),
      ),
    ),
    h('div.table-wrap',
      h('table.data',
        h('thead', h('tr',
          h('th', '用户名'),
          h('th', '显示名称'),
          h('th', '角色'),
          h('th', '创建时间'),
          h('th', { style: { textAlign: 'right' } }, '操作'),
        )),
        h('tbody', ...accounts.map((account) => h('tr',
          h('td',
            h('div.cell-main', account.username),
            account.mustChangePassword
              ? h('div.cell-sub', { style: { color: 'var(--warn)' } }, '仍使用初始密码')
              : null,
          ),
          h('td', account.displayName || '—'),
          h('td', account.role === 'admin'
            ? h('span.badge.badge-accent', '管理员')
            : h('span.badge.badge-neutral', account.role)),
          h('td', { style: { fontSize: '12px', color: 'var(--text-faint)' } }, formatDateTime(account.createdAt)),
          h('td.actions',
            h('button.btn.btn-sm', {
              type: 'button',
              onClick: () => openResetPasswordDialog(account),
            }, '重置密码'),
          ),
        ))),
      ),
    ),
  );
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
  const siteNameInput = h('input', { type: 'text', value: branding.siteName || '', placeholder: 'ClassIsland 集控系统' });
  const logoTextInput = h('input', { type: 'text', value: branding.logoText || '', placeholder: 'CI' });
  const logoImageInput = h('textarea', { placeholder: '粘贴图片 data URL 或图片地址，留空显示 Logo 文字' });
  logoImageInput.value = branding.logoImage || '';
  const faviconInput = h('textarea', { placeholder: '粘贴图片 data URL，留空使用默认图标' });
  faviconInput.value = branding.favicon || '';

  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', '品牌个性化'),
        h('p.card-desc', '替换站点名称、Logo 与浏览器图标，登录页与主界面会同步生效。'),
      ),
    ),
    h('div.form-row',
      field('站点名称', siteNameInput, '显示在登录页标题、浏览器标题与侧边栏。'),
      field('Logo 文字', logoTextInput, '显示在品牌标识方块中，留空使用默认「CI」。'),
    ),
    field('Logo 图片（可选）', logoImageInput, '填写图片地址或 data URL，会替代 Logo 文字。'),
    field('浏览器图标（可选）', faviconInput, '填写图片 data URL，会替换浏览器标签页图标。'),
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
