namespace Pool.Tests.Fakes;

// creates real Echo items up to a threshold, then throws on the next CreateItem(), so a test can
// drive a mid-seed factory failure and assert the items already created were disposed (findings I4)
internal sealed class ThrowAfterCountItemFactory(int throwAfter)
    : IItemFactory<IEcho>
{
    private int created;

    public List<Echo> CreatedItems { get; } = [];

    public IEcho CreateItem()
    {
        if (created >= throwAfter)
        {
            throw new InvalidOperationException("factory failed mid-seed");
        }

        created++;
        var echo = new Echo();
        CreatedItems.Add(echo);
        return echo;
    }
}
