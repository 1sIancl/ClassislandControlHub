/**
 * 批量操作的影响摘要（#52）。
 *
 * 为什么必须有：批量操作一律 `includeOffline: false` —— **离线设备静默不执行**。
 * 确认框里只写「所选 12 台」，操作完就会有人问「我明明批量关机了，怎么一半教室还开着」。
 *
 * 所以这里把口径固定成三句话：**在线几台会执行、离线几台不会收到、已停用几台被跳过**。
 * 数字直接来自当前设备列表（`online` / `revoked` 字段），不额外请求接口——
 * 批量操作本来就基于用户刚看到的那份列表，摘要和界面对得上才可信。
 */

/**
 * 统计所选设备的口径。
 * @param {Array} devices 当前设备列表（需含 `online` / `revoked`）。
 * @param {string[]} ids 选中的设备 ID（可能是原始选择，含已停用的）。
 * @returns {{total:number, online:number, offline:number, revoked:number, missing:number}}
 */
export function summarizeSelection(devices, ids) {
  const wanted = new Set(ids);
  const picked = devices.filter((d) => wanted.has(d.id));

  const revoked = picked.filter((d) => d.revoked).length;
  const active = picked.filter((d) => !d.revoked);
  const online = active.filter((d) => d.online).length;

  return {
    total: ids.length,
    online,
    offline: active.length - online,
    revoked,
    // 列表里找不到的（刚被别处删掉等）单独计：不然「共 N 台」和后面的数字对不上，反而让人怀疑界面算错了。
    missing: ids.length - picked.length,
  };
}

/**
 * 生成确认框用的一句话摘要。
 * @param {Array} devices 设备列表。
 * @param {string[]} ids 选中的设备 ID。
 * @param {{offlineNote?:string}} [options] 覆盖「离线会怎样」的措辞（默认：不会收到）。
 * @returns {string} 例如「共 12 台；在线 8 台会立即执行；离线 4 台不会收到（批量操作不排队）。」
 */
export function selectionSummary(devices, ids, options = {}) {
  const s = summarizeSelection(devices, ids);
  const offlineNote = options.offlineNote || '不会收到（批量操作不排队）';

  const parts = [`共 ${s.total} 台`];
  if (s.online > 0) {
    parts.push(`在线 ${s.online} 台会立即执行`);
  }

  if (s.offline > 0) {
    parts.push(`离线 ${s.offline} 台${offlineNote}`);
  }

  if (s.revoked > 0) {
    parts.push(`已停用 ${s.revoked} 台会被跳过`);
  }

  if (s.missing > 0) {
    parts.push(`${s.missing} 台已不在列表中`);
  }

  return `${parts.join('；')}。`;
}
