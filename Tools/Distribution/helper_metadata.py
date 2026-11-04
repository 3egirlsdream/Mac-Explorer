import plistlib
import re


def embedded_plist_errors(dump):
    errors = []
    for xml in re.findall(r"<plist\b[\s\S]*?</plist>", dump):
        metadata = plistlib.loads(('<?xml version="1.0"?>\n' + xml).encode())
        identifier = metadata.get('CFBundleIdentifier')
        if not isinstance(identifier, str) or not re.fullmatch(r'[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*', identifier):
            errors.append('Embedded Info.plist requires a valid CFBundleIdentifier')
    return errors
