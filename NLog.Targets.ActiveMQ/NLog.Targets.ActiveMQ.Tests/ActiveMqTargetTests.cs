using Apache.NMS;
using Apache.NMS.ActiveMQ;
using FluentAssertions;
using Moq;
using NLog.Config;

namespace NLog.Targets.ActiveMQ.Tests;

public class ActiveMqTargetTests
{
    [Theory]
    [InlineData("create-connection")]
    [InlineData("start-connection")]
    [InlineData("create-session")]
    [InlineData("get-queue")]
    [InlineData("create-producer")]
    [InlineData("set-delivery-mode")]
    public void InitializeTarget_CleansUpPartialResources_AndPreservesOriginalFailure(string failurePoint)
    {
        var failure = new InvalidOperationException($"{failurePoint} failed");
        var connection = new Mock<IConnection>();
        var session = new Mock<ISession>();
        var producer = new Mock<IMessageProducer>();
        var queue = new Mock<IQueue>().Object;
        connection.Setup(candidate => candidate.CreateSession()).Returns(session.Object);
        if (failurePoint == "start-connection")
            connection.Setup(candidate => candidate.Start()).Throws(failure);
        if (failurePoint == "create-session")
            connection.Setup(candidate => candidate.CreateSession()).Throws(failure);

        session.Setup(candidate => candidate.GetQueue("nlog.messages")).Returns(queue);
        if (failurePoint == "get-queue")
            session.Setup(candidate => candidate.GetQueue("nlog.messages")).Throws(failure);

        session.Setup(candidate => candidate.CreateProducer(It.IsAny<IDestination>())).Returns(producer.Object);
        if (failurePoint == "create-producer")
            session.Setup(candidate => candidate.CreateProducer(It.IsAny<IDestination>())).Throws(failure);
        if (failurePoint == "set-delivery-mode")
            producer.SetupSet(candidate => candidate.DeliveryMode = It.IsAny<MsgDeliveryMode>()).Throws(failure);

        connection.Setup(candidate => candidate.Dispose()).Throws(new InvalidOperationException("connection cleanup failed"));
        session.Setup(candidate => candidate.Dispose()).Throws(new InvalidOperationException("session cleanup failed"));
        producer.Setup(candidate => candidate.Dispose()).Throws(new InvalidOperationException("producer cleanup failed"));

        var target = new TestableActiveMqTarget(factory =>
        {
            if (failurePoint == "create-connection")
                throw failure;
            return connection.Object;
        });

        target.Invoking(candidate => candidate.InitializeForTest())
            .Should().Throw<InvalidOperationException>()
            .Which.Should().BeSameAs(failure);

        connection.Verify(candidate => candidate.Dispose(), failurePoint == "create-connection" ? Times.Never() : Times.Once());
        session.Verify(candidate => candidate.Dispose(),
            failurePoint is "get-queue" or "create-producer" or "set-delivery-mode" ? Times.Once() : Times.Never());
        producer.Verify(candidate => candidate.Dispose(), failurePoint == "set-delivery-mode" ? Times.Once() : Times.Never());
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
