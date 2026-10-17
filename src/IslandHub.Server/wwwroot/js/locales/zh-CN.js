/**
 * 简体中文文案（默认语言）。
 *
 * 说明（#48）：文案抽成资源是为了把「界面文字」与「代码」分开——改文案不用碰逻辑，
 * 也让以后的其它语言只是多一个文件。**占位符用 `{name}`**，缺键时回退到中文。
 */
export default {
  'lang.name': '简体中文',

  // 侧栏导航
  'nav.group.daily': '日常',
  'nav.group.teaching': '教学配置',
  'nav.group.devices': '设备',
  'nav.group.system': '系统',
  'nav.dashboard': '仪表盘',
  'nav.board': '大屏看板',
  'nav.deploy': '配置下发',
  'nav.remote': '远程管理',
  'nav.profiles': '配置档案',
  'nav.reminders': '定时提醒',
  'nav.examMode': '考试模式',
  'nav.profileSlots': '走班制',
  'nav.timesync': '时间偏移',
  'nav.lessons': '快捷换课',
  'nav.devices': '设备管理',
  'nav.plugins': '插件管理',
  'nav.reports': '报表',
  'nav.audit': '审计日志',
  'nav.approvals': '操作审批',
  'nav.appearance': '页面设置',
  'nav.accounts': '账号与权限',
  'nav.server': '服务器信息',
  'nav.integrations': '通知与集成',
  'nav.maintenance': '系统维护',

  // 顶栏与账号菜单
  'topbar.refresh': '刷新',
  'topbar.revision': '版本 {revision}',
  'topbar.changePassword': '修改密码',
  'topbar.logout': '退出登录',
  'topbar.language': '语言',

  // 登录页
  'login.heading': '欢迎回来',
  'login.subtitle': '登录以继续管理您的 IslandHub 服务',
  'login.username': '用户名',
  'login.usernamePlaceholder': '请输入用户名',
  'login.password': '密码',
  'login.passwordPlaceholder': '请输入密码',
  'login.submit': '登录',
  'login.showPassword': '显示密码',
  'login.connecting': '正在连接服务器…',

  // 大屏看板（#113）
  'board.title': '大屏看板',
  'board.total': '设备总数',
  'board.online': '在线',
  'board.offline': '离线',
  'board.pending': '待同步',
  'board.attention': '需要注意的教室（{count} 台）',
  'board.lessonsPanel': '教室实时课表（{count} 间）',
  'board.events': '最近事件',
  'board.allOnline': '全部在线，没有需要注意的教室。',
  'board.noLessons': '还没有教室上报课表状态（需要教室端插件 1.3.8+）。',
  'board.noEvents': '最近没有事件。',
  'board.noAuditPermission': '当前账号没有审计权限，看不到事件。',
  'board.loading': '正在载入看板数据…',
  'board.loadFailed': '载入失败：{message}',
  'board.uptime': 'v{version} · 已运行 {uptime}',
  'board.refreshHint': '每 {seconds} 秒自动刷新 · 最后刷新 {at}',
  'board.refreshNow': '立即刷新',
  'board.fullMode': '看板模式',
  'board.exitFullMode': '退出看板模式',
  'board.revoked': '{name}（已停用）',
  'board.neverOnline': '从未上线',
  'board.lastSeen': '最后在线 {at}',
  'board.inClass': '正在上 {subject}',
  'board.nextClass': '下一节 {subject} {time}',
  'board.weekOf': '第 {week}/{weeks} 周',
  'board.noLessonState': '还没上报课表状态',
  'board.breakTime': '课间',
};
