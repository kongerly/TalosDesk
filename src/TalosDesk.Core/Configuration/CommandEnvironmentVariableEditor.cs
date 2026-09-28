namespace TalosDesk.Core.Configuration;

public enum EnvironmentValueEditAction
{
    KeepExisting,
    EncryptExistingPlain,
    Replace,
    Require
}

public static class CommandEnvironmentVariableEditor
{
    public static CommandEnvironmentVariable Apply(CommandEnvironmentVariable? original, string name, bool isSensitive,
        EnvironmentValueEditAction action, string? enteredValue = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.Contains('=') || name.Contains('\0'))
            throw new ArgumentException("环境变量名称不能为空、首尾含空白，或包含等号和空字符。", nameof(name));

        if (!isSensitive)
        {
            if (action != EnvironmentValueEditAction.Replace || enteredValue is null || enteredValue.Contains('\0'))
                throw new ArgumentException("普通变量必须填写有效的明文值；空字符串也可以。", nameof(enteredValue));
            return new CommandEnvironmentVariable { Name = name, IsSensitive = false, Value = enteredValue };
        }

        return action switch
        {
            EnvironmentValueEditAction.KeepExisting when original?.IsSensitive == true &&
                string.Equals(original.Name, name, StringComparison.Ordinal) => original.Clone(),
            EnvironmentValueEditAction.KeepExisting when original?.IsSensitive == true => Required(name),
            EnvironmentValueEditAction.EncryptExistingPlain when original is { IsSensitive: false, Value: not null } =>
                Protected(name, original.Value),
            EnvironmentValueEditAction.Replace when enteredValue is not null => Protected(name, enteredValue),
            EnvironmentValueEditAction.Require => Required(name),
            _ => throw new ArgumentException("请选择敏感变量的有效处理方式。", nameof(action))
        };
    }

    private static CommandEnvironmentVariable Required(string name) => new()
    {
        Name = name,
        IsSensitive = true,
        ValueState = "Required"
    };

    private static CommandEnvironmentVariable Protected(string name, string value) => new()
    {
        Name = name,
        IsSensitive = true,
        ValueState = "Protected",
        ProtectedValue = SensitiveValueProtector.Protect(value)
    };
}
