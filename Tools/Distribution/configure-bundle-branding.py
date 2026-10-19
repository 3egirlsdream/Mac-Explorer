#!/usr/bin/env python3
"""Apply channel branding before signing; bundle and executable identities stay intact."""
import pathlib
import plistlib
import sys


STORE_NAMES = {'zh-Hans': '文件资源管理器', 'en': '文件资源管理器'}
NETWORK_USAGE = {
    'zh-Hans': '用于发现局域网中的共享电脑、连接 SMB 共享，以及通过文件速递发现设备和传输文件。',
    'en': 'Discover shared computers on your local network, connect to SMB shares, and find devices and transfer files with File Delivery.',
}


def configure(app, channel):
    info_path = app / 'Contents/Info.plist'
    info = plistlib.loads(info_path.read_bytes())
    if channel == 'AppStore':
        info['CFBundleDisplayName'] = info['CFBundleName'] = STORE_NAMES['en']
        info['CFBundleDevelopmentRegion'] = 'en'
        info['CFBundleLocalizations'] = list(STORE_NAMES)
        info['NSHumanReadableCopyright'] = '© 2026 文件资源管理器'
    elif channel != 'Website':
        raise ValueError('Unknown distribution channel: ' + channel)
    for locale, description in NETWORK_USAGE.items():
        directory = app / 'Contents/Resources' / (locale + '.lproj')
        directory.mkdir(parents=True, exist_ok=True)
        content = f'"NSLocalNetworkUsageDescription" = "{description}";\n'
        if channel == 'AppStore':
            name = STORE_NAMES[locale]
            content += f'"CFBundleDisplayName" = "{name}";\n"CFBundleName" = "{name}";\n'
        (directory / 'InfoPlist.strings').write_bytes(content.encode('utf-16'))
    info_path.write_bytes(plistlib.dumps(info, sort_keys=False))


if __name__ == '__main__':
    configure(pathlib.Path(sys.argv[1]), sys.argv[2])
