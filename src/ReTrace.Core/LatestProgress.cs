namespace ReTrace.Core;

/// <summary>
/// Retains only the latest progress snapshot. Consumers poll at their own cadence;
/// producers never enqueue UI callbacks or wait for a slow consumer.
/// </summary>
public sealed class LatestProgress<T> : IProgress<T> where T : class
{
    private T? latest;

    public T? Latest => Volatile.Read(ref latest);

    public void Report(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Volatile.Write(ref latest, value);
    }
}
