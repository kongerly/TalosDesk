# 为 TalosDesk 做贡献

感谢参与 TalosDesk。提交改动前，请先阅读 `README.md`、`docs/design.md` 和 `docs/roadmap.md`，并把每次改动限制在一个可验证的主题内。

## 开发环境

TalosDesk 面向 Windows 11 x64，使用仓库锁定的 .NET SDK 和 PowerShell 7：

```powershell
pwsh -NoProfile -File .\scripts\Install-Sdk.ps1
pwsh -NoProfile -File .\scripts\Test-Environment.ps1
pwsh -NoProfile -File .\scripts\With-Sdk.ps1 build TalosDesk.slnx --configuration Release
pwsh -NoProfile -File .\scripts\With-Sdk.ps1 test TalosDesk.slnx --configuration Release
```

测试必须使用临时目录、合成命令和显式的 `--workspace`；不要读取或修改日常工作区。不要提交工作区、运行日志、崩溃记录、真实路径、凭据或用户日志。敏感字段与异常路径使用明显的合成值。

## 修改与验证

- 行为改动需要有能证明行为的测试，并同步受影响的中文文档。
- 进程测试只管理测试本身启动且能确认 PID 或 Job 归属的进程。
- 导入必须先展示预览，任何示例都不能在导入后自动执行。
- 先运行相关测试，再运行 Release 全量测试。发布候选还必须运行 `scripts/Publish.ps1`。
- PR 或补丁说明应列出已完成内容、实际执行的检查、未执行检查和已知限制。

适合首次贡献的任务包括：补充不改变产品行为的文档、为已确认缺陷增加隔离复现测试、改善固定错误文案，以及补充合成示例。新增 Shell、安装器、遥测、自动更新或大范围重构需要先在 issue 中确认边界。

报告缺陷时请使用仓库模板。诊断记录必须先人工检查；不要附上完整工作区、命令日志、密钥或本机路径。
