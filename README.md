# USB 急救工具 + 畅通匣

PE / Windows 下的 **USB 修复** 与 **网络 / 共享急救** 合并包。以本仓库（USB-Fix-Kit）为主界面。

## 结构

- `源码/USBFixTool` — C# WinForms + WebView2 宿主（self-contained）
- `源码/web` — React 界面（顶部切换：**USB 急救** / **畅通匣**）
- `源码/lib` — PE / Windows USB 脚本
- `源码/畅通匣` — 畅通匣 Python 后端（WiFi / 共享 / 文件夹占用；**不删 USB 设备、不写 Enum\\USB**）
- `发布/` — 可直接拷到 U 盘的发布包
- `编译.bat` — 一键编译前端 + 后端，并同步畅通匣到 `发布/`

## 本地编译

1. 安装 .NET 9 SDK、Node.js；畅通匣功能另需 Python 3.12+（或自行打包 `畅通匣.exe` 放到 `发布/`）
2. （可选）苹方字体放到 `源码/web/public/fonts/`
3. 双击 `编译.bat`

## 使用

- **PE**：只用「USB 急救」页（畅通匣在 PE 下不可用）
- **正常 Windows**：可切换到「畅通匣」做 WiFi / 共享诊断与安全修复；「打开畅通匣完整窗口」可跑文件夹占用扫描等完整 UI

## 安全说明（畅通匣）

网络修复已去掉 `powercfg` USB 省电写入、`Enum\\USB` 改写、`pnputil /remove-device`。键鼠 / USB 总线异常请用 **USB 急救**，不要用畅通匣硬删设备。

## About

合并自 [USB-Fix-Kit](https://github.com/hailou00520/USB-Fix-Kit) 与畅通匣（tongchang-box）。
