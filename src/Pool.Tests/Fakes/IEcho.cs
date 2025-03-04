namespace Pool.Tests.Fakes;

public interface IEcho
    : IDisposable
{
    bool IsDisposed();
    string Shout(string message);
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken cancellationToken);
}
