import importlib.util
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location('button_audit', Path(__file__).with_name('check-buttons.py'))
audit_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit_module)

class ButtonAuditTests(unittest.TestCase):
    def check(self, name, source, folder="Views"):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path = root / folder / name
            path.parent.mkdir()
            path.write_text(source)
            return audit_module.audit(root)['violations']

    def test_shared_components_allow_nested_icon_sizes_and_page_layout(self):
        self.assertEqual([], self.check('Example.axaml', '<UserControl xmlns:ui="using:MacExplorer.Controls"><ui:AppButton Classes="secondary" MinWidth="80"><PathIcon Width="16" Height="16"/></ui:AppButton></UserControl>'))
        self.assertEqual([], self.check('Example.cs', 'var button = new AppButton { Classes = { "secondary" }, Content = new PathIcon { Height = 16 } };'))

    def test_raw_controls_and_local_themes_are_rejected(self):
        self.assertTrue(self.check('Example.axaml', '<UserControl><Button/></UserControl>'))
        self.assertTrue(self.check('Example.cs', 'var button = new Button { Content = "Save" };'))
        self.assertTrue(self.check('Example.axaml', '<UserControl><ControlTheme TargetType="Button"/></UserControl>'))

    def test_inline_and_late_appearance_overrides_are_rejected(self):
        self.assertTrue(self.check('Example.axaml', '<UserControl xmlns:ui="using:MacExplorer.Controls"><ui:AppButton Padding="2"/></UserControl>'))
        self.assertTrue(self.check('Example.cs', 'var button = new AppButton { Padding = new Thickness(2) };'))
        self.assertTrue(self.check('Example.cs', 'var button = new AppButton(); button.Background = Brushes.Red;'))
        self.assertTrue(self.check('Example.cs', 'var button = new AppButton(); button.Bind(Button.ForegroundProperty, source);'))

    def test_sdk_buttons_use_shared_variants_without_host_control_dependencies(self):
        self.assertEqual([], self.check('Example.cs', 'var button = new Button { Classes = { "primary" } };', 'Plugins'))
        self.assertTrue(self.check('Example.cs', 'var button = new Button { Content = "Save" };', 'Plugins'))
        self.assertTrue(self.check('Example.cs', 'var button = new Button { Classes = { "primary" }, Padding = new Thickness(2) };', 'Plugins'))

    def test_combobox_uses_the_shared_source_and_rejects_local_overrides(self):
        self.assertEqual([], self.check('Example.axaml', '<UserControl xmlns:ui="using:MacExplorer.Controls"><ui:AppComboBox Classes="compact" Width="140"/></UserControl>'))
        self.assertEqual([], self.check('Example.cs', 'var field = new AppComboBox { ItemsSource = options };'))
        self.assertTrue(self.check('Example.axaml', '<UserControl><ComboBox/></UserControl>'))
        self.assertTrue(self.check('Example.cs', 'ComboBox field = new();'))
        self.assertTrue(self.check('Example.cs', 'var field = new ComboBox();'))
        self.assertTrue(self.check('Example.cs', 'var field = new AppComboBox(); field.Padding = new Thickness(2);'))
        self.assertTrue(self.check('Example.axaml', '<UserControl><Style Selector="ComboBox"><Setter Property="Padding" Value="2"/></Style></UserControl>'))

    def test_page_state_is_allowed_but_page_appearance_styles_are_rejected(self):
        self.assertEqual([], self.check('Example.axaml', '<UserControl><Style Selector="Button.row"><Setter Property="IsVisible" Value="False"/></Style></UserControl>'))
        self.assertTrue(self.check('Example.axaml', '<UserControl><Style Selector="Button.row"><Setter Property="Background" Value="Red"/></Style></UserControl>'))

if __name__ == '__main__':
    unittest.main()
