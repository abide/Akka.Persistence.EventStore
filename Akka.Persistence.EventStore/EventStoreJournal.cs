using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.Serialization.Formatters;
using System.Text;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Event;
using Akka.Persistence.Journal;
using EventStore.ClientAPI;
using Newtonsoft.Json;

namespace Akka.Persistence.EventStore
{
    public class EventStoreJournal : AsyncWriteJournal
    {
        private const int BatchSize = 500;
        private readonly IEventStoreConnection _connection;
        private readonly JsonSerializerSettings _serializerSettings;
        private readonly ILoggingAdapter _log;

        public EventStoreJournal()
        {
            _log = Context.GetLogger();

            _serializerSettings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Objects,
                TypeNameAssemblyFormat = FormatterAssemblyStyle.Simple,
                Formatting = Formatting.Indented,
                Converters =
                {
                    new ActorRefConverter(Context)
                }
            };

            var extension = EventStorePersistence.Instance.Apply(Context.System);

            _connection = extension.JournalSettings.Connection;
        }

        public override async Task ReplayMessagesAsync(IActorContext context, string persistenceId, long fromSequenceNr,
            long toSequenceNr, long max,
            Action<IPersistentRepresentation> recoveryCallback)
        {
            if (toSequenceNr < fromSequenceNr || max == 0 || fromSequenceNr == 0) return;
            if (fromSequenceNr == toSequenceNr) max = 1;
            if (toSequenceNr > fromSequenceNr && max == toSequenceNr) max = toSequenceNr - fromSequenceNr + 1;

            long count = 0;
            int start = (int) fromSequenceNr - 1;
            var localBatchSize = BatchSize;
            StreamEventsSlice slice;
            do
            {
                if (max == long.MaxValue && toSequenceNr > fromSequenceNr)
                {
                    max = toSequenceNr - fromSequenceNr + 1;
                }
                if (max < localBatchSize)
                {
                    localBatchSize = (int) max;
                }
                slice = await _connection.ReadStreamEventsForwardAsync(persistenceId, start, localBatchSize, false);

                foreach (var @event in slice.Events)
                {
                    var json = Encoding.UTF8.GetString(@event.OriginalEvent.Data);
                    var representation = JsonConvert.DeserializeObject<IPersistentRepresentation>(json, _serializerSettings);
                    recoveryCallback(representation);
                    count++;
                    if (count == max) return;
                }

                start = slice.NextEventNumber;
            } while (!slice.IsEndOfStream);
        }

        public override async Task<long> ReadHighestSequenceNrAsync(string persistenceId, long fromSequenceNr)
        {
            var slice = await _connection.ReadStreamEventsBackwardAsync(persistenceId, StreamPosition.End, 1, false);

            long sequence = 0;

            if (slice.Events.Any())
                sequence = slice.Events.First().OriginalEventNumber + 1;

            return sequence;
        }

        protected override async Task<IImmutableList<Exception>> WriteMessagesAsync(IEnumerable<AtomicWrite> messages)
        {
            var exceptions = new List<Exception>();

            foreach (var msg in messages)
            {
                try
                {
                    await Write(msg);
                }
                catch (Exception e)
                {
                    exceptions.Add(e);
                }
                exceptions.Add(null);
            }

            return exceptions.ToImmutableList();
        }

        private Task Write(AtomicWrite message)
        {
            var stream = message.PersistenceId;

            var representations = (IImmutableList<IPersistentRepresentation>) message.Payload;

            var expectedVersion = (int) representations.First().SequenceNr - 2;

            var events = representations.Select(x =>
            {
                var eventId = GuidUtility.Create(GuidUtility.IsoOidNamespace, string.Concat(stream, x.SequenceNr));
                var json = JsonConvert.SerializeObject(x, _serializerSettings);
                var data = Encoding.UTF8.GetBytes(json);
                var meta = new byte[0];
                var payload = x.Payload;
                if (payload.GetType().GetProperty("Metadata") != null)
                {
                    var propType = payload.GetType().GetProperty("Metadata").PropertyType;
                    var metaJson = JsonConvert.SerializeObject(payload.GetType().GetProperty("Metadata").GetValue(x.Payload), propType, _serializerSettings);
                    meta = Encoding.UTF8.GetBytes(metaJson);
                }
                return new EventData(eventId, x.GetType().FullName, true, data, meta);
            });

            return _connection.AppendToStreamAsync(stream,
                    expectedVersion < 0 ? ExpectedVersion.NoStream : expectedVersion, events);
        }

        protected override Task DeleteMessagesToAsync(string persistenceId, long toSequenceNr)
        {
            return _connection.SetStreamMetadataAsync(persistenceId, ExpectedVersion.Any,
                StreamMetadata.Build().SetTruncateBefore((int) toSequenceNr));
        }
    }
}
