namespace Pool.Tests;

internal sealed class Echo
    : IEcho
{
    public string Shout(string message)
    {
        return message;
    }
}
