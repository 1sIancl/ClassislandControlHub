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
 * <para>与 `IslandHub.Protocol/HubProtocol.cs` 的 `HubErrorCodes` 对应；新增错误码时应同步这里。</para>
 */
// 文案资源（#48）：`i18n.js` **不依赖任何模块**（只 import 两个语言包），所以这里引它
// 不会造成 `ui.js → error-hints.js → ui.js` 那种循环——文件头那句「刻意不 import」
// 针对的是 ui.js / errors.js 这类会回指本文件的模块。
import { t } from './i18n.js?v=163';

const ERROR_HINTS = {
  // ── 认证 / 账号 ──
  AUTH_INVALID: t('hint.unauthorized'),
  AUTH_REQUIRED: t('hint.loginRequired'),
  ACCOUNT_LOCKED: t('hint.locked'),

  // ── 权限 ──
  PERMISSION_DENIED:
    t('hint.forbidden'),

  // ── 设备 ──
  DEVICE_UNKNOWN: t('hint.deviceUnknown'),
  DEVICE_REVOKED: t('hint.deviceRevoked'),

  // ── 注册码 ──
  ENROLL_CODE_INVALID: t('hint.enrollCodeInvalid'),
  ENROLL_CODE_EXPIRED:
    t('hint.enrollCodeUsed'),

  // ── 通用 ──
  VALIDATION_FAILED: t('hint.validation'),
  NOT_FOUND: t('hint.notFound'),
  CONFLICT: t('hint.conflict'),
  RATE_LIMITED: t('hint.rateLimited'),
  IP_NOT_ALLOWED:
    t('hint.ipBlocked'),
  PROTOCOL_UNSUPPORTED:
    t('hint.versionMismatch'),
  INTERNAL: t('hint.internal'),

  // ── 前端侧产生 ──
  NETWORK: t('hint.network'),
  BAD_RESPONSE:
    t('hint.badResponse'),

  // ── 教室端执行远程指令失败（#81，取值见 HubProtocol.CommandErrorCodes）──
  // 与上面那批的区别：这些**不是**「接口拒绝了请求」，而是「教室端为什么没做成」，
  // 所以措辞一律落到「到那台教室机上做什么」，而不是「改你的输入」。
  COMMAND_UNSUPPORTED:
    t('hint.cmdUnknown'),
  COMMAND_PAYLOAD_INVALID:
    t('hint.cmdPayload'),
  COMMAND_TARGET_NOT_FOUND:
    t('hint.cmdTarget'),
  HOST_API_UNAVAILABLE:
    t('hint.cmdUnsupported'),
  PLATFORM_UNSUPPORTED:
    t('hint.cmdOsUnsupported'),
  COMMAND_DISABLED_BY_POLICY:
    t('hint.cmdDisabled'),
  COMMAND_EXECUTION_FAILED:
    t('hint.cmdFailed'),
  COMMAND_INTEGRITY_FAILED:
    t('hint.cmdPackageMismatch'),
};

/** 没有错误码时按 HTTP 状态兜底（例如反向代理直接返回的响应）。 */
const STATUS_HINTS = {
  401: ERROR_HINTS.AUTH_INVALID,
  403: ERROR_HINTS.PERMISSION_DENIED,
  404: ERROR_HINTS.NOT_FOUND,
  409: ERROR_HINTS.CONFLICT,
  429: ERROR_HINTS.RATE_LIMITED,
  500: ERROR_HINTS.INTERNAL,
  502: t('hint.badGateway'),
  503: t('hint.unavailable'),
  504: t('hint.timeout'),
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
    message: message || (code === 'NETWORK' || status === 0 ? '' : t('hint.failed')),
    hint: ERROR_HINTS[code] || STATUS_HINTS[status] || '',
  };
}

/**
 * 把「原文 + 怎么办」拼成一行提示正文。
 * @param {any} err 错误对象。
 * @param {string} [fallback] 两者都没有时的兜底文案。
 */
export function formatErrorText(err, fallback = t('hint.retryLater')) {
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
