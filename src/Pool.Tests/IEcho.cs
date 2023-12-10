namespace Pool.Tests;

public interface IEcho
    : IDisposable
{
    string Shout(string message);
}
