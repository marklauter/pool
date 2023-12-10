namespace Pool.Tests.Fakes;

internal sealed class Echo
    : IEcho
{
    public bool IsReady { get; private set; }

    public void MakeReady()
    {
        IsReady = true;
    }

    public string Shout(string message)
    {
        return message;
    }
}
