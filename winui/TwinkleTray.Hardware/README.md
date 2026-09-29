# Windows monitor backend

`MonitorService` uses Windows' physical monitor APIs (`Dxva2.dll`) for DDC/CI and `System.Management` for built-in display brightness. All refreshes and writes are serialized and run away from the UI thread.

- Call `RefreshAsync()` before writing, and again after hotplug, resume, or display configuration changes. A refresh creates a replacement handle set, reads brightness/contrast, and disposes the old handles after the replacement succeeds. A failed or canceled refresh disposes its incomplete replacement.
- Check the snapshot's support flags before enabling a slider. Unsupported displays are retained. `Brightness` is meaningful only when `SupportsBrightness` is true; contrast is nullable. `LastRefreshErrors` reports nonfatal provider failures without preventing working displays from appearing.
- Monitor IDs combine the normalized display device interface ID with its physical index. WMI IDs are normalized to the same device identity, so built-in panels are not also shown as unsupported DDC devices. Windows may change the instance ID when a display is moved to a different physical port.
- Brightness and contrast use VCP `0x10` and `0x12`, with the actual maximum read from the monitor. WMI brightness snaps to a supported level when the driver advertises discrete levels. Arbitrary VCP writes require a successful read of that code first. Power-off sends VCP `0xD6 = 5`; firmware determines whether the display supports it and how it wakes.
- No monitor capability string is fetched during refresh. No hardware writes happen on refresh or disposal. Write completion means the driver accepted the request; call `RefreshAsync()` for a fresh observation.
- Cancellation is cooperative between driver calls. Windows does not offer an interruption mechanism for an in-progress DDC/CI call. Actual write behavior, WMI laptop brightness, and hotplug behavior should be verified on the target hardware before release.

Build the library:

```powershell
dotnet build winui/TwinkleTray.Hardware/TwinkleTray.Hardware.csproj
```

Run the explicitly read-only diagnostic:

```powershell
dotnet run --project winui/TwinkleTray.Hardware/Diagnostics/TwinkleTray.Hardware.Probe.csproj -- --read-only
```

The diagnostic validates ID normalization, enumerates real monitors twice, checks snapshot ranges and unique IDs, and checks argument validation, cancellation, and repeated disposal. It never sends valid arguments to any hardware write operation.

References: [Windows low-level monitor configuration](https://learn.microsoft.com/en-us/windows/win32/monitor/using-the-low-level-monitor-configuration-functions), [physical monitor lifetime](https://learn.microsoft.com/en-us/windows/win32/api/physicalmonitorenumerationapi/nf-physicalmonitorenumerationapi-getphysicalmonitorsfromhmonitor), [display device interface identities](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaydevicesw), and [WMI brightness methods](https://learn.microsoft.com/en-us/windows/win32/wmicoreprov/wmimonitorbrightnessmethods).
