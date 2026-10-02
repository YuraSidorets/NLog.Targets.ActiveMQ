# NLog.Targets.ActiveMQ [![NuGet Release](https://img.shields.io/nuget/vpre/NLog.Targets.ActiveMQ.svg)](https://nuget.org/packages/NLog.Targets.ActiveMQ) 
NLog custom target for ActiveMQ

# Options

| Name    | Type   | Description |
|---------|--------|-------------|
| `Uri` | Layout | URL for the ActiveMQ connection. Default: `tcp://localhost:61616`  |
| `Destination` | Layout | Destination for the ActiveMQ message. Default: `queue://nlog.messages` |
| `Layout`  | Layout | Payload for the ActiveMQ message |
| `Persistent` | Bool | Control delivery-mode whether Persistent or NonPersistent. Default = `True` |
| `UseCompression` | Bool | Control whether to enable compression for producer. Default = `False` |
| `Username` | Layout | Optional broker username |
| `Password` | Layout | Optional broker password |
| `ClientId` | Layout | Optional identifier for this publisher-client |

# Example NLog.config

```xml
<nlog>
  <extensions>
    <add assembly="NLog.Targets.ActiveMQ" />
  </extensions>

  <targets>
    <target type="ActiveMQ" name="ActiveMQ" Uri="tcp://localhost:61616" Destination="queue://nlog.messages">
      <layout>${longdate} ${level} ${message} ${exception}</layout>
    </target>
  </targets>

  <rules>
    <logger name="*" minlevel="Info" writeTo="ActiveMQ" />
  </rules>
</nlog>
```

See also: [ActiveMQ Uri Configuration](https://activemq.apache.org/components/nms/providers/activemq/uri-configuration)

At initialization, NLog renders the connection configuration (`Uri`, `Destination`,
`Username`, `Password`, and `ClientId`) once. NLog renders the message `Layout` for
each log event.

Supply credentials through `Username` and `Password`, not the URI.
Broker/client exceptions can include connection details in NLog's internal log.

## Throughput and broker outages

This target sends synchronously. Use NLog's built-in
[AsyncWrapper](https://github.com/NLog/NLog/wiki/AsyncWrapper-target) to move sends
off application threads during normal operation:

```xml
<target type="AsyncWrapper" name="ActiveMQ" queueLimit="10000" overflowAction="Block">
  <target type="ActiveMQ" Uri="tcp://localhost:61616" Destination="queue://nlog.messages">
    <layout>${longdate} ${level} ${message} ${exception}</layout>
  </target>
</target>
```

Replace the target in the first example with this wrapper.
When the bounded queue is full, the wrapper blocks callers.
If loss of log events is acceptable, use `Discard` instead.
Call `LogManager.Shutdown()` on application exit to flush buffered events and close
the connection.

A blocked send can delay flushing and shutdown.
Set broker timeouts to suit your application's shutdown budget.

NMS supports a `failover:` URI for reconnects. For example (XML attribute syntax):

```xml
Uri="failover:(tcp://broker-a:61616,tcp://broker-b:61616)?transport.timeout=5000&amp;transport.maxReconnectAttempts=5"
```

The timeout bounds how long a send waits during failover.
It is not an end-to-end startup or shutdown timeout.
For additional limits, configure TCP connection/request timeouts using the
[NMS URI configuration](https://activemq.apache.org/components/nms/providers/activemq/uri-configuration).

NLog handles synchronous send errors through `throwExceptions` and internal logging.
The asynchronous wrapper cannot throw background failures on the original logging caller.
Monitor NLog's internal log for these failures.
There is no target-owned retry queue or exactly-once guarantee. Persistence does
not protect events still buffered in process memory.

## Development and releases

Build/tests use the .NET 10 SDK. The shipped library remains `netstandard2.0` and
keeps its NLog 5.2.2 minimum. The integration tests require Docker with Linux containers.
Run these commands from the repository root:

```sh
dotnet restore NLog.Targets.ActiveMQ/NLog.Targets.ActiveMQ.sln
dotnet build NLog.Targets.ActiveMQ/NLog.Targets.ActiveMQ.sln -c Release --no-restore
dotnet test NLog.Targets.ActiveMQ/NLog.Targets.ActiveMQ.sln -c Release --no-build
# Unit tests only (no Docker):
dotnet test NLog.Targets.ActiveMQ/NLog.Targets.ActiveMQ.sln -c Release --no-build --filter "Category!=Integration"
```

Tests retain xUnit v2 and FluentAssertions v6 for this maintenance release.
Migration to a new test framework major is separate from the library fixes.
Integration tests use a pinned Apache broker image and a random host port, and
xUnit owns container startup and cleanup.

For a release, update the library's `Version` and changelog before you merge into
`master`. Only a matching `v<Version>` tag triggers publishing. The publish workflow
checks the tag against the package version and runs all tests before it publishes.
Ordinary branch pushes never publish a package.

Based on [Nlog.Contrib.ActiveMq](https://github.com/NLog/NLog.Contrib.ActiveMQ)
