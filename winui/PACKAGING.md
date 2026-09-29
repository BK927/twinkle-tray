# Packaging

Run `build.ps1` first, then create a portable ZIP and an unsigned MSIX from the published application:

```powershell
./winui/package.ps1 -Architecture x64 -Version 0.3.0-beta.1 -MsixRevision 1
```

The output is saved in `winui/artifacts/packages/`, with SHA256 checksums for the selected architecture. PDB files and `smoke-test.json` are excluded. `-SkipMsix` produces only the portable ZIP. Packaging does not install an application or certificate.

The archive filename keeps the complete semantic version. Windows uses a numeric MSIX version: `major.minor.patch.MsixRevision`. The revision defaults to `0` and accepts values from `0` through `65535`.

Publishers must increase the revision for successive MSIX releases sharing the same numeric base version, including the transition from preview to stable. For example, `0.3.0-beta.1` with revision `1`, `0.3.0-beta.2` with revision `2`, and `0.3.0` with revision `3` produce Windows package versions `0.3.0.1`, `0.3.0.2`, and `0.3.0.3`. Once the major, minor, or patch number increases, the revision may restart at `0`. Keep the package identity and publisher consistent to support upgrades. The release workflow uses revision `0` unless it explicitly passes another value.

MSIX installation requires a valid package signature trusted by the target computer. Certificate distribution and installation remain publisher-managed. The portable ZIP does not require MSIX signing.

The package declares the `location` device capability for the settings button that requests coordinates for sunrise/sunset schedules. Declaration alone does not obtain coordinates: the app requests access and reads the position only after the user selects that button, subject to Windows location permissions. See [Microsoft's location API documentation](https://learn.microsoft.com/en-us/windows/apps/develop/maps-and-location/get-location).
