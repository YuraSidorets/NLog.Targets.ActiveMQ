using Apache.NMS;
using Apache.NMS.ActiveMQ;
using Apache.NMS.Util;
using FluentAssertions;
using NLog.Config;

namespace NLog.Targets.ActiveMQ.Tests;

[Trait("Category", "Integration")]
public class ActiveMqTargetIntegrationTest : IClassFixture<ActiveMqFixture>
{
    private readonly ActiveMqFixture _activeMqFixture;

    public ActiveMqTargetIntegrationTest(ActiveMqFixture activeMqFixture)
    {
        _activeMqFixture = activeMqFixture;
    }

    [Theory]
    [InlineData("queue", true, false)]
    [InlineData("queue", false, true)]
    [InlineData("topic", true, true)]
    [InlineData("topic", false, false)]
    public void Write_SendsRenderedMessage(string destinationType, bool persistent, bool compression)
    {
        var container = _activeMqFixture.ActiveMqContainer;
        var uri = $"tcp://{container.Hostname}:{container.GetMappedPublicPort(61616)}";
        var destinationName = $"{destinationType}://nlog.tests.{Guid.NewGuid():N}";
        var message = new string('x', 4096);

        var connectionFactory = new ConnectionFactory(uri);
        using var connection = connectionFactory.CreateConnection();
        using var session = connection.CreateSession();
        var destination = SessionUtil.GetDestination(session, destinationName);
        // Subscribe before publishing so topic messages cannot race the consumer.
        using var consumer = session.CreateConsumer(destination);
        connection.Start();

        using var logFactory = new LogFactory { ThrowExceptions = true };
        var target = new ActiveMqTarget
        {
            Name = "broker",
            Uri = uri,
            Destination = destinationName,
            Layout = "${level}|${message}",
            Persistent = persistent,
            UseCompression = compression
        };
        var configuration = new LoggingConfiguration(logFactory);
        configuration.AddRule(LogLevel.Info, LogLevel.Fatal, target);
        logFactory.Configuration = configuration;

        logFactory.GetLogger("integration").Info(message);

        var receivedMessage = consumer.Receive(TimeSpan.FromSeconds(10))
            .Should().BeAssignableTo<ITextMessage>().Subject;
        receivedMessage.Text.Should().Be($"Info|{message}");
        receivedMessage.NMSDeliveryMode.Should().Be(
            persistent ? MsgDeliveryMode.Persistent : MsgDeliveryMode.NonPersistent);
    }
}
