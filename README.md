# AutomationAgent

Lightweight Windows tray app that launches and monitors a runner process (controlled via `run.cmd`). Provides a system-tray UI, local HTTP API, automatic restart (watchdog), and prevents system sleep/display while the runner is active.

---

## Quick start 🚀

- Build and run from source (development):

  dotnet build -c Debug
  dotnet run --project AutomationAgent.csproj

- Run the published executable (recommended for deployment):

  dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
  (then run `bin\Release\net8.0-windows\win-x64\publish\AutomationAgent.exe`)

- Important: make sure `run.cmd` (your runner startup script) is placed next to the exe — the app uses it to start the runner. The runner is started hidden; no console window will be shown by the application.

---

## Run at user logon (Task Scheduler) 📅

To run AutomationAgent automatically when a user logs on, create a scheduled task.

- Simple command (current user):

  schtasks /create /sc ONLOGON /tn "AutomationAgent" /tr ""C:\\Path\\To\\AutomationAgent.exe"" /rl HIGHEST /f

- PowerShell (current user):

  $action = New-ScheduledTaskAction -Execute "C:\\Path\\To\\AutomationAgent.exe"
  $trigger = New-ScheduledTaskTrigger -AtLogOn
  Register-ScheduledTask -TaskName "AutomationAgent" -Action $action -Trigger $trigger -RunLevel Highest

Notes:
- Replace `C:\\Path\\To\\AutomationAgent.exe` with the installed executable path.
- Use the interactive user account for apps that interact with the desktop (recommended).
- To remove: `schtasks /delete /tn "AutomationAgent" /f` or `Unregister-ScheduledTask -TaskName "AutomationAgent" -Confirm:$false`.

---

## Features ✅

- Tray application with context menu: Start, Stop, Restart runner, Exit.  
- Visual status via tray icon & tooltip (Running / Stopped / Error).  
- Watchdog: automatically restarts the runner when it exits unexpectedly.  
- Prevents system sleep & display turn‑off while active (keeps screen awake / prevents screensaver).  
- Local HTTP API (control and status): `http://localhost:9000` (see endpoints).  
- Launcher: executes `run.cmd` (must be present in application directory).  
- Per-run log files: `logs/automation-agent-<timestamp>.log`.  
- Auto-start registry entry (adds HKCU Run key on first run).  
- Clean icon & disposal handling (no native resource leaks).

---

## HTTP API (local) 🔌

Base: `http://localhost:9000`

- GET  /status    — returns { status: "running|stopped|error" }
- POST /start     — starts the runner
- POST /stop      — stops the runner
- POST /restart   — restarts the runner

Examples:

  curl http://localhost:9000/status
  curl -X POST http://localhost:9000/start

Or PowerShell:

  Invoke-RestMethod -Uri http://localhost:9000/status -Method GET
  Invoke-RestMethod -Uri http://localhost:9000/start -Method POST

---

## Files & locations 📁

- App executable: the folder where `AutomationAgent.exe` lives.  
- Runner script (required): `run.cmd` (must be updated to launch your real runner).  
- Logs: `logs/automation-agent-<yyyyMMdd-HHmmss>.log` (rotating by start time).  
- Runner logs: `logs/runner-<yyyyMMdd-HHmmss>.log` — dedicated runner output; **retained: last 5 files only**.

---

## Configuration & customization ⚙️

- Replace `run.cmd` with your runner script — the app will call `cmd.exe /c run.cmd` from its working directory.  
- To bundle `run.cmd` in published output, keep it next to the exe (project already copies it to publish output).  
- Port: API listens on `localhost:9000` (change requires code edit).

---

## Troubleshooting & tips 🛠️

- Runner doesn't start: verify `run.cmd` exists next to the EXE and is executable.  
- `status` reports `error`: check the latest log in `logs/`.  
- API unreachable: ensure no other process is using port `9000`.  
- Tray menu hangs: restart the app; if it persists, check logs for unhandled exceptions.  
- To run at logon for all users or as a service, use Task Scheduler or wrap with a service manager — the app is a GUI tray app and should run under an interactive user account.

---

## Uninstall / Remove scheduled start ❌

- Delete the scheduled task: `schtasks /delete /tn "AutomationAgent" /f`  
- Remove install directory and registry Run key (if present): `HKCU:\Software\Microsoft\Windows\CurrentVersion\Run\AutomationAgent`.

---

## Development notes for maintainers 🧰

- Runner logic: `Services/RunnerService.cs` — starts/stops `run.cmd`.  
- Watchdog: `Services/WatchdogService.cs` — monitors runner and prevents sleep.  
- Tray UI: `Core/TrayAppContext.cs` — NotifyIcon, context menu, icon handling.  
- API: `Services/ApiService.cs` — HTTP listener for control endpoints.  
- Logger: `Utils/Logger.cs` — async log writer (writes to logs/).  

---

If you want, I can draft a shorter end‑user README or add installation scripts for an MSI/zip. Tell me which format you prefer.
