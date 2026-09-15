# Contributing

Thanks for taking a look. This is a small app and it aims to stay small: one light in the tray that tells you whether your connection is any good.

## Building

Two ways, both producing the same exe:

- `build.cmd` uses the C# compiler included with Windows. Nothing to install.
- `dotnet build SignalQuality.csproj -c Release`, or open `SignalQuality.csproj` in Visual Studio or Rider.

The app targets .NET Framework 4.8, which is already on every supported Windows 10 and 11 machine, so the exe runs anywhere with nothing installed. Because `build.cmd` uses the compiler Windows ships, the code sticks to **C# 5**: no string interpolation, no `?.`, no expression-bodied members. The project file pins `LangVersion` to 5 so both paths agree.

## Tests

```
build.cmd test                       :: build and run everything, including live web checks
tests\bin\Release\Tests.exe --offline :: only the checks that need no network (what CI runs)
tests\bin\Release\Tests.exe <folder>  :: also write a contact sheet of every tray icon there
```

Live checks depend on the network you're on, and some networks (public Wi-Fi especially) interfere with them. Treat a failure there as information about the network before assuming it's a bug.

## Changing the icon

The tray icon is drawn in code in `IconFactory`. After changing it, run `tools\make-icon.cmd` to regenerate `assets\SignalQuality.ico`, which is the icon Explorer and Task Manager show. Check your change at 16 px as well as 32 px: the tray uses 16 px at 100% display scaling.

## Pull requests

Keep the existing style: comments explain why, not what, and the app stays a single source file unless there's a good reason to split it. Please say which Windows version and display scaling you tested at.
