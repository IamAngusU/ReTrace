# Components and provenance

ReTrace's original application/core code: MIT, see LICENSE. This does not
relicense external recovery tools or runtimes.

PhotoRec/TestDisk 7.2: Christophe Grenier and contributors, GPL-2.0-or-later.
The portable package includes the unchanged official Windows archive and the
matching upstream source archive. Every recovery run verifies the Windows
archive hash before extracting it into that run's chosen work directory;
executed components are verified and held open during the worker lifetime.
Upstream distribution license, authors and runtime-library files remain inside
the unchanged archive; COPYing/AUTHORS are also copied to package licenses.
Corresponding PhotoRec/TestDisk source is provided in components, rather than
relying solely on a future source offer. Additional upstream runtime-library
licenses continue to apply; do not infer that ReTrace's MIT license covers them.
For redistribution of modified tools or runtime libraries, provide their exact
applicable corresponding sources and required notices as well.

- Binary: https://www.cgsecurity.org/testdisk-7.2.win64.zip
  (27,354,549 bytes), SHA-256
  `e97e203ce77b6b1a3a37d01beccf069dc6c4632b579ffbb82ae739cdda229f38`.
- Matching source: https://www.cgsecurity.org/testdisk-7.2.tar.bz2
  (855,781 bytes), SHA-256
  `f8343be20cb4001c5d91a2e3bcd918398f00ae6d8310894a5a9f2feb813c283f`.
- Official pins: https://www.cgsecurity.org/testdisk_sha256.txt
- Recovery semantics: https://www.cgsecurity.org/testdisk_doc/photorec.html
- Scripted execution: https://www.cgsecurity.org/wiki/Scripted_run

Microsoft .NET/Windows Desktop runtime is supplied by the .NET SDK during
self-contained publication. The publisher copies the matching runtime versions'
LICENSE and THIRD-PARTY-NOTICES into licenses; retrace-package.json inventories
the exact files. The inventory is not a publisher signature. Runtime sources:
https://github.com/dotnet/runtime and https://github.com/dotnet/wpf.

Windows volume metadata/acquisition API contracts:
https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-ioctl_volume_get_volume_disk_extents
and https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_allow_extended_dasd_io.
Only read-access handles are requested. The extended partition read control
does not grant write access and subsequent reads remain bounded by the checked
partition length.
