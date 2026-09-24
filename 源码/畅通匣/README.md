# 畅通匣

Windows 急救小工具：解除文件/文件夹占用，诊断修复 WiFi，备份并一键导入无线配置。

## 功能

- **文件夹占用**：扫描并结束占用进程（Restart Manager、打开句柄、cwd/映像/模块、命令行、句柄表补检）
- **网络修复**：诊断、重置驱动、修复 WLAN 服务、协议栈、等待拔插 USB 网卡
- **文件共享**：一键开启本机共享、创建/删除共享、密码保护开关、公用文件夹、映射驱动器；Win11 24H2 NAS/来宾与 SMB 签名等疑难修复

## 运行

### 已打包

以**管理员**运行 `畅通匣.exe`（需自行用下方步骤生成）。

### 开发

```bat
cd web
npm install
npm run build
cd ..
python unlock_folder.py
```

### 打包

```bat
cd web && npm run build && cd ..
pyinstaller --noconfirm unlock_folder.spec
```

产物在 `dist\畅通匣.exe`。

## 技术

- 后端：Python + Win32 API / `netsh` / PowerShell
- 前端：React + Vite + Tailwind（pywebview 壳）

## 许可

自用工具，按需修改。
