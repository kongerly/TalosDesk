namespace TalosDesk.Core.Configuration;

public sealed class WorkspaceChangedException : IOException
{
    public WorkspaceChangedException() : base("工作区文件已被另一个程序修改，本次保存已取消。")
    {
    }
}
