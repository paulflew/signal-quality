# Signal Quality

A single coloured light in the Windows system tray that tells you whether your internet connection is any good right now.

![The light in each state](docs/lights.png)

Every 5 seconds it times a small HTTPS request to Google (`https://www.google.com/generate_204`) and colours the light by the **average of the last 30 seconds**:

| Light | Meaning (defaults) |
|---|---|
| 🟢 Green | average ≤ 150 ms, no failed checks |
| 🟡 Yellow | average 150–400 ms, or any failed check in the last 30 s |
| 🔴 Red | average > 400 ms, the last two checks failed, or half the recent checks failed |
| ⚪ Grey | starting up |

An arrow appears when the **latest** check falls outside the band the light is showing, so you can see which way things are heading before the average catches up. **↗** means the latest check landed in a better band, **↘** a worse one, and the arrow takes that band's colour. No arrow means the latest check agrees with the light.

Hover for the average, the latest reading and the failure rate. Click the light (either button) for a menu: check now, session stats, settings, notify on change, and start with Windows.

## Install

Both options need nothing else installed: the app uses the .NET Framework already built into Windows 10 and 11.

**Installer (recommended).** Download `SignalQuality-Setup-x.y.z.exe` from the [latest release](../../releases/latest) and run it. It installs for your user only, with no admin prompt, into `%LOCALAPPDATA%\Programs\Signal Quality`. It adds a Start menu shortcut and offers to start Signal Quality when you sign in. Uninstall from *Settings → Apps*. (Releases after v1.0.0 include the installer.)

**Portable.** Download `SignalQuality.exe`, a single file of about 60 KB, put it somewhere permanent such as `%LOCALAPPDATA%\SignalQuality\`, and run it. Turn on **Start with Windows** in its menu if you want it always running.

**Windows will warn you the first time.** The exe isn't code signed, so SmartScreen says "Windows protected your PC". Choose **More info → Run anyway**. If you'd rather check the download first, each release includes SHA-256 checksums:

```powershell
Get-FileHash .\SignalQuality.exe -Algorithm SHA256
```

Windows 11 hides new tray icons behind the **^** arrow. Drag the light onto the taskbar, or turn it on under *Settings → Personalization → Taskbar → Other system tray icons*.

## Why HTTPS rather than ping

Public Wi-Fi on trains, in hotels and in cafés often blocks ping entirely, which would leave a ping-based light stuck on red while your connection is fine. Plain HTTP is no better: those networks route it through their own gateway, which can be slow or answer on the real server's behalf, so you end up measuring the gateway instead of your connection.

An HTTPS response can only come from the real server. The app keeps the connection open between checks, so each reading is about one network round trip, which is what ping measures too, without connection setup inflating it. When the connection drops, the next check reopens it before timing anything. Failures are counted as failures rather than timed.

## Settings

**Settings…** in the menu, saved to `%APPDATA%\SignalQuality\settings.ini`: address, check interval, averaging window, the green and yellow limits, and the timeout.

Any `http(s)` address works. An address ending in `generate_204` also detects Wi-Fi sign-in pages. For other addresses any HTTP response counts as reachable, except 502, 503 and 504 gateway errors. Prefer `https://`, because on some networks the gateway answers plain-HTTP requests itself, even for servers it can't reach.

## Privacy

No telemetry, no analytics, no data collection. The app makes one request every 5 seconds to the address you configure (Google's connectivity-check URL by default), which returns an empty response, and records nothing beyond the timings it shows you. Settings stay in your own `%APPDATA%` folder.

## Build

Two ways, both producing the same exe. See [CONTRIBUTING.md](CONTRIBUTING.md) for details.

```
build.cmd                                  :: uses the C# compiler that ships with Windows; nothing to install
dotnet build SignalQuality.csproj -c Release   :: or open the .csproj in Visual Studio or Rider
```

Tests:

```
build.cmd test                              :: everything, including live web checks
tests\bin\Release\Tests.exe --offline        :: only the checks that need no network (what CI runs)
```

## Uninstall

**Installed:** *Settings → Apps → Signal Quality → Uninstall*. It stops the app and removes the files, the Start menu shortcut, the start-with-Windows entry and your settings.

**Portable:** exit from the tray menu, then delete the exe, the `%APPDATA%\SignalQuality` folder, and, if you enabled it, the `SignalQuality` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

## Licence

[MIT](LICENSE).
