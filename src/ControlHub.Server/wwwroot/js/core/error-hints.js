/**
 * 错误码 → 「怎么办」的映射（#51）。
 *
 * **本文件刻意不 import 任何模块**：`ui.js` 的兜底出口（`h()` 事件处理器、`modal` 的确认按钮、
 * `guard()`）也需要把「怎么办」拼进提示，而 `ui.js` 是最底层模块——如果映射表放在 `errors.js` 里，
 * 就会出现 `ui.js → errors.js → ui.js` 的循环依赖。
 *
 * 只依赖错误对象上的 `code` / `status` / `message` 三个字段（鸭子类型），
 * 因此也能用于 `ApiError` 之外的普通 `Error`。
 */

/**
 * 错误码 → 「怎么办」。措辞要求：说清**下一步动作与入口**，而不是把问题再复述一遍。
 * <para>与 `ControlHub.Protocol/HubProtocol.cs` 的 `HubErrorCodes` 对应；新增错误码时应同步这里。</para>
 */
const ERROR_HINTS = {
  // ── 认证 / 账号 ──
  AUTH_INVALID: '登录状态已失效，重新登录即可。',
  AUTH_REQUIRED: '这个操作需要登录，请先登录。',
  ACCOUNT_LOCKED: '连续登录失败次数过多，账号被临时锁定。稍后再试，或让管理员在「系统设置 → 账号管理」里处理。',

  // ── 权限 ──
  PERMISSION_DENIED:
    '当前账号没有这个操作需要的权限。请让超级管理员在「系统设置 → 账号管理」里为你的账号勾选对应权限，或改用有权限的账号。',

  // ── 设备 ──
  DEVICE_UNKNOWN: '这台设备没有注册过或已被移除。请在设备上重新注册，并确认注册码仍然有效。',
  DEVICE_REVOKED: '这台设备已被停用。到「设备管理」里重新启用它之后，再下发一次。',

  // ── 注册码 ──
  ENROLL_CODE_INVALID: '注册码不正确。注意区分大小写、不要带多余空格；也可以让管理员重新生成一个。',
  ENROLL_CODE_EXPIRED:
    '注册码已过期或可用次数已用完。让管理员生成新码，或在「设备管理 → 注册码」里调大它的次数 / 有效期。',

  // ── 通用 ──
  VALIDATION_FAILED: '提交的内容不符合要求，按上面的提示改一下再试。',
  NOT_FOUND: '目标不存在，可能刚被别人删掉了。刷新页面后重试。',
  CONFLICT: '与现有数据冲突（例如名称重复）。换个名称，或先处理掉冲突项再试。',
  RATE_LIMITED: '操作太频繁被暂时限制了，等一会儿再试。',
  IP_NOT_ALLOWED:
    '你当前的访问地址不在管理端的允许列表内。需要在服务端配置项 `ControlHub:AdminIpAllowList` 里把该地址加进去，或改用已在列表内的网络访问。',
  PROTOCOL_UNSUPPORTED:
    '服务端与页面的版本对不上。先刷新页面；仍然不行就确认服务端已升级到配套版本（两边必须同一版本）。',
  INTERNAL: '服务端内部出错。请查看服务端日志定位原因（数据目录同级的日志文件），刷新后可重试。',

  // ── 前端侧产生 ──
  NETWORK: '连不上服务器。确认服务端进程还在运行、网络可达；恢复后重试即可。',
  BAD_RESPONSE:
    '服务器返回了无法识别的响应，通常是版本不匹配或反向代理配置问题。可查看服务端日志，必要时重启服务。',

  // ── 教室端执行远程指令失败（#81，取值见 HubProtocol.CommandErrorCodes）──
  // 与上面那批的区别：这些**不是**「接口拒绝了请求」，而是「教室端为什么没做成」，
  // 所以措辞一律落到「到那台教室机上做什么」，而不是「改你的输入」。
  COMMAND_UNSUPPORTED:
    '教室端不认识这条指令，或其中的某项配置它不支持——多半是那台机器的插件 / ClassIsland 版本低于管理端。在教室机的插件市场里把接收端插件升到最新版，再下发一次。',
  COMMAND_PAYLOAD_INVALID:
    '发给教室端的内容不完整（缺了必需字段）。重新填一遍再下发；如果总是这样，先确认两端版本配套（管理端升级后插件要同步升级）。',
  COMMAND_TARGET_NOT_FOUND:
    '指令要操作的对象在教室机上不存在（插件可能已被卸载或改过名）。先点「刷新插件列表」看那边的实际情况，再重新选择。',
  HOST_API_UNAVAILABLE:
    '教室端的 ClassIsland 没提供这项能力：宿主版本偏低、接口签名不符，或服务还没就绪（插件刚启动）。确认那台机器的 ClassIsland 已升级并重启，过一会儿再试。',
  PLATFORM_UNSUPPORTED:
    '这台教室端的系统不支持该操作（例如电源管理只在 Windows 上可用）。改用受支持的操作，或到那台设备上手动处理。',
  COMMAND_DISABLED_BY_POLICY:
    '教室端自己关掉了这项能力（插件设置里的「远程协助与隐私」关了截图，或禁止卸载集控插件本身）。需要到那台教室机上把对应开关打开。',
  COMMAND_EXECUTION_FAILED:
    '教室端执行时报错了，具体原因见上面的原文。若原文是英文异常，多半出在那台机器的 ClassIsland 或系统环境上，按原文到教室里排查。',
  COMMAND_INTEGRITY_FAILED:
    '教室端取回的插件包与服务器的记录对不上（SHA256 不一致）——多半是 A 端缓存的文件被改过，或下载途中损坏（代理返回了错误页也会这样）。到「插件管理」对那个插件点「重新缓存」，再下发一次。',
};

/** 没有错误码时按 HTTP 状态兜底（例如反向代理直接返回的响应）。 */
const STATUS_HINTS = {
  401: ERROR_HINTS.AUTH_INVALID,
  403: ERROR_HINTS.PERMISSION_DENIED,
  404: ERROR_HINTS.NOT_FOUND,
  409: ERROR_HINTS.CONFLICT,
  429: ERROR_HINTS.RATE_LIMITED,
  500: ERROR_HINTS.INTERNAL,
  502: '服务端没有正常响应（网关错误）。确认服务端进程在运行、反向代理指向正确。',
  503: '服务暂时不可用，可能在重启或正在维护。稍后重试。',
  504: '服务端响应超时。网络可能不稳定，或服务端负载过高，稍后重试。',
};

/**
 * 解析错误对象，得到「原文 + 怎么办」。
 * @param {any} err 错误对象（ApiError 或普通 Error）。
 * @returns {{code:string, status:number, message:string, hint:string}}
 */
export function describeError(err) {
  const code = typeof err?.code === 'string' ? err.code : '';
  const status = Number(err?.status) || 0;
  const message = typeof err?.message === 'string' ? err.message.trim() : '';

  return {
    code,
    status,
    // 极端情况下可能连 message 都没有（例如被浏览器拦截的请求），给一句能看懂的话兜底；
    // NETWORK 这种 message 已经足够清楚的则保持为空，避免重复。
    message: message || (code === 'NETWORK' || status === 0 ? '' : '操作没有完成。'),
    hint: ERROR_HINTS[code] || STATUS_HINTS[status] || '',
  };
}

/**
 * 把「原文 + 怎么办」拼成一行提示正文。
 * @param {any} err 错误对象。
 * @param {string} [fallback] 两者都没有时的兜底文案。
 */
export function formatErrorText(err, fallback = '请稍后重试。') {
  const info = describeError(err);
  return [info.message, info.hint].filter(Boolean).join(' —— ') || fallback;
}

/** 是否带有「怎么办」建议（调用方据此决定提示停留时长）。 */
export function hasErrorHint(err) {
  return describeError(err).hint.length > 0;
}

/**
 * 按**裸错误码**取「怎么办」。
 * <para>给不是 `ApiError` 的场景用：教室端上报的**指令错误码**（`CommandErrorCodes`）只有
 * 一个码 + 一段原文，没有 HTTP 状态。没有对应条目时返回空串，调用方按「没有建议」处理，
 * 不要凭空编一句。</para>
 * @param {string} code 错误码。
 */
export function hintForCode(code) {
  return (typeof code === 'string' && ERROR_HINTS[code]) || '';
}
