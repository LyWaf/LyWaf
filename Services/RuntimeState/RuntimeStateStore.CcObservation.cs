namespace LyWaf.Services.RuntimeState;

public sealed partial class RuntimeStateStore
{
    internal bool IsCcObservationLossFree()
    { lock (_capacityGate) return Interlocked.Read(ref _dropped) == 0 && _capacityError is null; }

    // CC observations must not export/clean the entire store for each subject.
    internal IReadOnlyDictionary<string, long>? GetCcWindowComponents(string domain, string key, DateTimeOffset window)
    {
        var identity = Identity(domain, key + "|" + window.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        return _entries.TryGetValue(identity, out var entry) && IsActive(entry) ? entry.CounterComponents : null;
    }
}
