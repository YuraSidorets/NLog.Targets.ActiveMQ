using System.Collections.Concurrent;
using Apache.NMS;
using FluentAssertions;
using Moq;
using NLog.Config;
using NLog.Targets.Wrappers;

namespace NLog.Targets.ActiveMQ.Tests;

public class ActiveMqTargetConcurrencyTests
{
    [Fact]
    public async Task DirectDispose_WaitsForSendThenIgnoresLaterWrites()
    {
        using var broker = new BlockingBroker();
        var target = broker.CreateTarget();
        using var factory = CreateFactory(target);
        await using var workers = new Workers(broker.ReleaseSend);
        var logger = factory.GetLogger("direct-dispose");
        workers.Start(() => logger.Info("in flight"));
        broker.WaitForSend();

        var disposer = workers.Start(target.Dispose);
        disposer.WaitUntilBlocked();
        broker.AssertNotDisposed();

        broker.ReleaseSend();
        await workers.CompleteAsync();
        target.Dispose();
        logger.Info("after disposal");

        broker.Sent.Should().Equal("in flight");
        broker.AssertDisposedOnceAfterSends();
    }

    [Fact]
    public async Task ConcurrentDisposers_WaitForSendAndReleaseEachResourceOnce()
    {
        using var broker = new BlockingBroker();
        var target = broker.CreateTarget();
        using var factory = CreateFactory(target);
        await using var workers = new Workers(broker.ReleaseSend);
        workers.Start(() => factory.GetLogger("concurrent-dispose").Info("in flight"));
        broker.WaitForSend();

        var disposers = Enumerable.Range(0, 3).Select(_ => workers.Start(target.Dispose)).ToArray();
        foreach (var disposer in disposers)
            disposer.WaitUntilBlocked();
        broker.AssertNotDisposed();

        broker.ReleaseSend();
        await workers.CompleteAsync();

        broker.Sent.Should().Equal("in flight");
        broker.AssertDisposedOnceAfterSends();
    }

    [Fact]
    public async Task ParallelWriters_SerializeBrokerAccess()
    {
        using var broker = new BlockingBroker();
        using var factory = CreateFactory(broker.CreateTarget());
        await using var workers = new Workers(broker.ReleaseSend);
        var logger = factory.GetLogger("parallel-writes");
        workers.Start(() => logger.Info("first"));
        broker.WaitForSend();

        var messages = new[] { "second", "third", "fourth", "fifth" };
        var writers = messages.Select(message => workers.Start(() => logger.Info(message))).ToArray();
        foreach (var writer in writers)
            writer.WaitUntilBlocked();
        broker.Sent.Should().BeEmpty();

        broker.ReleaseSend();
        await workers.CompleteAsync();
        factory.Shutdown();

        broker.Sent.Should().BeEquivalentTo(new[] { "first", "second", "third", "fourth", "fifth" });
        broker.AssertDisposedOnceAfterSends();
    }

    [Fact]
    public async Task Shutdown_RacingWithWriters_DoesNotReleaseResourcesDuringSend()
    {
        using var broker = new BlockingBroker();
        using var factory = CreateFactory(broker.CreateTarget());
        await using var workers = new Workers(broker.ReleaseSend);
        var logger = factory.GetLogger("shutdown");
        workers.Start(() => logger.Info("first"));
        broker.WaitForSend();

        var writers = Enumerable.Range(0, 4)
            .Select(index => workers.Start(() => logger.Info($"racing {index}"))).ToArray();
        foreach (var writer in writers)
            writer.WaitUntilBlocked();
        var shutdown = workers.Start(factory.Shutdown);
        shutdown.WaitUntilBlocked();
        broker.AssertNotDisposed();

        broker.ReleaseSend();
        await workers.CompleteAsync();
        var sentAtShutdown = broker.Sent.ToArray();
        logger.Info("after shutdown");

        // Racing writes may win the lock or be rejected by shutdown; neither may use closed resources.
        broker.Sent.Should().Equal(sentAtShutdown);
        broker.Sent.Should().Contain("first");
        broker.AssertDisposedOnceAfterSends();
    }

    [Fact]
    public async Task Reconfiguration_RacingWithWriters_ClosesOldTargetAndRoutesLaterWrites()
    {
        using var broker = new BlockingBroker();
        using var factory = CreateFactory(broker.CreateTarget());
        await using var workers = new Workers(broker.ReleaseSend);
        var logger = factory.GetLogger("reconfiguration");
        workers.Start(() => logger.Info("first"));
        broker.WaitForSend();

        var writers = Enumerable.Range(0, 4)
            .Select(index => workers.Start(() => logger.Info($"racing {index}"))).ToArray();
        foreach (var writer in writers)
            writer.WaitUntilBlocked();
        var replacement = new MemoryTarget("replacement") { Layout = "${message}" };
        var configuration = new LoggingConfiguration(factory);
        configuration.AddRuleForAllLevels(replacement);
        var reconfigure = workers.Start(() => factory.Configuration = configuration);
        reconfigure.WaitUntilBlocked();
        broker.AssertNotDisposed();

        broker.ReleaseSend();
        await workers.CompleteAsync();
        var sentBeforeNewWrite = broker.Sent.ToArray();
        logger.Info("after reconfiguration");

        replacement.Logs.Should().Contain("after reconfiguration");
        broker.Sent.Should().Equal(sentBeforeNewWrite);
        broker.Sent.Should().Contain("first");
        broker.AssertDisposedOnceAfterSends();
    }

    [Fact]
    public async Task AsyncWrapper_ShutdownDrainsQueuedMessagesBeforeDisposingResources()
    {
        using var broker = new BlockingBroker();
        var wrapper = new AsyncTargetWrapper(broker.CreateTarget())
        {
            Name = "async",
            BatchSize = 1,
            TimeToSleepBetweenBatches = 1,
            QueueLimit = 100,
            OverflowAction = AsyncTargetWrapperOverflowAction.Block
        };
        using var factory = CreateFactory(wrapper);
        await using var workers = new Workers(broker.ReleaseSend);
        var logger = factory.GetLogger("async-shutdown");
        logger.Info("first");
        broker.WaitForSend();
        logger.Info("second");
        logger.Info("third");
        logger.Info("fourth");

        var shutdown = workers.Start(factory.Shutdown);
        shutdown.WaitUntilBlocked();
        broker.AssertNotDisposed();

        broker.ReleaseSend();
        await workers.CompleteAsync();
        logger.Info("after shutdown");

        broker.Sent.Should().Equal("first", "second", "third", "fourth");
        broker.AssertDisposedOnceAfterSends();
    }

    private static LogFactory CreateFactory(Target target)
    {
        var factory = new LogFactory { ThrowExceptions = true };
        var configuration = new LoggingConfiguration(factory);
        configuration.AddRuleForAllLevels(target);
        factory.Configuration = configuration;
        return factory;
    }

    private sealed class BlockingBroker : IDisposable
    {
        private readonly Mock<IConnection> _connection = new();
        private readonly Mock<ISession> _session = new();
        private readonly Mock<IMessageProducer> _producer = new();
        private readonly ManualResetEventSlim _sendEntered = new();
        private readonly ManualResetEventSlim _releaseSend = new();
        private readonly ConcurrentQueue<string> _events = new();
        private readonly ConcurrentQueue<string> _violations = new();
        private int _activeSends;
        private int _disposed;

        public ConcurrentQueue<string> Sent { get; } = new();

        public BlockingBroker()
        {
            _connection.Setup(connection => connection.CreateSession()).Returns(_session.Object);
            _session.Setup(session => session.GetQueue("nlog.messages")).Returns(new Mock<IQueue>().Object);
            _session.Setup(session => session.CreateProducer(It.IsAny<IDestination>())).Returns(_producer.Object);
            _session.Setup(session => session.CreateTextMessage(It.IsAny<string>())).Returns((string text) =>
            {
                CheckNotDisposed();
                var message = new Mock<ITextMessage>();
                message.SetupGet(candidate => candidate.Text).Returns(text);
                return message.Object;
            });
            _producer.Setup(producer => producer.Send(It.IsAny<IMessage>())).Callback((IMessage message) =>
            {
                CheckNotDisposed();
                if (Interlocked.Increment(ref _activeSends) != 1)
                    _violations.Enqueue("concurrent sends");
                try
                {
                    _sendEntered.Set();
                    if (!_releaseSend.Wait(TimeSpan.FromSeconds(10)))
                    {
                        _violations.Enqueue("send gate timed out");
                        throw new TimeoutException("The test did not release the send.");
                    }
                    Sent.Enqueue(((ITextMessage)message).Text);
                    _events.Enqueue("send completed");
                }
                finally
                {
                    Interlocked.Decrement(ref _activeSends);
                }
            });
            _producer.Setup(producer => producer.Dispose()).Callback(() => RecordDisposal("producer"));
            _session.Setup(session => session.Dispose()).Callback(() => RecordDisposal("session"));
            _connection.Setup(connection => connection.Dispose()).Callback(() => RecordDisposal("connection"));
        }

        public ActiveMqTarget CreateTarget() => new(_ => _connection.Object)
        {
            Name = "activeMq",
            Layout = "${message}"
        };

        public void WaitForSend() => _sendEntered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

        public void ReleaseSend() => _releaseSend.Set();

        public void AssertNotDisposed()
        {
            _producer.Verify(producer => producer.Dispose(), Times.Never);
            _session.Verify(session => session.Dispose(), Times.Never);
            _connection.Verify(connection => connection.Dispose(), Times.Never);
        }

        public void AssertDisposedOnceAfterSends()
        {
            _producer.Verify(producer => producer.Dispose(), Times.Once);
            _session.Verify(session => session.Dispose(), Times.Once);
            _connection.Verify(connection => connection.Dispose(), Times.Once);
            _violations.Should().BeEmpty();
            _events.TakeLast(3).Should().Equal("dispose producer", "dispose session", "dispose connection");
        }

        private void CheckNotDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                _violations.Enqueue("broker access after disposal");
        }

        private void RecordDisposal(string resource)
        {
            if (Volatile.Read(ref _activeSends) != 0)
                _violations.Enqueue($"{resource} disposed during send");
            Interlocked.Exchange(ref _disposed, 1);
            _events.Enqueue($"dispose {resource}");
        }

        public void Dispose()
        {
            _sendEntered.Dispose();
            _releaseSend.Dispose();
        }
    }

    private sealed class Workers(Action releaseSend) : IAsyncDisposable
    {
        private readonly List<LifecycleWorker> _workers = new();

        public LifecycleWorker Start(Action action)
        {
            var worker = new LifecycleWorker(action);
            _workers.Add(worker);
            return worker;
        }

        public Task CompleteAsync() => Task.WhenAll(_workers.Select(worker => worker.Completion))
            .WaitAsync(TimeSpan.FromSeconds(15));

        public async ValueTask DisposeAsync()
        {
            // Also release and join every worker when an assertion fails.
            releaseSend();
            await CompleteAsync();
        }
    }

}
