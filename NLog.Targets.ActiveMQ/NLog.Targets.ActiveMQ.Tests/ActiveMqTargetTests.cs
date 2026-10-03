using Apache.NMS;
using Apache.NMS.ActiveMQ;
using FluentAssertions;
using Moq;
using NLog.Config;

namespace NLog.Targets.ActiveMQ.Tests;

public class ActiveMqTargetTests
{
    [Fact]
    public void InitializeTarget_CreateConnectionFails_PreservesOriginalFailure()
    {
        var failure = new InvalidOperationException("create-connection failed");
        var resources = CreateResourcesWithFailingDisposal();
        var target = new TestableActiveMqTarget(_ => throw failure);

        target.Invoking(candidate => candidate.InitializeForTest())
            .Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(failure);

        resources.Connection.Verify(candidate => candidate.Dispose(), Times.Never);
        resources.Session.Verify(candidate => candidate.Dispose(), Times.Never);
        resources.Producer.Verify(candidate => candidate.Dispose(), Times.Never);
    }

    [Fact]
    public void InitializeTarget_StartFails_DisposesConnectionAndPreservesOriginalFailure()
    {
        var failure = new InvalidOperationException("start-connection failed");
        var resources = CreateResourcesWithFailingDisposal();
        resources.Connection.Setup(candidate => candidate.Start()).Throws(failure);
        var target = new TestableActiveMqTarget(_ => resources.Connection.Object);

        target.Invoking(candidate => candidate.InitializeForTest())
            .Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(failure);

        resources.Connection.Verify(candidate => candidate.Dispose(), Times.Once);
        resources.Session.Verify(candidate => candidate.Dispose(), Times.Never);
        resources.Producer.Verify(candidate => candidate.Dispose(), Times.Never);
    }

    [Fact]
    public void InitializeTarget_CreateSessionFails_DisposesConnectionAndPreservesOriginalFailure()
    {
        var failure = new InvalidOperationException("create-session failed");
        var resources = CreateResourcesWithFailingDisposal();
        resources.Connection.Setup(candidate => candidate.CreateSession()).Throws(failure);
        var target = new TestableActiveMqTarget(_ => resources.Connection.Object);

        target.Invoking(candidate => candidate.InitializeForTest())
            .Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(failure);

        resources.Connection.Verify(candidate => candidate.Dispose(), Times.Once);
        resources.Session.Verify(candidate => candidate.Dispose(), Times.Never);
        resources.Producer.Verify(candidate => candidate.Dispose(), Times.Never);
    }

    [Fact]
    public void InitializeTarget_GetQueueFails_DisposesSessionAndConnectionAndPreservesOriginalFailure()
    {
        var failure = new InvalidOperationException("get-queue failed");
        var resources = CreateResourcesWithFailingDisposal();
        resources.Session.Setup(candidate => candidate.GetQueue("nlog.messages")).Throws(failure);
        var target = new TestableActiveMqTarget(_ => resources.Connection.Object);

        target.Invoking(candidate => candidate.InitializeForTest())
            .Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(failure);

        resources.Producer.Verify(candidate => candidate.Dispose(), Times.Never);
        resources.Session.Verify(candidate => candidate.Dispose(), Times.Once);
        resources.Connection.Verify(candidate => candidate.Dispose(), Times.Once);
    }

    [Fact]
    public void InitializeTarget_CreateProducerFails_DisposesSessionAndConnectionAndPreservesOriginalFailure()
    {
        var failure = new InvalidOperationException("create-producer failed");
        var resources = CreateResourcesWithFailingDisposal();
        resources.Session.Setup(candidate => candidate.CreateProducer(It.IsAny<IDestination>())).Throws(failure);
        var target = new TestableActiveMqTarget(_ => resources.Connection.Object);

        target.Invoking(candidate => candidate.InitializeForTest())
            .Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(failure);

        resources.Producer.Verify(candidate => candidate.Dispose(), Times.Never);
        resources.Session.Verify(candidate => candidate.Dispose(), Times.Once);
        resources.Connection.Verify(candidate => candidate.Dispose(), Times.Once);
    }

    [Fact]
    public void InitializeTarget_SetDeliveryModeFails_DisposesAllResourcesAndPreservesOriginalFailure()
    {
        var failure = new InvalidOperationException("set-delivery-mode failed");
        var resources = CreateResourcesWithFailingDisposal();
        resources.Producer.SetupSet(candidate => candidate.DeliveryMode = It.IsAny<MsgDeliveryMode>()).Throws(failure);
        var target = new TestableActiveMqTarget(_ => resources.Connection.Object);

        target.Invoking(candidate => candidate.InitializeForTest())
            .Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(failure);

        resources.Producer.Verify(candidate => candidate.Dispose(), Times.Once);
        resources.Session.Verify(candidate => candidate.Dispose(), Times.Once);
        resources.Connection.Verify(candidate => candidate.Dispose(), Times.Once);
    }

    [Fact]
    public void LogFactory_UsesTargetSettingsRendersMessageAndClosesResources()
    {
        ConnectionFactory? capturedFactory = null;
        var connection = new Mock<IConnection>();
        var session = new Mock<ISession>();
        var producer = new Mock<IMessageProducer>();
        var queue = new Mock<IQueue>().Object;
        var textMessage = new Mock<ITextMessage>().Object;
        connection.Setup(candidate => candidate.CreateSession()).Returns(session.Object);
        session.Setup(candidate => candidate.GetQueue("orders.created")).Returns(queue);
        session.Setup(candidate => candidate.CreateProducer(It.IsAny<IDestination>())).Returns(producer.Object);
        session.Setup(candidate => candidate.CreateTextMessage("INFO|orders|created 42")).Returns(textMessage);

        var target = new ActiveMqTarget(factory =>
        {
            capturedFactory = factory;
            return connection.Object;
        })
        {
            Name = "activeMq",
            Uri = "tcp://broker.example:61617",
            Destination = "queue://orders.created",
            Username = "publisher",
            Password = "secret",
            ClientId = "nlog-tests",
            UseCompression = true,
            Persistent = false,
            Layout = "${level:uppercase=true}|${logger}|${message}"
        };
        using (var logFactory = CreateLogFactory(target))
            logFactory.GetLogger("orders").Info("created {0}", 42);

        capturedFactory.Should().NotBeNull();
        capturedFactory!.BrokerUri.ToString().Should().Be("tcp://broker.example:61617/");
        capturedFactory.UserName.Should().Be("publisher");
        capturedFactory.Password.Should().Be("secret");
        capturedFactory.ClientId.Should().Be("nlog-tests");
        capturedFactory.UseCompression.Should().BeTrue();
        producer.VerifySet(candidate => candidate.DeliveryMode = MsgDeliveryMode.NonPersistent, Times.Once);
        session.Verify(candidate => candidate.CreateProducer(It.Is<IDestination>(destination => ReferenceEquals(destination, queue))), Times.Once);
        session.Verify(candidate => candidate.CreateTextMessage("INFO|orders|created 42"), Times.Once);
        producer.Verify(candidate => candidate.Send(textMessage), Times.Once);
        producer.Verify(candidate => candidate.Dispose(), Times.Once);
        session.Verify(candidate => candidate.Dispose(), Times.Once);
        connection.Verify(candidate => candidate.Dispose(), Times.Once);
    }

    [Fact]
    public void LogFactory_DefaultsToPersistentWithoutOptionalConnectionSettings()
    {
        ConnectionFactory? capturedFactory = null;
        var connection = new Mock<IConnection>();
        var session = new Mock<ISession>();
        var producer = new Mock<IMessageProducer>();
        connection.Setup(candidate => candidate.CreateSession()).Returns(session.Object);
        session.Setup(candidate => candidate.GetQueue("nlog.messages")).Returns(new Mock<IQueue>().Object);
        session.Setup(candidate => candidate.CreateProducer(It.IsAny<IDestination>())).Returns(producer.Object);
        session.Setup(candidate => candidate.CreateTextMessage(It.IsAny<string>())).Returns(new Mock<ITextMessage>().Object);

        var target = new ActiveMqTarget(factory =>
        {
            capturedFactory = factory;
            return connection.Object;
        })
        {
            Name = "activeMq",
            Layout = "${message}"
        };

        using (var logFactory = CreateLogFactory(target))
            logFactory.GetLogger("defaults").Info("hello");

        capturedFactory.Should().NotBeNull();
        capturedFactory!.UserName.Should().BeNullOrEmpty();
        capturedFactory.Password.Should().BeNullOrEmpty();
        capturedFactory.ClientId.Should().BeNullOrEmpty();
        capturedFactory.UseCompression.Should().BeFalse();
        producer.VerifySet(candidate => candidate.DeliveryMode = MsgDeliveryMode.Persistent, Times.Once);
    }

    [Fact]
    public void LogFactory_RepeatedCloseContinuesAfterEachResourceDisposeFailure()
    {
        var connection = new Mock<IConnection>();
        var session = new Mock<ISession>();
        var producer = new Mock<IMessageProducer>();
        connection.Setup(candidate => candidate.CreateSession()).Returns(session.Object);
        session.Setup(candidate => candidate.GetQueue("nlog.messages")).Returns(new Mock<IQueue>().Object);
        session.Setup(candidate => candidate.CreateProducer(It.IsAny<IDestination>())).Returns(producer.Object);
        producer.Setup(candidate => candidate.Dispose()).Throws(new InvalidOperationException("producer dispose failed"));
        session.Setup(candidate => candidate.Dispose()).Throws(new InvalidOperationException("session dispose failed"));
        connection.Setup(candidate => candidate.Dispose()).Throws(new InvalidOperationException("connection dispose failed"));

        var target = new ActiveMqTarget(_ => connection.Object)
        {
            Name = "activeMq",
            Layout = "${message}"
        };
        var logFactory = CreateLogFactory(target);
        logFactory.GetLogger("close").Info("initialize");

        logFactory.Dispose();
        logFactory.Dispose();

        producer.Verify(candidate => candidate.Dispose(), Times.Once);
        session.Verify(candidate => candidate.Dispose(), Times.Once);
        connection.Verify(candidate => candidate.Dispose(), Times.Once);
    }

    [Fact]
    public void WriteAsyncLogEvent_ReportsSendFailureToContinuation()
    {
        var sendFailure = new InvalidOperationException("send failed");
        var connection = new Mock<IConnection>();
        var session = new Mock<ISession>();
        var producer = new Mock<IMessageProducer>();
        connection.Setup(candidate => candidate.CreateSession()).Returns(session.Object);
        session.Setup(candidate => candidate.GetQueue("nlog.messages")).Returns(new Mock<IQueue>().Object);
        session.Setup(candidate => candidate.CreateProducer(It.IsAny<IDestination>())).Returns(producer.Object);
        session.Setup(candidate => candidate.CreateTextMessage(It.IsAny<string>())).Returns(new Mock<ITextMessage>().Object);
        producer.Setup(candidate => candidate.Send(It.IsAny<IMessage>())).Throws(sendFailure);
        var target = new ActiveMqTarget(_ => connection.Object) { Name = "activeMq", Layout = "${message}" };
        var logFactory = CreateLogFactory(target);

        try
        {
            logFactory.GetLogger("callback").Info("initialize");
            Exception? callbackFailure = null;
            using var callbackCompleted = new ManualResetEventSlim();
            target.WriteAsyncLogEvent(new NLog.Common.AsyncLogEventInfo(
                new NLog.LogEventInfo(NLog.LogLevel.Info, "callback", "message"),
                exception =>
                {
                    callbackFailure = exception;
                    callbackCompleted.Set();
                }));

            callbackCompleted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the target should complete the write callback");
            callbackFailure.Should().BeSameAs(sendFailure);
        }
        finally
        {
            logFactory.Dispose();
        }
    }

    [Fact]
    public async Task Dispose_WaitsForAnInFlightWriteBeforeReleasingResources()
    {
        using var sendEntered = new ManualResetEventSlim();
        using var releaseSend = new ManualResetEventSlim();
        using var resourceDisposed = new ManualResetEventSlim();
        using var disposeEntered = new ManualResetEventSlim();
        var writeActive = 0;
        var disposedDuringWrite = 0;
        var sendCount = 0;
        var connection = new Mock<IConnection>();
        var session = new Mock<ISession>();
        var producer = new Mock<IMessageProducer>();
        connection.Setup(candidate => candidate.CreateSession()).Returns(session.Object);
        session.Setup(candidate => candidate.GetQueue("nlog.messages")).Returns(new Mock<IQueue>().Object);
        session.Setup(candidate => candidate.CreateProducer(It.IsAny<IDestination>())).Returns(producer.Object);
        session.Setup(candidate => candidate.CreateTextMessage(It.IsAny<string>())).Returns(new Mock<ITextMessage>().Object);
        producer.Setup(candidate => candidate.Send(It.IsAny<IMessage>())).Callback(() =>
        {
            Interlocked.Increment(ref sendCount);
            Interlocked.Exchange(ref writeActive, 1);
            sendEntered.Set();
            if (!releaseSend.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The test did not release the blocked send in time.");
            Interlocked.Exchange(ref writeActive, 0);
        });
        producer.Setup(candidate => candidate.Dispose()).Callback(() =>
        {
            if (Volatile.Read(ref writeActive) != 0)
                Interlocked.Exchange(ref disposedDuringWrite, 1);
            resourceDisposed.Set();
        });
        session.Setup(candidate => candidate.Dispose());
        connection.Setup(candidate => candidate.Dispose());

        var target = new SignalingActiveMqTarget(_ => connection.Object, disposeEntered)
        {
            Name = "activeMq",
            Layout = "${message}"
        };
        var logFactory = CreateLogFactory(target);
        var logger = logFactory.GetLogger("dispose-race");
        var write = Task.Run(() => logger.Info("blocked"));

        try
        {
            sendEntered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the producer send should start");
            var dispose = Task.Run(target.Dispose);
            disposeEntered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the direct dispose call should reach the target");
            resourceDisposed.Wait(TimeSpan.FromMilliseconds(200))
                .Should().BeFalse("resource disposal must wait while the write is in flight");

            releaseSend.Set();
            await Task.WhenAll(write, dispose).WaitAsync(TimeSpan.FromSeconds(5));
            resourceDisposed.IsSet.Should().BeTrue();
            Volatile.Read(ref disposedDuringWrite).Should().Be(0);
            producer.Verify(candidate => candidate.Dispose(), Times.Once);
            session.Verify(candidate => candidate.Dispose(), Times.Once);
            connection.Verify(candidate => candidate.Dispose(), Times.Once);

            logger.Info("after dispose");
            Interlocked.CompareExchange(ref sendCount, 0, 0).Should().Be(1, "writes after disposal should not reach the producer");
        }
        finally
        {
            releaseSend.Set();
            logFactory.Dispose();
        }
    }

    private static LogFactory CreateLogFactory(ActiveMqTarget target)
    {
        var logFactory = new LogFactory();
        var configuration = new LoggingConfiguration(logFactory);
        configuration.AddTarget(target.Name ?? "activeMq", target);
        configuration.AddRuleForAllLevels(target);
        logFactory.Configuration = configuration;
        return logFactory;
    }

    private static (Mock<IConnection> Connection, Mock<ISession> Session, Mock<IMessageProducer> Producer) CreateResourcesWithFailingDisposal()
    {
        var connection = new Mock<IConnection>();
        var session = new Mock<ISession>();
        var producer = new Mock<IMessageProducer>();
        connection.Setup(candidate => candidate.CreateSession()).Returns(session.Object);
        session.Setup(candidate => candidate.GetQueue("nlog.messages")).Returns(new Mock<IQueue>().Object);
        session.Setup(candidate => candidate.CreateProducer(It.IsAny<IDestination>())).Returns(producer.Object);
        connection.Setup(candidate => candidate.Dispose()).Throws(new InvalidOperationException("connection cleanup failed"));
        session.Setup(candidate => candidate.Dispose()).Throws(new InvalidOperationException("session cleanup failed"));
        producer.Setup(candidate => candidate.Dispose()).Throws(new InvalidOperationException("producer cleanup failed"));
        return (connection, session, producer);
    }

    private sealed class TestableActiveMqTarget : ActiveMqTarget
    {
        public TestableActiveMqTarget(Func<ConnectionFactory, IConnection> createConnection) : base(createConnection)
        {
        }

        public void InitializeForTest() => InitializeTarget();
    }

    private sealed class SignalingActiveMqTarget : ActiveMqTarget
    {
        private readonly ManualResetEventSlim _disposeEntered;

        public SignalingActiveMqTarget(
            Func<ConnectionFactory, IConnection> createConnection,
            ManualResetEventSlim disposeEntered) : base(createConnection)
        {
            _disposeEntered = disposeEntered;
        }

        protected override void Dispose(bool disposing)
        {
            _disposeEntered.Set();
            base.Dispose(disposing);
        }
    }
}
