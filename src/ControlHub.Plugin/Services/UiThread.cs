using Avalonia.Threading;

namespace ControlHub.Plugin.Services;

/// <summary>
/// 把操作切到 Avalonia 的 UI 线程执行。
/// <para><b>为什么必须有它</b>：远程指令是在同步循环的**后台线程**上执行的，而其中不少操作最终会碰 UI 对象——
/// 外观（主题/资源）、提醒（通知宿主）、截图（渲染主窗口）、以及写 ClassIsland 档案（课表控件订阅了档案对象）。
/// Avalonia 规定 UI 对象只能由 UI 线程访问，否则会抛：</para>
/// <code>
/// System.InvalidOperationException: The calling thread cannot access this object
/// because a different thread owns it.
///    at Avalonia.Threading.Dispatcher.VerifyAccess()
/// </code>
/// <para>已经在 UI 线程时直接执行，避免多余调度与潜在死锁。</para>
/// </summary>
public static class UiThread
{
    /// <summary>同步执行：需要拿到返回值、且调用方本来就是同步方法时用它。</summary>
    public static T Run<T>(Func<T> action) =>
        Dispatcher.UIThread.CheckAccess() ? action() : Dispatcher.UIThread.Invoke(action);

    /// <summary>执行无返回值的同步操作。</summary>
    public static void Run(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Invoke(action);
        }
    }

    /// <summary>
    /// 异步执行：适合内部还有 I/O 的操作（例如截图后上传）。
    /// <para>注意：整个委托都会在 UI 线程上运行，因此**不要**把耗时的网络请求整段塞进来——
    /// 只把「必须碰 UI 的那一小段」放进来。</para>
    /// </summary>
    public static async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return await action();
        }

        // Avalonia 的 InvokeAsync(Func<Task<T>>) 会自己展平 Task，await 之后直接就是 T。
        return await Dispatcher.UIThread.InvokeAsync(action);
    }

    /// <summary>异步执行无返回值的操作。</summary>
    public static async Task RunAsync(Func<Task> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            await action();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(action);
    }
}
