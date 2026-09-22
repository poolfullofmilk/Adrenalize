# CLAUDE.md

Guidance for Claude Code when working in this repository. Personal rules that apply everywhere — reporting style, git and commit rules, comment style, naming, regions, Razor and MudBlazor conventions, CSharpier and RazorStyle — live in `~/.claude/CLAUDE.md`. Only what is specific to Adrenalize is written here.

## What This Is

A single-project .NET 10 Windows console application that watches for a game process to start and then restarts AMD Adrenalin.

The problem it solves: AMD Adrenalin's overlay, performance metrics, and driver hooks frequently fail to attach when a game launches. Restarting the whole Adrenalin stack (services first, then processes, then the app) after the game is already running makes it attach reliably. Doing that by hand every session is tedious, so this automates it.

It is deliberately a console app with a tray icon rather than a GUI app. The console is a read-only status display, it prints what it found and what it is doing, and accepts no input. Every action lives in the tray icon's context menu. The tray icon also lets the window be hidden without killing the process.

Every source file sits at the repository root in one `Adrenalize` namespace. There are no folders and no sub-namespaces: eight files do not need seven directories.

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

`--selftest` runs `GameScanner.SelfTest()`, `UserSettings.SelfTest()`, `Logger.SelfTest()`, and `Program.SelfTestNativeInterop()`, prints `SelfTest OK`, and exits with code 0. It throws on failure. These are plain assertion methods, not a test framework, there is no test project and none is wanted. If you change name normalization, executable scoring, or the settings parser, extend the matching `SelfTest` method in that same file.

`SelfTestNativeInterop` exists because `EnumWindows` takes a managed callback marshalled as a function pointer, and broken callback marshalling fails silently rather than at compile time. It enumerates top-level windows and asserts the callback fired at least once. It needs an interactive window station, so it will report zero over a session that has none.

Anything past parsing, scoring, and settings needs a real machine with AMD hardware and installed games. State plainly what was verified and what was not.

## Shipping A Single Executable

Release builds take their version from `<Version>` in the `.csproj`, and the published exe is renamed to match before it is attached to a release.

One command. No packaging step, no installer, no extra tooling:

```
dotnet publish -c Release
```

The result is one file:

```
bin\Release\net10.0-windows\win-x64\publish\Adrenalize.exe
```

Roughly 50 MB, and that is the whole application. Copy it anywhere and run it, no .NET runtime on the target machine, no DLLs beside it, no install. Double-clicking it raises a UAC prompt, which is the manifest doing its job.

A `Adrenalize.pdb` lands next to the exe. That is the debug symbol file, used only to get line numbers in stack traces. The exe does not need it. Ship the exe alone.

Four `.csproj` properties produce this, and all four are required:

| Property | Effect if removed |
| --- | --- |
| `SelfContained` | Target machine must have .NET 10 installed |
| `RuntimeIdentifier` | Cannot self-contain without a concrete target; `win-x64` here |
| `PublishSingleFile` | Publish folder fills with loose runtime DLLs instead of one file |
| `EnableCompressionInSingleFile` | Exe roughly doubles in size |

The size is the price of self-containment: the .NET runtime plus the WinForms stack are inside the exe. Compression is already on. Do not reach for trimming (`PublishTrimmed`) or Native AOT to shrink it, WinForms is not trim-safe and is unsupported under AOT, so both will either fail the build or produce an exe that crashes at runtime when the tray icon is created.

To publish for a different architecture, override the runtime identifier rather than editing the file:

```
dotnet publish -c Release -r win-arm64
```

## Formatting And Style Checks

Run this after every change, before reporting the work as done. It is not optional and it is not covered by `dotnet build`.

CSharpier formats all C#. It is installed as a global tool and invoked as `csharpier`, not `dotnet csharpier`:

```
csharpier format .
csharpier check .
```

`format` rewrites files in place, `check` reports without writing and exits 1 on a difference. Default width is 100 columns, which is what the existing layout matches. CSharpier leaves raw string literal contents alone, so the ASCII banner in `PrintConsoleHeader` is safe, but its closing quotes set the indent that gets stripped from every line, so do not re-indent that block by hand.

`.editorconfig` carries the three naming rules, the file-scoped namespace rule, and `var` preferences. Nothing else. Every layout key was removed because CSharpier owns layout, and roughly ninety lines of suggestion-level and silent-level entries were removed because they only restated Roslyn defaults. Do not add `csharp_space_*`, `csharp_new_line_*`, or `csharp_indent_*` keys back, and do not re-add preference keys that change no diagnostic.

## Architecture

Flow, start to finish:

1. `Program.Main` claims a global mutex. A second instance signals the first through a named `EventWaitHandle` and exits, so double-clicking the exe again just un-hides the existing window.
2. Settings load from `%AppData%\Adrenalize\settings.ini`, then startup registration is applied to match.
3. A WinForms message pump starts on a background thread purely to host the tray icon.
4. `GameScanner.ScanInstalledGameProcessNames()` walks the disk and returns a map of process name to display name. It runs at startup and again on the tray's Rescan Games item.
5. Console input echo is switched off, then WMI event watchers subscribe to process creation, process deletion and AMD service state. A started or exited game triggers a reset 30 seconds later, a dead AMD service triggers a repair, and Adrenalin exiting on its own is simply started again.
6. `AmdReset.ExecuteReset()` stops AMD services, kills AMD processes, restarts the services, restarts Adrenalin as the signed-in user through AMD's own logon command, then verifies the result and returns whether the reset actually completed.

### Files

| File | Responsibility |
| --- | --- |
| `Program.cs` | Entry point, console output, settings table, monitoring loop, console window state |
| `AmdReset.cs` | The whole reset sequence: services, processes, Adrenalin launch and hide |
| `GameScanner.cs` | Disk and registry scanning per launcher, executable picking, name normalization |
| `UserSettings.cs` | INI load, parse, save |
| `TrayManager.cs` | Tray icon and context menu, calls straight into `Program` |
| `StartupManager.cs` | Task Scheduler registration and removal |
| `NativeMethods.cs` | P/Invoke declarations and Win32 constants |
| `Logger.cs` | Timestamped colored console output, mirrored to the log file |

## Design Decisions

These are the non-obvious calls. Do not undo them without a reason.

**A reset repairs, it does not just cycle.** The start phase starts every service in `s_requiredServiceNames` that is not Running, not only the ones this reset stopped. That sounds like a detail and was the single worst bug in the app: AMD's services crash on their own after some games exit, and a reset run in that state stopped nothing, therefore started nothing, and still printed Reset Done. The whole point of the tool, repairing a broken stack, silently did not work, and the only way out was killing every AMD process by hand in Task Manager.

**Four things trigger a reset.** A watched game starting, a watched game exiting, an AMD service dying, and a health check one minute after launch that repairs a stack which was already broken before the app started. The exit reset waits the same 30 seconds as the start reset, because AMD tends to fall over shortly after a game closes rather than at the moment it closes. Adrenalin disappearing on its own is handled separately and cheaply: `RestartAdrenalin` starts it again without touching services, since the services are usually fine when only the interface dies.

**AMD service crashes trigger a repair on their own.** A second WMI subscription watches for `AMD External Events Utility` or `AMD Crash Defender Service` going to Stopped and runs a reset when it happens, because this is how the stack breaks in practice: the services die a minute or two after a game exits, Adrenalin is left orphaned, and nothing in Windows brings them back. Events are ignored while a reset is in flight, since a reset stops those services itself, and a two minute cooldown keeps a crash loop from resetting over and over.

**A reset reports whether it worked.** `ExecuteReset` returns a bool. `VerifyReset` checks that every service it stopped is Running again and that an Adrenalin process exists, logging each failure in red. Before this the app printed Reset Done unconditionally, including the weeks where the reset stopped everything and Adrenalin never came back.

**No window is a success, not a failure.** Adrenalin sometimes comes up with no window at all, which is exactly what this app wants, so a reset that never sees one still counts as done. A version that demanded a window before calling the reset complete was wrong and reported Reset Incomplete on perfectly good runs.

**The reset waits 30 seconds after a game starts.** The old delay was 2 seconds, which fired while the game was still loading and before its driver hooks existed. It is deliberately not configurable: one value that is long enough for every game beats a setting nobody knows how to tune. The game is re-checked after the wait, so a launch that was closed again in the meantime is skipped.

**Extra games come from `games.txt`.** `%AppData%\Adrenalize\games.txt` holds one process name per line and is merged into the scan result. The file is created with two comment lines on first run so it can be discovered without reading this document. It exists because the scanner covers Steam, Epic, Riot, Roblox, Rockstar and three common folders, and anything outside those was previously unreachable without a code change.

**Only the two services Adrenalin needs are touched.** `s_requiredServiceNames` names them outright: AMD External Events Utility and AMD Crash Defender Service. The previous version matched any service whose name or display name contained AMD or Radeon, which also restarted `AmdPpkgSvc`, a driver-install provisioning service Adrenalin does not use and the slowest thing in the reset at roughly 12 seconds. Naming the services also means a reset never touches something unrelated on a machine with other AMD software. Log lines use the WMI `DisplayName`, not the service key.

**Services are discovered and controlled through WMI, but waited on through `ServiceController`.** `System.Management` is a dependency because discovery needs `Win32_Service`, which carries `DisplayName` and `State` in the same query. `ControlServices` takes the filter, the method name, the target state and the timeout, so stopping and starting are one method rather than two near-identical ones. It enumerates and calls `InvokeMethod` on the same objects, so no process is spawned, and it logs any non-zero return code instead of discarding it, which is how a silently failing stop or start used to go unnoticed. The previous version shelled out to `sc.exe` for every stop, start, and state poll, roughly 60 process launches per reset, and scraped the text output for RUNNING and STOPPED. Do not go back to that.

Waiting for the target state is `ServiceController.WaitForStatus`, which replaced about fifty lines that re-queried every service on the machine every 250 ms. `WaitForServiceStates` keeps one shared deadline across the whole list rather than giving each service its own timeout, or three services would turn a 15-second cap into 45. `WaitForStatus` throws on timeout and on a service that no longer exists; both are swallowed, because the old polling loop treated a vanished service as reached and the reset must continue either way. The start wait is 25 seconds rather than 10 because `AmdPpkgSvc` takes about 12 seconds to reach Running, while the two services Adrenalin actually needs are up instantly. A shorter cap made the verification step report a false failure every run. The reset continues either way, so the deadline is a cap, not a requirement.

**Adrenalin is started with `cncmd.exe startwithdelay`, the command Windows runs at logon.** AMD registers a scheduled task called StartCN that runs exactly this, which is why Adrenalin is in the tray after a logon without a window ever appearing. Starting `RadeonSoftware.exe` directly does the opposite: the window opens, usually maximized, anywhere from twenty seconds to over a minute later, and hiding it afterwards is a race that flashes over a fullscreen game and can steal its focus. Measured with a 400 ms sampler across a full reset: 427 samples with Adrenalin running, zero with a visible window. The command sleeps about eighteen seconds before it spawns anything, which is why the wait for the process is 60 seconds.

**The command runs through a throwaway scheduled task, not `Process.Start`.** Adrenalin 26.10 quits during its own startup when it inherits elevation, the process appears, shows its splash, and exits after roughly eight seconds. The app is manifest-elevated, so anything it starts directly inherits a high-integrity token and dies. `RunAsSignedInUser` registers a task named `AdrenalizeLaunch` with `TaskRunLevel.LUA` and an interactive logon type, runs it, waits a second for the spawn and deletes it; a task-launched process keeps running after its task is gone. The `TaskScheduler` package is already a dependency for startup registration, so this costs nothing new. Do not switch this to `Process.Start`, and do not reach for `explorer.exe` as the de-elevation trick, from an elevated process it launches a second elevated Explorer that starts the target elevated anyway, which was tried and fails the same way.

**The hide sweep is insurance, not the mechanism.** Because the logon command never draws a window there is normally nothing to hide, so `HideAdrenalin` watches for only 15 seconds. If a window does turn up it gets `ShowWindow(SW_HIDE)` within 100 ms and then `cncmd.exe hide` to put AMD's own state in step, and that path logs in yellow because it means the launch behaved unexpectedly. Do not grow this back into a 60-second watcher; if the window starts appearing again, the launch command is what changed.

**Startup uses Task Scheduler, not the `Run` registry key.** The app requires administrator rights. A `Run` key entry for an admin-manifested app produces a UAC prompt at every logon. A scheduled task with `RunLevel.Highest` does not. `StartupManager.Disable()` and `Enable()` both clear the old `Run` key entry, so upgrades from the registry-based versions clean themselves up.

**Elevation is enforced by the manifest, not by code.** There is no `IsAdministrator()` check and no self-relaunch path. Windows refuses to start the apphost unelevated, so such a check could never fail. This is why `--selftest` has to go through `dotnet` on the DLL.

**One table drives every setting.** `Program.SettingToggles` holds a label, a reader, and a writer per setting. The tray builds its checkable menu items by looping it, `RefreshToggleStates` re-reads it when the menu opens, and `PrintSettingsStatus` prints it. Before, the same four settings were listed once as tray toggles, once as four near-identical setter methods, and once as a local array in the status printer. Adding a setting now costs one row plus two lines in `UserSettings`.

**The console accepts no input.** `DisableConsoleInput` clears `ENABLE_ECHO_INPUT` and `ENABLE_LINE_INPUT` on the standard input handle, so keystrokes neither echo nor form lines. There is no command parser, every action is a tray menu item. Do not add console commands back, that split meant the same settings were written in two places. `GetConsoleMode` fails when standard input is redirected, which is why the call is guarded rather than asserted. `--selftest` deliberately does not call `DisableConsoleInput`, the mode change is not restored on exit, so a self-check run from an interactive terminal would leave that terminal with echo off.

**AMD processes are matched by publisher first.** `IsAmdProcess` reads `FileVersionInfo.CompanyName` and matches Advanced Micro Devices, which is the same signal Task Manager shows and the only one that stays true across driver versions. Names and install paths are the fallback for processes whose `MainModule` cannot be read. Before this the matcher guessed from names and folders, so a binary named neither AMD nor Radeon and living outside the known folders was left running, which is why killing everything by hand worked better than a reset. `GetForeignServiceProcessIds` collects the process ids of every running service outside `s_requiredServiceNames` and the sweep skips them, so a reset can never leave an unrelated service dead. `MainModule` throws for protected and already-exited processes, hence the swallowed exception. `IsAmdProcess` also refuses to match the current process, if the exe ever lives under an AMD path the app would otherwise kill itself. `ContainsAmdKeyword` rejects anything containing AMD64 first, because that is an architecture suffix: without it, Logitech's `logi_lamparray_service.AMD64` matched the AMD keyword and was killed on every reset.

**The process kill is one sweep loop, not a pass plus a wait.** AMD services restart their helper processes, so a single kill pass is not enough. The loop kills, sleeps 200 ms, and repeats until a full pass finds nothing, capped at 10 seconds. Each PID is logged once.

**Executable picking rejects first, then scores.** A game folder usually holds several `.exe` files. `IsRejectedExecutable` drops every candidate whose name contains a token in `s_executableRejectTokens`, which is helper, service, crash, report, uninstall, and setup, before any of them is scored. `ScoreExecutable` then rewards names containing win64 or shipping and names matching the folder, and subtracts five for launcher. Highest score wins. Both take the bare file name, computed once per candidate by the caller.

The order matters and used to be the other way round. The veto ran in `TryAddGame` on the already-chosen winner, so a folder whose best-scoring exe happened to be a crash handler lost the whole game instead of falling through to the runner-up. Rejecting during enumeration also splits the job cleanly: reject means never a game, penalty means probably not the best exe here. Launcher is the only penalty, and it is an inline check rather than a token list, because a game that ships nothing but a launcher-named exe still needs to be watched.

These weights are tuning against real installs, not a general algorithm, adjust them when a game is detected wrongly, and add a case to the self-check.

**`NormalizeProcessKey` has hardcoded special cases.** Assetto Corsa and VALORANT ship under several executable names that must collapse to one key. This is calibration, not cruft. Add cases here when a game is missed for the same reason.

**Directory enumeration materialises inside the `try`.** `Directory.EnumerateFiles` is lazy, so a missing or locked directory throws on the first `MoveNext`, not at the call site, and a `try` around a returned iterator catches nothing. Building the list inside the `try` moves every throw back where it can be caught, which replaced a 25-line hand-rolled safe enumerator with two four-line methods. `EnumerationOptions` also sets `IgnoreInaccessible` to true, which the `SearchOption` overloads do not.

**`TrayManager` calls `Program` static methods directly.** It used to take ten callback delegates in its constructor. There is one instance, created from one place, in the same assembly. Direct calls are shorter and easier to follow.

**Nothing polls.** Game detection is a WMI `__InstanceCreationEvent` subscription on `Win32_Process`, which needs elevation the app already has and costs nothing while idle. The previous version called `Process.GetProcesses()` every 2 seconds forever, opening a handle per process each time, and detected a launch up to 2 seconds late. Minimize-to-tray is a `SetWinEventHook` on `EVENT_SYSTEM_MINIMIZESTART` installed on the WinForms pump thread, replacing a 150 ms poll of `IsIconic`. The hook callback delegate is held in a static field because the runtime would otherwise collect it while Windows still holds the pointer. Both watchers need the pump or the event thread to stay alive, so `Main` ends on `Task.Delay(Timeout.Infinite)` rather than a loop.

**Incoming process names are normalized before lookup.** `GameScanner.NormalizeProcessKey` builds the map keys, so the watcher runs the same function over the started process name. The old poll compared the raw name against normalized keys, which silently missed exactly the games the special cases exist for: `VALORANT-Win64-Shipping` never matched the key `valorant`.

**Settings stay in an INI file, not JSON.** `UserSettings.Parse` is about thirty hand-written lines that `JsonSerializer` would do in four, and swapping was considered. It was rejected: the win only materialises if the INI parser is deleted, and deleting it silently resets every existing user's settings on upgrade, including turning autostart off while leaving the scheduled task registered until the next launch removes it. Four stable booleans do not justify that.

**The log file is a tee on `Console.Out`, not a logging path.** `Logger.StartLogFile` wraps the existing `Console.Out` in a `TeeWriter` and hands it to `Console.SetOut`, so everything printed lands in `%AppData%\Adrenalize\log.txt` without a single call site changing. Colors are set through `Console.ForegroundColor`, which does not pass through the writer, so the file stays plain text. `TextWriter.Synchronized` covers the poll loop and the tray thread writing at once, so there is no lock here. `TeeWriter` overrides only `Write(char)`, the base class routes every other overload through it, which is char-by-char but irrelevant at a few hundred lines per run. The file is truncated at startup rather than rotated, and `StartLogFile` is called after the single-instance check so a second launch cannot wipe the running instance's log.

**There is no cancellation plumbing.** The app exits through `Environment.Exit(0)`. Background loops are `while (true)` and die with the process. A previous Restart Monitoring feature existed, cancelled and respawned the loops, did not re-scan games, and re-fired a reset for whatever was already running. It was removed.

## Working Here

This codebase is small enough to hold in your head, so read it instead of guessing. This repository has been audited for over-engineering three times and lost roughly a third of its lines: dead configuration arrays, unused P/Invoke declarations, wrapper classes that only delegated, an unreachable elevation path, four utility files that each existed for one caller, seven single-file folders, and ninety lines of `.editorconfig` that changed no diagnostic. Do not reintroduce that shape.

The three dependencies all earn their place: `System.Management` for WMI service discovery and control, `System.ServiceProcess.ServiceController` for waiting on service state, and `TaskScheduler` for both the logon-without-UAC requirement and the de-elevated Adrenalin launch.

Verify with `dotnet build`, the `--selftest` run, and `csharpier format .`. All three, every time.
