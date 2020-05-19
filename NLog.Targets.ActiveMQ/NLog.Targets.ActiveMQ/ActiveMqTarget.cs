using System;
using System.ComponentModel;
using Apache.NMS;
using Apache.NMS.ActiveMQ;
using Apache.NMS.Util;
using NLog.Config;
using NLog.Layouts;

namespace NLog.Targets.ActiveMQ
{
    [Target("ActiveMQ")]
    public class ActiveMqTarget : TargetWithLayout
    {
        private readonly string _activeMqConnectionString = "tcp://localhost:61616";
        private readonly string _activeMqDestination = "queue://nlog.messages";

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

        protected override void Write(LogEventInfo logEvent)
        {
            var factory = new ConnectionFactory(new Uri(Uri));
            if (!string.IsNullOrEmpty(Username))
            {
                factory.UserName = Username;
                factory.Password = Password;
            }
            if (!string.IsNullOrEmpty(ClientId))
                factory.ClientId = ClientId;

            using (var connection = factory.CreateConnection())
            using (var session = connection.CreateSession())
            {
                var destination = SessionUtil.GetDestination(session, Destination.Render(logEvent));
                using (var producer = session.CreateProducer(destination))
                {
                    connection.Start();
                    producer.DeliveryMode = Persistent
                            ? MsgDeliveryMode.Persistent
                            : MsgDeliveryMode.NonPersistent;

                    var logMessage = Layout.Render(logEvent);
                    var request = session.CreateTextMessage(logMessage);
                    producer.Send(request);
                }
            }
        }
    }
}