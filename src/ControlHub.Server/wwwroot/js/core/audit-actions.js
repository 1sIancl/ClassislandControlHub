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
  device: '设备',
  group: '分组',
  profile: '配置档案',
  account: '账号',
  registration: '自助注册',
  enrollcode: '注册码',
  webhook: 'Webhook',
  reminder: '定时提醒',
  'notice-template': '通知模板',
  settings: '系统设置',
  backup: '备份',
  timetable: '临时换课',
  apikey: 'API 密钥',
  totp: '两步验证',
  audit: '审计日志',
  tag: '标签',
  template: '课表模板',
  // 插件管理（#121）：同步索引 / 缓存包 / 批量下发。
  plugin: '插件',
  admin: '登录',
};

/** 动作码其余部分 → 动词。多段动作（如 `command.broadcast`）整体作为键。 */
const VERB_LABELS = {
  create: '创建',
  update: '修改',
  delete: '删除',
  clear: '清空',
  test: '测试',
  push: '下发',
  run: '手动触发',
  cancel: '取消',
  restore: '恢复',
  login: '登录',
  logout: '退出登录',
  export: '导出',
  import: '导入',
  setdefault: '设为默认',
  duplicate: '复制',
  // 拼出来是「标签变更」；用「分配」或「设置」都会读出别扭的语序。
  assign: '变更',
  approve: '批准',
  reject: '拒绝',
  request: '申请',
  switch: '开关变更',
  command: '下发指令',
  'command.broadcast': '广播指令',
  'command.cancel': '取消指令',
  'diagnostics.clear': '清空诊断记录',
  appearance: '下发外观',
  notify: '发送提醒',
  'plugin.refresh': '刷新插件列表',
  'import-cses': '导入 CSES 课表',
  'version.delete': '删除历史版本',
  'deliveries.clear': '清空投递记录',
  // 插件管理（#121）。前缀是 plugin，这里填的是动作码去掉前缀后的部分。
  refresh: '刷新插件列表',
  cache: '缓存插件包',
  'cache.delete': '删除插件包缓存',
  deploy: '下发插件',
  'market.sync': '同步市场索引',
  'market.settings': '修改市场设置',
  'inventory.refresh': '刷新已装清单',
  // 考试模式（#21）：临时切档案 + 还原，事后要看得出「谁在什么时候开的、涉及哪些教室」。
  'exam.start': '开启考试模式',
  'exam.end': '结束考试模式',
  // 配置同步（#17 / #79）。这些码一直存在（`device.sync.success` 等），但此前没登记，
  // 审计页显示的是「设备 · sync.success」这种半英文。
  'sync.pull': '拉取配置',
  'sync.success': '应用配置',
  'sync.partial': '应用配置（部分成功）',
  'sync.failed': '应用配置失败',
  'sync.retry': '自动重推配置',
};

/**
 * 把审计动作码翻成中文标签。
 * @param {string} action 例如 `device.command`。
 * @returns {string} 例如 `设备 · 下发指令`；认不出时原样返回。
 */
export function auditActionLabel(action) {
  if (!action) {
    return '（未知动作）';
  }

  const dot = action.indexOf('.');
  if (dot <= 0) {
    return action;
  }

  const prefix = action.slice(0, dot);
  const rest = action.slice(dot + 1);
  const prefixLabel = PREFIX_LABELS[prefix];
  if (!prefixLabel) {
    return action;
  }

  const verbLabel = VERB_LABELS[rest];
  // 只认得模块、不认得动作时，仍给出模块名 + 原动作，比全文原文好读、也不丢信息。
  return verbLabel ? `${prefixLabel} · ${verbLabel}` : `${prefixLabel} · ${rest}`;
}
