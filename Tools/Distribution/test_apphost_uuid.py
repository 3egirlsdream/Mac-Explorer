import importlib.util
import pathlib
import struct
import tempfile
import unittest


spec = importlib.util.spec_from_file_location('apphost_uuid', pathlib.Path(__file__).with_name('configure-apphost-uuid.py'))
apphost_uuid = importlib.util.module_from_spec(spec)
spec.loader.exec_module(apphost_uuid)


class ApphostUuidTests(unittest.TestCase):
    def test_identity_is_distinct_per_app_and_build_and_preserves_other_bytes(self):
        header = struct.pack('<8I', 0xFEEDFACF, 0x100000C, 0, 2, 1, 24, 0, 0)
        original = header + struct.pack('<II', 0x1B, 24) + bytes(range(16)) + b'host-code'
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            host, assembly = root / 'host', root / 'app.dll'
            host.write_bytes(original)
            assembly.write_bytes(b'first build')
            first = apphost_uuid.configure(host, 'example.one', assembly)
            self.assertEqual(first, apphost_uuid.configure(host, 'example.one', assembly))
            self.assertEqual(original[:40], host.read_bytes()[:40])
            self.assertEqual(original[56:], host.read_bytes()[56:])
            self.assertNotEqual(first, apphost_uuid.configure(host, 'example.two', assembly))
            assembly.write_bytes(b'second build')
            self.assertNotEqual(first, apphost_uuid.configure(host, 'example.one', assembly))

    def test_missing_or_truncated_uuid_fails_without_modifying_host(self):
        with tempfile.TemporaryDirectory() as temporary:
            host, assembly = pathlib.Path(temporary) / 'host', pathlib.Path(temporary) / 'app.dll'
            assembly.write_bytes(b'app')
            for data in [b'not-mach-o', struct.pack('<8I', 0xFEEDFACF, 0x100000C, 0, 2, 0, 0, 0, 0),
                         struct.pack('<8I', 0xFEEDFACF, 0x100000C, 0, 2, 1, 24, 0, 0)]:
                host.write_bytes(data)
                with self.assertRaises(ValueError):
                    apphost_uuid.configure(host, 'example.one', assembly)
                self.assertEqual(data, host.read_bytes())
