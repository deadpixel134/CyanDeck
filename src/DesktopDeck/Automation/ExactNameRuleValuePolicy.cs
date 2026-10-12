namespace DesktopDeck.Automation;

public static class ExactNameRuleValuePolicy
{
    /// <summary>
    /// Empty or whitespace-only rule values are invalid. Otherwise preserve the
    /// user's text verbatim; exact-name matching must not trim or normalize it.
    /// </summary>
    public static bool IsValid(string? value) => !string.IsNullOrWhiteSpace(value);
}
