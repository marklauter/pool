namespace Pool.Tests.Fakes;

public interface IEcho
{
    string Shout(string message);

    bool IsReady { get; }
    void MakeReady();
}
