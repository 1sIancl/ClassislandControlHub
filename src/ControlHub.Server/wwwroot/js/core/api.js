/**
 * 集控系统 API 客户端。
 * 统一处理鉴权令牌、响应封套（ApiResult）、错误抛出与会话失效。
 */

const TOKEN_KEY = 'controlhub.admin.token';
const API_PREFIX = '/api/v1';

/** 全局会话与运行时状态。 */
export const session = {
  token: localStorage.getItem(TOKEN_KEY) || '',
  me: null,
  serverInfo: null,
  revision: 0,
};

/** 会话失效（令牌过期）时触发的回调，由 app.js 注册。 */
let onSessionExpired = () => {};
export function setSessionExpiredHandler(handler) {
  onSessionExpired = handler;
}

/** 保存令牌。 */
export function saveToken(token) {
  session.token = token || '';
  if (token) {
    localStorage.setItem(TOKEN_KEY, token);
  } else {
    localStorage.removeItem(TOKEN_KEY);
  }
}

/** 协议错误。 */
export class ApiError extends Error {
  constructor(code, message, status) {
    super(message || '请求失败');
    this.name = 'ApiError';
    this.code = code || 'UNKNOWN';
    this.status = status || 0;
  }
}

/**
 * 发起一次 API 请求。
 * @param {string} path 以 `/` 开头的接口路径（不含 /api/v1 前缀）。
 * @param {{method?:string, body?:any, query?:object, auth?:boolean}} options 请求选项。
 * @returns {Promise<any>} 响应封套中的 data 字段。
 */
export async function api(path, options = {}) {
  const { method = 'GET', body, query, auth = true } = options;

  let url = API_PREFIX + path;
  if (query) {
    const params = new URLSearchParams();
    for (const [key, value] of Object.entries(query)) {
      if (value !== undefined && value !== null && value !== '') {
        params.append(key, value);
      }
    }
    const qs = params.toString();
    if (qs) {
      url += (url.includes('?') ? '&' : '?') + qs;
    }
  }

  const headers = {};
  if (body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }
  if (auth && session.token) {
    headers['Authorization'] = `Bearer ${session.token}`;
  }

  let response;
  try {
    response = await fetch(url, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch (networkError) {
    throw new ApiError('NETWORK', '无法连接到服务器，请检查网络或服务是否已启动。', 0);
  }

  let payload = null;
  const text = await response.text();
  if (text) {
    try {
      payload = JSON.parse(text);
    } catch {
      throw new ApiError('BAD_RESPONSE', `服务器返回了无法解析的内容（HTTP ${response.status}）。`, response.status);
    }
  }

  if (!payload || typeof payload.ok !== 'boolean') {
    if (response.status === 401) {
      handleExpired();
      throw new ApiError('AUTH_INVALID', '登录状态已失效，请重新登录。', 401);
    }
    throw new ApiError('BAD_RESPONSE', `服务器返回格式异常（HTTP ${response.status}）。`, response.status);
  }

  if (!payload.ok) {
    const err = payload.error || {};
    if (err.code === 'AUTH_INVALID' || err.code === 'AUTH_REQUIRED') {
      handleExpired();
    }
    throw new ApiError(err.code, err.message, response.status);
  }

  return payload.data;
}

function handleExpired() {
  saveToken('');
  session.me = null;
  onSessionExpired();
}

/** 读取服务器公开信息（无需登录）。 */
export function fetchServerInfo() {
  return api('/server/info', { auth: false });
}
