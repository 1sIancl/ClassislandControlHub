/** 系统维护视图（系统设置拆分而来）：自动更新、备份与数据库整理。 */

import { api, hasPermission, fetchBlob } from '../core/api.js?v=141';
import { toastError } from '../core/errors.js?v=141';
import {
  h, clear, formatDateTime, toast, modal, confirmDialog, field, loadingBlock, kvRow,
} from '../core/ui.js?v=141';
import { t } from '../core/i18n.js?v=141';

export const meta = {
  title: t('maint.title'),
  subtitle: t('maint.subtitle'),
};

const BACKUP_TYPE_LABELS = {
  manual: t('maint.kindManual'),
  auto: t('maint.kindAuto'),
  update: t('maint.kindPreUpdate'),
  // 从上传的文件导入的（#125）：与「手动新建」区分开，否则用户会以为是自己刚点出来的。
  imported: t('maint.kindImported'),
};

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
      h('div', t('maint.noPermission'))),
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
    t('maint.migrateIntro1')
    + t('maint.migrateIntro2'));
  const previewHost = h('div', { style: { marginTop: '12px' } });
  let pendingToken = null;

  const exportBtn = h('button.btn.btn-sm', {
    type: 'button',
    onClick: async () => {
      exportBtn.disabled = true;
      exportBtn.textContent = t('maint.exporting');
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
        status.textContent = t('maint.exportedSummary', { tables: Object.keys(pack.tables).length, rows });
      } catch (e) {
        toastError(e, t('maint.exportFailed'));
      } finally {
        exportBtn.disabled = false;
        exportBtn.textContent = t('maint.exportPackage');
      }
    },
  }, t('maint.exportPackage'));

  const applyBtn = h('button.btn.btn-sm.btn-danger', {
    type: 'button',
    disabled: true,
    onClick: async () => {
      if (!pendingToken) return;
      const ok = await confirmDialog(t('maint.importTitle'),
        t('maint.importConfirm1')
        + t('maint.confirmContinue'), t('maint.import'), true);
      if (!ok) return;

      applyBtn.disabled = true;
      applyBtn.textContent = t('maint.importing');
      try {
        const result = await api('/admin/migration/apply', { method: 'POST', body: { token: pendingToken } });
        pendingToken = null;
        previewHost.replaceChildren();
        status.textContent = t('maint.importDone', {
          added: result.tables.reduce((s, x) => s + x.added, 0),
          updated: result.tables.reduce((s, x) => s + x.updated, 0),
          skipped: result.tables.reduce((s, x) => s + x.skipped, 0),
          backupId: result.backupId,
        });
        toast('ok', t('maint.importDoneTitle'), t('maint.importDoneHint'));
        applyBtn.textContent = t('maint.importTitle');
      } catch (e) {
        toastError(e, t('maint.importFailed'));
        applyBtn.disabled = false;
        applyBtn.textContent = t('maint.importTitle');
      }
    },
  }, t('maint.importTitle'));

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
            h('th', t('maint.colTable')), h('th', t('maint.colAdded')), h('th', t('maint.colUpdated')), h('th', t('maint.colSkipped')), h('th', t('maint.colNote')))),
          h('tbody', ...preview.tables.map((t) => h('tr',
            h('td', t.label),
            h('td', String(t.added)),
            h('td', t.updated > 0 ? h('span', { style: { color: 'var(--warn)' } }, String(t.updated)) : '0'),
            h('td', String(t.skipped)),
            h('td', { style: { fontSize: '12px', color: 'var(--text-faint)' } }, t.note || ''),
          ))));

        previewHost.replaceChildren(h('div',
          h('div', { style: { fontSize: '13px', marginBottom: '8px' } },
            t('maint.previewSource', { name: preview.serverName || t('maint.unsigned'), version: preview.version || '?' }),
            t('maint.previewTotals', {
              added: preview.totalAdded, updated: preview.totalUpdated, skipped: preview.totalSkipped,
            })),
          table,
          ...(preview.warnings || []).map((w) => h('div.notice.notice-warn', { style: { marginTop: '8px' } },
            h('span.notice-icon', '!'), h('div', w))),
        ));
        applyBtn.disabled = !canWrite;
      } catch (e) {
        toastError(e, t('maint.badPackage'));
        previewHost.replaceChildren();
      }
    },
  });

  const pickBtn = h('button.btn.btn-sm', {
    type: 'button',
    disabled: !canWrite,
    onClick: () => fileInput.click(),
  }, t('maint.pickPackage'));

  return h('div.card', { style: { marginTop: '18px' } },
    h('div.card-head',
      h('h3', t('maint.migrateCard')),
      h('span.card-sub', t('maint.migrateCardDesc'))),
    h('div.card-body',
      h('div', { style: { display: 'flex', gap: '8px', flexWrap: 'wrap', marginBottom: '10px' } },
        exportBtn, pickBtn, applyBtn, fileInput),
      status,
      canWrite ? null : h('div', { style: { fontSize: '12px', color: 'var(--warn)', marginTop: '6px' } },
        t('maint.migrateReadOnly')),
      previewHost,
    ));
}

function renderUpdateCard(update) {
  const statusText = h('div', { style: { fontSize: '13px', color: 'var(--text-dim)' } }, update.status || t('maint.updateNotChecked'));
  const versionText = h('div', { style: { fontSize: '13px', color: 'var(--text-dim)' } },
    t('maint.updateCurrent', { version: update.currentVersion || '—' })
      + (update.latestVersion ? t('maint.updateLatest', { version: update.latestVersion }) : ''));

  const checkBtn = h('button.btn.btn-sm', {
    type: 'button',
    onClick: async () => {
      checkBtn.disabled = true;
      checkBtn.textContent = t('maint.checking');
      try {
        const r = await api('/admin/update/check');
        toast('ok', r.status || t('maint.checkDone'));
        await render(document.getElementById('content'));
      } catch (e) {
        toastError(e, t('maint.checkFailed'));
        checkBtn.disabled = false;
        checkBtn.textContent = t('maint.checkUpdate');
      }
    },
  }, t('maint.checkUpdate'));

  const applyBtn = h('button.btn.btn-primary.btn-sm', {
    type: 'button',
    onClick: async () => {
      const ok = await confirmDialog(t('maint.applyTitle'),
        t('maint.applyConfirm', { version: update.latestVersion }),
        t('maint.applyConfirmButton'));
      if (!ok) return;
      applyBtn.disabled = true;
      applyBtn.textContent = t('maint.updating');
      try {
        const r = await api('/admin/update/apply', { method: 'POST' });
        toast('ok', t('maint.updateStarted'), r.status || '');
      } catch (e) {
        toastError(e, t('maint.updateFailed'));
        applyBtn.disabled = false;
        applyBtn.textContent = t('maint.updateNow');
      }
    },
  }, t('maint.updateNow'));

  return h('div.card', { style: { marginTop: '16px' } },
    h('div.card-head',
      h('div',
        h('h3', t('maint.autoUpdate')),
        h('p.card-desc', t('maint.autoUpdateDesc')),
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
    // 回滚（#89）：更新前会自动拍快照，这里列出来并提供「回滚」。
    renderRollbackSection(),
  );
}

/**
 * 回滚区（#89）：列出程序文件快照，提供「手动快照」与「回滚到该版本」。
 *
 * 为什么放在更新卡片里：它和「更新」是同一件事的两端——更新前自动拍的那份快照，
 * 就是为了「更新完发现不对」时有退路；分成两个页面反而让人想不到还能退。
 */
function renderRollbackSection() {
  const host = h('div', { style: { marginTop: '14px', borderTop: '1px solid var(--line)', paddingTop: '12px' } });

  const manualBtn = h('button.btn.btn-sm', {
    type: 'button',
    onClick: async () => {
      manualBtn.disabled = true;
      manualBtn.textContent = t('maint.snapshotting');
      try {
        const snap = await api('/admin/update/rollbacks', { method: 'POST' });
        toast('ok', t('maint.snapshotCreatedTitle'), snap ? t('maint.snapshotCreated', { version: snap.version, files: snap.fileCount }) : '');
        await reload();
      } catch (e) {
        toastError(e, t('maint.snapshotFailed'));
      } finally {
        manualBtn.disabled = false;
        manualBtn.textContent = t('maint.snapshotManual');
      }
    },
  }, t('maint.snapshotManual'));

  const rollback = async (item) => {
    if (!await confirmDialog(t('maint.rollbackTitle'),
      `将把程序文件换回「${item.version}」（${new Date(item.createdAt).toLocaleString()} 的快照）。\n`
      + t('maint.rollbackConfirm'),
      t('maint.rollback'), true)) {
      return;
    }

    try {
      const result = await api(`/admin/update/rollbacks/${encodeURIComponent(item.id)}`, { method: 'POST' });
      if (result && result.executed === false) {
        toast('ok', t('maint.submittedApproval'), result.message || t('maint.waitingApproval'), 9000);
      } else {
        toast('ok', result?.executed ? t('maint.rolledBack') : t('maint.rollbackIncomplete'), result?.message || '', 9000);
        await reload();
      }
    } catch (e) {
      toastError(e, t('maint.rollbackFailed'));
    }
  };

  const reload = async () => {
    host.replaceChildren(h('div', { style: { fontSize: '12px', color: 'var(--text-faint)' } }, t('maint.loadingSnapshots')));

    let items = [];
    try {
      items = await api('/admin/update/rollbacks');
    } catch (e) {
      host.replaceChildren(h('div', { style: { fontSize: '12px', color: 'var(--warn)' } },
        t('maint.snapshotsFailed', { message: e.message || e })));
      return;
    }

    const head = h('div', { style: { display: 'flex', gap: '8px', alignItems: 'baseline', marginBottom: '8px' } },
      h('strong', t('maint.rollbackSection')),
      h('span', { style: { fontSize: '12px', color: 'var(--text-faint)' } },
        t('maint.rollbackSectionHint')));

    if (!Array.isArray(items) || items.length === 0) {
      host.replaceChildren(head,
        h('div', { style: { fontSize: '12px', color: 'var(--text-faint)', marginBottom: '8px' } },
          t('maint.noSnapshots')),
        manualBtn);
      return;
    }

    const rows = items.map((item) => h('div', {
      style: {
        display: 'grid', gridTemplateColumns: '1fr auto auto', gap: '10px', alignItems: 'center',
        padding: '6px 0', borderBottom: '1px dashed var(--line)', fontSize: '13px',
      },
    },
    h('div',
      h('div', t('maint.snapshotTitle', {
        version: item.version,
        reason: ROLLBACK_REASON[item.reason] || item.reason || t('maint.snapshot'),
      })),
      h('div', { style: { fontSize: '11px', color: 'var(--text-faint)' } },
        t('maint.snapshotMeta', {
          at: new Date(item.createdAt).toLocaleString(),
          files: item.fileCount || '?',
        }))),
    h('span', { style: { fontSize: '12px', color: 'var(--text-dim)' } }, formatBytes(item.sizeBytes || 0)),
    h('button.btn.btn-sm', { type: 'button', onClick: () => rollback(item) }, t('maint.rollbackToThis'))));

    host.replaceChildren(head, h('div', rows), h('div.card-actions', manualBtn));
  };

  reload();
  return host;
}

/** 快照原因的中文说明（与后端 `RollbackReasons` 对应）。 */
const ROLLBACK_REASON = {
  'pre-update': t('maint.reasonPreUpdate'),
  'pre-rollback': t('maint.reasonPreRollback'),
  manual: t('maint.reasonManual'),
};

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
        h('h3', t('maint.backupCard')),
        h('p.card-desc',
          t('maint.backupIntro1')
          + t('maint.backupIntro2')),
      ),
      canWrite
        ? h('div.card-actions',
          // 导入备份（#125）：换机器迁移、或把别人给的备份拿进来时用。加密包会问口令。
          h('button.btn.btn-sm', {
            type: 'button',
            onClick: () => openImportBackupDialog(container),
          }, t('maint.importBackup')),
          h('button.btn.btn-primary.btn-sm', {
            type: 'button',
            onClick: () => openCreateBackupDialog(container),
          }, t('maint.newBackup')),
        )
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
    healthBox.appendChild(h('p.card-desc', t('maint.checkingDatabase')));
    try {
      const health = await api('/admin/database/health');
      clear(healthBox);
      healthBox.appendChild(renderHealth(health, load));
    } catch (err) {
      clear(healthBox);
      healthBox.appendChild(h('p.card-desc', t('maint.healthFailed', { message: err.message || t('maint.checkFailed') })));
    }
  }

  function renderHealth(health, reload) {
    const reclaimable = health.reclaimableBytes || 0;
    return h('div', { style: { marginBottom: '16px' } },
      h('div.config-section-title', t('maint.database')),
      h('div', { style: { display: 'grid', gap: '6px', fontSize: '13px' } },
        kvRow(t('maint.diskUsage'), t('maint.diskUsageValue', {
          total: formatBytes(health.totalBytes),
          db: formatBytes(health.databaseBytes),
          wal: formatBytes(health.walBytes),
          shm: formatBytes(health.sharedMemoryBytes),
        })),
        kvRow(t('maint.integrity'), health.integrityOk
          ? t('maint.integrityOk')
          : t('maint.integrityBad', { message: health.integrityMessage })),
        kvRow(t('maint.reclaimable'), reclaimable > 0
          ? t('maint.reclaimableValue', { size: formatBytes(reclaimable), pages: health.freePageCount })
          : t('maint.reclaimableNone')),
      ),
      health.integrityOk
        ? null
        : h('div.notice.notice-danger', { style: { marginTop: '10px' } },
          h('span.notice-icon', '!'),
          h('div', t('maint.integrityWarn'))),
      reclaimable > 0
        ? h('p.card-desc', { style: { marginTop: '8px' } },
          t('maint.vacuumIntro1')
          + t('maint.vacuumSpaceHint', { size: formatBytes(health.databaseBytes) }))
        : null,
      canWrite
        ? h('button.btn.btn-sm', {
          type: 'button',
          style: { marginTop: '10px' },
          onClick: async () => {
            if (!await confirmDialog(t('maint.vacuumTitle'),
              t('maint.vacuumConfirm', {
                space: reclaimable > 0 ? t('maint.vacuumSpace', { size: formatBytes(reclaimable) }) : '',
              }),
              t('maint.vacuumStart'))) {
              return;
            }

            try {
              const result = await api('/admin/database/vacuum', { method: 'POST' });
              toast('ok', t('maint.vacuumDoneTitle'),
                `${formatBytes(result.beforeBytes)} → ${formatBytes(result.afterBytes)}`
                + t('maint.vacuumDone', { size: formatBytes(result.reclaimedBytes), ms: result.durationMs }));
              await reload();
            } catch (err) {
              toastError(err, t('maint.vacuumFailed'));
            }
          },
        }, t('maint.vacuum'))
        : null,
    );
  }

  async function loadList() {
    clear(listBox);
    listBox.appendChild(h('p.card-desc', t('maint.loadingBackups')));
    try {
      const entries = await api('/admin/backups');
      clear(listBox);
      listBox.appendChild(renderList(entries, load));
    } catch (err) {
      clear(listBox);
      listBox.appendChild(h('p.card-desc', err.message || t('maint.loadFailed')));
    }
  }

  function renderList(entries, reload) {
    const title = h('div.config-section-title', t('maint.backupsTitle', { count: entries.length }));
    if (entries.length === 0) {
      return h('div', title,
        h('p.card-desc', { style: { margin: 0 } },
          t('maint.noBackups')));
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
            ? h('span.badge.badge-danger', { title: t('maint.withKeyHint') }, t('maint.withKey'))
            : h('span.badge.badge-neutral', { title: t('maint.withoutKeyHint') }, t('maint.withoutKey')),
          h('button.btn.btn-sm', { type: 'button', onClick: () => downloadBackup(entry.id) }, t('maint.download')),
          canWrite
            ? h('button.btn.btn-sm', {
              type: 'button',
              title: t('maint.exportEncryptedHint'),
              onClick: () => openEncryptedExportDialog(entry),
            }, t('maint.exportEncrypted'))
            : null,
          canWrite
            ? h('button.btn.btn-sm', {
              type: 'button',
              onClick: async () => {
                if (!await confirmDialog(t('maint.restoreTitle'),
                  `将用「${entry.id}」覆盖当前数据库，并需要重启服务才生效。\n`
                  + t('maint.restoreConfirm'), t('maint.restore'), true)) {
                  return;
                }

                try {
                  // 操作审批（#35）：非超级管理员点下去只会**提交审批**，返回 executed=false。
                  const result = await api(`/admin/backups/${encodeURIComponent(entry.id)}/restore`,
                    { method: 'POST' });
                  if (result && result.executed === false) {
                    toast('ok', t('maint.submittedApproval'), result.message || t('maint.waitingApprovalPage'), 9000);
                  } else {
                    toast('ok', t('maint.restored'), t('maint.restartNeeded'), 8000);
                  }

                  await reload();
                } catch (err) {
                  toastError(err, t('maint.restoreFailed'));
                }
              },
            }, t('maint.restore'))
            : null,
          canWrite
            ? h('button.btn.btn-sm.btn-danger', {
              type: 'button',
              onClick: async () => {
                if (!await confirmDialog(t('maint.deleteBackup'), t('maint.deleteBackupConfirm', { id: entry.id }), t('maint.delete'), true)) {
                  return;
                }

                try {
                  await api(`/admin/backups/${encodeURIComponent(entry.id)}`, { method: 'DELETE' });
                  toast('ok', t('maint.deleted'));
                  await reload();
                } catch (err) {
                  toastError(err, t('maint.deleteFailed'));
                }
              },
            }, t('maint.delete'))
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
    toastError(err, t('maint.downloadFailed'));
  }
}

/**
 * 加密导出（#60）：口令走请求体（不进 URL / 访问日志），服务端**不保存**它——
 * 忘了口令这份备份就解不开，所以界面必须把这句话说在前面，并要求二次输入。
 */
function openEncryptedExportDialog(entry) {
  const p1 = h('input', { type: 'password', placeholder: t('maint.atLeast6') });
  const p2 = h('input', { type: 'password', placeholder: t('maint.repeatOnce') });
  const hint = h('p.card-desc', { style: { margin: '8px 0 0' } }, '');

  const fail = (text) => {
    hint.textContent = text;
    hint.style.color = 'var(--danger)';
  };

  modal({
    title: t('maint.encryptedExportTitle', { id: entry.id }),
    width: 'wide',
    body: h('div',
      h('div.notice.notice-info',
        h('span.notice-icon', 'i'),
        h('div', t('maint.encryptedNote1')
          + t('maint.encryptedNote2'))),
      field(t('maint.passphrase'), p1, t('maint.passphraseHint')),
      field(t('maint.passphraseConfirm'), p2),
      hint,
      h('p.card-desc', { style: { marginTop: '10px' } },
        t('maint.decryptCommand')),
    ),
    confirmText: t('maint.export'),
    onConfirm: async () => {
      if (p1.value.length < 6) {
        fail(t('maint.passphraseTooShort'));
        return false;
      }

      if (p1.value !== p2.value) {
        fail(t('maint.passphraseMismatch'));
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

        toast('ok', t('maint.encryptedExported'), t('maint.encryptedExportedHint'), 8000);
        return true;
      } catch (err) {
        fail(err.message || t('maint.exportFailedDot'));
        return false;
      }
    },
  });
}

/** 新建备份对话框：可选内容默认「数据库 + 配置文件」，密钥要显式勾选并看到警告。 */
function openCreateBackupDialog(container) {
  const noteInput = h('input', { type: 'text', placeholder: t('maint.notePlaceholder') });
  const configChk = h('input', { type: 'checkbox' });
  configChk.checked = true;
  const keyChk = h('input', { type: 'checkbox' });
  const tokenChk = h('input', { type: 'checkbox' });

  const keyWarning = h('div.notice.notice-warn', { hidden: true },
    h('span.notice-icon', '!'),
    h('div', t('maint.withKeyWarn1')
      + t('maint.withKeyWarn2')));
  keyChk.addEventListener('change', () => {
    keyWarning.hidden = !keyChk.checked;
  });

  modal({
    title: t('maint.newBackupTitle'),
    width: 'wide',
    body: h('div',
      field(t('maint.note'), noteInput, t('maint.noteHint')),
      h('div', { style: { marginTop: '12px' } },
        h('div.config-section-title', t('maint.backupContents')),
        h('div.config-panel',
          h('div.config-item',
            h('span', t('maint.database')), h('span.badge.badge-neutral', { style: { marginLeft: 'auto' } }, t('maint.required'))),
          h('label.config-item', configChk, h('span', t('maint.contentConfig'))),
          h('label.config-item', keyChk, h('span', t('maint.contentSecrets'))),
          h('label.config-item', tokenChk, h('span', t('maint.contentLocalToken'))),
        )),
      h('div', { style: { marginTop: '10px' } }, keyWarning),
    ),
    confirmText: t('maint.create'),
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
        toast('ok', t('maint.backupCreated'));
        await render(container);
        return true;
      } catch (err) {
        toastError(err, t('maint.createFailed'));
        return false;
      }
    },
  });
}

/**
 * 导入备份文件（#125）：支持普通 zip 与加密包（.zip.enc，要口令）。
 *
 * <p>
 * 导入只是**落成一份备份**：它会出现在列表里，但要真正覆盖数据还得再点「从备份恢复」
 * （那条带操作审批）。所以这里不做「导入即恢复」——传错文件不该直接换掉整库。
 * </p>
 */
function openImportBackupDialog(container) {
  const fileInput = h('input', { type: 'file', accept: '.zip,.enc' });
  const passInput = h('input', {
    type: 'password',
    autocomplete: 'new-password',
    placeholder: t('maint.importPassphrasePlaceholder'),
  });
  const noteInput = h('input', { type: 'text', placeholder: t('maint.notePlaceholder') });

  modal({
    title: t('maint.importBackupTitle'),
    width: 'wide',
    body: h('div',
      field(t('maint.importFile'), fileInput, t('maint.importFileHint')),
      field(t('maint.importPassphrase'), passInput, t('maint.importPassphraseHint')),
      field(t('maint.note'), noteInput, t('maint.noteHint')),
    ),
    confirmText: t('maint.import'),
    onConfirm: async () => {
      const file = fileInput.files?.[0];
      if (!file) {
        toast('warn', t('maint.importNeedFile'));
        return false;
      }

      try {
        const form = new FormData();
        form.append('file', file);
        form.append('passphrase', passInput.value);
        form.append('note', noteInput.value.trim());
        const entry = await api('/admin/backups/import', { method: 'POST', body: form });
        // 提醒一句「还没生效」：用户很容易以为导入完数据就换好了。
        toast('ok', t('maint.importDone', { name: entry?.id ?? '' }), t('maint.importDoneHint'), 6000);
        await render(container);
        return true;
      } catch (err) {
        toastError(err, t('maint.importFailed'));
        return false;
      }
    },
  });
}