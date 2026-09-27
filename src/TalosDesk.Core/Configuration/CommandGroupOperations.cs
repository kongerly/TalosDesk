namespace TalosDesk.Core.Configuration;

public static class CommandGroupOperations
{
    public static IReadOnlyList<CommandGroupDefinition> GetSequentialGroupsContaining(ProjectDefinition project, Guid commandId) =>
        project.Groups.Where(group => group.ExecutionMode == CommandGroupExecutionMode.Sequential && group.CommandIds.Contains(commandId)).ToArray();

    public static void RemoveCommandReferences(ProjectDefinition project, Guid commandId)
    {
        ArgumentNullException.ThrowIfNull(project);
        foreach (var group in project.Groups) group.CommandIds.RemoveAll(id => id == commandId);
    }

    public static List<Guid> RemapCommandIds(IEnumerable<Guid> commandIds, IReadOnlyDictionary<Guid, Guid> commandIdMap)
    {
        ArgumentNullException.ThrowIfNull(commandIds);
        ArgumentNullException.ThrowIfNull(commandIdMap);
        return commandIds.Select(commandId => commandIdMap.TryGetValue(commandId, out var mappedId)
            ? mappedId
            : throw new InvalidDataException($"Command group references unmapped command ID '{commandId}'.")).ToList();
    }
}
