/** 服务器信息视图（系统设置拆分而来）：运行参数、数据目录、部署与接入说明。 */

import { api, session } from '../core/api.js?v=146';
import { t } from '../core/i18n.js?v=146';
import {
  h, clear, formatDateTime, formatDuration, toast, confirmDialog, copyText, loadingBlock, kvRow,
} from '../core/ui.js?v=146';

export const meta = {
  title: t('server.title'),
  subtitle: t('server.subtitle'),
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
        h('h3', t('server.title')),
        h('p.card-desc', info.serverName || t('server.serverFallback')),
      ),
    ),
    h('div', { style: { display: 'grid', gap: '10px', fontSize: '13px' } },
      kvRow(t('server.adminUrl'), baseUrl, true),
      kvRow(t('server.version'), info.version),
      kvRow(t('server.protocol'), `v${info.protocolVersion}`),
      kvRow(t('server.revision'), `#${info.revision}`),
      kvRow(t('server.uptime'), formatDuration(info.uptimeSeconds)),
      kvRow(t('server.startedAt'), formatDateTime(info.startedAt)),
      kvRow(t('server.port'), `HTTP ${info.httpPort}`),
      kvRow(t('server.discovery'), info.discoveryEnabled
        ? t('server.discoveryOn', { port: info.discoveryPort })
        : t('server.discoveryOff')),
      kvRow(t('server.enroll'), info.requiresEnrollCode ? t('server.enrollRequired') : t('server.enrollOpen')),
      kvRow(t('server.deviceStats'), t('server.deviceStatsValue', {
        total: info.deviceCount,
        online: info.onlineDeviceCount,
        pending: info.pendingDeviceCount,
      })),
      kvRow(t('server.dataDir'), info.dataDirectory, true),
      kvRow(t('server.secrets'), formatSecrets(info.secretsEncrypted)),
      kvRow(t('server.secretsKey'), info.secretsEncrypted?.keyFile || '—', true),
    ),
    h('div.card-actions', { style: { marginTop: '14px' } },
      h('button.btn.btn-sm', {
        type: 'button',
        onClick: () => copyText(baseUrl, t('server.copied')),
      }, t('server.copyUrl')),
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
    t('server.secretsEnroll', secrets.enrollCodes),
    t('server.secretsInvite', secrets.registerCodes),
    t('server.secretsWebhook', secrets.webhooks),
  ];
  const unavailable = (secrets.enrollCodes.unavailable || 0)
    + (secrets.registerCodes.unavailable || 0)
    + (secrets.webhooks.unavailable || 0);
  // 列表分隔符也走资源：中文用「，」、英文用「, 」——写死一个在另一种语言下都别扭。
  const list = parts.join(t('ui.listSeparator'));
  if (unavailable > 0) {
    return t('server.secretsBad', { list, count: unavailable });
  }
  return t('server.secretsOk', { list });
}

/**
 * 本地外壳（桌面端）专用卡片：只在被外壳内嵌时出现。
 * 页面通过 WebMessage 通道请求外壳执行动作（见 src/IslandHub.Shell 的 HandleShellMessage）。
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
      toast('ok', t('server.shellNotified'), t('server.commandSent', { label }));
    },
  }, label);

  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', t('server.shell')),
        h('p.card-desc', t('server.shellDesc')),
      ),
    ),
    h('div.toolbar',
      actionButton('restart', t('server.restart'), true),
      actionButton('open-browser', t('server.openInBrowser')),
      h('div.spacer'),
      h('button.btn.btn-sm', {
        type: 'button',
        onClick: async () => {
          if (!await confirmDialog(t('server.stop'), t('server.stopWarning'), t('server.stopConfirm'))) return;
          post('stop');
          toast('ok', t('server.shellNotified'), t('server.stopping'));
        },
      }, t('server.stop')),
    ),
    h('div.notice.notice-info', { style: { marginTop: '10px' } },
      h('span.notice-icon', 'i'),
      h('div', t('server.stopHint'))),
  );
}

function renderDeployCard(info) {
  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', t('server.deployTitle')),
        h('p.card-desc', t('server.deployIntro')),
      ),
    ),
    h('div', { style: { display: 'grid', gap: '14px', fontSize: '13px', lineHeight: '1.85' } },
      block(t('server.howToConnect'),
        h('ol', { style: { margin: 0, paddingLeft: '20px' } },
          h('li', t('server.step1')),
          h('li', t('server.step2')),
          h('li', t('server.step3')),
          h('li', t('server.step4')),
        )),
      block(t('server.localTitle'),
        h('div', t('server.localP1') + ' '
          + t('server.localUrl', { port: info.httpPort })
          + t('server.localUdp') + ' ' + info.discoveryPort + t('server.localUdp2'))),
      block(t('server.cloudTitle'),
        h('div', t('server.cloudP1')
          + t('server.cloudP2') + ' '
          + t('server.cloudP3'))),
      block(t('server.dataTitle'),
        h('div', t('server.dataP1')
          + t('server.dataCurrent', { dir: info.dataDirectory }))),
    ),
  );
}

function block(title, content) {
  return h('div', { style: { padding: '13px 15px', background: 'var(--bg-panel-2)', borderRadius: 'var(--radius-sm)' } },
    h('div', { style: { fontWeight: '600', marginBottom: '6px' } }, title),
    h('div', { style: { color: 'var(--text-dim)', fontSize: '12.5px' } }, content),
  );
}