using System;
using System.IO;

namespace CyanDeck.ManagedDeployment;

public static class ManagedPolicySourceResolver
{
    public const string EnvironmentVariable = "CYANDECK_MANAGED_POLICY";
    public const string AdjacentFileName = "CyanDeck.managed.json";

    public static string? Resolve(string? commandLinePath, string executableDirectory)
    {
        if (!string.IsNullOrWhiteSpace(commandLinePath))
            return Normalize(commandLinePath);

        var environmentPath = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentPath))
            return Normalize(environmentPath);

        if (!string.IsNullOrWhiteSpace(executableDirectory) && Path.IsPathFullyQualified(executableDirectory))
        {
            var adjacent = Path.Combine(executableDirectory, AdjacentFileName);
            if (File.Exists(adjacent))
                return adjacent;
        }

        return null;
    }

    private static string Normalize(string path)
    {
        path = path.Trim();
        if (!ManagedPolicyReader.TryClassifyPath(path, out _, out var error))
            throw new ArgumentException(error, nameof(path));
        return path;
    }
}
