# CLAUDE.md

Guidance for Claude Code when working in this repository.

## Git

Work directly on `master`. Never create a branch or a worktree unless explicitly asked to.

## What This Is

A single-project .NET 10 Windows console application that watches for a game process to start and then restarts AMD Adrenalin.

The problem it solves: AMD Adrenalin's overlay, performance metrics, and driver hooks frequently fail to attach when a game launches. Restarting the whole Adrenalin stack (services first, then processes, then the app) after the game is already running makes it attach reliably. Doing that by hand every session is tedious, so this automates it.

It is deliberately a console app with a tray icon rather than a GUI app. The console is a read-only status display — it prints what it found and what it is doing, and accepts no input. Every action lives in the tray icon's context menu. The tray icon also lets the window be hidden without killing the process.

## Build, Run, Test

```
dotnet build
```

`Adrenalize.slnx` exists for Visual Studio and holds nothing but a pointer to the one `.csproj`. Build the `.csproj` directly; nothing in the build depends on the solution file.

`AllowUnsafeBlocks` is required even though no source file contains the `unsafe` keyword. The `LibraryImport` source generator emits unsafe marshalling code for every P/Invoke in `NativeMethods.cs`, and removing the property fails the build with `SYSLIB1062`.

The app manifest requests `requireAdministrator`, so launching `Adrenalize.exe` always triggers UAC. To run the self-check without elevation, bypass the apphost and run the managed DLL:

```
dotnet bin/Debug/net10.0-windows/win-x64/Adrenalize.dll --selftest
```

`--selftest` runs `GameScanner.SelfTest()`, `UserSettings.SelfTest()`, `Logger.SelfTest()`, and `Program.SelfTestNativeInterop()`, prints `SelfTest OK`, and exits with code 0. It throws on failure. These are plain assertion methods, not a test framework — there is no test project and none is wanted. If you change name normalization, executable scoring, or the settings parser, extend the matching `SelfTest` method in that same file.

`SelfTestNativeInterop` exists because `EnumWindows` takes a managed callback marshalled as a function pointer, and broken callback marshalling fails silently rather than at compile time. It enumerates top-level windows and asserts the callback fired at least once. It needs an interactive window station, so it will report zero over a session that has none.

## Shipping A Single Executable

One command. No packaging step, no installer, no extra tooling:

```
dotnet publish -c Release
```

The result is one file:

```
bin\Release\net10.0-windows\win-x64\publish\Adrenalize.exe
```

Roughly 50 MB, and that is the whole application. Copy it anywhere and run it — no .NET runtime on the target machine, no DLLs beside it, no install. Double-clicking it raises a UAC prompt, which is the manifest doing its job.

A `Adrenalize.pdb` lands next to the exe. That is the debug symbol file, used only to get line numbers in stack traces. The exe does not need it. Ship the exe alone.

Four `.csproj` properties produce this, and all four are required:

| Property | Effect if removed |
| --- | --- |
| `SelfContained` | Target machine must have .NET 10 installed |
| `RuntimeIdentifier` | Cannot self-contain without a concrete target; `win-x64` here |
| `PublishSingleFile` | Publish folder fills with loose runtime DLLs instead of one file |
| `EnableCompressionInSingleFile` | Exe roughly doubles in size |

The size is the price of self-containment: the .NET runtime plus the WinForms stack are inside the exe. Compression is already on. Do not reach for trimming (`PublishTrimmed`) or Native AOT to shrink it — WinForms is not trim-safe and is unsupported under AOT, so both will either fail the build or produce an exe that crashes at runtime when the tray icon is created.

To publish for a different architecture, override the runtime identifier rather than editing the file:

```
dotnet publish -c Release -r win-arm64
```

## Formatting And Style Checks

Run this after every change, before reporting the work as done. It is not optional and it is not covered by `dotnet build`.

**CSharpier** formats all C#. It is installed as a global tool and invoked as `csharpier`, not `dotnet csharpier`:

```
csharpier format .
csharpier check .
```

`format` rewrites files in place, `check` reports without writing and exits 1 on a difference. Default width is 100 columns, which is what the existing layout matches. CSharpier leaves raw string literal contents alone, so the ASCII banner in `PrintConsoleHeader` is safe — but its closing `"""` sets the indent that gets stripped from every line, so do not re-indent that block by hand.

`.editorconfig` carries analyzer preferences only — naming rules, `var` usage, expression-bodied members. Every layout key it once held was removed because CSharpier owns layout and a second opinion on the same thing can only disagree. Do not add `csharp_space_*`, `csharp_new_line_*`, or `csharp_indent_*` keys back.

A `RazorStyle.ps1` script used to live at the repository root and check the razor conventions below. It was deleted: 366 lines checking zero files in a WinForms console app. The conventions survive as prose in Code Style, which is where they get read.

## Architecture

Flow, start to finish:

1. `Program.Main` claims a global mutex. A second instance signals the first through a named `EventWaitHandle` and exits, so double-clicking the exe again just un-hides the existing window.
2. Settings load from `%AppData%\Adrenalize\settings.ini`, then startup registration is applied to match.
3. A WinForms message pump starts on a background thread purely to host the tray icon.
4. `GameScanner.ScanInstalledGameProcessNames()` walks the disk and returns a `Dictionary<processName, displayName>`. It runs at startup and again on the tray's Rescan Games item.
5. Console input echo is switched off, then the monitoring loop polls `Process.GetProcesses()` every 2 seconds and diffs against the previous snapshot. A process name that appears and is in the game map triggers a reset.
6. `AmdReset.ExecuteReset()` stops AMD services, kills AMD processes, restarts the services, relaunches Adrenalin, and hides every window Adrenalin opens so nothing appears on screen.

### Files

| File | Responsibility |
| --- | --- |
| `Program.cs` | Entry point, console output, settings mutation, monitoring loop, console window state |
| `Amd/AmdReset.cs` | The whole reset sequence: services, processes, Adrenalin launch and close |
| `Game/GameScanner.cs` | Disk and registry scanning per launcher, executable picking, name normalization |
| `Configuration/UserSettings.cs` | INI load, parse, save |
| `Tray/TrayManager.cs` | Tray icon and context menu, calls straight into `Program` |
| `Startup/StartupManager.cs` | Task Scheduler registration and removal |
| `Native/NativeMethods.cs` | P/Invoke declarations and Win32 constants |
| `Utilities/Logger.cs` | Timestamped colored console output, mirrored to the log file |

## Design Decisions

These are the non-obvious calls. Do not undo them without a reason.

**Services are discovered and controlled through WMI, but waited on through `ServiceController`.** `System.Management` is a dependency because discovery needs `Win32_Service` — the filter has to match on `DisplayName` as well as `Name`, which `ServiceController` cannot do in one pass. `InvokeOnServices` enumerates and calls `InvokeMethod("StopService")` on the same objects, so no process is spawned. The previous version shelled out to `sc.exe` for every stop, start, and state poll — roughly 60 process launches per reset — and scraped the text output for `RUNNING` and `STOPPED`. Do not go back to that.

Waiting for the target state is `ServiceController.WaitForStatus`, which replaced about fifty lines that re-queried every service on the machine every 250 ms. `WaitForServiceStates` keeps one shared deadline across the whole list rather than giving each service its own timeout, or three services would turn a 15-second cap into 45. `WaitForStatus` throws on timeout and on a service that no longer exists; both are swallowed, because the old polling loop treated a vanished service as "reached" and the reset must continue either way.

**Startup uses Task Scheduler, not the `Run` registry key.** The app requires administrator rights. A `Run` key entry for an admin-manifested app produces a UAC prompt at every logon. A scheduled task with `RunLevel.Highest` does not. `StartupManager.Disable()` and `Enable()` both clear the old `Run` key entry, so upgrades from the registry-based versions clean themselves up.

**Elevation is enforced by the manifest, not by code.** There is no `IsAdministrator()` check and no self-relaunch path. Windows refuses to start the apphost unelevated, so such a check could never fail. This is why `--selftest` has to go through `dotnet <dll>`.

**Adrenalin is hidden by sweeping every top-level window it owns, not by `MainWindowHandle`.** Adrenalin ignores the hidden start style in `ProcessStartInfo` and shows itself anyway, usually maximized. The previous version posted one `WM_CLOSE` to `Process.MainWindowHandle` and gave up — `MainWindowHandle` returns the first top-level window of the main thread, which for Adrenalin is often an invisible helper, so the real window stayed on screen. `HideAdrenalinWindows` instead collects the PIDs of every `RadeonSoftware` and `RadeonSettings` process, walks all top-level windows with `EnumWindows`, and for each visible window owned by one of them calls `ShowWindow(SW_HIDE)` followed by `PostMessage(WM_CLOSE)`. Hiding kills the on-screen flash; `WM_CLOSE` is what Adrenalin interprets as "go to tray", so its internal state stays consistent. The sweep repeats every 200 ms until five consecutive passes find nothing, capped at 20 seconds, because the window appears late and can reappear once.

**The console accepts no input.** `DisableConsoleInput` clears `ENABLE_ECHO_INPUT` and `ENABLE_LINE_INPUT` on the standard input handle, so keystrokes neither echo nor form lines. There is no command parser — every action is a tray menu item. Do not add console commands back; that split meant the same three settings were written in two places. `GetConsoleMode` fails when standard input is redirected, which is why the call is guarded rather than asserted. `--selftest` deliberately does not call `DisableConsoleInput` — the mode change is not restored on exit, so a self-check run from an interactive terminal would leave that terminal with echo off.

**AMD processes are matched by name or by install path.** Some AMD binaries are not named `AMD` or `Radeon`, so `IsAmdProcess` falls back to checking the executable path against `s_amdExecutablePathMarkers`. `MainModule` throws for protected and already-exited processes, hence the swallowed exception. `IsAmdProcess` also refuses to match the current process — if the exe ever lives under an AMD path, the app would otherwise kill itself.

**The process kill is one sweep loop, not a pass plus a wait.** AMD services restart their helper processes, so a single kill pass is not enough. The loop kills, sleeps 200 ms, and repeats until a full pass finds nothing, capped at 10 seconds. Each PID is logged once.

**Executable picking rejects first, then scores.** A game folder usually holds several `.exe` files. `IsRejectedExecutable` drops every candidate whose name contains a token in `s_executableRejectTokens` — `helper`, `service`, `crash`, `report`, `uninstall`, `setup` — before any of them is scored. `ScoreExecutable` then rewards names containing `win64` or `shipping` and names matching the folder, and penalizes `launcher`. Highest score wins.

The order matters and used to be the other way round. The veto ran in `TryAddGame` on the already-chosen winner, so a folder whose best-scoring exe happened to be a crash handler lost the whole game instead of falling through to the runner-up. Rejecting during enumeration also collapses the two token lists into one job each: reject means never a game, penalty means probably not the best exe here. `launcher` is the only penalty token left, because a game that ships nothing but a launcher-named exe still needs to be watched.

These weights are tuning against real installs, not a general algorithm — adjust them when a game is detected wrongly, and add a case to the self-check.

**`NormalizeProcessKey` has hardcoded special cases.** Assetto Corsa and VALORANT ship under several executable names that must collapse to one key. This is calibration, not cruft. Add cases here when a game is missed for the same reason.

**Directory enumeration goes through `EnumerateSafely`.** `Directory.EnumerateFiles` is lazy, so a missing or locked directory throws on the first `MoveNext`, not at the call site. A `try` wrapped around the call would catch nothing. `EnumerateSafely` drives the enumerator manually and stops on any throw. `EnumerationOptions` also sets `IgnoreInaccessible` to `true`, which the `SearchOption` overloads do not.

**`TrayManager` calls `Program` static methods directly.** It used to take ten callback delegates in its constructor. There is one instance, created from one place, in the same assembly. Direct calls are shorter and easier to follow.

**Two poll loops with different intervals.** The game scan runs every 2 seconds because `Process.GetProcesses()` opens a handle per process and is not cheap. The minimize-to-tray watcher runs every 150 ms because a slower interval leaves the window visible for a noticeable beat after the user minimizes it. They are not merged on purpose.

**Settings stay in an INI file, not JSON.** `UserSettings.Parse` is about thirty hand-written lines that `JsonSerializer` would do in four, and swapping was considered. It was rejected: the win only materialises if the INI parser is deleted, and deleting it silently resets every existing user's settings on upgrade — including turning autostart off while leaving the scheduled task registered until the next launch removes it. Four stable booleans do not justify that. Adding a setting costs two lines in `Parse` and one in `Save`.

**The log file is a tee on `Console.Out`, not a logging path.** `Logger.StartLogFile` wraps the existing `Console.Out` in a `TeeWriter` and hands it to `Console.SetOut`, so everything printed — banner, version, status block, every `Log` line — lands in `%AppData%\Adrenalize\log.txt` without a single call site changing. Colors are set through `Console.ForegroundColor`, which does not pass through the writer, so the file stays plain text. `TextWriter.Synchronized` covers the poll loop and the tray thread writing at once, so there is no lock here. `TeeWriter` overrides only `Write(char)`; the base class routes every other overload through it, which is char-by-char but irrelevant at a few hundred lines per run. The file is truncated at startup rather than rotated, and `StartLogFile` is called after the single-instance check so a second launch cannot wipe the running instance's log.

**There is no cancellation plumbing.** The app exits through `Environment.Exit(0)`. Background loops are `while (true)` and die with the process. A previous "Restart Monitoring" feature existed, cancelled and respawned the loops, did not re-scan games, and re-fired a reset for whatever was already running. It was removed.

## Code Style

Follow the existing style exactly. It is enforced by hand, not by a formatter config in the repo, but the layout matches CSharpier defaults at 100 columns.

**Razor.** No `.razor` files exist yet. These apply if any are added. Prefer MudBlazor components; fall back to Bootstrap classes, then a MudBlazor `Style` property, then custom CSS, in that order. Prefer `MudElement` over plain HTML, and plain HTML only as a last resort — a raw `<div>` is a violation. Comments use `@* *@`, sit above a group of components, and contain only the main component name of that group. No comments above text, parameters, bindings, individual attributes, or small fragments. When a component has two or more attributes, each one after the first goes on its own line, aligned to the first. A component whose opening tag spans lines starts its content on the next line. Large containers such as `MudTable`, `MudGrid`, `MudDialog`, and `EditForm` get one blank line inside each end. Where a `.razor.cs` code-behind exists, all logic lives there and the `.razor` file gets no `@code` block.

**Naming.** Full descriptive names everywhere — variables, fields, properties, methods. No abbreviations, no single letters. `settings`, not `s`. `configuration`, not `cfg`. `executablePath`, not `exePath`. Static fields use the `s_` prefix, instance fields use `_`.

**Comments.** Very short and clear, a couple of words, up to about ten words when genuinely needed. Every word starts with a capital letter. No trailing punctuation. Always on their own line above the code they describe. Never multi-line.

Comments are allowed only inside method bodies, or above a group of related fields or properties. Do not comment classes, interfaces, models, or services. No XML documentation comments. Do not comment obvious code. Do not add comments to the `.csproj`.

```csharp
// Never Kill Ourselves
if (processInstance.Id == Environment.ProcessId)
    return false;
```

**Regions.** Only around methods, never around fields or properties. Only when there are two or more of them — never a single region on its own. No blank line directly after `#region` or directly before `#endregion`. One blank line before `#region` and one after `#endregion`.

```csharp
    #region Services
    private static List<string> StopAmdServices()
    {
    }
    #endregion
```

**User-facing text.** Console output, command names, tray labels, and balloon tips follow the same rule as comments: short, clear, each word capitalized, no trailing punctuation.

## Working Here

Before changing anything, read the file you are touching and every caller of the method you are about to edit. The codebase is small enough to hold in your head — do that instead of guessing.

Prefer deleting to adding. This repository was audited for over-engineering and lost roughly a third of its lines: dead configuration arrays, unused P/Invoke declarations, wrapper classes that only delegated, an unreachable elevation path, and four utility files that each existed for one caller. Do not reintroduce that shape. No interface with one implementation, no factory for one product, no configuration value nobody sets.

Reach for the standard library and the platform before writing code, and before adding a package. The three existing dependencies all earn their place: `System.Management` for WMI service discovery and control, `System.ServiceProcess.ServiceController` for waiting on service state, `TaskScheduler` because the logon-without-UAC requirement cannot be met any other way.

Verify with `dotnet build`, the `--selftest` run, and `csharpier format .`. All three, every time. Anything beyond parsing and scoring needs a real machine with AMD hardware and installed games, so state plainly what you did and did not verify.
