# Adrenalize

Automatically Restarts AMD Adrenalin When A Game Launches So Its Overlay, Metrics And Driver Hooks Attach Every Time

![Adrenalize](Screenshot.png)

## The Problem

AMD Adrenalin regularly fails to hook a game that is already running. The overlay does not open, performance metrics stay empty, and recording does nothing. Restarting the whole Adrenalin stack after the game has started fixes it every time, and doing that by hand every session is tedious.

Adrenalize watches for a game to start, waits until it has finished loading, then restarts AMD Adrenalin in the background. Nothing appears on screen and nothing needs a click.

## Features
- Detects Installed Games From Steam, Epic, Riot, Roblox, Rockstar And Common Game Folders
- Reacts To A Game Starting Through Windows Process Events, Nothing Is Polled
- Full Reset: Stop Services, Kill Processes, Restart Services, Relaunch Adrenalin In The Background
- Only The Two Services Adrenalin Needs Are Touched, Nothing Else On The System
- Reports Whether The Reset Actually Completed Instead Of Assuming It Did
- Tray Menu For Manual Reset, Status And Rescan Games
- Optional: Run On Startup, Minimize To Tray, Start Minimized, Notifications

## Quick Start
1. Download And Run Adrenalize_v2.0.exe
2. Accept The UAC Prompt
3. Wait For The Game Scan To Finish
4. Launch A Game
5. Adrenalin Restarts Itself 30 Seconds Later, In The Background

## Adding A Game

The scanner covers the common launchers. Anything it misses goes in `%AppData%\Adrenalize\games.txt`, one process name per line, then pick Rescan Games in the tray menu.

```
# One Process Name Per Line
Cyberpunk2077
```

## Important
- Administrator Rights Are Required To Stop And Start The AMD Services
- The Console Is A Read Only Status Display, Every Action Lives In The Tray Menu
- The Log File Is At %AppData%\Adrenalize\log.txt
- Only Scanned Games Trigger A Reset, Use Rescan Games After Installing One

## Technical Details
- Windows Console App With A Tray Icon, Built With C# On .NET 10
- Game Detection Runs On A WMI Process Creation Subscription
- AMD Services Are Controlled Through WMI And Waited On With ServiceController
- Adrenalin Is Relaunched Without Elevation, Recent Drivers Quit When They Inherit Admin Rights
- Startup Runs Through Task Scheduler So Logon Never Shows A UAC Prompt
- Ships As One Self Contained Executable, No Runtime Or Install Needed

## Download
Get The Latest Version From The [Latest Release](https://github.com/poolfullofmilk/Adrenalize/releases/latest)
