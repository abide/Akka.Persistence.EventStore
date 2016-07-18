using Akka.Actor;
using Akka.Configuration;

namespace Akka.Persistence.EventStore
{
    /// <summary>
    /// An actor system extension initializing support for EventStore persistence layer.
    /// </summary>
    public class EventStorePersistenceExtension : IExtension
    {
        /// <summary>
        /// Journal-related settings loaded from HOCON configuration.
        /// </summary>
        public readonly JournalSettings JournalSettings;

        /// <summary>
        /// Snapshot store related settings loaded from HOCON configuration.
        /// </summary>
        public readonly SnapshotStoreSettings SnapshotStoreSettings;

        public EventStorePersistenceExtension(ExtendedActorSystem system)
        {
            system.Settings.InjectTopLevelFallback(EventStorePersistence.DefaultConfiguration());

            JournalSettings = new JournalSettings(system.Log, system.Settings.Config.GetConfig(JournalSettings.ConfigPath));
            SnapshotStoreSettings = new SnapshotStoreSettings(system.Log, system.Settings.Config.GetConfig(SnapshotStoreSettings.ConfigPath));
        }
    }

    /// <summary>
    /// Singleton class used to setup EventStore backend for akka persistence plugin.
    /// </summary>
    public class EventStorePersistence : ExtensionIdProvider<EventStorePersistenceExtension>
    {
        public static readonly EventStorePersistence Instance = new EventStorePersistence();

        /// <summary>
        /// Initializes a EventStore persistence plugin inside provided <paramref name="actorSystem"/>.
        /// </summary>
        public static void Init(ActorSystem actorSystem)
        {
            Instance.Apply(actorSystem);
        }

        private EventStorePersistence() { }

        /// <summary>
        /// Creates an actor system extension for akka persistence EventStore support.
        /// </summary>
        /// <param name="system"></param>
        /// <returns></returns>
        public override EventStorePersistenceExtension CreateExtension(ExtendedActorSystem system)
        {
            return new EventStorePersistenceExtension(system);
        }

        /// <summary>
        /// Returns a default configuration for akka persistence EventStore-based journals and snapshot stores.
        /// </summary>
        /// <returns></returns>
        public static Config DefaultConfiguration()
        {
            return ConfigurationFactory.FromResource<EventStorePersistence>("Akka.Persistence.EventStore.event-store.conf");
        }
    }

}
