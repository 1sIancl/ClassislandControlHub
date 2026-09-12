namespace ControlHub.Server.Services;

/// <summary>
/// 变更广播器。
/// <para>
/// 客户端通过长轮询接口挂起等待，服务端在配置版本递增或发生定向推送时唤醒等待者，
/// 从而做到「管理员一保存，客户端秒级拿到新配置」，同时避免客户端高频轮询。
/// </para>
/// <para>
/// 等待条件是一个委托而非固定版本号，这样同一个机制既能表达
/// 「全局配置版本变化」，也能表达「本设备的推送世代变化」。
/// </para>
/// </summary>
public sealed class RevisionNotifier
{
    private readonly object _gate = new();
    private TaskCompletionSource _signal = NewSignal();
    private long _revision;

    /// <summary>创建广播器并同步一次当前版本号。</summary>
    public RevisionNotifier(long initialRevision)
    {
        _revision = initialRevision;
    }

    /// <summary>当前配置版本号。</summary>
    public long Current
    {
        get
        {
            lock (_gate)
            {
                return _revision;
            }
        }
    }

    /// <summary>
    /// 唤醒全部等待者。
    /// </summary>
    /// <param name="revision">新的配置版本号。传 <c>null</c> 表示版本号未变（定向推送场景），仅唤醒等待者重新判定条件。</param>
    public void Publish(long? revision = null)
    {
        TaskCompletionSource previous;
        lock (_gate)
        {
            if (revision.HasValue)
            {
                _revision = revision.Value;
            }

            previous = _signal;
            _signal = NewSignal();
        }

        previous.TrySetResult();
    }

    /// <summary>
    /// 等待条件成立。
    /// </summary>
    /// <param name="condition">判定委托。返回 <c>true</c> 表示可以结束等待。</param>
    /// <param name="timeout">最长等待时长。</param>
    /// <param name="cancellationToken">调用方取消令牌（客户端断开时触发）。</param>
    /// <returns>条件成立返回 <c>true</c>；超时返回 <c>false</c>。</returns>
    public async Task<bool> WaitAsync(Func<CancellationToken, Task<bool>> condition, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var token = timeoutCts.Token;

        while (true)
        {
            // 先取信号再判定条件：若在此期间发生广播，信号已完成，下一次等待会立即返回，
            // 不会错过变更，也不会空等到超时。
            Task signal;
            lock (_gate)
            {
                signal = _signal.Task;
            }

            if (await SafeCheckAsync(condition, token, cancellationToken))
            {
                return true;
            }

            try
            {
                await signal.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                // 区分「超时」与「调用方主动取消」：超时属于正常返回路径。
                cancellationToken.ThrowIfCancellationRequested();
                return await SafeCheckAsync(condition, CancellationToken.None, cancellationToken);
            }
        }
    }

    private static async Task<bool> SafeCheckAsync(Func<CancellationToken, Task<bool>> condition,
        CancellationToken token, CancellationToken outerToken)
    {
        if (token.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            return await condition(token);
        }
        catch (OperationCanceledException)
        {
            outerToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
