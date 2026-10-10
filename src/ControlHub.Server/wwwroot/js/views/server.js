/** 服务器信息视图（系统设置拆分而来）：运行参数、数据目录、部署与接入说明。 */

import { api, session } from '../core/api.js?v=90';
import {
  h, clear, formatDateTime, formatDuration, toast, confirmDialog, copyText, loadingBlock, kvRow,
} from '../core/ui.js?v=90';

export const meta = {
  title: '服务器信息',
  subtitle: '运行参数、数据目录与部署接入说明',
};


/**
 * 服务器信息：运行参数、数据目录、敏感数据加密状态、本地外壳与部署接入说明。
 *
 * <para>与原「系统设置」里的那张卡相比没有内容变化，只是不再和账号、备份挤在一屏里。</para>
 */
export async function render(container) {
  clear(container);
  container.appendChild(loadingBlock());

  // /server/info 不需要鉴权（登录页也要用它显示服务器名与版本），这里同样不带令牌。
  const info = await api('/server/info', { auth: false });
  session.serverInfo = info;

  clear(container);
  container.appendChild(h('div',
    renderServerCard(info),
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
      kvRow('管理界面地址', baseUrl, true),
      kvRow('服务版本', info.version),
      kvRow('通信协议', `v${info.protocolVersion}`),
      kvRow('当前配置版本', `#${info.revision}`),
      kvRow('运行时长', formatDuration(info.uptimeSeconds)),
      kvRow('启动时间', formatDateTime(info.startedAt)),
      kvRow('监听端口', `HTTP ${info.httpPort}`),
      kvRow('局域网发现', info.discoveryEnabled ? `已开启（UDP ${info.discoveryPort}）` : '已关闭'),
      kvRow('设备注册', info.requiresEnrollCode ? '需要注册码' : '无需注册码'),
      kvRow('设备统计', `共 ${info.deviceCount} 台，在线 ${info.onlineDeviceCount} 台，待同步 ${info.pendingDeviceCount} 台`),
      kvRow('数据目录', info.dataDirectory, true),
      kvRow('敏感数据加密', formatSecrets(info.secretsEncrypted)),
      kvRow('加密密钥文件', info.secretsEncrypted?.keyFile || '—', true),
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