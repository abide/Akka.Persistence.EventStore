using Akka.Configuration;
using System;
using System.Threading.Tasks;
using Akka.Event;
using EventStore.ClientAPI;

namespace Akka.Persistence.EventStore
{
    public class JournalSettings
    {
        public const string ConfigPath = "akka.persistence.journal.eventstore";

        private readonly Task<IEventStoreConnection> _init;

        public JournalSettings(ILoggingAdapter log, Config config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config), "EventStore journal settings cannot be initialized, because required HOCON section couldn't been found");

            var host = config.GetString("host");
            var tcpPort = config.GetInt("tcp-port");

            var settingsFactoryType = Type.GetType(config.GetString("connection-factory"));
            var factory = settingsFactoryType != null
                ? (IConnectionFactory)Activator.CreateInstance(settingsFactoryType)
                : new DefaultConnectionFactory();

            _init = factory.CreateAsync(log, host, tcpPort);
        }

        public IEventStoreConnection Connection => _init.Result;
    }

    public class SnapshotStoreSettings
    {
        public const string ConfigPath = "akka.persistence.snapshot-store.eventstore";

        private readonly Task<IEventStoreConnection> _init;

        public SnapshotStoreSettings(ILoggingAdapter log, Config config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config), "EventStore snapshot settings cannot be initialized, because required HOCON section couldn't been found");

            var host = config.GetString("host");
            var tcpPort = config.GetInt("tcp-port");

            var settingsFactoryType = Type.GetType(config.GetString("connection-factory"));
            var factory = settingsFactoryType != null
                ? (IConnectionFactory)Activator.CreateInstance(settingsFactoryType)
                : new DefaultConnectionFactory();

            _init = factory.CreateAsync(log, host, tcpPort);
        }

        public IEventStoreConnection Connection => _init.Result;
    }
}
