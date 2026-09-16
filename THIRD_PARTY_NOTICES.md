# Third-party notices

## Windows RC003 HID tap reference implementation

The enhanced Back/Volume HID-over-GATT capture path is adapted from the
hardware-validated Windows implementation in `miaomiaozii/windows-remote-mic-app`.
It uses the same fixed Gadget runtime model, loopback port, message protocol,
IOCTL and 9-byte report layout.

- Project: https://github.com/miaomiaozii/windows-remote-mic-app
- Source area: `apps/windows/rc003/src/ovb_rc003/frida_hid_tap_runtime.py`
- License: GPL-3.0-only

## Frida Gadget 17.15.3

The optional enhanced-key feature embeds the unmodified
`frida-gadget-17.15.3-windows-x86_64.dll.xz` from Frida's official GitHub
release in the application package. Both the embedded archive and decompressed
DLL are verified with pinned SHA-256 hashes before loading.

- Project: https://github.com/frida/frida
- Release: https://github.com/frida/frida/releases/tag/17.15.3
- License: wxWindows Library Licence 3.1 with its stated binary-code exception
  (https://github.com/frida/frida/blob/main/COPYING)

## SharpCompress 0.50.4

SharpCompress is used to decompress the official XZ release stream.

- Project: https://github.com/adamhathcock/sharpcompress
- License: MIT (https://github.com/adamhathcock/sharpcompress/blob/master/LICENSE.txt)
