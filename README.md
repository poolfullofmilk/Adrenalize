# Adrenalize

Automatically Restarts AMD Adrenalin When A Game Launches So Its Overlay And Metrics Attach Every Time

## Features
- Detects Installed Games From Steam, Epic, Riot, Roblox, Rockstar And Common Game Folders
- Watches For A Game To Start And Resets Adrenalin Seconds Later
- Full Reset: Stop Services, Kill Processes, Restart Services, Relaunch Adrenalin Hidden
- Tray Menu For Manual Reset, Status And Rescan Games
- Optional: Run On Startup, Minimize To Tray, Start Minimized, Notifications

## Quick Start
1. Download And Run Adrenalize.exe
2. Accept The UAC Prompt
3. Wait For The Game Scan To Finish
4. Launch A Game
5. Adrenalin Resets Itself In The Background

## ⚠️ Important
- Administrator Rights Are Required To Stop And Start The AMD Services
- The Console Is A Read Only Status Display, Every Action Lives In The Tray Menu
- Only Scanned Games Trigger A Reset, Use Rescan Games After Installing One

## Technical Details
- Windows Console App With A Tray Icon, Built With C# On .NET 10
- AMD Services Discovered And Controlled Through WMI, Waited On With ServiceController
- Startup Runs Through Task Scheduler So Logon Never Shows A UAC Prompt
- Ships As One Self Contained Executable, No Runtime Or Install Needed

## Download
Get The Latest Version From The [Latest Release](https://github.com/poolfullofmilk/Adrenalize/releases/latest)
