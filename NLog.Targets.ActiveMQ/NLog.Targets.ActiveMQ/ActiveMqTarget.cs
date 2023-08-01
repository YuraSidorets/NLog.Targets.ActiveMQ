using Apache.NMS.ActiveMQ;
using Apache.NMS.Util;
using Apache.NMS;
using NLog.Config;
using NLog.Layouts;
using System.ComponentModel;
using System;

namespace NLog.Targets.ActiveMQ
{
    [Target("ActiveMQ")]
    public class ActiveMqTarget : TargetWithLayout
    {
        private readonly string _activeMqConnectionString = "tcp://localhost:61616";
        private readonly string _activeMqDestination = "queue://nlog.messages";

        private IConnection _connection;
        private ISession _session;
        private IMessageProducer _producer;

        public ActiveMqTarget()
        {
            Destination = _activeMqDestination;
            Uri = _activeMqConnectionString;
            Persistent = true;
        }

        [RequiredParameter]
        public Layout Destination { get; set; }
        [RequiredParameter]
        public string Uri { get; set; }
        [DefaultValue(true)]
        public bool Persistent { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string ClientId { get; set; }

        protected override void InitializeTarget()
        {
            base.InitializeTarget();

            var factory = new ConnectionFactory(new Uri(Uri));
            if (!string.IsNullOrEmpty(Username))
            {
                factory.UserName = Username;
                factory.Password = Password;
            }
            if (!string.IsNullOrEmpty(ClientId))
                factory.ClientId = ClientId;

            _connection = factory.CreateConnection();
            _session = _connection.CreateSession();

            var destination = SessionUtil.GetDestination(_session, Destination.Render(new LogEventInfo()));
            _producer = _session.CreateProducer(destination);

            _connection.Start();
            _producer.DeliveryMode = Persistent ? MsgDeliveryMode.Persistent : MsgDeliveryMode.NonPersistent;
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
            var logMessage = Layout.Render(logEvent);
            var request = _session.CreateTextMessage(logMessage);
            _producer.Send(request);
        }
    }
}
