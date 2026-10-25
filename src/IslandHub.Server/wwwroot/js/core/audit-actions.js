import { t } from './i18n.js?v=146';

/**
 * 审计动作码 → 中文（#42）。
 *
 * 审计里 action 是稳定的英文码（`device.command`、`profile.update`…），
 * 直接展示原文只有开发者看得懂；全量硬编码又会在新增动作时漏掉。
 * 这里做「前缀 + 动作」的**组合翻译**，认不出时**退回原文**——
 * 宁可让人看到英文，也不要显示「未知动作」让人以为数据坏了。
 */

/** 动作码第一段 → 模块名。 */
const PREFIX_LABELS = {
  'device': 'audit.prefix.device',
  'group': 'audit.prefix.group',
  'profile': 'audit.prefix.profile',
  'account': 'audit.prefix.account',
  'registration': 'audit.prefix.registration',
  'enrollcode': 'audit.prefix.enrollcode',
  'webhook': 'audit.prefix.webhook',
  'reminder': 'audit.prefix.reminder',
  'notice-template': 'audit.prefix.notice-template',
  'settings': 'audit.prefix.settings',
  'backup': 'audit.prefix.backup',
  'timetable': 'audit.prefix.timetable',
  'apikey': 'audit.prefix.apikey',
  'totp': 'audit.prefix.totp',
  'audit': 'audit.prefix.audit',
  'tag': 'audit.prefix.tag',
  'template': 'audit.prefix.template',
  // 插件管理（#121）：同步索引 / 缓存包 / 批量下发。
  'plugin': 'audit.prefix.plugin',
  'admin': 'audit.prefix.admin',
  // 下面这些是「凑齐 104 个动作码、一个不漏」时补的：少了它们，审计页会出现
  // 「exam.start」「device.enroll」这种整串英文（前缀认不出来就整串退回原文）。
  'exam': 'audit.prefix.exam',
  'profile-slots': 'audit.prefix.profile-slots',
  'register-code': 'audit.prefix.register-code',
  'update': 'audit.prefix.update',
  'config': 'audit.prefix.config',
  'database': 'audit.prefix.database',
  'branding': 'audit.prefix.branding',
  'ai': 'audit.prefix.ai',
  'lesson': 'audit.prefix.lesson',
  'timeoffset': 'audit.prefix.timeoffset',};

/** 动作码其余部分 → 动词。多段动作（如 `command.broadcast`）整体作为键。 */
const VERB_LABELS = {
  'create': 'audit.verb.create',
  'update': 'audit.verb.update',
  'delete': 'audit.verb.delete',
  'clear': 'audit.verb.clear',
  'test': 'audit.verb.test',
  'push': 'audit.verb.push',
  'run': 'audit.verb.run',
  'cancel': 'audit.verb.cancel',
  'restore': 'audit.verb.restore',
  'login': 'audit.verb.login',
  'logout': 'audit.verb.logout',
  'export': 'audit.verb.export',
  'import': 'audit.verb.import',
  'setdefault': 'audit.verb.setdefault',
  'duplicate': 'audit.verb.duplicate',
  // 拼出来是「标签变更」；用「分配」或「设置」都会读出别扭的语序。
  'assign': 'audit.verb.assign',
  'approve': 'audit.verb.approve',
  'reject': 'audit.verb.reject',
  'request': 'audit.verb.request',
  'switch': 'audit.verb.switch',
  'command': 'audit.verb.command',
  'command.broadcast': 'audit.verb.command_broadcast',
  'command.cancel': 'audit.verb.command_cancel',
  'diagnostics.clear': 'audit.verb.diagnostics_clear',
  'appearance': 'audit.verb.appearance',
  'notify': 'audit.verb.notify',
  'plugin.refresh': 'audit.verb.plugin_refresh',
  'import-cses': 'audit.verb.import-cses',
  'import-cidata': 'audit.verb.import-cidata',
  'version.delete': 'audit.verb.version_delete',
  'deliveries.clear': 'audit.verb.deliveries_clear',
  // 插件管理（#121）。前缀是 plugin，这里填的是动作码去掉前缀后的部分。
  'refresh': 'audit.verb.refresh',
  'cache': 'audit.verb.cache',
  'cache.delete': 'audit.verb.cache_delete',
  'deploy': 'audit.verb.deploy',
  'market.sync': 'audit.verb.market_sync',
  'market.settings': 'audit.verb.market_settings',
  // ── 补齐剩下的动作码（`_shot/audit-labels-report.js` 会把「渲染后仍含英文」的全列出来）──
  // 登录 / 密码（前缀 admin）
  'login.failed': 'audit.verb.login_failed',
  'login.locked': 'audit.verb.login_locked',
  'login.totp': 'audit.verb.login_totp',
  'login.totp.failed': 'audit.verb.login_totp_failed',
  'password.changed': 'audit.verb.password_changed',
  'password.expiring': 'audit.verb.password_expiring',
  'password.reset': 'audit.verb.password_reset',
  'seed': 'audit.verb.seed',
  // 自助注册 / 注册码
  'register': 'audit.verb.register',
  'register.approve': 'audit.verb.register_approve',
  'register.reject': 'audit.verb.register_reject',
  'register.request': 'audit.verb.register_request',
  'register.delete': 'audit.verb.register_delete',
  'register.failed': 'audit.verb.register_failed',
  'approval.switch': 'audit.verb.approval_switch',
  // 设备
  'enroll': 'audit.verb.enroll',
  'wake': 'audit.verb.wake',
  'command.result': 'audit.verb.command_result',
  'export.csv': 'audit.verb.export_csv',
  'import.csv': 'audit.verb.import_csv',
  // 档案 / 课表 / 模板
  'apply': 'audit.verb.apply',
  'ai-import': 'audit.verb.ai-import',
  'override.create': 'audit.verb.override_create',
  'override.delete': 'audit.verb.override_delete',
  'swap': 'audit.verb.swap',
  'adjust': 'audit.verb.adjust',
  // 其他
  'config': 'audit.verb.config',
  'start': 'audit.verb.start',
  'end': 'audit.verb.end',
  'updated': 'audit.verb.updated',
  // 多校区时间偏移（#26）：动作码是 `timeoffset.group.updated`，缺了这条会显示成
  // 「授时 · group.updated」这种半英文。
  'group.updated': 'audit.verb.group_updated',
  'check': 'audit.verb.check',
  'vacuum': 'audit.verb.vacuum',
  'image.upload': 'audit.verb.image_upload',
  'disable': 'audit.verb.disable',
  'enable': 'audit.verb.enable',
  'inventory.refresh': 'audit.verb.inventory_refresh',
  // 考试模式（#21）：临时切档案 + 还原，事后要看得出「谁在什么时候开的、涉及哪些教室」。
  'exam.start': 'audit.verb.exam_start',
  'exam.end': 'audit.verb.exam_end',
  // 配置同步（#17 / #79）。这些码一直存在（`device.sync.success` 等），但此前没登记，
  // 审计页显示的是「设备 · sync.success」这种半英文。
  'sync.pull': 'audit.verb.sync_pull',
  'sync.success': 'audit.verb.sync_success',
  'sync.partial': 'audit.verb.sync_partial',
  'sync.failed': 'audit.verb.sync_failed',
  'sync.retry': 'audit.verb.sync_retry',};

/**
 * 把审计动作码翻成中文标签。
 * @param {string} action 例如 `device.command`。
 * @returns {string} 例如 `设备 · 下发指令`；认不出时原样返回。
 */
export function auditActionLabel(action) {
  if (!action) {
    return t('audit.unknownAction');
  }

  const dot = action.indexOf('.');
  if (dot <= 0) {
    return action;
  }

  const prefix = action.slice(0, dot);
  const rest = action.slice(dot + 1);
  const prefixLabel = PREFIX_LABELS[prefix] ? t(PREFIX_LABELS[prefix]) : '';
  if (!prefixLabel) {
    return action;
  }

  const verbLabel = VERB_LABELS[rest] ? t(VERB_LABELS[rest]) : '';

  // 前缀与动作同名时只显示一次（`admin.login` → 「登录 · 登录」这种重复很蠢，
  // 英文下更明显：Sign-in · Sign-in）。
  if (verbLabel && verbLabel === prefixLabel) {
    return prefixLabel;
  }

  // 只认得模块、不认得动作时，仍给出模块名 + 原动作，比全文原文好读、也不丢信息。
  return verbLabel ? `${prefixLabel} · ${verbLabel}` : `${prefixLabel} · ${rest}`;
}
