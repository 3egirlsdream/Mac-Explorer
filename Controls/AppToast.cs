using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using MacExplorer.Services.Impl;

namespace MacExplorer.Controls;

internal static class AppToast
{
    private static readonly ConditionalWeakTable<Window, WindowNotificationManager> Managers = new();

    internal static WindowNotificationManager For(Window window) => Managers.GetValue(window, owner =>
    {
        var manager = new WindowNotificationManager(owner)
        {
            Position = NotificationPosition.BottomRight,
            MaxItems = 3
        };
        manager.ApplyTemplate();
        return manager;
    });

    internal static void ShowError(Window window, string message) => For(window).Show(
        new Notification(LocalizationText.Get("操作失败"), message, NotificationType.Error, TimeSpan.FromSeconds(8)));
}
