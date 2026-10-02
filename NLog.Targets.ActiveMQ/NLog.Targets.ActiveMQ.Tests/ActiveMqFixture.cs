using DotNet.Testcontainers.Builders;

namespace NLog.Targets.ActiveMQ.Tests;

public sealed class ActiveMqFixture : IAsyncLifetime
{
    public DotNet.Testcontainers.Containers.IContainer ActiveMqContainer { get; } =
        new ContainerBuilder("apache/activemq:6.2.6")
            .WithPortBinding(61616, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(61616))
            .Build();

    public Task InitializeAsync() => ActiveMqContainer.StartAsync();

    public Task DisposeAsync() => ActiveMqContainer.DisposeAsync().AsTask();
}
