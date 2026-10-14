using System.Globalization;
using System.Text.RegularExpressions;

namespace ControlHub.Protocol;

/// <summary>
/// 提醒标题 / 内容里的模板变量（`#11` 的一部分）。
/// <para>让管理员能写「上课时间 {time}，{targets} 请查收」这类内容，而不是把时间/范围写死——
/// 同一个提醒每天触发时间相同，但「推送到哪」「这次几台」是每次都可能变的。</para>
/// <para>渲染发生在**发送那一刻**（<c>FireNotifyAsync</c>），不是在保存时：
/// 保存时渲染会把第一次的值固化进数据库，之后每次都发那个旧时间。</para>
/// <para>不认识的占位符**原样保留**：把 <c>{time}</c> 写成 <c>{tme}</c> 时，通知里还能看见
/// 那个写错的占位符，管理员一眼就能发现；静默删掉则要等现场问「这里怎么空了」。</para>
/// </summary>
public static class ReminderTemplate
{
    /// <summary>给界面用的一句话说明。</summary>
    public const string Hint =
        "可用变量：{time}（时刻）、{date}（日期）、{datetime}（日期时刻）、{targets}（推送到哪）、"
        + "{count}（本次台数）——都在**发送那一刻**替换。";

    /// <summary>
    /// 渲染模板。
    /// </summary>
    /// <param name="text">原始文本（可能为空）。</param>
    /// <param name="now">触发时刻（UTC，内部会转本地时间显示）。</param>
    /// <param name="targetLabel">推送到哪的描述（例如「教室「高一(1)班」」/「全部在线教室」）。</param>
    /// <param name="targetCount">本次实际推送台数。</param>
    public static string Render(string? text, DateTimeOffset now, string? targetLabel, int targetCount)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('{'))
        {
            return text ?? string.Empty;
        }

        var local = now.ToLocalTime();
        var result = text;

        // `{datetime}` 必须先于 `{date}` / `{time}` 处理（它更长，且不与之冲突，但顺序清楚更好读）。
        result = Replace(result, "datetime", local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        result = Replace(result, "date", local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        result = Replace(result, "time", local.ToString("HH:mm", CultureInfo.InvariantCulture));
        result = Replace(result, "targets",
            string.IsNullOrWhiteSpace(targetLabel) ? "全部教室" : targetLabel);
        result = Replace(result, "count", targetCount.ToString(CultureInfo.InvariantCulture));
        return result;
    }

    /// <summary>替换一个变量（大小写都认：老师不一定照文档的大小写写）。</summary>
    private static string Replace(string text, string name, string value) =>
        Regex.Replace(text, "\\{" + name + "\\}", _ => value, RegexOptions.IgnoreCase);
}
