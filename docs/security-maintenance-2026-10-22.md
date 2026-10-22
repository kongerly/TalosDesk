# 2026-10-22 安全维护与本地重建

本记录描述安全重建阶段的本地审阅产物：该阶段未提交、推送、打标签或发布，未覆盖历史附件，未替换或重启用户正在使用的应用。公开源码已固定 SDK 10.0.402，没有修改应用逻辑、配置 schema 或测试断言。后续按用户授权提交和推送本文档，不代表发布或上传这些产物。

## 运行时基线

Microsoft [官方发布元数据](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)将 2026-10-13 的 10.0.13 标记为安全更新，配套 SDK 为 10.0.402。本机缓存 SDK ZIP 的 SHA-512 与官方元数据及 `eng/dotnet-sdk.json` 一致。这里只核实组件版本，不声称每项 .NET CVE 都能通过 TalosDesk 触发。

| 本地历史包 | 包内 NETCore / Windows Desktop |
| --- | --- |
| v0.1.0、v0.1.1、v0.2.0、v0.3.0-preview.1、v0.3.0 | 均为 10.0.12 |
| 原 v0.3.1-rc.1 | 均为 10.0.13 |

读取 ZIP 内的 `TalosDesk.App.runtimeconfig.json` 核实 `includedFrameworks`，同时核对发布目录实际 DLL 版本。自包含应用使用包内运行时，更新系统 .NET 不会更新这些旧包。旧附件保持历史证据身份，不应继续作为安全基线分发。

## 公开源码与稳定版维护副本

- 当前候选的安全重建基线：`1360e9cd65dbeefd8c23844249d06fe8d1e5316d`，版本 `0.3.1-rc.1`、Preview、schema 5。在独立副本复核，不将其转为正式版。
- 稳定版安全重建：基于 tag `v0.3.0` 对应提交 `6fbe87ae1776ba129b146801941190398bbd2646`，保留 schema 4 和原有功能。只更新 `global.json`、`eng/dotnet-sdk.json`、构建标识及说明文档，不包含自动重启候选功能。
- 维护构建使用 `0.3.0+security.20261022` 区分本地产物；程序集及文件数值版本保持 `0.3.0.0`。构建元数据不参与 SemVer 优先级比较，现有更新检查不会提示同版本重建。它不是已发布的新版本，正式分发前仍需明确版本、渠道与兼容范围。
- 不逐个重发所有旧功能版本：保留五个原始 10.0.12 包，以独立的 v0.3.0 基线维护副本作为旧稳定系列的本地替代准备。

本次工作位于被本机 Git 排除的 `work/security-20261022/`；维护副本与当前分支副本分别位于 `maintenance/`、`current/`。副本复用仓库本地 SDK 与 NuGet 缓存，不修改系统工具链。原有三份未提交文档的内容保持不变，历史 ZIP 的 SHA-256 在前后检查中逐个比较。

## 实际验证与保留失败

先通过 `scripts/Test-Environment.ps1`，再执行 Core 测试，随后使用原有 `scripts/Publish.ps1` 完成完整 Release 构建、全量测试、自包含发布、ZIP 和 SHA-256 生成。Release 构建均为 0 警告、0 错误。

| 对象 | 先行 Core | 发布脚本内全量测试 | 结论 |
| --- | --- | --- | --- |
| 当前 rc.1 | 266/266 | Core 266/266，App 48/48 | 共 314 项通过；独立重建，不替换原 rc.1 包 |
| v0.3.0 安全维护副本 | 193/193（单工作线程） | Core 193/193，App 33/33（单工作线程配置） | 共 226 项通过；默认并发问题仍保留 |

默认并发配置下，维护副本的 `ChangingLocationRejectsActiveRunsAndExistingDestination` 两次遇到 `.logs.moving-*` 目录重命名访问拒绝；单项复查通过。改用 MSTest `Workers=1` 后完整通过，未删除、跳过或放宽断言，未修补日志迁移业务逻辑。尚未确定是运行环境争用还是既有并发问题，不能把单线程通过解释为默认并发已通过或发布验收完全完成。

本机执行环境还需两项测试设置：把 `TEMP`/`TMP` 指向副本的 `.tools/test-temp`，并设 `_MSTestEnableParentProcessQuery=false`，避免默认临时目录权限及 TestHost 父进程查询拒绝访问。后者只作用于测试宿主运行配置；产品包不包含该开关。首次中止和权限失败日志保留，未计入通过结果。

设置 `DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=0` 避免首次运行写入全局工具路径。测试仅使用独立临时目录、合成命令、模拟网络及占位凭据；App 测试含 WPF 操作链，不能替代最终 EXE 的完整人工验收。

以下为本次使用的本地复核入口，`work/` 内的辅助脚本、配置及证据均未纳入 Git；全新克隆不包含这些文件。仓库内可复用的构建入口是 `scripts/Test-Environment.ps1`、`scripts/With-Sdk.ps1` 和 `scripts/Publish.ps1`。

```powershell
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
pwsh -NoProfile -File .\work\security-20261022\Invoke-Validation.ps1 -Repository current
$env:VSTestSetting = (Resolve-Path .\work\security-20261022\serial.runsettings).Path
pwsh -NoProfile -File .\work\security-20261022\Invoke-Validation.ps1 -Repository maintenance
```

以上入口只应在独立工作副本中复用；原发布脚本会替换该副本中同版本的输出，不能对历史发布目录直接重跑。

## 未执行与交付边界

- 未进行本轮最终 EXE 的完整启动、运行、失败、停止、强停、重启、导入预览和异常关闭人工操作链；不沿用旧包验收结果冒充本次通过。
- 未进行干净 Win11 客户端、真实工具项目、跨账户 DPAPI、多屏/DPI、真实托盘鼠标与资源管理器重建检查；未签名。
- 默认并发日志迁移失败仍待定位。历史 v0.3.0 的其他未执行项继续保留，不因运行时升级而消除。
- 未检查或替换实际部署目录，未终止用户应用；旧包只要仍被使用，仍需由用户停止命令、退出、备份配置与旁文件后更换完整应用目录。
- 维护副本的 schema 4 不能直接读取 rc.1 的 schema 5 工作区；不能将维护包作为 schema 5 的回退方案。可分享导出不保留敏感值，不能代替本机完整备份。
- 未更改远端 Releases、下载附件或仓库可见性。本地完成安全重建不等于现有下载已经修复。

最终 ZIP、SHA-256、文件版本及证据文件清单如下；这些记录仅覆盖本次本地副本。

## 最终包与证据

| 产物（均未发布） | 字节 | SHA-256 | ZIP 文件核对 |
| --- | ---: | --- | ---: |
| `TalosDesk-v0.3.1-rc.1-win-x64.zip` | 76933262 | `85d6405524abccb86a1c08b531ddcb73ca6ba91986bd25bef41fb65d9e5ff6ae` | 479 |
| `TalosDesk-v0.3.0+security.20261022-win-x64.zip` | 76917372 | `cca236ddd36ca5793f5f310be20ae615004e701475ba3b71a3fb8ffd68443ab1` | 479 |

表中名称只标识本地档案，不是公开下载链接。两份 ZIP 分别位于 `work/security-20261022/current/artifacts/` 和 `work/security-20261022/maintenance/artifacts/`，均未上传。

两个包的 `includedFrameworks` 均恰为 NETCore 10.0.13 与 Windows Desktop 10.0.13。逐个比较 ZIP 内全部文件与发布目录，校验同名 SHA-256 文件；关键 `System.Private.CoreLib.dll`、`coreclr.dll`、`hostfxr.dll`、`PresentationFramework.dll`、`wpfgfx_cor3.dll` 与固定 SDK 中对应文件的 SHA-256 完全一致。托管 CoreLib、PresentationFramework 的文件版本均为 `10.0.1326.50504`。包内许可与 .NET 第三方说明齐全，未混入工作区、日志、PDB 或指定私钥文件类型。

包内 Core.dll 与实际 Core 测试所加载的 DLL 哈希相同。RID 发布的 App.dll 与普通 App 测试构建哈希不同，因此另复制完整 App 测试输出到隔离目录，以包内 App.dll/Core.dll 替换后运行原有桌面测试；该检查是包内程序集回归，仍不是最终 EXE 人工操作验收。

本地证据均位于 `work/security-20261022/`：`official-baseline.json`、`historical-inventory-before.json`、`preservation-verification.json`、`package-verification.json`、`current-console.log`、`maintenance-console.log`、各副本 `artifacts/security-tests/core.trx`、`*-package-app-tests.log` 及包内程序集测试 TRX。`maintenance-first-publish-failed.log`、`maintenance-second-core-failed.log` 保留默认并发失败，`maintenance-location-recheck.log` 保留单项通过，`serial.runsettings` 记录单工作线程设置。

包内程序集回归实际结果：当前 rc.1 App 48/48、稳定版维护副本 App 33/33，均无失败或跳过。TRX 位于各副本 `artifacts/package-app-tests/results/packaged-app.trx`；测试所用 App.dll SHA-256 与 ZIP 内一致。抽查了维护包 800×520 命令页、rc.1 窄窗口等待状态及诊断页截图；截图来自隔离 WPF 渲染检查，未替代真实 DPI、人工鼠标或最终 EXE 验收。
