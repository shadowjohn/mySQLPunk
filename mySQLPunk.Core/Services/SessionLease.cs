namespace MySqlPunk.Core.Services;

public sealed class SessionLease<TSession>(TSession session) : IDisposable where TSession : class, IDisposable
{
    private TSession? _session = session;

    public TSession Session => _session ?? throw new ObjectDisposedException(nameof(SessionLease<TSession>));

    public TSession Transfer(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owned = Session;
        _session = null;
        return owned;
    }

    public void Dispose()
    {
        var owned = _session;
        _session = null;
        owned?.Dispose();
    }
}
