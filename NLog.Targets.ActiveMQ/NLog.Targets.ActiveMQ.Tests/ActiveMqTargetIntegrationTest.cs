using Apache.NMS;
using Apache.NMS.ActiveMQ;
using Apache.NMS.Util;
using FluentAssertions;
using NLog.Common;
using System.Reflection;

namespace NLog.Targets.ActiveMQ.Tests;

public class ActiveMqTargetIntegrationTest : IClassFixture<ActiveMqFixture>
{
    private readonly ActiveMqFixture _activeMqFixture;

    public ActiveMqTargetIntegrationTest(ActiveMqFixture activeMqFixture)
    {
        _activeMqFixture = activeMqFixture;
    }

    [Fact]
    public async Task Write_SendsMessageToActiveMq()
    {
        // Arrange
        var activeMqContainer = await _activeMqFixture.ActiveMqContainer;
        var target = new ActiveMqTarget
        {
            Uri = $"activemq:tcp://{activeMqContainer.Hostname}:{activeMqContainer.GetMappedPublicPort(61616)}",
            Destination = "queue://nlog.messages",
            Layout = "${message}"
        };

        InitializeTarget(target);

        var logEvent = new LogEventInfo(LogLevel.Info, "TestLogger", "Test Message");
        var asyncLogEvent = new AsyncLogEventInfo(logEvent, e => { });

        // Act
        try
        {
            target.WriteAsyncLogEvent(asyncLogEvent);
        }
        finally
        {
            CloseTarget(target);
        }

        // Assert 
        var uri = target.Uri?.Render(LogEventInfo.CreateNullEvent());
        var factory = new ConnectionFactory(uri);
        using (var connection = factory.CreateConnection())
        using (var session = connection.CreateSession())
        {
            var destinationName = target.Destination?.Render(LogEventInfo.CreateNullEvent());
            var destination = SessionUtil.GetDestination(session, destinationName);
            using (var consumer = session.CreateConsumer(destination))
            {
                connection.Start();

                await Task.Delay(1000);

                var receivedMessage = consumer.Receive(new TimeSpan(0, 0, 5)) as ITextMessage;
                receivedMessage.Should().NotBeNull();
                logEvent.FormattedMessage.Should().Be(receivedMessage.Text);
            }
        }
    }

    private static void InitializeTarget(Target target) => target.GetType().GetMethod("Initialize", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(target, new object?[] { null });

    private static void CloseTarget(Target target) => target.GetType().GetMethod("Close", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(target, Array.Empty<object>());

    public ValueTask DisposeAsync()
    {
        return _activeMqFixture.DisposeAsync();
    }
}
