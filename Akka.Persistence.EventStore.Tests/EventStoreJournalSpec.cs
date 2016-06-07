using Akka.Configuration;
using Akka.Persistence.TestKit.Journal;

namespace Akka.Persistence.EventStore.Tests
{
    public class EventStoreJournalSpec : JournalSpec
    {
        private static readonly Config SpecConfig = ConfigurationFactory.ParseString(@"
            akka {
                stdout-loglevel = DEBUG
	            loglevel = DEBUG
                loggers = [""Akka.Logger.NLog.NLogLogger,Akka.Logger.NLog""]

                persistence {

                publish-plugin-commands = on
                journal {
                    plugin = ""akka.persistence.journal.event-store""
                    event-store {
                        class = ""EventStore.Persistence.EventStoreJournal, Akka.Persistence.EventStore""
                        plugin-dispatcher = ""akka.actor.default-dispatcher""
                        
                        # the event store connection string
			            connection-string = ""ConnectTo=tcp://admin:changeit@127.0.0.1:1113;""

			            # name of the connection
			            connection-name = ""akka.net""
                    }
                }
            }
        }
        ");

        public EventStoreJournalSpec()
            : base(SpecConfig, "EventStoreJournalSpec") 
        {
            Initialize();

        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            //cleanup
            StorageCleanup.Clean();
        }
    }
}
