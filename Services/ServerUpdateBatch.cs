namespace ZScape.Services;

/// <summary>Coalesces worker notifications until the UI can consume them.</summary>
internal sealed class ServerUpdateBatch
{
    private readonly object _gate = new();
    private HashSet<string> _addresses = new(StringComparer.OrdinalIgnoreCase);
    private bool _membershipChanged;

    public void Add(string address)
    {
        lock (_gate) _addresses.Add(address);
    }

    public void MembershipChanged()
    {
        lock (_gate) _membershipChanged = true;
    }

    public (HashSet<string> Addresses, bool MembershipChanged) Take()
    {
        lock (_gate)
        {
            var result = (_addresses, _membershipChanged);
            _addresses = new(StringComparer.OrdinalIgnoreCase);
            _membershipChanged = false;
            return result;
        }
    }
}
