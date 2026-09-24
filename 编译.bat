@echo off
chcp 65001 >nul
title 编译 USB 急救工具 + 畅通匣

set "ROOT=%~dp0"
set "SRC=%ROOT%源码"
set "OUT=%ROOT%发布"

echo [0/4] 检查前端依赖...
cd /d "%SRC%\web"
if not exist "node_modules\" (
  echo 首次编译，正在 npm install ...
  call npm install
  if errorlevel 1 (
    echo [失败] npm install 出错
    pause
    exit /b 1
  )
)

echo [1/4] 构建前端...
call npm run build
if errorlevel 1 (
  echo [失败] 前端构建出错
  pause
  exit /b 1
)

echo.
echo [2/4] 发布 C# 程序...
cd /d "%SRC%\USBFixTool"
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:ApplicationManifest=app.manifest
if errorlevel 1 (
  echo [失败] 后端编译出错
  pause
  exit /b 1
)

echo.
echo [3/4] 同步到 发布\ ...
set "PUBLISH=%SRC%\USBFixTool\bin\Release\net9.0-windows\win-x64\publish"
if not exist "%OUT%" mkdir "%OUT%"
copy /Y "%PUBLISH%\USB急救工具.exe" "%OUT%\USB急救工具.exe" >nul
if exist "%OUT%\wwwroot" rd /s /q "%OUT%\wwwroot"
xcopy /E /I /Y "%PUBLISH%\wwwroot" "%OUT%\wwwroot" >nul
if exist "%SRC%\USBFixTool\wwwroot\fonts" xcopy /E /I /Y "%SRC%\USBFixTool\wwwroot\fonts" "%OUT%\wwwroot\fonts\" >nul
if exist "%OUT%\wwwroot\wwwroot" rd /s /q "%OUT%\wwwroot\wwwroot"
xcopy /E /I /Y "%SRC%\lib" "%OUT%\lib\" >nul
if not exist "%OUT%\Remote" mkdir "%OUT%\Remote"

echo.
echo [4/4] 同步畅通匣后端到 发布\畅通匣\ ...
if not exist "%OUT%\畅通匣" mkdir "%OUT%\畅通匣"
copy /Y "%SRC%\畅通匣\unlock_folder.py" "%OUT%\畅通匣\unlock_folder.py" >nul
if exist "%SRC%\畅通匣\畅通匣.exe" copy /Y "%SRC%\畅通匣\畅通匣.exe" "%OUT%\畅通匣.exe" >nul
if exist "%SRC%\畅通匣\畅通匣.bat" copy /Y "%SRC%\畅通匣\畅通匣.bat" "%OUT%\畅通匣.bat" >nul
if exist "%SRC%\畅通匣\畅通匣.ico" copy /Y "%SRC%\畅通匣\畅通匣.ico" "%OUT%\畅通匣.ico" >nul

echo.
echo ========================================
echo   完成：发布\USB急救工具.exe
echo   畅通匣脚本：发布\畅通匣\unlock_folder.py
echo   （畅通匣页需要本机 Python，或自备 畅通匣.exe）
echo   把整个「发布」文件夹拷到 U 盘即可
echo ========================================
pause
