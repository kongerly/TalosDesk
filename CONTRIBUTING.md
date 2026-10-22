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

测试必须使用临时目录、合成命令和显式的 `--workspace`，不要读取或修改日常工作区。不要提交工作区、运行日志、崩溃记录、真实路径、凭据或用户日志。敏感字段和异常路径请使用明显的合成值。

## 修改与验证

- 行为改动需要能证明该行为的测试，并同步受影响的中文文档。
- 进程测试只管理测试本身启动、且能确认 PID 或 Job 归属的进程。
- 导入必须先展示预览，任何示例都不能在导入后自动执行。
- 先运行相关测试，再运行 Release 全量测试。发布候选还必须运行 `scripts/Publish.ps1`。
- PR 或补丁说明应列出已完成内容、实际执行的检查、未执行检查和已知限制。

适合首次贡献的任务包括：补充不改变产品行为的文档、为已确认缺陷增加隔离复现测试、改善固定错误文案，以及补充合成示例。新增 Shell、安装器、遥测、自动更新或大范围重构，需要先在 issue 中确认边界。

报告缺陷时请使用仓库模板。诊断记录必须先人工检查，不要附上完整工作区、命令日志、密钥或本机路径。

## 文档与本地资料

公开文档面向使用者和贡献者：README 负责上手，`docs/design.md` 记录当前架构与行为契约，`docs/roadmap.md` 记录公开方向，`docs/testing.md` 提供可复现的验证方法，版本说明记录用户可见的变更与限制。

维护者的实施计划、逐次验收报告、操作流水和讨论备忘保存在 `docs/local/`；脚本副本、TRX、截图、PID、哈希清单和包等证据保存在 `work/` 或 `artifacts/`。这些目录由仓库 `.gitignore` 排除，不随克隆提供，也不进入发布包。不要强制添加其中的文件；提交前仍要检查差异和附件内容。

公开发布必须说明实际验证范围、未执行项、安全问题和已知限制，内部报告不能替代对使用者的披露。可复用的技术约束和验证步骤应整理进公开指南，并去掉本机路径、临时编号、对话过程和重复的结果数字。

忽略规则只防止后续文件被纳入版本控制，不会删除已经提交的历史。敏感资料的历史处理需要单独评估，不能靠移动文件实现保密。
