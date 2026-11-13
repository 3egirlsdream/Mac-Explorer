using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace MacExplorer.Controls;

public class AppButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);
}

public class AppToggleButton : ToggleButton
{
    protected override Type StyleKeyOverride => typeof(ToggleButton);
}

public class AppSplitButton : SplitButton
{
    protected override Type StyleKeyOverride => typeof(SplitButton);
}

public class AppComboBox : ComboBox
{
    protected override Type StyleKeyOverride => typeof(ComboBox);
}
