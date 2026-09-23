@echo off
chcp 65001 >nul
title 编译 USB 急救工具

set "ROOT=%~dp0"
set "SRC=%ROOT%源码"
set "OUT=%ROOT%发布"

echo [0/3] 检查前端依赖...
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

echo [1/3] 构建前端...
call npm run build
if errorlevel 1 (
  echo [失败] 前端构建出错
  pause
  exit /b 1
)

echo.
echo [2/3] 发布 C# 程序...
cd /d "%SRC%\USBFixTool"
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:ApplicationManifest=app.manifest
if errorlevel 1 (
  echo [失败] 后端编译出错
  pause
  exit /b 1
)

echo.
echo [3/3] 同步到 发布\ ...
set "PUBLISH=%SRC%\USBFixTool\bin\Release\net9.0-windows\win-x64\publish"
if not exist "%OUT%" mkdir "%OUT%"
copy /Y "%PUBLISH%\USB急救工具.exe" "%OUT%\USB急救工具.exe" >nul
if exist "%OUT%\wwwroot" rd /s /q "%OUT%\wwwroot"
xcopy /E /I /Y "%PUBLISH%\wwwroot" "%OUT%\wwwroot" >nul
REM 字体体积大，确保从构建产物补齐
if exist "%SRC%\USBFixTool\wwwroot\fonts" xcopy /E /I /Y "%SRC%\USBFixTool\wwwroot\fonts" "%OUT%\wwwroot\fonts\" >nul
if exist "%OUT%\wwwroot\wwwroot" rd /s /q "%OUT%\wwwroot\wwwroot"
xcopy /E /I /Y "%SRC%\lib" "%OUT%\lib\" >nul
if not exist "%OUT%\Remote" mkdir "%OUT%\Remote"

echo.
echo ========================================
echo   完成：发布\USB急救工具.exe
echo   把整个「发布」文件夹拷到 U 盘即可
echo ========================================
pause
