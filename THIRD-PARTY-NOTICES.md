# Third-party notices

TalosDesk 的 Windows 自包含发布包包含 Microsoft .NET 运行时组件。

发布脚本会从仓库锁定的 .NET SDK 中复制 `LICENSE.txt` 和
`ThirdPartyNotices.txt`，并以 `DOTNET-LICENSE.txt` 和
`DOTNET-THIRD-PARTY-NOTICES.txt` 的名称放入便携包。

测试工程使用 MSTest。测试依赖不会包含在 TalosDesk 便携包中；其许可信息可在
[MSTest 项目](https://github.com/microsoft/testfx)中查看。

命令编辑器使用 [AvalonEdit 6.3.1.120](https://github.com/icsharpcode/AvalonEdit)。
AvalonEdit 以 MIT License 发布，版权与完整许可文本以其软件包和上游仓库中的
`LICENSE` 文件为准。
