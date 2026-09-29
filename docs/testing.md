# 隔离测试说明

所有桌面和集成测试都应把工作区放在独立临时目录，并传入明确身份：

```powershell
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("TalosDesk-Manual-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$workspace = Join-Path $testRoot "workspace.json"
.\src\TalosDesk.App\bin\Release\net10.0-windows\TalosDesk.App.exe --workspace $workspace --profile-label "隔离测试"
```

测试结束后先关闭应用并确认合成命令已经退出，再删除这个明确的临时目录。不要把 `%LOCALAPPDATA%\TalosDesk\workspace.json` 用于自动化。

## 自动化层次

1. 相关 Core 测试验证纯逻辑、存储失败和边界条件。
2. App 测试使用 STA Dispatcher、模拟网络和浏览器替身验证桌面流程。
3. 全量验证运行 Release 构建及整个解决方案测试。
4. 候选包运行 `scripts/Publish.ps1`，再从最终解压目录执行桌面验收。

诊断测试只使用合成秘密，并检查 `.crashes` 中的 JSON 与临时文件都不包含该值。异常测试不得破坏测试运行器；真实进程退出场景应放在独立宿主进程中。跨账户 DPAPI 必须在两个隔离 Windows 账户中执行，模拟解密失败不能替代该结果。

候选包的人工步骤见 [v0.2.0 验收清单](acceptance-v0.2.0.md)。
