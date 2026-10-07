# Vendored ANT SDK binaries

These are Garmin/Dynastream's ANT Managed Library plus its native wrapper. They
are committed here because the managed library is not published on NuGet and the
native wrapper (`ANT_WrappedLib.dll`) is a 32-bit mixed-mode C++/CLI assembly —
which is why the whole bridge is built `x86` (see
`bridge/DescendersBridge/DescendersBridge.csproj`, the project that references them).

| File | Purpose |
|---|---|
| `ANT_NET.dll` | Managed wrapper (`ANT_Managed_Library` namespace). This is the assembly the project references. |
| `ANT_NET.xml` | XML doc comments for the above (IntelliSense only, not shipped). |
| `ANT_WrappedLib.dll` | Native mixed-mode wrapper over the ANT protocol + USB transport. Loaded at runtime by `ANT_NET.dll`. **x86 only.** |
| `DSI_CP210xManufacturing_3_1.dll` | Silicon Labs CP210x helper, used by CP210x-based sticks (ANTUSB2, PID 0x1004). Not used by our ANTUSB-m (PID 0x1008) but `ANT_WrappedLib` expects it present. |
| `DSI_SiUSBXp_3_1.dll` | Silicon Labs USBXpress runtime, same story. |

## Where these came from

Extracted from the `SmallEarthTech.AntUsbStick` NuGet package v4.1.7 (which
bundles them verbatim). The version resource on every DLL reads
`Dynastream Innovations Inc.`; `ANT_NET.dll` is file version `3.6.1.0`,
`ANT_WrappedLib.dll` is `AOA 03.05.0.0`. The byte content matches the copies
shipped in the SimCycling project (same company/version resources).

The DLLs are **not Authenticode-signed** — Dynastream never signed these
redistributables. If you want a first-party copy, download "ANT Libraries" from
<https://www.thisisant.com/developer/resources/downloads/> with your ANT+
adopter login and replace these files; the API is identical.

## Licence

These binaries are covered by the **ANT+ Shared Source Licence**. That licence
permits redistribution in binary form provided the copyright notice, the
conditions and the disclaimer are reproduced in the documentation and/or other
materials provided with the distribution. That is the basis for committing them
to this repo and shipping them in releases:

- the full text is in [`LICENSE-ANT-Shared-Source.txt`](LICENSE-ANT-Shared-Source.txt), next to the DLLs;
- [`THIRD-PARTY-NOTICES.txt`](../../THIRD-PARTY-NOTICES.txt) at the repo root
  reproduces it and is shipped in every release zip.

The licence prohibits two things, and both apply to this repo:

1. **Never commit source (or docs, tests, fixtures, logs) containing the ANT+
   network key.** A build from source reads its key from a git-ignored
   `config.json`.
2. **Never reverse engineer, decompile or disassemble these DLLs** (no IL
   scanning either). Use `ANT_NET.xml` for the API.
