namespace CyanDeck.Calendar;

// Separates an OAuth candidate from the last durable, account-verified token.
// A failed verification OR a failed disk write cannot evict verified credentials.
// No Windows, network or DPAPI dependency: exercised by the domain harness.
internal sealed class VerifiedCredentialCheckpoint<T> where T : class
{
    private T? _lastVerified;
    public T? Active { get; private set; }
    public bool IsPending { get; private set; }

    public void LoadVerified(T? value)
    {
        if (IsPending) throw new InvalidOperationException("Cannot replace an unverified credential candidate.");
        Active = value;
    }

    public void Stage(T candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!IsPending) _lastVerified = Active;
        Active = candidate;
        IsPending = true;
    }

    public void Reject()
    {
        if (!IsPending) return;
        Active = _lastVerified;
        _lastVerified = null;
        IsPending = false;
    }

    public void PersistVerified(Action<T> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        if (!IsPending) return;
        var candidate = Active ?? throw new InvalidOperationException("No OAuth credential candidate exists.");
        persist(candidate); // if this fails, the previous durable token is retained
        IsPending = false;
        _lastVerified = null;
    }
}
