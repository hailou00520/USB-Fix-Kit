@echo off
chcp 65001 >nul
cd /d "%~dp0"
:: 预览你记得的那套 PE 网页界面（青绿大按钮 + 页签）
start "" "%~dp0USB急救工具.exe" --pe
