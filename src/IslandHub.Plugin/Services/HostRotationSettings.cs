using System.Collections;
using System.Globalization;
using ClassIsland.Services;
using ClassIsland.Shared;
using IslandHub.Protocol.Dtos;
using Microsoft.Extensions.Logging;

namespace IslandHub.Plugin.Services;

/// <summary>
/// 把档案里的「多周轮换」设置写进宿主 ClassIsland。
///
/// <para>
/// 课表自己带「周期 + 第几周」，但**哪一周算第 1 周**由宿主的三个设置决定：
/// <c>SingleWeekStartTime</c>（轮换起点 / 开学日期）、<c>MultiWeekRotationOffset</c>（按周期分开的手动偏移）、
/// <c>MultiWeekRotationMaxCycle</c>（最大轮换周期）。不把它们一起下发，一个学校几十间教室就会各按各的
/// 本地设置算周次——课表全对、日子全错。
/// </para>
///
/// <para>
/// <b>为什么全走反射</b>：CI 用 ClassIsland **发行包**作参考程序集，比本机源码树少成员，直写属性名会
/// CS1061 让「打包 .cipx」失败（项目里已踩过三次）。反射的另一个好处是老宿主上不会崩，
/// 只是如实回报「该版本不支持」。
/// </para>
///
/// <para>
/// <b>写一次原则</b>：这些设置落在宿主的 <c>Settings.json</c> 里，宿主自己也在写同一个文件，
/// 两个写入方撞车会让宿主把「文件被占用」当严重错误退出（2026-10-03 崩过）。因此只在**值真的不同**
/// 时才写，并在一次写入里合并全部改动。
/// </para>
/// </summary>
public static class HostRotationSettings
{
    /// <summary>应用结果。</summary>
    /// <param name="Changed">是否真的改动了宿主设置（没改动就不会触发落盘）。</param>
    /// <param name="Note">给管理员看的说明（写进设备的上报信息）。</param>
    public readonly record struct RotationApplyResult(bool Changed, string Note);

    /// <summary>
    /// 把轮换设置应用到宿主，返回一句「做了什么 / 为什么没做」的说明（不会抛异常）。
    /// </summary>
    public static string Apply(RotationSettingsDto? rotation, IReadOnlyCollection<ClassPlanDto> plans, ILogger logger)
    {
        // 先判「宿主在不在」：这一句不引用 ClassIsland 主程序集里的类型，所以在没有宿主的进程里
        // （验证台就是）也能安全执行。**不能**直接去 TryGetService<SettingsService>()——
        // 那会把主程序集的类型加载拖进来，无宿主环境下抛 FileNotFoundException，
        // 并顺着 ApplyCore 的 catch 变成「应用配置失败」（验证台实测踩到）。
        if (IAppHost.Host is null)
        {
            return " 轮换起点 / 偏移未下发：宿主尚未就绪，拿不到设置服务。";
        }

        return ApplyToHost(rotation, plans, logger);
    }

    /// <summary>真正读写宿主设置的部分。单独成方法，只有宿主可用时才被调用。</summary>
    private static string ApplyToHost(
        RotationSettingsDto? rotation, IReadOnlyCollection<ClassPlanDto> plans, ILogger logger)
    {
        var service = IAppHost.TryGetService<SettingsService>();
        if (service is null)
        {
            return " 轮换起点 / 偏移未下发：拿不到宿主的设置服务（IAppHost 未就绪）。";
        }

        RotationApplyResult result;
        try
        {
            result = ApplyTo(service.Settings, rotation, plans);
        }
        catch (Exception ex)
        {
            // 设置模型在各版本间有差异，这里绝不能把整次配置下发拖垮。
            logger.LogWarning(ex, "下发多周轮换设置失败。");
            return " 轮换起点 / 偏移下发失败：" + ex.Message;
        }

        if (result.Changed)
        {
            service.SaveSettings("IslandHub 配置下发");
            logger.LogInformation("已下发多周轮换设置：{Note}", result.Note);
        }

        return string.IsNullOrEmpty(result.Note) ? string.Empty : " " + result.Note;
    }

    /// <summary>
    /// 实际的写入逻辑（不改文件、不碰宿主的保存流程，便于在无宿主环境下验证）。
    /// </summary>
    /// <param name="settings">宿主的设置对象（真机上是 <c>SettingsService.Settings</c>）。</param>
    public static RotationApplyResult ApplyTo(
        object settings,
        RotationSettingsDto? rotation,
        IReadOnlyCollection<ClassPlanDto> plans)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(plans);

        var weeks = rotation?.Weeks ?? 1;
        // 课表自带的周期也要算进去：导入进来的课表可能带 3 周轮换，而档案级周期还没被设置过。
        var planCycle = plans.Where(p => p.WeekInterval >= 2).Select(p => p.WeekInterval).DefaultIfEmpty(0).Max();
        var cycle = Math.Max(weeks >= 2 ? weeks : 0, planCycle);

        var startText = rotation?.SemesterStartDate?.Trim();
        var hasStart = !string.IsNullOrEmpty(startText)
            && DateOnly.TryParse(startText, CultureInfo.InvariantCulture, out _);

        if (cycle < 2 && !hasStart)
        {
            // 这份档案没启用轮换：宿主的本地设置与它无关，一律不动。
            return new RotationApplyResult(false, string.Empty);
        }

        var changes = new List<string>();

        if (cycle >= 2)
        {
            // 宿主会把「周期比 MaxCycle 还长」的课表直接判为不生效（LessonsService.IsClassPlanEnabledAt），
            // 所以这里必须先把上限抬上去——不然 5 周以上的轮换在教室端是**静默失效**。
            var currentMax = TryGetProperty(settings, "MultiWeekRotationMaxCycle") as int? ?? 0;
            if (currentMax < cycle)
            {
                if (!TrySetProperty(settings, "MultiWeekRotationMaxCycle", cycle))
                {
                    return new RotationApplyResult(false,
                        $"轮换未生效：当前 ClassIsland 没有「最大轮换周期」设置（现为 {currentMax}），"
                        + $"{cycle} 周轮换的课表在教室端不会被启用。");
                }

                changes.Add($"最大轮换周期 {currentMax} → {cycle} 周");
            }

            if (!TrySetRotationOffset(settings, cycle, rotation?.Offset ?? 0, out var offsetNote,
                    out var offsetError))
            {
                return new RotationApplyResult(false, offsetError!);
            }

            if (offsetNote is not null)
            {
                changes.Add(offsetNote);
            }
        }

        if (hasStart && DateOnly.TryParse(startText, CultureInfo.InvariantCulture, out var startDate))
        {
            var target = startDate.ToDateTime(TimeOnly.MinValue);
            var current = TryGetProperty(settings, "SingleWeekStartTime") as DateTime?;
            if (current is null)
            {
                changes.Add("轮换起点未下发（当前 ClassIsland 没有该项设置，教室端仍按本地日期算周次）");
            }
            else if (current.Value.Date != target)
            {
                if (TrySetProperty(settings, "SingleWeekStartTime", target))
                {
                    changes.Add($"轮换起点 → {target:yyyy-MM-dd}");
                }
                else
                {
                    changes.Add("轮换起点未下发（该设置当前不可写）");
                }
            }
        }

        return changes.Count == 0
            ? new RotationApplyResult(false, string.Empty)
            : new RotationApplyResult(true, "轮换设置：" + string.Join("；", changes) + "。");
    }

    /// <summary>
    /// 写入「周期 = <paramref name="cycle"/>」的手动偏移。
    /// 宿主的 <c>MultiWeekRotationOffset</c> 是**下标为周期长度**的整数集合（2-first，<c>-1</c> 表示未设置），
    /// 默认只有 5 项，长周期要先补位。
    /// </summary>
    private static bool TrySetRotationOffset(
        object settings, int cycle, int offset, out string? note, out string? error)
    {
        note = null;
        error = null;

        var property = settings.GetType().GetProperty("MultiWeekRotationOffset");
        if (property?.GetValue(settings) is not IList list)
        {
            error = "轮换未生效：当前 ClassIsland 的多周轮换偏移不是可写的整数集合。";
            return false;
        }

        while (list.Count <= cycle)
        {
            // ⚠️ 补位必须填 **0**，不能填宿主的「未设置」哨兵值 -1：
            // 宿主对「超出列表长度」的档位取 `GetValueOrDefault(cycle)` = 0，即不偏移；
            // 填 -1 会让那些档位变成「偏移 -1 周」，等于悄悄改掉别的周期长度的进度。
            list.Add(0);
        }

        if (list[cycle] is int current && current == offset)
        {
            return true;
        }

        list[cycle] = offset;
        note = $"{cycle} 周轮换的手动偏移 → {offset} 周";
        return true;
    }

    /// <summary>
    /// 读出教室端「当前处于轮换周期的第几周」，供心跳上报（管理端设备详情直接显示）。
    /// </summary>
    /// <param name="cycle">当前生效档案的轮换周期（来自教师端实际用到的课表；&lt;2 表示不轮换）。</param>
    /// <param name="today">教室端的当前日期。</param>
    /// <returns><c>(第几周, 周期)</c>；不轮换或拿不到宿主设置时返回 <c>(0, 1)</c>。</returns>
    public static (int Week, int Weeks) ReadCurrentWeek(int cycle, DateOnly today)
    {
        if (cycle < 2 || IAppHost.Host is null)
        {
            return (0, 1);
        }

        return ReadCurrentWeekFromHost(cycle, today);
    }

    /// <summary>读宿主设置的部分（同 Apply：只有宿主可用时才会被调用）。</summary>
    private static (int Week, int Weeks) ReadCurrentWeekFromHost(int cycle, DateOnly today)
    {
        var settings = IAppHost.TryGetService<SettingsService>()?.Settings;
        if (settings is null || TryGetProperty(settings, "SingleWeekStartTime") is not DateTime start)
        {
            return (0, 1);
        }

        var offset = 0;
        if (settings.GetType().GetProperty("MultiWeekRotationOffset")?.GetValue(settings) is IList list
            && cycle >= 2 && cycle < list.Count && list[cycle] is int value)
        {
            offset = value;
        }

        return (ResolveWeek(today, DateOnly.FromDateTime(start), offset, cycle), cycle);
    }

    /// <summary>
    /// 复刻宿主的算法（<c>LessonsService.GetCyclePositionsByDate</c>）：从轮换起点按**整周**计数，
    /// 叠上手动偏移后对周期取模，返回 1 起的周次；不轮换时返回 0。
    /// <para>
    /// ⚠️ 必须用 <c>Math.Floor</c> 而不是整数除法：C# 的整数除法向零截断，起点还没到时
    /// （负数天数）会算出与宿主不同的周次。**两边算法不一致 = 管理端显示的周次与教室大屏对不上**。
    /// </para>
    /// </summary>
    public static int ResolveWeek(DateOnly today, DateOnly start, int offset, int cycle)
    {
        if (cycle < 2)
        {
            return 0;
        }

        var elapsedWeeks = (int)Math.Floor((today.DayNumber - start.DayNumber) / 7d);
        var position = (elapsedWeeks + offset) % cycle;
        if (position < 0)
        {
            position += cycle;
        }

        return position + 1;
    }

    private static object? TryGetProperty(object target, string name) =>
        target.GetType().GetProperty(name)?.GetValue(target);

    private static bool TrySetProperty(object target, string name, object value)
    {
        var property = target.GetType().GetProperty(name);
        if (property is null || !property.CanWrite)
        {
            return false;
        }

        try
        {
            property.SetValue(target, Convert.ChangeType(value, property.PropertyType,
                CultureInfo.InvariantCulture));
            return true;
        }
        catch (Exception)
        {
            // 类型对不上（例如宿主的字段换了类型）：当作「写不了」，由调用方如实回报。
            return false;
        }
    }
}
