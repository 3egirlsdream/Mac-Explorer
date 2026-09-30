using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.Controls;

public sealed class ShortcutTip : AvaloniaObject
{
    public static readonly AttachedProperty<string?> CommandProperty =
        AvaloniaProperty.RegisterAttached<ShortcutTip, Control, string?>("Command");
    private static readonly AttachedProperty<Subscription?> SubscriptionProperty =
        AvaloniaProperty.RegisterAttached<ShortcutTip, Control, Subscription?>("Subscription");
    public static string? GetCommand(Control control) => control.GetValue(CommandProperty);
    public static void SetCommand(Control control, string? value) => control.SetValue(CommandProperty, value);

    static ShortcutTip() => CommandProperty.Changed.AddClassHandler<Control>((control, change) =>
    {
        control.GetValue(SubscriptionProperty)?.Dispose();
        control.SetValue(SubscriptionProperty, change.GetNewValue<string?>() == null ? null : new Subscription(control));
    });

    private sealed class Subscription : IDisposable
    {
        private readonly Control _control;
        private IShortcutService? _service;
        public Subscription(Control control)
        {
            _control = control;
            control.AttachedToVisualTree += Attach;
            control.DetachedFromVisualTree += Detach;
            Update();
            if (control.IsAttachedToVisualTree()) Attach(null, null!);
        }
        private void Attach(object? sender, VisualTreeAttachmentEventArgs e)
        {
            if (_service != null) _service.Changed -= Update;
            _service = (TopLevel.GetTopLevel(_control) as AppWindow)?.Shortcuts ?? ShortcutService.Resolve();
            _service.Changed += Update;
            Update();
        }
        private void Detach(object? sender, VisualTreeAttachmentEventArgs e)
        {
            if (_service != null) _service.Changed -= Update;
            _service = null;
        }
        private void Update()
        {
            if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(Update); return; }
            if (GetCommand(_control) is not { } id) return;
            var service = _service ?? ShortcutService.Resolve();
            var label = AutomationProperties.GetName(_control);
            ToolTip.SetTip(_control, $"{(string.IsNullOrEmpty(label) ? service.Definitions.First(d => d.Id == id).Name : label)} {service.GetDisplay(id)}");
        }
        public void Dispose()
        {
            Detach(null, null!);
            _control.AttachedToVisualTree -= Attach;
            _control.DetachedFromVisualTree -= Detach;
        }
    }
}
