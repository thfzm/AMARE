namespace Amare.Windows.Core;

public sealed class ClosedTabHistory
{
    private readonly LinkedList<TabSnapshot> _items = new();
    private readonly int _capacity;

    public ClosedTabHistory(int capacity = 20)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        _capacity = capacity;
    }

    public int Count => _items.Count;
    public int Capacity => _capacity;

    public void Push(TabSnapshot snapshot)
    {
        _items.AddLast(snapshot);
        while (_items.Count > _capacity)
        {
            _items.RemoveFirst();
        }
    }

    public TabSnapshot? Pop()
    {
        if (_items.Last is null)
        {
            return null;
        }
        var value = _items.Last.Value;
        _items.RemoveLast();
        return value;
    }

    public IReadOnlyList<TabSnapshot> Snapshot() => _items.Reverse().ToArray();

    public void Clear() => _items.Clear();
}
