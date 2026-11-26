"""Mark a Windows .exe as a GUI program so it never opens a console window."""
import struct, sys

path = sys.argv[1]
with open(path, "r+b") as f:
    data = bytearray(f.read())
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    assert data[pe:pe + 4] == b"PE\0\0", "not a PE file"
    off = pe + 4 + 20 + 68  # optional header Subsystem field
    old = struct.unpack_from("<H", data, off)[0]
    struct.pack_into("<H", data, off, 2)  # IMAGE_SUBSYSTEM_WINDOWS_GUI
    f.seek(0)
    f.write(data)
print(f"subsystem {old} -> 2 in {path}")
