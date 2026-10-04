using Apache.NMS;
using Apache.NMS.ActiveMQ;
using Apache.NMS.Util;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using NLog.Common;
using NLog.Config;
using NLog.Targets.Wrappers;

namespace NLog.Targets.ActiveMQ.Tests;

[Trait("Category", "Integration")]
public class ActiveMqTargetBrokerLifecycleTests
{
    private static readonly TimeSpan BrokerWait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Dispose_WaitsForPausedBrokerSendToReportRequestTimeout()
    {
        await using var broker = CreateBroker();
        await broker.StartAsync().WaitAsync(BrokerWait);
        using var writeEntered = new ManualResetEventSlim();
        using var closeEntered = new ManualResetEventSlim();
        var target = new ObservedTarget(writeEntered, closeEntered)
        {
            Name = "broker",
            Uri = GetBrokerUri(broker),
            Destination = $"queue://nlog.stalled.{Guid.NewGuid():N}",
            Layout = "${message}"
        };
        using var factory = CreateFactory(target);
        var logger = factory.GetLogger("stalled-send");
        ConfirmDelivery(broker, target, logger);
        writeEntered.Reset();
        LifecycleWorker? write = null;
        LifecycleWorker? dispose = null;
        var paused = false;

        try
        {
            await broker.PauseAsync().WaitAsync(BrokerWait);
            paused = true;
            Exception? sendFailure = null;
            write = new LifecycleWorker(() => sendFailure = Record.Exception(() => logger.Info("paused")));
            writeEntered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            write.WaitUntilBlocked();
            dispose = new LifecycleWorker(target.Dispose);
            dispose.WaitUntilBlocked();
            closeEntered.IsSet.Should().BeFalse("the real send still owns the target's resources");

            await write.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            sendFailure.Should().NotBeNull();
            sendFailure!.GetBaseException().Should().BeOfType<RequestTimedOutException>();
            await dispose.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            closeEntered.IsSet.Should().BeTrue();
        }
        finally
        {
            // Restore the broker before closing clients or joining writers, including on assertion failure.
            try
            {
                if (paused)
                    await broker.UnpauseAsync().WaitAsync(BrokerWait);
            }
            finally
            {
                await Task.WhenAll(write?.Completion ?? Task.CompletedTask, dispose?.Completion ?? Task.CompletedTask)
                    .WaitAsync(BrokerWait);
            }
        }
    }

    [Fact]
    public async Task WriteAsyncLogEvent_AfterBrokerStopsReportsNmsFailure()
    {
        await using var broker = CreateBroker();
        await broker.StartAsync().WaitAsync(BrokerWait);
        var target = new ActiveMqTarget
        {
            Name = "broker",
            Uri = GetBrokerUri(broker),
            Destination = $"queue://nlog.outage.{Guid.NewGuid():N}",
            Layout = "${message}"
        };
        using var factory = CreateFactory(target);
        ConfirmDelivery(broker, target, factory.GetLogger("outage"));
        // With NLog's default exception policy, write failures must reach the continuation.
        factory.ThrowExceptions = false;
        await broker.StopAsync().WaitAsync(BrokerWait);
        var callback = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var write = new LifecycleWorker(() => target.WriteAsyncLogEvent(new AsyncLogEventInfo(
            new LogEventInfo(LogLevel.Info, "outage", "after stop"), exception => callback.SetResult(exception))));

        try
        {
            await write.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            var failure = await callback.Task.WaitAsync(TimeSpan.FromSeconds(5));
            failure.Should().BeAssignableTo<NMSException>();
        }
        finally
        {
            await write.Completion.WaitAsync(BrokerWait);
        }
    }

    [Fact]
    public async Task AsyncWrapper_FactoryDisposalDeliversQueuedMessagesToBroker()
    {
        await using var broker = CreateBroker();
        await broker.StartAsync().WaitAsync(BrokerWait);
        var destination = $"queue://nlog.drain.{Guid.NewGuid():N}";
        using var receiver = new ConnectionFactory(GetBrokerUri(broker)).CreateConnection();
        using var session = receiver.CreateSession();
        using var consumer = session.CreateConsumer(SessionUtil.GetDestination(session, destination));
        receiver.Start();
        var target = new ActiveMqTarget
        {
            Name = "broker", Uri = GetBrokerUri(broker), Destination = destination, Layout = "${message}"
        };
        using var factory = CreateFactory(new AsyncTargetWrapper(target)
        {
            Name = "async", TimeToSleepBetweenBatches = 60000, QueueLimit = 100,
            OverflowAction = AsyncTargetWrapperOverflowAction.Block
        });
        var logger = factory.GetLogger("drain");
        logger.Info("first");
        logger.Info("second");
        logger.Info("third");
        var close = new LifecycleWorker(factory.Dispose);

        try
        {
            await close.Completion.WaitAsync(BrokerWait);
            var received = Enumerable.Range(0, 3).Select(_ => consumer.Receive(TimeSpan.FromSeconds(5))
                .Should().BeAssignableTo<ITextMessage>().Subject.Text).ToArray();
            received.Should().Equal("first", "second", "third");
        }
        finally
        {
            await close.Completion.WaitAsync(BrokerWait);
        }
    }

    private static IContainer CreateBroker() => new ContainerBuilder("apache/activemq:6.2.6")
        .WithPortBinding(61616, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(61616))
        .Build();

    // Bound synchronous send requests without changing the target's production defaults.
    private static string GetBrokerUri(IContainer broker) =>
        $"tcp://{broker.Hostname}:{broker.GetMappedPublicPort(61616)}?connection.requestTimeout=3000&connection.alwaysSyncSend=true";

    private static LogFactory CreateFactory(Target target)
    {
        var factory = new LogFactory { ThrowExceptions = true };
        var configuration = new LoggingConfiguration(factory);
        configuration.AddRuleForAllLevels(target);
        factory.Configuration = configuration;
        return factory;
    }

    private static void ConfirmDelivery(IContainer broker, ActiveMqTarget target, Logger logger)
    {
        using var receiver = new ConnectionFactory(GetBrokerUri(broker)).CreateConnection();
        using var session = receiver.CreateSession();
        using var consumer = session.CreateConsumer(SessionUtil.GetDestination(session, target.Destination.ToString()));
        receiver.Start();
        logger.Info("connected");
        consumer.Receive(TimeSpan.FromSeconds(10))
            .Should().BeAssignableTo<ITextMessage>().Subject.Text.Should().Be("connected");
    }

    private sealed class ObservedTarget(ManualResetEventSlim writeEntered, ManualResetEventSlim closeEntered) : ActiveMqTarget
    {
        protected override void Write(LogEventInfo logEvent)
        {
            // NLog has acquired SyncRoot before entering this hook; no artificial send delay is added.
            writeEntered.Set();
            base.Write(logEvent);
        }

        protected override void CloseTarget()
        {
            closeEntered.Set();
            base.CloseTarget();
        }
    }
}
