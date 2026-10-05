# QuietKey 1.1

A small native Windows tray app built in C# and WPF. Press **Ctrl + Alt + Space** to show or hide the popup. Closing it or pressing Escape keeps QuietKey running in the tray.

## Install and use

Double-click **QuietKey.Setup.exe** in the `dist` folder. The setup wizard installs for your Windows account, enables startup by default, creates desktop and Start menu shortcuts, and registers an uninstaller in **Windows Settings > Apps > Installed apps**. Updating preserves your saved profiles. No administrator rights are required.

The installed app is `%LocalAppData%\QuietKey\app\QuietKey.exe`. Startup launches it quietly after sign-in. Use Settings to change the global shortcut or disable startup.

- **Sleep timer:** enter 1–10,080 whole minutes. QuietKey runs the system `shutdown.exe /s /t <seconds>` directly without a console window. Cancel from the popup or tray to run `shutdown.exe /a`.
- **Sleep mode:** slide the switch right to apply a bedtime profile while the computer stays awake. Edit its brightness, Night Light, ASUS fan preset, and Windows power preference. Defaults are 25% brightness, Night Light on, Silent, and best efficiency.
- **Gaming mode:** slide its switch right to apply 100% brightness, Night Light off, ASUS Turbo, Windows best performance, and Windows Game Mode. Edit these choices as needed. Gaming requires AC power. This uses normal Windows and firmware presets; it does not overclock or change custom fan curves.
- **Switch off:** slide left to restore the settings captured before the mode was enabled. Only one mode is active at a time. Switching from Sleep to Gaming first restores Sleep, then applies Gaming. Originals survive app restarts. If a hardware action fails, the app reports the individual failure and retains restoration data so you can retry.
- **Exit:** right-click the tray icon and choose Exit. Applied modes remain until you switch them off. A Windows shutdown timer also continues after the app exits.

Windows treats a nonzero shutdown timeout as a forced shutdown at expiry. Save your work before starting a timer. Windows may display its own shutdown notification. An existing Windows shutdown is reported instead of replaced. If another app cancels a timer, QuietKey's local countdown may remain until Cancel or the displayed deadline.

## Hardware support

Brightness uses Windows `WmiMonitorBrightness` / `WmiSetBrightness` on active built-in displays. The driver on this ASUS PC returns no output even when a write succeeds, so QuietKey verifies the actual brightness afterward. Brightness is applied after the power and fan settings because changing a power plan can reset it. External displays without this WMI interface are reported as unsupported.

ASUS presets use the installed **ASUS System Control Interface** driver (`ATKACPI`). Manufacturer, endpoint support, and successful firmware responses are checked. Silent / Performance / Turbo are firmware performance presets, not fixed fan RPMs. Turbo requires AC. The endpoint reports capabilities rather than the current preset, so QuietKey infers the previous fan preset from a Silent or Turbo power plan name, otherwise Performance. Armoury Crate can subsequently override the selected preset. Other manufacturers currently have no fan backend.

Night Light updates the current user's shared and per-device state on this Windows version. Its CloudStore binary format is undocumented and may change with Windows updates. QuietKey uses a strict CompactBinary v1 parser, preserves other fields including strength and schedule, backs up original bytes, and rejects unfamiliar or ambiguous formats. Successful readback verifies the stored state; visible color also depends on the display driver and HDR. **Open Windows Night Light settings** remains available in the profile editor.

Windows power preferences use the Balanced base plan with the best-efficiency or best-performance overlay. Switching off restores the original plan and overlay. Gaming enables the documented current-user Windows Game Mode preference and restores its previous value when switched off.

Settings and restoration data live in `%LocalAppData%\QuietKey\settings.json`, with a `.bak` copy after each save. Hardware action reports are in `actions.log`, errors in `errors.log`, and Night Light byte backups in `night-light-before-change*.bin`.

## Build and verification

Requires 64-bit Windows 10/11 with .NET Framework 4.8. The build uses the compiler supplied with Windows; no additional SDK or NuGet packages are required.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test -Package
```

This builds the app, native setup wizard, native uninstaller, and `dist\QuietKey-1.1.0-win-x64.zip`. Focused tests cover timer validation and command arguments, settings recovery, Night Light transformations including per-device inheritance, mutually exclusive modes, capture and restoration, partial failures, AC checks, and brightness after power-plan changes. Automated tests use fake hardware and never schedule a real shutdown.

For unattended installation, use `QuietKey.Setup.exe --quiet`; add `--no-launch` to avoid opening the app. `Install.ps1` wraps the same installer. The ZIP also contains a portable `QuietKey.exe`; keep it in a stable location if enabling startup for that executable.

Read-only hardware diagnostics:

```powershell
Start-Process .\build\QuietKey.exe -ArgumentList '--diagnostics', 'C:\absolute\path\diagnostics.json' -Wait
```

UI rendering check using synthetic state, with no hardware actions:

```powershell
Start-Process .\build\QuietKey.exe -ArgumentList '--ui-smoke', 'C:\absolute\path\previews' -Wait
```

Developer hardware integration check: `QuietKey.exe --mode-test <absolute-output-directory>` temporarily applies and restores both modes on real hardware. It writes isolated test settings and a report, checks final restoration, and never starts a shutdown timer. Close the normal app first to avoid concurrent settings changes.

Uninstall through **Windows Settings > Apps > Installed apps > QuietKey**, the Start menu uninstaller, or `Uninstall.ps1`. The uninstaller restores pending mode settings before removing the app, startup entry, and shortcuts. If restoration fails, the app stays installed so you can retry. Saved profiles are retained. Uninstalling does not cancel a Windows shutdown timer; use `shutdown /a` if needed.

## Implementation references

- [Microsoft: system-wide hotkeys](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)
- [Microsoft: shutdown command](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/shutdown)
- [Microsoft: WmiSetBrightness](https://learn.microsoft.com/en-us/windows/win32/wmicoreprov/wmisetbrightness-method-in-class-wmimonitorbrightnessmethods)
- [Microsoft: Run startup registry keys](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)
- [Microsoft: Windows power slider overlays](https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/customize-power-slider)
- [Microsoft: Windows Game Mode preference](https://learn.microsoft.com/en-us/windows/apps/develop/settings/settings-windows-11)
- [ASUS protocol reference in G-Helper](https://github.com/seerge/g-helper/blob/main/app/AsusACPI.cs)
- [Night Light binary format research](https://github.com/kvnxiao/win-nightlight-cli/blob/main/docs/nightlight-registry-format.md)

Protocol adapters and the binary parser are implemented locally; no third-party source or compiled dependency is bundled.
