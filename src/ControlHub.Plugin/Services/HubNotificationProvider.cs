using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Models.Notification;

namespace ControlHub.Plugin.Services;

/// <summary>
/// 集控提醒提供方：接收来自 A 端的提醒并通过 ClassIsland 展示。
/// </summary>
[NotificationProviderInfo("8f1c2a54-3e7b-4d19-9a62-2c5b7e0d4a81", "集控提醒",
    "lucide(\ue0ff)", "来自集控服务器（A 端）的远程提醒。")]
public sealed class HubNotificationProvider : NotificationProviderBase
{
    /// <summary>初始化并登记为当前实例，供远程指令执行器调用。</summary>
    public HubNotificationProvider()
    {
        HubNotificationProviderHolder.Current = this;
    }

    /// <summary>
    /// 展示一条集控提醒。
    /// </summary>
    /// <returns>
    /// 提交给 ClassIsland 的通知请求。调用方（远程指令执行器）拿它挂
    /// <see cref="NotificationRequest.Completed"/>——通知真正结束（超时或被关掉）
    /// 时才能回报「展示结束」，#7 的展示生命周期靠这个事件。
    /// </returns>
    public NotificationRequest Show(string title, string message, bool speak, TimeSpan? duration)
    {
        var text = string.IsNullOrWhiteSpace(title) ? message : $"{title}\n{message}";
        var mask = NotificationContent.CreateTwoIconsMask(text);
        var overlay = NotificationContent.CreateRollingTextContent(text, duration, 1);
        overlay.IsSpeechEnabled = speak;

        var request = new NotificationRequest
        {
            MaskContent = mask,
            OverlayContent = overlay,
        };
        if (duration is { } d)
        {
            mask.Duration = d;
        }

        ShowNotification(request);
        return request;
    }
}
