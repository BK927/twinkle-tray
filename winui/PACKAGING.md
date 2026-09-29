# Twinkle Tray Native: packaging and upgrades

The supported release target is Windows x64. macOS, Apple hardware and Windows ARM64 execution are outside the current support scope; retained ARM64 build tooling is for development and does not establish runtime support.

Run `build.ps1` first, then create a portable ZIP and an unsigned MSIX from the published application:

```powershell
./winui/package.ps1 -Architecture x64 -Version 0.3.2
```

The output is saved in `winui/artifacts/packages/`, with SHA256 checksums for the selected architecture. PDB files and `smoke-test.json` are excluded. `-SkipMsix` produces only the portable ZIP. Packaging does not install an application or certificate.

Release archives use the `TwinkleTray-Native-<version>-x64` prefix, for example `TwinkleTray-Native-0.3.2-x64.zip`. Published packages are listed under [BK927/twinkle-tray-native releases](https://github.com/BK927/twinkle-tray-native/releases); CI results and uploaded artifacts belong to their matching [Actions run](https://github.com/BK927/twinkle-tray-native/actions). The `winui-v<version>` tag format and `SHA256SUMS.txt` are retained for updater compatibility. A local verification report does not establish publication.

## Moving from 0.2.x to the Native releases

Close the old application and install the complete new x64 build manually. Until a portable ZIP is published, build and package it locally using the commands above. The first transition from the old repository must be installed manually: existing 0.2.x binaries pin the old update location and archive naming, so repository redirects alone do not guarantee an automatic upgrade.

The public product name changes to **Twinkle Tray Native**, while these compatibility identifiers stay in place:

- Executable: `TwinkleTray.WinUI.exe`, with its matching runtime files.
- User settings: `%APPDATA%\TwinkleTray.WinUI\settings.json`; existing files need no manual relocation.
- Native JSON export format: `twinkle-tray-winui`.
- User/session single-instance and command-pipe identity, including the separate demo namespace.
- MSIX identity `BK927.TwinkleTray.WinUI`, default publisher `CN=BK927`, application ID `TwinkleTray` and startup task `TwinkleTrayStartup`.
- Portable startup registry value `TwinkleTray.WinUI`.

Keep the package publisher and signing identity consistent when updating a signed installation. Branding changes do not authorize replacing the upstream Microsoft Store application or its identity.

## MSIX versioning and signing

The archive filename keeps the complete semantic version. Windows uses a numeric MSIX version: `major.minor.patch.MsixRevision`. The revision defaults to `0` and accepts values from `0` through `65535`.

Publishers must increase the revision for successive MSIX releases sharing the same numeric base version, including the transition from preview to stable. For example, `0.3.0-beta.1` with revision `1`, `0.3.0-beta.2` with revision `2`, and `0.3.0` with revision `3` produce Windows package versions `0.3.0.1`, `0.3.0.2`, and `0.3.0.3`. Once the major, minor, or patch number increases, the revision may restart at `0`. Keep the package identity and publisher consistent to support upgrades. The release workflow uses revision `0` unless it explicitly passes another value.

MSIX installation requires a valid package signature trusted by the target computer. Certificate distribution and installation remain publisher-managed. The portable ZIP does not require MSIX signing.

The package declares the `location` device capability for the settings button that requests coordinates for sunrise/sunset schedules. Declaration alone does not obtain coordinates: the app requests access and reads the position only after the user selects that button, subject to Windows location permissions. See [Microsoft's location API documentation](https://learn.microsoft.com/en-us/windows/apps/develop/maps-and-location/get-location).
