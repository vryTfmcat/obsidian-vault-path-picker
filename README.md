# Obsidian Vault Path Picker / Obsidian 文件快选器

一个轻量的 Windows 桌面工具：通过全局快捷键搜索指定 Obsidian 库内的文件，再复制相对路径或绝对路径，方便在 Codex 等应用中引用。

## 功能

- **Ctrl+Alt+Shift+O** 唤出搜索窗口；快捷键被占用时可通过托盘图标打开。
- 按文件名或库内路径搜索，支持空格分隔关键词和按字符顺序匹配。
- 两个按钮：**复制相对路径**、**复制绝对路径**。
- **Enter**、双击和 **Ctrl+Shift+C** 默认复制相对路径。
- 相对路径基于所选库根目录，统一使用 `/`，保留扩展名。
- 支持多选，复制时每条路径占一行。
- **Esc** 收起窗口；**F5** 刷新索引。
- 只读取文件名称、路径和属性，不读取笔记正文，也不上传文件。

例如同一个文件可以复制为：

```text
相对路径：Projects/Example.md
绝对路径：D:\Notes\MyVault\Projects\Example.md
```

## 环境与构建

需要 Windows 和 .NET Framework 4.5 或更高版本，以及系统自带的 .NET Framework C# 编译器。不需要安装第三方库。

在 PowerShell 中进入项目目录运行：

```powershell
.\build.ps1
```

生成 `VaultPicker.exe`。双击运行后程序驻留托盘；首次启动请选择 Obsidian 库根目录。选择结果保存到程序旁的 `vault.local.txt`，这个文件不会被 Git 跟踪。

也可把 `vault.example.txt` 复制为 `vault.local.txt`，将其唯一一行改为自己的库根目录。

程序目录需要可写。若脚本被系统执行策略阻止，可审阅脚本后在单次进程中运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

不默认设置开机启动。右键托盘图标选择“退出”会彻底退出；关闭搜索窗口只会收起到托盘。

## 搜索范围

仅索引配置的库内普通文件，不显示文件夹。隐藏或系统文件、以点开头的名称、符号链接和目录联接，以及 `node_modules`、`bin`、`obj`、`__pycache__`、`dist`、`build` 目录会被跳过。

结果超过 300 个时显示前 300 个，可继续输入关键词缩小范围。每次唤出时，距上次刷新超过一分钟会刷新索引。复制前再次检查文件存在且处于配置的库内。

## 验证

```powershell
Start-Process .\VaultPicker.exe -ArgumentList '--self-test' -Wait
Get-Content .\self-test.txt
```

自检使用临时测试库，涵盖中文和空格文件名、相对路径与绝对路径、多选、搜索匹配、库外文件与文件夹拒绝。测试库会在结束时清理。

`--preview-test` 会生成本机窗口截图 `preview.png`，仅供开发检查；其中可能包含库内文件名，因此不纳入版本控制。

## 仓库内容

- `VaultPicker.cs`：应用源码。
- `build.ps1`：构建脚本。
- `vault.example.txt`：无个人信息的配置示例。
- `.gitignore`：排除个人配置、程序及运行输出。

程序仅复制路径文本，不提供附件上传、文件拖放、文件移动或删除操作。
