using Apache.NMS;
using Apache.NMS.ActiveMQ;
using FluentAssertions;
using Moq;
using NLog;
using NLog.Config;

namespace NLog.Targets.ActiveMQ.Tests;

public class ActiveMqTargetInitializationLifecycleTests
{
    [Fact]
    public void LogFactory_CreateConnectionFails_ReportsFailureAndDisposesNoUncreatedResources()
    {
        var failure = new InvalidOperationException("create-connection failed");
        var resources = CreateResourcesWithFailingDisposal();
        var target = CreateTarget(_ => throw failure);

        var callerFailure = InitializeThroughLogFactoryAndVerifyCleanup(target, () =>
        {
            resources.Producer.Verify(candidate => candidate.Dispose(), Times.Never);
            resources.Session.Verify(candidate => candidate.Dispose(), Times.Never);
            resources.Connection.Verify(candidate => candidate.Dispose(), Times.Never);
        });

        AssertFailureReachedCaller(callerFailure, failure);
    }

    [Fact]
    public void LogFactory_StartFails_DisposesConnectionOnceAndReportsFailure()
    {
        var failure = new InvalidOperationException("start-connection failed");
        var resources = CreateResourcesWithFailingDisposal();
        resources.Connection.Setup(candidate => candidate.Start()).Throws(failure);
        var target = CreateTarget(_ => resources.Connection.Object);

        var callerFailure = InitializeThroughLogFactoryAndVerifyCleanup(target, () =>
        {
            resources.Producer.Verify(candidate => candidate.Dispose(), Times.Never);
            resources.Session.Verify(candidate => candidate.Dispose(), Times.Never);
            resources.Connection.Verify(candidate => candidate.Dispose(), Times.Once);
        });

        AssertFailureReachedCaller(callerFailure, failure);
    }

    [Fact]
    public void LogFactory_CreateSessionFails_DisposesConnectionOnceAndReportsFailure()
    {
        var failure = new InvalidOperationException("create-session failed");
        var resources = CreateResourcesWithFailingDisposal();
        resources.Connection.Setup(candidate => candidate.CreateSession()).Throws(failure);
        var target = CreateTarget(_ => resources.Connection.Object);

        var callerFailure = InitializeThroughLogFactoryAndVerifyCleanup(target, () =>
        {
            resources.Producer.Verify(candidate => candidate.Dispose(), Times.Never);
            resources.Session.Verify(candidate => candidate.Dispose(), Times.Never);
            resources.Connection.Verify(candidate => candidate.Dispose(), Times.Once);
        });

        AssertFailureReachedCaller(callerFailure, failure);
    }

    [Fact]
    public void LogFactory_GetQueueFails_DisposesSessionAndConnectionOnceAndReportsFailure()
    {
        var failure = new InvalidOperationException("get-queue failed");
        var resources = CreateResourcesWithFailingDisposal();
        resources.Session.Setup(candidate => candidate.GetQueue("nlog.messages")).Throws(failure);
        var target = CreateTarget(_ => resources.Connection.Object);

        var callerFailure = InitializeThroughLogFactoryAndVerifyCleanup(target, () =>
        {
            resources.Producer.Verify(candidate => candidate.Dispose(), Times.Never);
            resources.Session.Verify(candidate => candidate.Dispose(), Times.Once);
            resources.Connection.Verify(candidate => candidate.Dispose(), Times.Once);
        });

        AssertFailureReachedCaller(callerFailure, failure);
    }

    [Fact]
    public void LogFactory_CreateProducerFails_DisposesSessionAndConnectionOnceAndReportsFailure()
    {
        var failure = new InvalidOperationException("create-producer failed");
        var resources = CreateResourcesWithFailingDisposal();
        resources.Session.Setup(candidate => candidate.CreateProducer(It.IsAny<IDestination>())).Throws(failure);
        var target = CreateTarget(_ => resources.Connection.Object);

        var callerFailure = InitializeThroughLogFactoryAndVerifyCleanup(target, () =>
        {
            resources.Producer.Verify(candidate => candidate.Dispose(), Times.Never);
            resources.Session.Verify(candidate => candidate.Dispose(), Times.Once);
            resources.Connection.Verify(candidate => candidate.Dispose(), Times.Once);
        });

        AssertFailureReachedCaller(callerFailure, failure);
    }

    [Fact]
    public void LogFactory_SetDeliveryModeFails_DisposesAllResourcesOnceAndReportsFailure()
    {
        var failure = new InvalidOperationException("set-delivery-mode failed");
        var resources = CreateResourcesWithFailingDisposal();
        resources.Producer.SetupSet(candidate => candidate.DeliveryMode = It.IsAny<MsgDeliveryMode>()).Throws(failure);
        var target = CreateTarget(_ => resources.Connection.Object);

        var callerFailure = InitializeThroughLogFactoryAndVerifyCleanup(target, () =>
        {
            resources.Producer.Verify(candidate => candidate.Dispose(), Times.Once);
            resources.Session.Verify(candidate => candidate.Dispose(), Times.Once);
            resources.Connection.Verify(candidate => candidate.Dispose(), Times.Once);
        });

        AssertFailureReachedCaller(callerFailure, failure);
    }

    private static Exception? InitializeThroughLogFactoryAndVerifyCleanup(ActiveMqTarget target, Action verifyCleanup)
    {
        var logFactory = new LogFactory { ThrowExceptions = true };
        var configuration = new LoggingConfiguration(logFactory);
        configuration.AddTarget(target.Name ?? "activeMq", target);
        configuration.AddRuleForAllLevels(target);

        Exception? callerFailure;
        try
        {
            callerFailure = Record.Exception(() =>
            {
                logFactory.Configuration = configuration;
                logFactory.GetLogger("initialization").Info("initialize target");
            });
            verifyCleanup();
        }
        finally
        {
            logFactory.Dispose();
            logFactory.Dispose();
        }

        verifyCleanup();
        return callerFailure;
    }

    private static void AssertFailureReachedCaller(Exception? callerFailure, Exception originalFailure)
    {
        callerFailure.Should().NotBeNull("NLog should surface the target initialization failure when ThrowExceptions is enabled");

        callerFailure!.GetBaseException().Should().BeSameAs(originalFailure);
    }

    private static ActiveMqTarget CreateTarget(Func<ConnectionFactory, IConnection> createConnection)
    {
        return new ActiveMqTarget(createConnection)
        {
            Name = "activeMq",
            Layout = "${message}"
        };
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
}
