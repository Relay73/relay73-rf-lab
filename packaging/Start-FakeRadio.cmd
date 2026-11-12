@echo off
cd /d "%~dp0"
echo FakeRadio software-only soundcard lab. No real radio PTT is controlled.
echo Press Ctrl+C to stop the simulation.
"%~dp0FakeRadio.exe"
if errorlevel 1 pause
