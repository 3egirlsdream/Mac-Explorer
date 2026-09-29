#!/usr/bin/env python3
"""Generate the small, uncompressed RGGB DNG used by LibRawThumbnailTests.

Uses only Python's standard library. Pixels form a bright red/green/blue grid;
the TIFF orientation is 6 so a decoded thumbnail must be taller than wide.
"""

from pathlib import Path
import struct

WIDTH, HEIGHT = 96, 64
OUTPUT = Path(__file__).resolve().parents[2] / "Tests/MacExplorer.Tests/TestData/generated-orientation-6.dng"
MONO_OUTPUT = OUTPUT.with_name("generated-monochrome.dng")


def short(*values):
    return struct.pack("<" + "H" * len(values), *values)


def long(*values):
    return struct.pack("<" + "I" * len(values), *values)


def ascii(value):
    return value.encode("ascii") + b"\0"


def rational(*values):
    return b"".join(struct.pack("<II", numerator, denominator) for numerator, denominator in values)


def signed_rational(*values):
    return b"".join(struct.pack("<ii", numerator, denominator) for numerator, denominator in values)


# TIFF type, count, packed payload. Strip offset is filled in after the IFD.
entries = [
    (254, 4, 1, long(0)),             # NewSubfileType
    (256, 4, 1, long(WIDTH)),         # ImageWidth
    (257, 4, 1, long(HEIGHT)),        # ImageLength
    (258, 3, 1, short(16)),          # BitsPerSample
    (259, 3, 1, short(1)),           # Compression: none
    (262, 3, 1, short(32803)),       # CFA
    (271, 2, 9, ascii("FKFinder")),
    (272, 2, 14, ascii("Generated DNG")),
    (274, 3, 1, short(6)),           # Orientation: 90 degrees clockwise
    (273, 4, 1, long(0)),            # StripOffsets
    (277, 3, 1, short(1)),           # SamplesPerPixel
    (278, 4, 1, long(HEIGHT)),       # RowsPerStrip
    (279, 4, 1, long(WIDTH * HEIGHT * 2)),
    (284, 3, 1, short(1)),           # PlanarConfiguration
    (33421, 3, 2, short(2, 2)),      # CFARepeatPatternDim
    (33422, 1, 4, bytes((0, 1, 1, 2))),  # RGGB
    (50706, 1, 4, bytes((1, 4, 0, 0))),  # DNGVersion 1.4
    (50707, 1, 4, bytes((1, 2, 0, 0))),  # DNGBackwardVersion
    (50708, 2, 28, ascii("FKFinder Generated Test DNG")),
    (50710, 1, 3, bytes((0, 1, 2))),     # CFAPlaneColor
    (50711, 3, 1, short(1)),         # CFALayout
    (50714, 3, 1, short(0)),         # BlackLevel
    (50717, 4, 1, long(65535)),      # WhiteLevel
    (50721, 10, 9, signed_rational(*[(1 if i in (0, 4, 8) else 0, 1) for i in range(9)])),
    (50728, 5, 3, rational((1, 1), (1, 1), (1, 1))),  # AsShotNeutral
    (50778, 3, 1, short(21)),        # CalibrationIlluminant1: D65
]

def write_dng(output, tags, monochrome):
    tags.sort()
    header_size = 8 + 2 + len(tags) * 12 + 4
    extra = bytearray()
    records = []
    for tag, kind, count, payload in tags:
        if len(payload) <= 4:
            value = payload.ljust(4, b"\0")
        else:
            value = long(header_size + len(extra))
            extra.extend(payload)
            if len(extra) % 2:
                extra.append(0)
        records.append([tag, kind, count, value])

    for record in records:
        if record[0] == 273:
            record[3] = long(header_size + len(extra))

    pixels = bytearray()
    for y in range(HEIGHT):
        for x in range(WIDTH):
            if monochrome:
                value = 8000 + (x + y) * 200
            else:
                # Each area emphasizes a different Bayer color.
                region = (x * 3) // WIDTH
                color = (0 if y % 2 == 0 and x % 2 == 0 else
                         2 if y % 2 == 1 and x % 2 == 1 else 1)
                value = 50000 if color == region else 8000 + (x + y) * 50
            pixels.extend(short(min(value, 65535)))

    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("wb") as out:
        out.write(b"II" + short(42) + long(8))
        out.write(short(len(records)))
        for tag, kind, count, value in records:
            out.write(struct.pack("<HHI", tag, kind, count) + value)
        out.write(long(0) + extra + pixels)
    print(output)


write_dng(OUTPUT, entries.copy(), False)
mono_tags = [(tag, kind, 4 if tag == 33422 else 1 if tag == 50710 else count,
              bytes((0, 0, 0, 0)) if tag == 33422 else
              bytes((0,)) if tag == 50710 else payload)
             for tag, kind, count, payload in entries]
write_dng(MONO_OUTPUT, mono_tags, True)
