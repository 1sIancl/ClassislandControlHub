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
  // 下面这些是「凑齐 104 个动作码、一个不漏」时补的：少了它们，审计页会出现
  // 「exam.start」「device.enroll」这种整串英文（前缀认不出来就整串退回原文）。
  exam: '考试模式',
  'profile-slots': '走班制',
  'register-code': '注册码',
  update: '程序更新',
  config: '配置',
  database: '数据库',
  branding: '品牌',
  ai: 'AI',
  lesson: '换课',
  timeoffset: '授时',
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
  // ── 补齐剩下的动作码（`_shot/audit-labels-report.js` 会把「渲染后仍含英文」的全列出来）──
  // 登录 / 密码（前缀 admin）
  'login.failed': '登录失败',
  'login.locked': '账号被锁定',
  'login.totp': '两步验证登录',
  'login.totp.failed': '两步验证失败',
  'password.changed': '修改密码',
  'password.expiring': '密码即将到期',
  'password.reset': '重置密码',
  seed: '初始化管理员',
  // 自助注册 / 注册码
  register: '注册',
  'register.approve': '批准注册申请',
  'register.reject': '拒绝注册申请',
  'register.request': '提交注册申请',
  'register.delete': '删除注册申请',
  'register.failed': '注册失败',
  'approval.switch': '开关变更',
  // 设备
  enroll: '注册接入',
  wake: '网络唤醒',
  'command.result': '指令执行结果',
  'export.csv': '导出 CSV',
  'import.csv': '导入 CSV',
  // 档案 / 课表 / 模板
  apply: '套用',
  'ai-import': 'AI 导入',
  'override.create': '新建临时换课',
  'override.delete': '删除临时换课',
  swap: '对调',
  adjust: '微调',
  // 其他
  config: '配置',
  start: '开启',
  end: '结束',
  updated: '修改',
  check: '检查',
  vacuum: '清理空间',
  'image.upload': '上传图片',
  disable: '关闭',
  enable: '启用',
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
