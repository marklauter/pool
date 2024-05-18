namespace Pool.Tests.Fakes;

public interface IEcho
{
    string Shout(string message);
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken cancellationToken);
}
