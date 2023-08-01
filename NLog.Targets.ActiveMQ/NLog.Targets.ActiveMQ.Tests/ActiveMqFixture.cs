using DotNet.Testcontainers.Builders;

namespace NLog.Targets.ActiveMQ.Tests;

public class ActiveMqFixture : IAsyncDisposable
{
    public readonly AsyncLazy<DotNet.Testcontainers.Containers.IContainer> ActiveMqContainer = new(async () =>
    {
        var container = new ContainerBuilder()
          .WithImage("rmohr/activemq:latest")
          .WithPortBinding(61616, 61616)
          .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(61616))
          .Build();
        await container.StartAsync();
        return container;
    });

    public ActiveMqFixture()
    {
    }

    public async ValueTask DisposeAsync()
    {
        await (await ActiveMqContainer).DisposeAsync().ConfigureAwait(false);
    }
}
