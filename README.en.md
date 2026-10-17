# Aside

[简体中文](README.md) · [English](README.en.md)

<p align="center">
  <img src="./assets/readme/hero.svg" width="100%" alt="Aside — Capture it. Stay in flow. Quick capture saves a passing thought to a local Inbox while the current task stays pinned">
</p>

*Interaction sketch, not an application screenshot.*

You are working on one thing when another idea appears. Switching apps to record it breaks your flow; ignoring it risks losing it. Aside keeps the detour short: **press a shortcut → write one line → return to your work.**

It is a small desktop Inbox, not a wall of separate sticky-note windows. Every thought goes into one list, unpinned by default. The items you pin stay in sight, and you can set the pin limit in Settings. Notes stay on your device; no account, cloud sync, or AI service is required.

## Get started

1. Download the installer for your system from [v1.1.3 Releases](https://github.com/savannahliz/aside/releases/tag/v1.1.3). The source archives are not runnable apps.
2. Launch Aside. Press the capture shortcut from any app to bring up the centered input bar.
3. Type and press **Enter**. The bar disappears and the item lands in Inbox. Press **Esc** to cancel. You can change the capture shortcut and submit key in Settings.

| Platform | Download | Default capture shortcut |
| --- | --- | --- |
| macOS 13+ · Apple Silicon / Intel | [Download DMG](https://github.com/savannahliz/aside/releases/download/v1.1.3/Aside-1.1.3-macOS-universal.dmg) | Option + Space |
| Windows 10/11 · x64 | [Download EXE](https://github.com/savannahliz/aside/releases/download/v1.1.3/Aside-1.1.3-Windows-x64.exe) | Ctrl + Alt + Space |

> [!WARNING]
> ### Installing on Mac (please read)
> Aside is not yet signed with an Apple Developer ID, so **macOS blocks it the first time you open it and you must allow it once**; otherwise macOS offers to move it to the Trash. Follow these steps in order:
>
> 1. Click **Download DMG** in the table above, then double-click the downloaded DMG.
> 2. In the window that opens, drag the **Aside** icon onto the **Applications** folder next to it.
> 3. Open **Finder → Applications** and double-click **Aside**. macOS says it cannot verify that Aside is free of malware — **click Done, not Move to Trash.**
> 4. Open **System Settings → Privacy & Security**, scroll down to **Security**, find the notice about Aside, click **Open Anyway**, authenticate with your password or Touch ID, then click **Open**.
> 5. On first launch Aside opens Settings and guides you to set your capture shortcut. After that it lives in the menu bar (pin icon), not the Dock.
>
> Steps 3 and 4 are needed only once. Make sure the installer came from this repository's Release first; [details below](#macos-blocks-the-first-launch).

On Windows, run `Aside.exe`; no installation or separate .NET is needed. On first launch it opens Settings to help you set a shortcut; afterwards you can find Aside in the system tray.

> The Windows installer is not signed either. If Windows shows “Windows protected your PC,” click **More info → Run anyway**.

## Set the thought aside; keep the important work in view

- **Quick capture:** The centered bar appears on demand and disappears after saving. Its cancel button appears on hover; new items go to the regular Inbox by default.
- **One Inbox, adjustable pin limit:** Expand to review everything or collapse to see just your pins. Click an item to edit it in place. When pins are full, choose which one to replace; the replaced item stays in Inbox.
- **Less desktop clutter:** Use the full list, pinned-item view, or a slim strip. Drag, resize, keep the window on top, or tuck it against either screen edge, where it hides without leaving anything on screen; move the pointer back to that edge to reveal it.
- **A quieter appearance:** Pick any background color with the system color picker, save favorites, and separately adjust idle, active, and quick-capture opacity.
- **Tidy up later:** Restore completed items or clear only the completed ones. The first clear asks for confirmation; you can opt out of future prompts.

## First launch and upgrading

### macOS blocks the first launch

The current DMG is not signed with an Apple Developer ID or notarized. If macOS says it cannot verify that Aside is free of malware, this is neither a malware detection nor a safety certification. **Proceed only if you have verified that the file came from this repository's Release and you trust its source.** Follow [Apple's official instructions](https://support.apple.com/en-us/102445):

1. Click **Done**, not **Move to Trash**.
2. Open **System Settings → Privacy & Security**, then scroll down to **Security**.
3. Find Aside's blocked-app notice, click **Open Anyway**, and confirm **Open**.

Changing “Allow applications downloaded from” to “App Store and identified developers” does not replace **Open Anyway** for this unsigned build. You do not need to disable Gatekeeper or run a Terminal command. Removing the first-launch warning requires a future Developer ID–signed, Apple-notarized release.

### Upgrading from QuietPin

Quit the old app before launching Aside. Internal data locations and stable identifiers stay the same to preserve existing notes and settings.

- **Mac:** `Aside.app` does not replace `QuietPin.app` automatically. Once you have checked that your notes appear in Aside, you may remove the old app; do not delete the data directory below.
- **Windows:** The new program is `Aside.exe`. If launch-on-login was enabled, turn it off and back on in Aside's Settings to update the saved EXE path. Mac users should also re-enable launch-on-login after upgrading.

## Data and current limits

| Platform | Local notes |
| --- | --- |
| macOS | `~/Library/Application Support/QuietPin/inbox.json` |
| Windows | `%LOCALAPPDATA%\QuietPin\inbox.json` |

The `QuietPin` directory name remains for compatibility. On Mac, appearance and window settings are stored in UserDefaults; on Windows, settings and notes share the same file. Quit the app before copying a data file for backup. There is no cross-device sync, and Mac and Windows data files cannot be swapped directly.

Automated macOS tests cover the data model, saved colors, window modes, screen-edge behavior, and quick capture. The Windows build has passed cross-compilation and data-model tests, and its UI, dragging and resizing, quick capture, global shortcut, and screen-edge docking were tested in a Windows 11 VM (ARM, running the x64 build under emulation). Multi-monitor behavior and launch-on-login have **not yet been tested on a physical x64 machine**. Intel Mac, full-screen apps, and multiple desktops have not all been tested individually either.

## Build from source

macOS requires macOS 13+ and Xcode Command Line Tools. The app uses SwiftUI, AppKit, and Carbon, with no third-party code dependencies.

```sh
bash scripts/test-macos.sh
bash scripts/build-macos.sh
```

This produces `dist/Aside.app` and a universal DMG. Windows requires the .NET 10 SDK; the app uses WPF and Win32:

```powershell
dotnet run --project windows/Tests/CoreChecks.csproj -c Release
dotnet publish windows/QuietPin.Windows.csproj -c Release -r win-x64 --self-contained true -o dist/windows-x64
```

On macOS, you can also point `DOTNET_BIN` to the .NET 10 SDK and run `bash scripts/build-windows.sh` to cross-compile the Windows package.

## Feedback and license

Please report issues through [GitHub Issues](https://github.com/savannahliz/aside/issues), including steps to reproduce, OS version, and screenshots. Check logs and images for private notes before sharing them. If Aside helps you, a [⭐ on the repository](https://github.com/savannahliz/aside) is appreciated.

Aside is released under [GNU GPL v3.0](LICENSE) (version 3 only).
