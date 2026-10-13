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
    /// <param name="title">标题。</param>
    /// <param name="message">正文。</param>
    /// <param name="speak">是否语音播报。</param>
    /// <param name="duration">展示时长；<c>null</c> 表示用宿主默认。</param>
    /// <param name="emphasize">
    /// 是否强调展示（#22）：<c>true</c> = 全屏遮罩 + 提示音；<c>false</c> = 只出角落滚动条。
    /// </param>
    /// <returns>
    /// 提交给 ClassIsland 的通知请求。调用方（远程指令执行器）拿它挂
    /// <see cref="NotificationRequest.Completed"/>——通知真正结束（超时或被关掉）
    /// 时才能回报「展示结束」，#7 的展示生命周期靠这个事件。
    /// </returns>
    public NotificationRequest Show(string title, string message, bool speak, TimeSpan? duration, bool emphasize)
    {
        var text = string.IsNullOrWhiteSpace(title) ? message : $"{title}\n{message}";
        var overlay = NotificationContent.CreateRollingTextContent(text, duration, 1);
        overlay.IsSpeechEnabled = speak;

        var request = new NotificationRequest
        {
            // 温和通知（emphasize = false）只给滚动条：角落提示不挡讲台，内容照样看得到。
            OverlayContent = overlay,
        };

        if (emphasize)
        {
            var mask = NotificationContent.CreateTwoIconsMask(text);
            if (duration is { } d)
            {
                mask.Duration = d;
            }

            request.MaskContent = mask;

            // 提示音（#22）：`IsSettingsEnabled = true` 表示「按宿主设置来」（教室端的总开关
            // 因此仍然说话，我们不会绕过它）；其中**声音这一项按强调强行打开**——
            // 管理员标了强调就是要求它被听见，静音通知等于没发。
            request.RequestNotificationSettings.IsSettingsEnabled = true;
            request.RequestNotificationSettings.IsNotificationSoundEnabled = true;
        }

        ShowNotification(request);
        return request;
    }
}
