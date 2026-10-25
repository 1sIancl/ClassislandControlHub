namespace IslandHub.Protocol.Dtos;

/// <summary>
/// 首次使用引导的状态（#126）。
/// <para>
/// 这是**实例级**状态（存服务端 settings 表），不是账号级：一个学校部署一次，
/// 第一个人看完（或跳过）之后，后面的人进来就该直接看到管理台。
/// </para>
/// </summary>
public sealed class WelcomeStatusDto
{
    /// <summary>是否已看完（或跳过）。</summary>
    public bool Completed { get; set; }
}
