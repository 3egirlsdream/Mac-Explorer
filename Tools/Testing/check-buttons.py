#!/usr/bin/env python3
"""Enforce the shared button and ComboBox components and reject page-specific appearance overrides."""
import argparse
import json
import re
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
APPEARANCE = {'Padding', 'Background', 'Foreground', 'BorderBrush', 'BorderThickness',
              'CornerRadius', 'FontFamily', 'FontSize', 'FontWeight', 'Height', 'MinHeight', 'Theme', 'Template'}
NATIVE = {'Button', 'ToggleButton', 'SplitButton', 'ComboBox'}
SHARED = {'AppButton', 'AppToggleButton', 'AppSplitButton', 'AppComboBox'}

def audit(root=ROOT):
    violations = []
    counts = {'xaml_buttons': 0, 'code_buttons': 0, 'plugin_buttons': 0, 'xaml_comboboxes': 0, 'code_comboboxes': 0, 'files': 0}
    for folder in ('Views', 'Controls', 'Services', 'Assets', 'Plugins'):
        for path in sorted((root / folder).rglob('*')):
            if any(part in {'bin', 'obj'} for part in path.relative_to(root).parts):
                continue
            if path.suffix not in ('.axaml', '.cs'):
                continue
            counts['files'] += 1
            source = path.read_text()
            relative = str(path.relative_to(root))
            if path.suffix == '.axaml':
                for node in ET.fromstring(source).iter():
                    tag = node.tag.split('}')[-1]
                    if tag in NATIVE:
                        violations.append(f'{relative}: use App{tag}')
                    if tag in SHARED:
                        counts['xaml_comboboxes' if tag == 'AppComboBox' else 'xaml_buttons'] += 1
                        for attribute in node.attrib:
                            if attribute in APPEARANCE:
                                violations.append(f'{relative}: {tag} overrides {attribute}')
                    if folder != 'Assets' and tag == 'ControlTheme' and node.get('TargetType') in NATIVE:
                        violations.append(f'{relative}: control themes belong in Assets/ButtonStyles.axaml')
                    if relative != 'Assets/ButtonStyles.axaml' and tag == 'Style' and re.search(r'\b(?:App)?(?:Button|ToggleButton|SplitButton|ComboBox|RepeatButton)\b', node.get('Selector', '')):
                        for setter in node:
                            if setter.tag.split('}')[-1] == 'Setter' and setter.get('Property') in APPEARANCE:
                                violations.append(f'{relative}: local button/ComboBox style {node.get("Selector")}')
            else:
                for match in re.finditer(r'\bnew\s+(?:Avalonia\.Controls\.)?(Button|ToggleButton|SplitButton|ComboBox)\s*[{(]', source):
                    if folder == 'Plugins':
                        end = source.find('};', match.end())
                        initializer = source[match.end():end]
                        if re.search(r'Classes\s*=\s*\{\s*"(?:primary|secondary|ghost|danger|icon-action)"', initializer):
                            counts['plugin_buttons'] += 1
                            if re.search(r'\b(?:' + '|'.join(APPEARANCE) + r')\s*=', initializer):
                                violations.append(f'{relative}: plugin button overrides shared appearance')
                            continue
                    violations.append(f'{relative}:{source.count(chr(10), 0, match.start()) + 1}: use App{match[1]}')
                for typed in re.finditer(r'\b(AppButton|AppToggleButton|AppSplitButton|AppComboBox)\s+\w+\s*=\s*new\s*\(', source):
                    declaration = source[typed.start():source.find(';', typed.start())]
                    counts['code_comboboxes' if typed[1] == 'AppComboBox' else 'code_buttons'] += len(re.findall(r'\b\w+\s*=\s*new\s*\(', declaration))
                for native in re.finditer(r'\b(Button|ToggleButton|SplitButton|ComboBox)\s+\w+\s*=\s*new\s*\(', source):
                    violations.append(f'{relative}:{source.count(chr(10), 0, native.start()) + 1}: use App{native[1]}')
                variables = set(re.findall(r'(?:var|(?:App)?Button|(?:App)?ToggleButton|(?:App)?SplitButton|(?:App)?ComboBox)\s+(\w+)\s*=\s*new\s+App(?:Button|ToggleButton|SplitButton|ComboBox)\b', source))
                for variable in variables:
                    for match in re.finditer(r'\b' + re.escape(variable) + r'\.(\w+)\s*=', source):
                        if match[1] in APPEARANCE:
                            violations.append(f'{relative}:{source.count(chr(10), 0, match.start()) + 1}: button overrides {match[1]} after creation')
                    for binding in re.finditer(r'\b' + re.escape(variable) + r'\.(?:Bind|SetValue|SetCurrentValue)\(\s*(?:[\w.]+\.)?(\w+)Property\b', source):
                        if binding[1] in APPEARANCE:
                            violations.append(f'{relative}: button overrides {binding[1]} through a binding or setter')
                for match in re.finditer(r'\bnew\s+App(?:Button|ToggleButton|SplitButton|ComboBox)(?:\s*\(\s*\))?\s*\{', source):
                    counts['code_comboboxes' if 'AppComboBox' in match[0] else 'code_buttons'] += 1
                    depth = 1
                    end = match.end()
                    while depth and end < len(source):
                        depth += (source[end] == '{') - (source[end] == '}')
                        end += 1
                    body = source[match.end():end - 1]
                    # Ignore nested icon/content initializers; only the button's own properties count.
                    while re.search(r'\{[^{}]*\}', body):
                        body = re.sub(r'\{[^{}]*\}', '', body)
                    for attribute in re.findall(r'(?:^|[,\n])\s*(\w+)\s*=', body):
                        if attribute in APPEARANCE:
                            violations.append(f'{relative}:{source.count(chr(10), 0, match.start()) + 1}: button overrides {attribute}')
    return {'success': not violations, **counts, 'violations': violations}

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--json', type=Path)
    args = parser.parse_args()
    result = audit()
    if args.json:
        args.json.parent.mkdir(parents=True, exist_ok=True)
        args.json.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n')
    for violation in result['violations']:
        print(violation)
    print(f"Button audit: {result['xaml_buttons']} XAML buttons, {result['code_buttons']} code buttons, {result['plugin_buttons']} SDK buttons, {result['xaml_comboboxes']} XAML + {result['code_comboboxes']} code ComboBoxes; {len(result['violations'])} violations")
    return 0 if result['success'] else 1

if __name__ == '__main__':
    raise SystemExit(main())
