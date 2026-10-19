namespace OpenFsso.Core;

public sealed class StartupBarrier
{
    private long activated = long.MaxValue;
    public void Activate(long timestamp)
    {
        if (timestamp <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timestamp));
        }

        Interlocked.Exchange(ref activated, timestamp);
    }

    public bool Accepts(long connectionStarted) => connectionStarted > 0 && connectionStarted > Interlocked.Read(ref activated);
}
