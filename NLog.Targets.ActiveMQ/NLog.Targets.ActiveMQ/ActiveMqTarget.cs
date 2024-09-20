using System;
using Apache.NMS.ActiveMQ;
using Apache.NMS.Util;
using Apache.NMS;
using NLog.Common;
using NLog.Config;
using NLog.Layouts;

namespace NLog.Targets.ActiveMQ
{
    [Target("ActiveMQ")]
    public class ActiveMqTarget : TargetWithLayout
    {
        private const string _activeMqConnectionString = "tcp://localhost:61616";
        private const string _activeMqDestination = "queue://nlog.messages";

        private IConnection _connection;
        private ISession _session;
        private IMessageProducer _producer;

        public ActiveMqTarget()
        {
            Destination = _activeMqDestination;
            Uri = _activeMqConnectionString;
            Persistent = true;
        }

        /// <summary>
        /// Example: queue://FOO.BAR
        /// Example: topic://FOO.BAR
        /// </summary>
        [RequiredParameter]
        public Layout Destination { get; set; }
        /// <summary>
        /// Example: tcp://localhost:61616
        /// </summary>
        [RequiredParameter]
        public Layout Uri { get; set; }
        public bool Persistent { get; set; }
        public bool UseCompression { get; set; }
        public Layout Username { get; set; }
        public Layout Password { get; set; }
        public Layout ClientId { get; set; }

        protected override void InitializeTarget()
        {
            var uri = RenderLogEvent(Uri, LogEventInfo.CreateNullEvent());
            var username = RenderLogEvent(Username, LogEventInfo.CreateNullEvent());
            var password = RenderLogEvent(Password, LogEventInfo.CreateNullEvent());
            var clientId = RenderLogEvent(ClientId, LogEventInfo.CreateNullEvent());
            var destinationName = RenderLogEvent(Destination, LogEventInfo.CreateNullEvent());

            InternalLogger.Info("ActiveMQ(Name={0}): Creating connection to Uri={1} and Destination={2}", Name, uri, destinationName);

            try
            {
                var factory = new ConnectionFactory(new Uri(uri));
                if (!string.IsNullOrEmpty(username))
                {
                    factory.UserName = username;
                    factory.Password = password;
                }
                
                if (!string.IsNullOrEmpty(clientId))
                    factory.ClientId = clientId;

                if (UseCompression)
                    factory.UseCompression = true;

                factory.OnException -= MonitorFactoryExceptions;    // Avoid double subscriptions
                factory.OnException += MonitorFactoryExceptions;

                _connection = factory.CreateConnection();
                _connection.Start();

                _session = _connection.CreateSession();

                var destination = SessionUtil.GetDestination(_session, destinationName);
                _producer = _session.CreateProducer(destination);
                _producer.DeliveryMode = Persistent ? MsgDeliveryMode.Persistent : MsgDeliveryMode.NonPersistent;
            }
            catch (Exception ex)
            {
                InternalLogger.Error(ex, "ActiveMQ(Name={0}): Failed to create ActiveMQ connection to Uri={1} and Destination={2}", Name, uri, destinationName);
                throw;
            }

            base.InitializeTarget();
        }

        private static void MonitorFactoryExceptions(Exception ex)
        {
            InternalLogger.Error(ex, "ActiveMQ: Exception from ActiveMQ connection");
        }

        protected override void CloseTarget()
        {
            base.CloseTarget();

            _producer?.Dispose();
            _session?.Dispose();
            _connection?.Dispose();
        }

        protected override void Write(LogEventInfo logEvent)
        {
            var logMessage = RenderLogEvent(Layout, logEvent);
            var request = _session.CreateTextMessage(logMessage);
            _producer.Send(request);
        }
    }
}
