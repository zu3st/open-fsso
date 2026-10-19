namespace OpenFsso.Core;
// Ordinary successful sends cannot hide an earlier failed mapping change. A
// complete reassertion must succeed before that delivery failure is acknowledged.
public sealed class DeliveryRecovery
{
    private long failedVersion;
    private long recoveredVersion;
    public long Version => Interlocked.Read(ref failedVersion);
    public bool Healthy => Version == Interlocked.Read(ref recoveredVersion);

    public void Failed() => Interlocked.Increment(ref failedVersion);
    public void CompleteReassert(long version) => Interlocked.Exchange(ref recoveredVersion, version);
}
