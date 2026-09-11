using SerialScout.Core.Sessions;

namespace SerialScout.Core.Tests;

/// <summary>
/// <see cref="ISerialLinkFactory"/> that serves scripted <see cref="FakeSerialLink"/>
/// instances so reconnect tests control exactly what each attempt gets. Scripted links
/// are used in order; once exhausted, fresh default links are created.
/// </summary>
public sealed class FakeSerialLinkFactory : ISerialLinkFactory
{
    private readonly object _gate = new();
    private readonly Queue<Func<FakeSerialLink>> _script = new();
    private readonly List<FakeSerialLink> _created = [];

    /// <summary>Every link handed out, in creation order.</summary>
    public IReadOnlyList<FakeSerialLink> Created
    {
        get
        {
            lock (_gate)
            {
                return _created.ToArray();
            }
        }
    }

    /// <summary>The most recently created link.</summary>
    public FakeSerialLink Last
    {
        get
        {
            lock (_gate)
            {
                return _created[^1];
            }
        }
    }

    /// <summary>Queues a pre-built link for the next <see cref="Create"/> call.</summary>
    public FakeSerialLinkFactory Script(FakeSerialLink link) => Script(() => link);

    /// <summary>Queues a link factory for the next <see cref="Create"/> call.</summary>
    public FakeSerialLinkFactory Script(Func<FakeSerialLink> selector)
    {
        lock (_gate)
        {
            _script.Enqueue(selector);
        }

        return this;
    }

    public ISerialLink Create(string portPath)
    {
        Func<FakeSerialLink> selector;
        lock (_gate)
        {
            selector = _script.Count > 0 ? _script.Dequeue() : DefaultSelector;
        }

        var link = selector();
        lock (_gate)
        {
            _created.Add(link);
        }

        return link;
    }

    private static FakeSerialLink DefaultSelector() => new();
}
