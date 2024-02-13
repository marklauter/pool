namespace Pool.Tests.Fakes;

public interface IEcho
{
    string Shout(string message);
    bool IsReady { get; }
    Task MakeReadyAsync(CancellationToken cancellationToken);
}
