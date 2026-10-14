namespace MySqlPunk.Core.Services;

public sealed class OperationGate
{
    private int _active;

    public IDisposable? TryEnter()
    {
        return Interlocked.CompareExchange(ref _active, 1, 0) == 0 ? new Lease(this) : null;
    }

    private sealed class Lease(OperationGate owner) : IDisposable
    {
        private OperationGate? _owner = owner;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is not null) Interlocked.Exchange(ref current._active, 0);
        }
    }
}
