namespace Pool.Tests;

internal sealed class Echo
    : IEcho
{
    public void Dispose()
    {
        // nothing to do
    }

    public string Shout(string message)
    {
        return message;
    }
}
