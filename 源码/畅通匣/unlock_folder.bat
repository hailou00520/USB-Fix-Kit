@echo off
if exist "%~dp0畅通匣.exe" (
  start "" "%~dp0畅通匣.exe" %*
) else (
  start "" "%~dp0unlock_folder.exe" %*
)
