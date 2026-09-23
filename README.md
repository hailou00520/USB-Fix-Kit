# USB 急救工具

PE / Windows 下的 USB 修复与开机自检部署工具。

## 结构

- `源码/USBFixTool` — C# WinForms + WebView2 宿主（self-contained）
- `源码/web` — React 界面
- `源码/lib` — PE / Windows 脚本
- `发布/` — 可直接拷到 U 盘的发布包（本仓库不包含 exe）
- `编译.bat` — 一键编译前端 + 后端到 `发布/`

## 本地编译

1. 安装 .NET 9 SDK、Node.js
2. （可选）将苹方可变字体放到 `源码/web/public/fonts/`：
   - `PingFang-VF.ttf`（简体 UI）
   - `PingFangUI-VF.ttf`（回退）
3. 双击 `编译.bat`，或：

```bat
cd 源码\web && npm install && npm run build
cd ..\USBFixTool && dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## PE 使用

检测到 PE 时自动走 **WinForms 原生界面**（不依赖 WebView2）。  
把整个 `发布` 文件夹拷到 U 盘，管理员运行 `USB急救工具.exe` 或 `PE_一键启动.bat`。

## 说明

界面字体若未放入 `public/fonts`，会回退到系统微软雅黑等字体。
