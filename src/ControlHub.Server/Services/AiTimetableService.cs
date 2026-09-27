using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ControlHub.Protocol;
using ControlHub.Protocol.Dtos;
using ControlHub.Server.Data;

namespace ControlHub.Server.Services;

/// <summary>AI 辅助导入的模型配置，持久化在设置表的 <c>ai</c> 键下。</summary>
public sealed class AiConfig
{
    /// <summary>是否启用 AI 辅助导入。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>接口地址，可填到 <c>/v1</c> 或直接填完整的 <c>/chat/completions</c> 地址。</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>接口密钥。留空表示该接口无需鉴权（如本地 Ollama）。</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>模型名称。</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>单次请求超时（秒）。解析整周课表耗时较长，默认给足。</summary>
    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>配置是否完整到可以发起请求。</summary>
    public bool IsReady =>
        Enabled && !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);
}

/// <summary>
/// AI 辅助课表导入：把一段任意格式的课表文本交给兼容 OpenAI Chat Completions 的模型，
/// 解析成结构化的「作息时间表 + 科目 + 逐格课表」。
/// </summary>
public sealed class AiTimetableService(HubStore store, ILogger<AiTimetableService> logger)
{
    /// <summary>设置表中存放 AI 配置的键名。</summary>
    public const string SettingKey = "ai";

    /// <summary>单次解析允许的最大输入长度，避免把整本手册塞进提示词。</summary>
    private const int MaxInputLength = 20000;

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private const string SystemPrompt = """
        你是一名课表结构化助手。用户会给你一段课表文字（可能来自 Excel 粘贴、教务系统网页复制或手工整理，
        行列往往不整齐，也可能混有表头、班级名、上课时间等杂项）。

        请把它整理成如下 JSON 并【只输出这个 JSON】，不要输出解释、注释或 Markdown 代码块：

        {
          "periods": [ { "start": "08:00", "end": "08:45" } ],
          "days": [ { "day": 1, "courses": ["语文", "数学", ""] } ],
          "subjects": [ { "name": "语文", "simplified": "语", "teacher": "" } ]
        }

        字段说明：
        - periods：一天的作息，按时间先后排列，start/end 使用 24 小时制 HH:MM。若原文没有时间，
          按「08:00 开始、每节 45 分钟、课间 10 分钟」推算，保证依次递增且互不重叠。
        - days：day 取 1~7，依次表示周一至周日；courses 按节次顺序填科目名，
          长度必须与 periods 完全一致，空堂填空字符串 ""。
        - subjects：列出课表里出现过的全部科目，simplified 取 1 个汉字（如「信息技术」→「信」）。

        必须遵守：
        1. 不要编造原文没有的信息；教师姓名未知时留空字符串。
        2. 每个 day 的 courses 必须与 periods 逐节对齐，不多不少。
        3. 只输出 JSON 本身。
        """;

    /// <summary>读取 AI 配置。</summary>
    public async Task<AiConfig> LoadConfigAsync(CancellationToken cancellationToken = default)
    {
        var json = await store.GetSettingAsync(SettingKey, null, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AiConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<AiConfig>(json, HubJson.Create()) ?? new AiConfig();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "AI 配置无法解析，按默认值处理。");
            return new AiConfig();
        }
    }

    /// <summary>保存 AI 配置。</summary>
    public Task SaveConfigAsync(AiConfig config, CancellationToken cancellationToken = default) =>
        store.SetSettingAsync(SettingKey, JsonSerializer.Serialize(config, HubJson.Create()),
            cancellationToken);

    /// <summary>用当前配置做一次最小请求，验证地址、密钥与模型是否可用。</summary>
    public async Task<(bool Ok, string Message, long ElapsedMs)> TestAsync(
        CancellationToken cancellationToken = default)
    {
        var config = await LoadConfigAsync(cancellationToken);
        if (!config.IsReady)
        {
            return (false, "请先填写接口地址与模型名称。", 0);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var reply = await CompleteAsync(config,
                "你是连通性测试助手，收到任何内容都只回复两个字：正常。",
                "ping",
                cancellationToken);
            stopwatch.Stop();
            return (true, $"连接正常，模型回复：{Truncate(reply, 40)}", stopwatch.ElapsedMilliseconds);
        }
        catch (HubException ex)
        {
            stopwatch.Stop();
            return (false, ex.Message, stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>把课表文本解析成内容包；不写库，由调用方确认后再合并。</summary>
    public async Task<ContentBundleDto> ParseTimetableAsync(string text,
        CancellationToken cancellationToken = default)
    {
        var config = await LoadConfigAsync(cancellationToken);
        if (!config.Enabled)
        {
            throw HubException.Validation("AI 辅助导入未启用，请到「系统设置 → AI 辅助导入」中开启。");
        }

        if (!config.IsReady)
        {
            throw HubException.Validation("AI 辅助导入尚未配置，请到「系统设置 → AI 辅助导入」中填写接口地址与模型。");
        }

        var input = (text ?? string.Empty).Trim();
        if (input.Length == 0)
        {
            throw HubException.Validation("课表内容为空。");
        }

        if (input.Length > MaxInputLength)
        {
            throw HubException.Validation($"课表内容过长（{input.Length} 字符），请只保留课表本身（上限 {MaxInputLength} 字符）。");
        }

        var reply = await CompleteAsync(config, SystemPrompt, $"下面是课表原文：\n---\n{input}\n---",
            cancellationToken);

        var parsed = ReadResponse(reply);
        return BuildContent(parsed);
    }

    // ────────────────────────────── 调用模型 ──────────────────────────────

    private async Task<string> CompleteAsync(AiConfig config, string system, string user,
        CancellationToken cancellationToken)
    {
        // 部分兼容实现不认 response_format，遇到 4xx 时去掉该字段重试一次。
        var payload = BuildPayload(config, system, user, jsonMode: true);
        var (status, body) = await SendAsync(config, payload, cancellationToken);
        if (status is >= 400 and < 500 && status is not 429)
        {
            payload = BuildPayload(config, system, user, jsonMode: false);
            (status, body) = await SendAsync(config, payload, cancellationToken);
        }

        if (status is < 200 or >= 300)
        {
            throw new HubException(HubErrorCodes.Internal,
                $"AI 接口返回 HTTP {status}。", 502, Truncate(body, 500));
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var choices = Find(document.RootElement, "choices");
            var first = choices is { ValueKind: JsonValueKind.Array } c ? c.EnumerateArray().FirstOrDefault() : default;
            var content = first.ValueKind == JsonValueKind.Object
                ? AsText(Find(first, "message") is { } message ? Find(message, "content") : null)
                : null;

            if (string.IsNullOrWhiteSpace(content))
            {
                throw new HubException(HubErrorCodes.Internal, "AI 接口没有返回内容。", 502, Truncate(body, 500));
            }

            return content;
        }
        catch (JsonException ex)
        {
            throw new HubException(HubErrorCodes.Internal, "无法解析 AI 接口的响应。", 502,
                $"{ex.Message}；原始响应：{Truncate(body, 300)}");
        }
    }

    private static string BuildPayload(AiConfig config, string system, string user, bool jsonMode)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = config.Model,
            ["temperature"] = 0,
            ["messages"] = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
        };

        if (jsonMode)
        {
            payload["response_format"] = new { type = "json_object" };
        }

        return JsonSerializer.Serialize(payload);
    }

    private async Task<(int Status, string Body)> SendAsync(AiConfig config, string payload,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 10, 900));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, ResolveEndpoint(config.BaseUrl))
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {config.ApiKey.Trim()}");
        }

        try
        {
            using var response = await Http.SendAsync(request, cts.Token);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(cts.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HubException(HubErrorCodes.Internal,
                $"AI 接口超时（{timeout.TotalSeconds:F0} 秒）。可在系统设置中调大超时，或换用更快的模型。", 504);
        }
        catch (HttpRequestException ex)
        {
            throw new HubException(HubErrorCodes.Internal, "无法连接 AI 接口。", 502, ex.Message);
        }
    }

    /// <summary>补全接口地址：填到 /v1 或直接填完整的 /chat/completions 都可以。</summary>
    private static string ResolveEndpoint(string baseUrl)
    {
        var url = baseUrl.Trim().TrimEnd('/');
        return url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? url
            : url + "/chat/completions";
    }

    // ────────────────────────────── 解析模型输出 ──────────────────────────────

    private sealed class ParsedTimetable
    {
        public List<TimetableBuilder.Slot> Slots { get; init; } = [];
        public List<TimetableBuilder.DayCourses> Days { get; init; } = [];
        public List<TimetableBuilder.SubjectSpec> Subjects { get; init; } = [];
    }

    private ParsedTimetable ReadResponse(string reply)
    {
        var json = ExtractJson(reply);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new HubException(HubErrorCodes.Internal, "AI 返回的内容不是合法 JSON，请重试或换用能力更强的模型。",
                502, $"{ex.Message}；原始内容：{Truncate(reply, 300)}");
        }

        using (document)
        {
            var root = document.RootElement;
            return new ParsedTimetable
            {
                Slots = ReadSlots(root),
                Days = ReadDays(root),
                Subjects = ReadSubjects(root),
            };
        }
    }

    private static string ExtractJson(string reply)
    {
        var text = reply.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = text.IndexOf('\n');
            var fence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && fence > firstLine)
            {
                text = text[(firstLine + 1)..fence];
            }
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new HubException(HubErrorCodes.Internal, "AI 返回的内容里没有找到 JSON。", 502,
                Truncate(reply, 300));
        }

        return text[start..(end + 1)];
    }

    private static List<TimetableBuilder.Slot> ReadSlots(JsonElement root)
    {
        var slots = new List<TimetableBuilder.Slot>();
        if (Find(root, "periods", "slots", "times", "sections") is not { ValueKind: JsonValueKind.Array } array)
        {
            return slots;
        }

        foreach (var element in array.EnumerateArray())
        {
            var (rawStart, rawEnd) = element.ValueKind == JsonValueKind.Array
                ? ReadPair(element)
                : (AsText(Find(element, "start", "startTime", "start_time", "from")),
                    AsText(Find(element, "end", "endTime", "end_time", "to")));

            var start = TimetableBuilder.NormalizeTime(rawStart)
                        ?? (slots.Count > 0 ? slots[^1].End : "08:00:00");

            var end = TimetableBuilder.NormalizeTime(rawEnd);
            if (end is null || string.CompareOrdinal(end, start) <= 0)
            {
                end = TimetableBuilder.AddMinutes(start, 45);
            }

            slots.Add(new TimetableBuilder.Slot(start, end));
        }

        return slots;
    }

    private static (string? Start, string? End) ReadPair(JsonElement array)
    {
        var items = array.EnumerateArray().ToArray();
        return (items.Length > 0 ? AsText(items[0]) : null, items.Length > 1 ? AsText(items[1]) : null);
    }

    private static List<TimetableBuilder.DayCourses> ReadDays(JsonElement root)
    {
        var days = new List<TimetableBuilder.DayCourses>();
        if (Find(root, "days", "schedules", "week") is not { ValueKind: JsonValueKind.Array } array)
        {
            return days;
        }

        foreach (var element in array.EnumerateArray())
        {
            var day = ReadDay(element);
            if (day is null)
            {
                continue;
            }

            var courses = new List<string?>();
            if (Find(element, "courses", "lessons", "classes", "subjects", "periods")
                is { ValueKind: JsonValueKind.Array } list)
            {
                courses.AddRange(list.EnumerateArray().Select(ReadCourse));
            }

            days.Add(new TimetableBuilder.DayCourses(day.Value, courses));
        }

        return days;
    }

    private static string? ReadCourse(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Object => AsText(Find(element, "subject", "name", "course", "title")),
        _ => null,
    };

    private static int? ReadDay(JsonElement element)
    {
        var text = AsText(Find(element, "day", "weekday", "dayOfWeek", "day_of_week"))?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (int.TryParse(text, out var number))
        {
            return number == 0 ? 7 : number is >= 1 and <= 7 ? number : null;
        }

        return text.ToLowerInvariant() switch
        {
            "mon" or "monday" or "周一" or "星期一" => 1,
            "tue" or "tuesday" or "周二" or "星期二" => 2,
            "wed" or "wednesday" or "周三" or "星期三" => 3,
            "thu" or "thursday" or "周四" or "星期四" => 4,
            "fri" or "friday" or "周五" or "星期五" => 5,
            "sat" or "saturday" or "周六" or "星期六" => 6,
            "sun" or "sunday" or "周日" or "周天" or "星期日" or "星期天" => 7,
            "一" => 1,
            "二" => 2,
            "三" => 3,
            "四" => 4,
            "五" => 5,
            "六" => 6,
            "日" or "天" => 7,
            _ => null,
        };
    }

    private static List<TimetableBuilder.SubjectSpec> ReadSubjects(JsonElement root)
    {
        var subjects = new List<TimetableBuilder.SubjectSpec>();
        if (Find(root, "subjects", "courses") is not { ValueKind: JsonValueKind.Array } array)
        {
            return subjects;
        }

        foreach (var element in array.EnumerateArray())
        {
            var name = element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : AsText(Find(element, "name", "subject", "title"));
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var simplified = element.ValueKind == JsonValueKind.Object
                ? AsText(Find(element, "simplified", "simplifiedName", "simplified_name", "initial", "short"))
                : null;
            var teacher = element.ValueKind == JsonValueKind.Object
                ? AsText(Find(element, "teacher", "teacherName", "teacher_name"))
                : null;

            subjects.Add(new TimetableBuilder.SubjectSpec(name.Trim(), simplified, teacher));
        }

        return subjects;
    }

    // ────────────────────────────── 组装内容包 ──────────────────────────────

    private ContentBundleDto BuildContent(ParsedTimetable parsed)
    {
        var slots = parsed.Slots;

        // 模型没给作息时退化成标准的 8 节作息，保证课表还能排下去。
        if (slots.Count == 0)
        {
            var cursor = "08:00:00";
            for (var i = 0; i < 8; i++)
            {
                var end = TimetableBuilder.AddMinutes(cursor, 45);
                slots.Add(new TimetableBuilder.Slot(cursor, end));
                cursor = TimetableBuilder.AddMinutes(end, 10);
            }
        }

        var days = new List<TimetableBuilder.DayCourses>();
        var seenDays = new HashSet<int>();
        foreach (var day in parsed.Days)
        {
            if (!seenDays.Add(day.Day))
            {
                continue;
            }

            // 课程数按作息对齐：少了补空堂，多了截断。
            var courses = new List<string?>(day.Courses.Take(slots.Count));
            while (courses.Count < slots.Count)
            {
                courses.Add(null);
            }

            days.Add(new TimetableBuilder.DayCourses(day.Day, courses));
        }

        if (days.Count == 0)
        {
            throw new HubException(HubErrorCodes.ValidationFailed,
                "AI 没能从这段内容里识别出任何一天的课程，请检查文本是否为课表，或换用能力更强的模型。");
        }

        logger.LogInformation("AI 导入解析完成：作息 {Slots} 节、{Days} 天、{Subjects} 个科目。",
            slots.Count, days.Count, parsed.Subjects.Count);

        return TimetableBuilder.BuildBundle("AI 导入作息", slots, days, parsed.Subjects);
    }

    // ────────────────────────────── JSON 读取辅助 ──────────────────────────────

    private static JsonElement? Find(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value;
                }
            }
        }

        return null;
    }

    private static string? AsText(JsonElement? element) => element is null
        ? null
        : element.Value.ValueKind switch
        {
            JsonValueKind.String => element.Value.GetString(),
            JsonValueKind.Number => element.Value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };

    private static string Truncate(string? text, int max) =>
        string.IsNullOrEmpty(text) || text.Length <= max ? text ?? string.Empty : text[..max] + "…";
}
