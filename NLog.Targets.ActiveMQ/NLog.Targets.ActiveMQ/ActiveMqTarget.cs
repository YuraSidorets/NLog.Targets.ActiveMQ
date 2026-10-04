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
        private static readonly object _factoryExceptionLock = new object();

        private IConnection? _connection;
        private ISession? _session;
        private IMessageProducer? _producer;
        private readonly Func<ConnectionFactory, IConnection> _createConnection;

        public ActiveMqTarget() : this(factory => factory.CreateConnection())
        {
        }

        internal ActiveMqTarget(Func<ConnectionFactory, IConnection> createConnection)
        {
            _createConnection = createConnection ?? throw new ArgumentNullException(nameof(createConnection));
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
        public Layout? Username { get; set; }
        public Layout? Password { get; set; }
        public Layout? ClientId { get; set; }

        protected override void InitializeTarget()
        {
            var uri = RenderLogEvent(Uri, LogEventInfo.CreateNullEvent());
            var username = RenderLogEvent(Username, LogEventInfo.CreateNullEvent());
            var password = RenderLogEvent(Password, LogEventInfo.CreateNullEvent());
            var clientId = RenderLogEvent(ClientId, LogEventInfo.CreateNullEvent());
            var destinationName = RenderLogEvent(Destination, LogEventInfo.CreateNullEvent());

            InternalLogger.Info("ActiveMQ(Name={0}): Creating connection and producer", Name);

            base.InitializeTarget();

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

                // NMS exposes a process-wide static event through an instance accessor.
                lock (_factoryExceptionLock)
                {
                    factory.OnException -= MonitorFactoryExceptions;
                    factory.OnException += MonitorFactoryExceptions;
                }

                _connection = _createConnection(factory);
                _connection.Start();

                _session = _connection.CreateSession();

                var destination = SessionUtil.GetDestination(_session, destinationName);
                _producer = _session.CreateProducer(destination);
                _producer.DeliveryMode = Persistent ? MsgDeliveryMode.Persistent : MsgDeliveryMode.NonPersistent;
            }
            catch (Exception ex)
            {
                DisposeResources();
                InternalLogger.Error(ex, "ActiveMQ(Name={0}): Failed to create connection and producer", Name);
                throw;
            }
        }

        private static void MonitorFactoryExceptions(Exception ex)
        {
            InternalLogger.Error(ex, "ActiveMQ: Exception from ActiveMQ connection");
        }

        protected override void CloseTarget()
        {
            try
            {
                base.CloseTarget();
            }
            finally
            {
                DisposeResources();
            }
        }

        protected override void Dispose(bool disposing)
        {
            // NLog serializes normal writes and close; direct Dispose must use the same lock.
            lock (SyncRoot)
            {
                base.Dispose(disposing);
            }
        }

        private void DisposeResources()
        {
            var producer = _producer;
            var session = _session;
            var connection = _connection;

            _producer = null;
            _session = null;
            _connection = null;

            DisposeResource(producer, nameof(_producer));
            DisposeResource(session, nameof(_session));
            DisposeResource(connection, nameof(_connection));
        }

        private void DisposeResource(IDisposable? resource, string resourceName)
        {
            if (resource == null)
                return;

            try
            {
                resource.Dispose();
            }
            catch (Exception ex)
            {
                InternalLogger.Warn(ex, "ActiveMQ(Name={0}): Failed to dispose {1}", Name, resourceName);
            }
        }

        protected override void Write(LogEventInfo logEvent)
        {
            var logMessage = RenderLogEvent(Layout, logEvent);
            var request = _session!.CreateTextMessage(logMessage);
            _producer!.Send(request);
        }
    }
}
