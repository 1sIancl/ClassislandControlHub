/**
 * English strings.
 *
 * #48: strings are collected here so the UI text can be changed (or translated) without touching logic.
 * Missing keys fall back to Simplified Chinese, so a partial translation never shows blank text.
 */
export default {
  'lang.name': 'English',

  // Sidebar navigation
  'nav.group.daily': 'Daily',
  'nav.group.teaching': 'Teaching',
  'nav.group.devices': 'Devices',
  'nav.group.system': 'System',
  'nav.dashboard': 'Dashboard',
  'nav.board': 'Wall Board',
  'nav.deploy': 'Deploy Config',
  'nav.remote': 'Remote',
  'nav.profiles': 'Profiles',
  'nav.reminders': 'Reminders',
  'nav.examMode': 'Exam Mode',
  'nav.profileSlots': 'Rotating Classes',
  'nav.timesync': 'Clock Offset',
  'nav.lessons': 'Lesson Swap',
  'nav.devices': 'Devices',
  'nav.plugins': 'Plugins',
  'nav.reports': 'Reports',
  'nav.audit': 'Audit Log',
  'nav.approvals': 'Approvals',
  'nav.appearance': 'Appearance',
  'nav.accounts': 'Accounts',
  'nav.server': 'Server Info',
  'nav.integrations': 'Notifications',
  'nav.maintenance': 'Maintenance',

  // Top bar and account menu
  'topbar.refresh': 'Refresh',
  'topbar.revision': 'Rev {revision}',
  'topbar.changePassword': 'Change password',
  'topbar.logout': 'Sign out',
  'topbar.language': 'Language',

  // Login page
  'login.heading': 'Welcome back',
  'login.subtitle': 'Sign in to manage your IslandHub deployment',
  'login.username': 'Username',
  'login.usernamePlaceholder': 'Enter your username',
  'login.password': 'Password',
  'login.passwordPlaceholder': 'Enter your password',
  'login.submit': 'Sign in',
  'login.showPassword': 'Show password',
  'login.connecting': 'Connecting to server…',

  // Wall board (#113)
  'board.title': 'Wall Board',
  'board.total': 'Devices',
  'board.online': 'Online',
  'board.offline': 'Offline',
  'board.pending': 'Pending',
  'board.attention': 'Needs attention ({count})',
  'board.lessonsPanel': 'Live lessons ({count})',
  'board.events': 'Recent events',
  'board.allOnline': 'All classrooms are online.',
  'board.noLessons': 'No classroom has reported lesson status yet (requires plugin 1.3.8+).',
  'board.noEvents': 'No recent events.',
  'board.noAuditPermission': 'Your account lacks the audit permission, so events are hidden.',
  'board.loading': 'Loading board data…',
  'board.loadFailed': 'Failed to load: {message}',
  'board.uptime': 'v{version} · up {uptime}',
  'board.refreshHint': 'Auto refresh every {seconds}s · last refresh {at}',
  'board.refreshNow': 'Refresh now',
  'board.fullMode': 'Full screen',
  'board.exitFullMode': 'Exit full screen',
  'board.revoked': '{name} (disabled)',
  'board.neverOnline': 'Never online',
  'board.lastSeen': 'Last seen {at}',
  'board.inClass': 'Now: {subject}',
  'board.nextClass': 'Next: {subject} {time}',
  'board.weekOf': 'Week {week}/{weeks}',
  'board.noLessonState': 'No lesson status reported yet',
  'board.breakTime': 'Break',
};
