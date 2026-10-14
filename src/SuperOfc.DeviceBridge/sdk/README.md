# ZKTeco SDK files (not in source control)

The bridge talks to ZKTeco terminals through the vendor's Standalone SDK (`zkemkeeper.dll`, 32-bit COM).
The SDK is not redistributed in this repository until the licence question (docs/10) is answered.

To enable real devices on a build machine:
1. Download the ZKTeco Standalone SDK (32-bit) from ZKTeco.
2. Copy `zkemkeeper.dll` and every DLL that ships next to it into this `sdk` folder.
3. Add `zkemkeeper.sxs.manifest` here (template below). The bridge activates it at run time, so no
   `regsvr32` and no administrator rights are needed. If the file is missing, the bridge falls back to a
   registered copy of the SDK (installed by the vendor setup), and otherwise reports "SDK missing".

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <assemblyIdentity type="win32" name="SuperOfc.ZkSdk" version="1.0.0.0" processorArchitecture="x86" />
  <file name="zkemkeeper.dll">
    <comClass clsid="{00853A19-BD51-419B-9269-2DABE57EB61F}" progid="zkemkeeper.ZKEM.1" threadingModel="Apartment" />
  </file>
</assembly>
```

Verify the CLSID against the type library of the SDK version you download (OleView or `reg query`),
and record the tested SDK version in `docs/testing/device-matrix.md`.
