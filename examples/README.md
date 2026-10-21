# 示例工作区与演示

这些 JSON 文件用于演示导入、运行和停止，不包含真实项目、凭据或用户日志。所有路径均为占位路径，导入后需要改成你本机已存在的目录。

导入总是先展示预览，确认后只保存配置，不会自动运行命令、连接探测端口或安装依赖。

## 可用示例

| 文件 | 演示内容 | 运行后应看到 |
| --- | --- | --- |
| [basic-tasks.json](basic-tasks.json) | 成功任务和非零退出任务 | `sample-success`；`sample-failure` 与退出码 `2` |
| [service-stop.json](service-stop.json) | 持续运行的服务 | `sample-service-started`，随后可验证停止 |
| [group-and-secret.json](group-and-secret.json) | 顺序分组和敏感变量待填写状态 | 先运行“准备”，再要求填写 `SAMPLE_TOKEN` |

也可以直接下载稳定版示例：

- [基础任务示例](https://raw.githubusercontent.com/kongerly/TalosDesk/v0.3.0/examples/basic-tasks.json)
- [服务停止示例](https://raw.githubusercontent.com/kongerly/TalosDesk/v0.3.0/examples/service-stop.json)
- [分组与敏感变量示例](https://raw.githubusercontent.com/kongerly/TalosDesk/v0.3.0/examples/group-and-secret.json)

## 2 分钟跑通

1. 启动 TalosDesk，点击“导入”。
2. 选择 `basic-tasks.json`，在预览中确认项目和两条命令后保存。
3. 在左侧选择“基础任务示例”，点击“编辑项目”，把项目目录改成任意已存在的本地目录，例如临时目录；工作目录会随项目目录自动迁移。
4. 在“命令管理”中选择“成功任务”并点击“运行”，在“运行输出”中确认 `sample-success`。
5. 在“命令管理”中选择“失败任务”并点击“运行”，确认 stderr 显示 `sample-failure`，最终状态为失败，退出码为 `2`。
6. 导入 `service-stop.json`，更新目录后运行“合成长运行服务”。确认输出持续出现后点击“停止”，并确认进程已经退出。

## 一键演示

如果已经安装或构建了 TalosDesk，可以在仓库根目录执行：

```powershell
pwsh -NoProfile -File .\examples\demo\Start-Demo.ps1 -ApplicationPath .\TalosDesk\TalosDesk.App.exe
```

如果使用源码构建，把 `-ApplicationPath` 改为 `.\src\TalosDesk.App\bin\Release\net10.0-windows\TalosDesk.App.exe`。

脚本会在 `%TEMP%` 下建立隔离工作区并打开应用，包含“成功任务”“失败任务”“合成长运行服务”和“顺序演示”分组。它不会自动运行命令，也不会读取正式工作区；关闭应用后可删除脚本打印的临时目录。

## 分组与敏感变量

导入 `group-and-secret.json` 后，预览会显示顺序分组和 `SAMPLE_TOKEN` 的待填写状态。保存配置不会运行命令；直接运行“使用占位凭据”会被待填写敏感变量阻止。请在命令编辑器中填写本地合成值后再运行。不要把真实令牌写入示例或提交到仓库。

## 注意

- 示例中的 `C:\Path\To\...` 只是占位路径，不能直接运行。
- 服务示例会持续循环，验证后请使用 TalosDesk 的“停止”，不要按进程名称批量结束系统进程。
- 示例只用于验证界面和进程归属流程，不代表真实项目的构建或健康检查。
