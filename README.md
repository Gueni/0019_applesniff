# <img src="Assets/sniffer.png" width="32" align="top" /> Sniffer

A small Windows app that copies all photos and videos from an iPhone or Android phone to a folder on your PC over USB.

## Features

- Detects iPhones and Android phones automatically when plugged in (MTP/PTP).
- Copies from every storage on the phone (internal memory and SD card).
- Organizes files by month, by the phone's own folders, or in one folder.
- Filters for photos, videos and date taken.
- Skips anything already copied, so a run can be stopped and resumed safely.
- Keeps the original dates, reading them from file names when the phone doesn't store them.
- Can start copying automatically as soon as a phone is connected.
- Sends files the other way too (Android only): click **Send files to phone…** or drop files and folders on the window, and they go to the phone's `Download` folder. Files already on the phone are skipped.

## Install

Run `dist\Sniffer-Setup-<version>.exe`. It installs for the current user without admin rights and needs nothing else, because .NET is bundled.

## Phone setup

- **iPhone:** unlock the phone and tap **Allow / Trust**. For faster, more reliable transfers, set Settings → Apps → Photos → *Transfer to Mac or PC* to **Keep Originals**.
- **Android:** unlock the phone and choose **File transfer** in the USB notification.

## Build

Requires the .NET 10 SDK and [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`).

```powershell
dotnet run                      # run from source
.\build-installer.ps1           # builds dist\Sniffer-Setup-<version>.exe
```

The version comes from `<Version>` in `Sniffer.csproj`.

## Files and logs

- Settings and the log live in `%AppData%\Sniffer` (`settings.json`, `log.txt`).
- A hidden `.sniffer-index` file in the destination folder remembers what was already copied.
