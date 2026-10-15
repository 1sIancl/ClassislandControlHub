namespace ControlHub.Protocol;

/// <summary>
/// 分组（校区）授时偏移的解析规则（#26）。
/// <para>
/// 多校区场景下，不同楼栋 / 校区的大屏可能各自需要一点校正（例如某个校区整体慢 2 分钟）。
/// 规则：**在全局偏移之上叠加**。设备取自己所属分组配过的值；没配就沿上级分组上溯，
/// 取**最近**一个配过的分组（就近优先）；一路都没有则不叠加。
/// </para>
/// <para>
/// 这里是纯函数，放在 Protocol 里是为了能被单元测试直接钉住——「层级 + 就近 + 环」这三件事
/// 都是容易写错、错了又只表现为「时间差了但看不出为什么」的地方。
/// </para>
/// </summary>
public static class TimeGroupOffset
{
    /// <summary>取值范围的上下界（秒）。与全局偏移一致：±24 小时。</summary>
    public const double MaxSeconds = 86400;

    /// <summary>沿上级链上溯的深度上限。层级正常时只有两层，这个上限只用来兜住脏数据造成的环。</summary>
    public const int MaxDepth = 32;

    /// <summary>把偏移夹取到 ±24 小时（NaN 视为 0，避免把脏值原样发出去）。</summary>
    public static double Clamp(double seconds) =>
        double.IsNaN(seconds) ? 0 : Math.Clamp(seconds, -MaxSeconds, MaxSeconds);

    /// <summary>
    /// 解析某个分组应当叠加的偏移秒数：自身 → 上级 → 再上级，取最近一个配过的值。
    /// </summary>
    /// <param name="groupId">设备所属分组（可为空）。</param>
    /// <param name="offsets">已配置的分组偏移（分组 ID → 秒数）。</param>
    /// <param name="parents">分组层级（分组 ID → 上级 ID，空表示顶层）。</param>
    /// <returns>偏移秒数；没有可用的配置时返回 0（即只叠加全局偏移）。</returns>
    public static double Resolve(string? groupId, IReadOnlyDictionary<string, double> offsets,
        IReadOnlyDictionary<string, string?> parents)
    {
        var current = string.IsNullOrWhiteSpace(groupId) ? null : groupId.Trim();

        for (var depth = 0; current is not null && depth < MaxDepth; depth++)
        {
            if (offsets.TryGetValue(current, out var seconds))
            {
                return Clamp(seconds);
            }

            // 分组表里查不到（已被删除 / 层级数据不完整）时就此打住：宁可退化为全局偏移，
            // 也不要继续猜一个不存在的上级。
            if (!parents.TryGetValue(current, out var parent))
            {
                return 0;
            }

            current = string.IsNullOrWhiteSpace(parent) ? null : parent.Trim();
        }

        return 0;
    }
}
