@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"
title Upload to GitHub

echo ========================================
echo   USB Fix Kit - Upload to GitHub
echo ========================================
echo.

set "GIT=C:\Program Files\Git\cmd\git.exe"
set "GH=%~dp0tools\gh\bin\gh.exe"
if not exist "%GH%" set "GH=%~dp0tools\gh\gh.exe"
if not exist "%GH%" if exist "%~dp0tools\gh" (
  for /r "%~dp0tools\gh" %%F in (gh.exe) do set "GH=%%F"
)

if not exist "%GIT%" (
  echo [ERROR] Git not found: "%GIT%"
  echo Install Git first, then run this again.
  goto :END
)

if not exist "%GH%" (
  echo [ERROR] GitHub CLI ^(gh.exe^) not found.
  echo Expected: %~dp0tools\gh\bin\gh.exe
  echo.
  echo Install with: winget install --id GitHub.cli -e
  goto :END
)

echo Using Git: "%GIT%"
echo Using gh:  "%GH%"
echo.

"%GH%" auth status
if errorlevel 1 (
  echo.
  echo Login required. A browser window will open...
  echo Follow the prompts in this window, then press Enter in browser.
  echo.
  "%GH%" auth login -h github.com -p https -w
  if errorlevel 1 (
    echo [ERROR] Login failed.
    goto :END
  )
)

echo.
echo Creating repo and pushing...
"%GH%" repo create USB-Fix-Kit --public --source=. --remote=origin --push --description "USB Fix Kit"
if errorlevel 1 (
  echo.
  echo Repo may already exist. Trying push...
  "%GIT%" remote remove origin 2>nul
  set "GHUSER="
  for /f "usebackq delims=" %%u in (`"%GH%" api user -q .login`) do set "GHUSER=%%u"
  if "!GHUSER!"=="" (
    echo [ERROR] Cannot read GitHub username. Run: "%GH%" auth login
    goto :END
  )
  echo Remote: https://github.com/!GHUSER!/USB-Fix-Kit.git
  "%GIT%" remote add origin "https://github.com/!GHUSER!/USB-Fix-Kit.git"
  "%GIT%" push -u origin main
  if errorlevel 1 (
    echo [ERROR] Push failed.
    goto :END
  )
)

echo.
echo Done. Opening repo in browser...
"%GH%" repo view --web

:END
echo.
echo ----------------------------------------
echo Press any key to close this window.
pause >nul
endlocal
