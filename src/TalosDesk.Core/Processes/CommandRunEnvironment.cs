using TalosDesk.Core.Configuration;

namespace TalosDesk.Core.Processes;

public sealed record CommandRunEnvironment(
    IReadOnlyDictionary<string, string> Overrides,
    IReadOnlyList<string> SensitiveValues);

public sealed class CommandEnvironmentException(string message) : Exception(message);

public static class CommandRunEnvironmentResolver
{
    public static CommandRunEnvironment Resolve(CommandDefinition command, Func<string, string>? unprotect = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        unprotect ??= SensitiveValueProtector.Unprotect;
        if (command.EnvironmentVariables is null) throw new CommandEnvironmentException("环境变量配置无效。");

        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sensitiveValues = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variable in command.EnvironmentVariables)
        {
            if (variable is null || string.IsNullOrWhiteSpace(variable.Name) || variable.Name != variable.Name.Trim() ||
                variable.Name.Contains('=') || variable.Name.Contains('\0') || overrides.ContainsKey(variable.Name))
                throw new CommandEnvironmentException("环境变量名称无效或重复。");

            string value;
            if (!variable.IsSensitive)
            {
                if (variable.Value is null || variable.ValueState is not null || variable.ProtectedValue is not null || variable.Value.Contains('\0'))
                    throw new CommandEnvironmentException($"变量“{variable.Name}”的配置无效。");
                value = variable.Value;
            }
            else if (variable.ValueState == "Required" && variable.Value is null && variable.ProtectedValue is null)
            {
                throw new CommandEnvironmentException($"敏感变量“{variable.Name}”尚未填写。");
            }
            else if (variable.ValueState == "Protected" && variable.Value is null && !string.IsNullOrEmpty(variable.ProtectedValue))
            {
                try { value = unprotect(variable.ProtectedValue); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    throw new CommandEnvironmentException($"敏感变量“{variable.Name}”不可解密，请重新录入。");
                }
                if (value is null || value.Length > SensitiveValueProtector.MaximumValueLength || value.Contains('\0'))
                    throw new CommandEnvironmentException($"敏感变量“{variable.Name}”不可解密，请重新录入。");
                if (value.Length > 0) sensitiveValues.Add(value);
            }
            else
            {
                throw new CommandEnvironmentException($"敏感变量“{variable.Name}”的配置无效。");
            }

            overrides.Add(variable.Name, value);
        }

        return new CommandRunEnvironment(overrides, sensitiveValues.ToArray());
    }
}
