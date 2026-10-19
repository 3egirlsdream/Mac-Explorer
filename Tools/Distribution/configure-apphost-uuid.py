#!/usr/bin/env python3
"""Give the SDK apphost an app-specific build UUID before final code signing.

.NET copies the SDK host's LC_UUID into every application. Apple TN3178 and
TN3179 require distinct main executable UUIDs for reliable network identity.
"""
import hashlib
import pathlib
import struct
import sys
import uuid


def configure(host, bundle_id, assembly):
    data = bytearray(host.read_bytes())
    if len(data) < 32 or data[:4] != b'\xcf\xfa\xed\xfe':
        raise ValueError('Expected a thin, little-endian 64-bit Mach-O apphost')
    commands, command_bytes = struct.unpack_from('<II', data, 16)
    end = 32 + command_bytes
    if end > len(data):
        raise ValueError('Truncated Mach-O load commands')
    offset = 32
    uuid_offset = None
    for _ in range(commands):
        if offset + 8 > end:
            raise ValueError('Truncated Mach-O load command')
        command, size = struct.unpack_from('<II', data, offset)
        if size < 8 or offset + size > end:
            raise ValueError('Invalid Mach-O load command size')
        if command == 0x1B:  # LC_UUID
            if size != 24 or uuid_offset is not None:
                raise ValueError('Invalid or duplicate LC_UUID')
            uuid_offset = offset + 8
        offset += size
    if offset != end or uuid_offset is None:
        raise ValueError('Apphost must have exactly one LC_UUID')
    data[uuid_offset:uuid_offset + 16] = bytes(16)
    digest = hashlib.sha256(bundle_id.encode('utf-8') + b'\0' + assembly.read_bytes() + data).digest()
    identifier = uuid.UUID(bytes=digest[:16], version=5)
    data[uuid_offset:uuid_offset + 16] = identifier.bytes
    host.write_bytes(data)
    return identifier


if __name__ == '__main__':
    result = configure(pathlib.Path(sys.argv[1]), sys.argv[2], pathlib.Path(sys.argv[3]))
    print('Application build UUID:', result)
