# 0.3.5: repeated-work reductions

This change preserves the accepted 0.3.4 tray appearance, animation and theme policy. Brightness throttling, automation cadence, hardware serialization and write validation are unchanged.

## Changes

- **Monitor refreshes:** callers waiting behind another monitor operation share one scan. Requests arriving during an active scan share a subsequent scan, so hardware configuration or topology changes are not silently dropped. Each caller waits for its own batch. The shared gate is released between scans to let queued brightness writes proceed.
- **Background automation:** no foreground-process query is made without an eligible or active automatic profile. An active profile still observes departure and restores captured levels after it is disabled or removed. Empty/disabled schedules skip evaluation while advancing the observation time. Sensor-exclusion collections are built only when applying scheduled levels.
- **Tray rendering:** a typed snapshot replaces the previous JSON layout signature. Brightness, contrast and current VCP values update existing controls without explicitly queuing a window measure/move. Settings changes, structural changes and actual size events still trigger layout. Snapshots copy mutable capability lists, so in-place edits remain visible. Appearance updates reuse an accent brush and avoid writing unchanged Acrylic color properties.
- **Backdrop lifetime:** the custom Acrylic material detaches default policy listeners before disposing its controller. Its optional configuration-change notification does not forward a potentially expired target through the base ABI. The controller continues observing the live default configuration, including activation and accessibility policy.

## Regression coverage

The package-free core suite contains nine refresh-queue tests: a blocked burst of 20 requests becomes one scan; concurrent callers share that batch; a burst during an active scan becomes one trailing scan; later requests remain fresh; writes can run between scans; failure and cancellation recover or drain correctly; callbacks keep the caller's synchronization context.

Five additional runtime assertions exercise 600 inactive-profile ticks with zero foreground-query calls, profile enable/disable/removal and restoration, and schedule re-enabling. Seven assertions check actual retained or rebuilt WinUI controls, including brightness/contrast/VCP values and in-place changes to monitor names/order, feature bounds/icons and input choices.

Positive automation-completion checks now observe transition completion for up to five seconds and still assert the final target and absence of an active transition. They previously sampled after 1,150 ms and could observe an unfinished transition when the UI dispatcher was busy. Tests that observe manual overrides remain unchanged. The full GUI suite has a ten-minute timeout after one run reached all 144 layouts but exceeded the former four-minute limit during popup checks. Individual assertions and ordinary CLI command timeouts are unchanged.

The settings restoration test no longer forces a layout pass every 25 ms while waiting for low-priority restoration. The production restoration path already performs the required layout passes; the test retains its three-second bound, final layout settlement and state assertions. Settings restoration priorities and animation timing are unchanged.

## Measurement limits

The final local Windows x64 run completed at **2026-09-30 07:46:12 UTC** (`artifacts/test-results/20260930T074318428-a4a2ca5fc4714f86922fa31d6cc835d9/`). It passed **63 core tests, 56 runtime assertions, 11 settings pages, 144/144 layout cases and 63/64 UI checks**, with 17 PNGs, no harness errors and zero hardware/user-settings writes. The existing native foreground-transfer assertion still failed, so the run remains partial and the IPC phase did not run. The exit code was the expected test-failure code 1; the Acrylic target exception observed on the first attempt did not recur in this run.

Earlier attempts remain separate: the first recorded that Acrylic shutdown exception; the second sampled two unfinished transitions; the third exceeded the old whole-suite timeout after all 144 settings cases; the fourth timed out waiting for initial settings restoration before its matrix began. The final results above come from one complete run after the focused fixes, rather than combining passing fragments. Final light, dark and accent content previews retain the existing tray design; these images do not show compositor effects or animation.

These tests demonstrate reduced repeated work and preserved behavior. They do not establish a CPU percentage, working-set reduction, startup improvement, battery saving or a speedup over Electron. A hardware refresh that is already running is not interrupted. Continuous new requests can require further scans; batching bounds each pending burst, not all future requests.

An Electron comparison still needs matching monitor connections, settings, polling intervals and interaction scenarios, with all application processes counted. Demo-mode numbers are not a hardware performance benchmark.
