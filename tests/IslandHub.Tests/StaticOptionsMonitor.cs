using Microsoft.Extensions.Options;

namespace IslandHub.Tests;

/// <summary>
/// 测试用的「不会变的」<see cref="IOptionsMonitor{T}"/> 实现。
///
/// <para>
/// 背景（#87 配置热重载）：服务端把配置注入从 <c>IOptions</c> 换成了 <c>IOptionsMonitor</c>
/// （这样运行中改 appsettings 才能生效）。测试里构造这些服务时不需要真的热重载能力，
/// 给一个始终返回同一份配置的实现即可——比引入 <c>Microsoft.Extensions.Options.ConfigurationExtensions</c>
/// 搭一套配置源清爽得多，也不会让「到底测了什么」变得含糊。
/// </para>
/// </summary>
/// <typeparam name="T">选项类型。</typeparam>
public sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    /// <inheritdoc />
    public T CurrentValue { get; } = value;

    /// <inheritdoc />
    public T Get(string? name) => CurrentValue;

    /// <summary>不做变更通知（测试里配置不会变）。</summary>
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
