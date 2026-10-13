namespace CyanDeck.Services;

public static class StateSchema
{
    public const int Current = 35;
}

public sealed class UnsupportedStateVersionException : Exception
{
    public UnsupportedStateVersionException(int foundVersion, string source)
        : base($"This CyanDeck configuration uses state schema v{foundVersion}, but this build supports up to v{StateSchema.Current}. Update CyanDeck before importing it. Source: {source}")
    {
        FoundVersion = foundVersion;
        SourcePath = source;
    }

    public int FoundVersion { get; }
    public string SourcePath { get; }
}
