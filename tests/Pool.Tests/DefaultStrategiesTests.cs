using Microsoft.Extensions.DependencyInjection;
using Pool.DefaultStrategies;
using Pool.Tests.Fakes;
using System.Diagnostics.CodeAnalysis;

namespace Pool.Tests;

public sealed class DefaultStrategiesTests
{
    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP005:Return type should indicate that the value should be disposed", Justification = "the constructor throws; no factory is constructed to dispose")]
    public void DefaultItemFactory_Null_Provider_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new DefaultItemFactory<IEcho>(null!));

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP016:Don't use disposed instance", Justification = "the test deliberately exercises double dispose")]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP017:Prefer using", Justification = "the test deliberately calls Dispose twice to verify idempotency")]
    public void DefaultItemFactory_Double_Dispose_Is_NoOp()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var factory = new DefaultItemFactory<IEcho>(provider);

        factory.Dispose();
        factory.Dispose(); // idempotent: the second dispose is a no-op
    }

    [Fact]
    public async Task DefaultPreparationStrategy_Is_Always_Ready_And_Prepares()
    {
        var strategy = new DefaultPreparationStrategy<IEcho>();
        using var echo = new Echo();

        await strategy.PrepareAsync(echo, TestContext.Current.CancellationToken);
        Assert.True(await strategy.IsReadyAsync(echo, TestContext.Current.CancellationToken));
    }
}
