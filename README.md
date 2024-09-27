# NLog.Targets.ActiveMQ [![NuGet Release](https://img.shields.io/nuget/vpre/NLog.Targets.ActiveMQ.svg)](https://nuget.org/packages/NLog.Targets.ActiveMQ) 
NLog custom target for ActiveMQ

# Options

| Name    | Type   | Description |
|---------|--------|-------------|
| `Uri` | Layout | URL for the ActiveMQ Connecttion. Default: `tcp://localhost:61616`  |
| `Destination` | Layout | Destination for the ActiveMQ message. Default: `queue://nlog.messages` |
| `Layout`  | Layout | Payload for the ActiveMQ message |
| `Persistent` | Bool | Control delivery-mode whether Persistent or NonPersistent. Default = `True` |
| `UseCompression` | Bool | Control whether to enable compression for producer. Default = `False` |
| `Username` | Layout | Optional UserName for basic authentication |
| `Password` | Layout | Optional Password for basic authentication |
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

Based on [Nlog.Contrib.ActiveMq](https://github.com/NLog/NLog.Contrib.ActiveMQ)
