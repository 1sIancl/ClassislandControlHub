/** 系统维护视图（系统设置拆分而来）：自动更新、备份与数据库整理。 */

import { api, hasPermission, fetchBlob } from '../core/api.js?v=123';
import { toastError } from '../core/errors.js?v=123';
import {
  h, clear, formatDateTime, toast, modal, confirmDialog, field, loadingBlock, kvRow,
} from '../core/ui.js?v=123';

export const meta = {
  title: '系统维护',
  subtitle: '自动更新、备份与数据库整理',
};

const BACKUP_TYPE_LABELS = { manual: '手动', auto: '自动', update: '更新前' };

/**
 * 系统维护：自动更新、备份与数据库整理。
 *
 * <para>这两件事的共同点是「都会让服务停顿或者重来一次」，所以放在同一页，
 * 与日常配置（账号、设备）分开——避免有人只是想改个账号，却顺手点了「立即更新」。</para>
 *
 * <para>授时偏移已经挪到「教学配置 → 时间偏移」：它调的是教室大屏上的时钟，
 * 属于教学配置的事，不是服务器维护。</para>
 */
export async function render(container) {
  clear(container);
  container.appendChild(loadingBlock());

  const canSettings = hasPermission('settings.read');
  const canBackup = hasPermission('backup.read');

  const updateState = canSettings ? await api('/admin/update/state') : null;

  clear(container);
  container.appendChild(h('div',
    canSettings ? renderUpdateCard(updateState) : null,
    // 备份与数据库维护（#59 / #62）：权限独立于系统设置，有 backup.read 就可见。
    canBackup ? renderBackupCard(container) : null,
    // 数据迁移（#61）：导出看 settings.read，导入需要 settings.write。
    canSettings ? renderMigrationCard(hasPermission('settings.write')) : null,
    (canSettings || canBackup) ? null : h('div.notice.notice-warn',
      h('span.notice-icon', '!'),
      h('div', '当前账号没有「系统设置」或「备份」权限，本页没有可操作的内容。')),
  ));
}

/**
 * 数据迁移（#61）：整包导出 / 导入。
 *
 * 为什么放在「系统维护」而不是「系统设置」：它与「备份与恢复」是一类事——
 * 都会整体动这台服务器的数据，出问题的补救方式也一样（先备份、再动手）。
 */
function renderMigrationCard(canWrite) {
  const status = h('div', { style: { fontSize: '13px', color: 'var(--text-dim)' } },
    '导出会把「人配出来的东西」（档案 / 分组 / 标签 / 设备 / 账号 / 通知渠道 / 提醒 / 模板 / 系统设置）打成'
    + '一个 JSON 包；设备令牌、账号密码、Webhook 密钥不在包里。');
  const previewHost = h('div', { style: { marginTop: '12px' } });
  let pendingToken = null;

  const exportBtn = h('button.btn.btn-sm', {
    type: 'button',
    onClick: async () => {
      exportBtn.disabled = true;
      exportBtn.textContent = '导出中…';
      try {
        const pack = await api('/admin/migration/export');
        const rows = Object.values(pack.tables).reduce((sum, t) => sum + t.rows.length, 0);
        const blob = new Blob([JSON.stringify(pack, null, 2)], { type: 'application/json' });
        const url = URL.createObjectURL(blob);
        const link = h('a', {
          href: url,
          download: `islandhub-migration-${new Date().toISOString().slice(0, 10)}.json`,
        });
        document.body.appendChild(link);
        link.click();
        link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 5000);
        status.textContent = `已导出：${Object.keys(pack.tables).length} 张表、${rows} 行（包里不含令牌 / 密码 / 密钥）。`;
      } catch (e) {
        toastError(e, '导出失败');
      } finally {
        exportBtn.disabled = false;
        exportBtn.textContent = '导出迁移包';
      }
    },
  }, '导出迁移包');

  const applyBtn = h('button.btn.btn-sm.btn-danger', {
    type: 'button',
    disabled: true,
    onClick: async () => {
      if (!pendingToken) return;
      const ok = await confirmDialog('确认导入',
        '导入会覆盖本机同名（同 ID）的档案 / 分组 / 设置。导入前会自动生成一份备份，'
        + '确认继续吗？', '导入', true);
      if (!ok) return;

      applyBtn.disabled = true;
      applyBtn.textContent = '导入中…';
      try {
        const result = await api('/admin/migration/apply', { method: 'POST', body: { token: pendingToken } });
        pendingToken = null;
        previewHost.replaceChildren();
        status.textContent = `导入完成：新增 ${result.tables.reduce((s, t) => s + t.added, 0)} / `
          + `覆盖 ${result.tables.reduce((s, t) => s + t.updated, 0)} / `
          + `跳过 ${result.tables.reduce((s, t) => s + t.skipped, 0)}。导入前的备份：${result.backupId}。`;
        toast('ok', '导入完成', '出问题可在下方「备份与恢复」里用刚才那份备份回退。');
        applyBtn.textContent = '确认导入';
      } catch (e) {
        toastError(e, '导入失败');
        applyBtn.disabled = false;
        applyBtn.textContent = '确认导入';
      }
    },
  }, '确认导入');

  const fileInput = h('input', {
    type: 'file',
    accept: '.json,application/json',
    style: { display: 'none' },
    onChange: async (event) => {
      const file = event.target.files?.[0];
      event.target.value = '';
      if (!file) return;

      previewHost.replaceChildren(loadingBlock());
      pendingToken = null;
      applyBtn.disabled = true;
      try {
        const pack = JSON.parse(await file.text());
        const preview = await api('/admin/migration/preview', { method: 'POST', body: pack });
        pendingToken = preview.token;

        const table = h('table.table', { style: { fontSize: '13px' } },
          h('thead', h('tr',
            h('th', '内容'), h('th', '新增'), h('th', '覆盖'), h('th', '跳过'), h('th', '说明'))),
          h('tbody', ...preview.tables.map((t) => h('tr',
            h('td', t.label),
            h('td', String(t.added)),
            h('td', t.updated > 0 ? h('span', { style: { color: 'var(--warn)' } }, String(t.updated)) : '0'),
            h('td', String(t.skipped)),
            h('td', { style: { fontSize: '12px', color: 'var(--text-faint)' } }, t.note || ''),
          ))));

        previewHost.replaceChildren(h('div',
          h('div', { style: { fontSize: '13px', marginBottom: '8px' } },
            `来源：${preview.serverName || '（未署名）'} v${preview.version || '?'}`,
            ` · 合计新增 ${preview.totalAdded} / 覆盖 ${preview.totalUpdated} / 跳过 ${preview.totalSkipped}`),
          table,
          ...(preview.warnings || []).map((w) => h('div.notice.notice-warn', { style: { marginTop: '8px' } },
            h('span.notice-icon', '!'), h('div', w))),
        ));
        applyBtn.disabled = !canWrite;
      } catch (e) {
        toastError(e, '这个文件不是有效的迁移包');
        previewHost.replaceChildren();
      }
    },
  });

  const pickBtn = h('button.btn.btn-sm', {
    type: 'button',
    disabled: !canWrite,
    onClick: () => fileInput.click(),
  }, '选择迁移包…');

  return h('div.card', { style: { marginTop: '18px' } },
    h('div.card-head',
      h('h3', '数据迁移'),
      h('span.card-sub', '把一台服务器上配好的东西整包搬到另一台')),
    h('div.card-body',
      h('div', { style: { display: 'flex', gap: '8px', flexWrap: 'wrap', marginBottom: '10px' } },
        exportBtn, pickBtn, applyBtn, fileInput),
      status,
      canWrite ? null : h('div', { style: { fontSize: '12px', color: 'var(--warn)', marginTop: '6px' } },
        '当前账号没有「系统设置 · 修改」权限：可以导出，但不能导入。'),
      previewHost,
    ));
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

/** 字节数写成可读文本。 */
function formatBytes(bytes) {
  const n = Number(bytes) || 0;
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  if (n < 1024 * 1024 * 1024) return `${(n / 1024 / 1024).toFixed(1)} MB`;
  return `${(n / 1024 / 1024 / 1024).toFixed(2)} GB`;
}

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
        kvRow('占用空间', `${formatBytes(health.totalBytes)}（主库 ${formatBytes(health.databaseBytes)}`
          + `，WAL ${formatBytes(health.walBytes)}，SHM ${formatBytes(health.sharedMemoryBytes)}）`),
        kvRow('完整性检查', health.integrityOk ? '正常' : `异常：${health.integrityMessage}`),
        kvRow('可回收空间', reclaimable > 0
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
                  // 操作审批（#35）：非超级管理员点下去只会**提交审批**，返回 executed=false。
                  const result = await api(`/admin/backups/${encodeURIComponent(entry.id)}/restore`,
                    { method: 'POST' });
                  if (result && result.executed === false) {
                    toast('ok', '已提交审批', result.message || '等待超级管理员在「操作审批」里确认。', 9000);
                  } else {
                    toast('ok', '已恢复', '需要重启服务端才能生效。', 8000);
                  }

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
        '解密命令：IslandHub.Server --decrypt-backup <加密文件> <输出.zip>（会提示输入口令）。'),
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