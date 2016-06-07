using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.Serialization.Formatters;
using System.Text;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Event;
using Akka.Persistence;
using Akka.Persistence.EventStore;
using Akka.Persistence.Journal;
using EventStore.ClientAPI;
using Newtonsoft.Json;

namespace EventStore.Persistence
{
    public class EventStoreJournal : AsyncWriteJournal
    {
        private int _batchSize = 500;
        private readonly Lazy<Task<IEventStoreConnection>> _connection;
        private readonly JsonSerializerSettings _serializerSettings;
        private ILoggingAdapter _log;
        private readonly EventStorePersistenceExtension _extension;

        public EventStoreJournal()
        {
            _log = Context.GetLogger();
            _extension = EventStorePersistence.Instance.Apply(Context.System);

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

            _connection = new Lazy<Task<IEventStoreConnection>>(async () =>
            {
                try
                {
                    IEventStoreConnection connection = EventStoreConnection.Create(_extension.EventStoreJournalSettings.ConnectionString, _extension.EventStoreJournalSettings.ConnectionName);
                    await connection.ConnectAsync();
                    return connection;
                }
                catch(Exception exc)
                {
                    _log.Error(exc.ToString());
                    return null;
                }
            });
        }

        private Task<IEventStoreConnection> GetConnection()
        {
            return _connection.Value;
        }

        public override async Task<long> ReadHighestSequenceNrAsync(string persistenceId, long fromSequenceNr)
        {
            try
            {
                var connection = await GetConnection();

                var slice = await connection.ReadStreamEventsBackwardAsync(persistenceId, StreamPosition.End, 1, false);

                long sequence = 0;

                if (slice.Events.Any())
                    sequence = slice.Events.First().OriginalEventNumber + 1;

                return sequence;
            }
            catch (Exception e)
            {
                _log.Error(e, e.Message);
                throw;
            }
        }

        public override async Task ReplayMessagesAsync(
            IActorContext context, 
            string persistenceId, 
            long fromSequenceNr, 
            long toSequenceNr, 
            long max,
            Action<IPersistentRepresentation> recoveryCallback)
        {
            try
            {
                if (toSequenceNr < fromSequenceNr || max == 0) return;
                if (fromSequenceNr == toSequenceNr) max = 1;
                if (toSequenceNr > fromSequenceNr && max == toSequenceNr) max = toSequenceNr - fromSequenceNr + 1;
                var connection = await GetConnection();
                long count = 0;
                int start = ((int) fromSequenceNr-1);
                var localBatchSize = _batchSize;
                StreamEventsSlice slice;
                do
                {
                    if (max == long.MaxValue && toSequenceNr > fromSequenceNr)
                    {
                        max = toSequenceNr - fromSequenceNr + 1;
                    }
                    if (max < localBatchSize)
                    {
                        localBatchSize = (int)max;
                    }
                    slice = await connection.ReadStreamEventsForwardAsync(persistenceId, start, localBatchSize, false);

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
            catch (Exception e)
            {
                _log.Error(e, "Error replaying messages for: {0}", persistenceId);
                throw;
            }
        }

        protected override async Task<IImmutableList<Exception>> WriteMessagesAsync(IEnumerable<Akka.Persistence.AtomicWrite> messages)
        {
            var atomicWriteTasks = messages.Select(atomicWrite =>
                ((IImmutableList<IPersistentRepresentation>) atomicWrite.Payload)
                    .GroupBy(representation => representation.PersistenceId)
                    .Select(AppendToStreamAsync))
                    .Select(AggregateTaskExceptions);

            var atomicWriteExceptions = await CaptureTaskExceptions(atomicWriteTasks);
            var list = atomicWriteExceptions.ToList();
            return list.ToImmutableList();
        }

        private async Task<Exception> AggregateTaskExceptions(IEnumerable<Task> streamTasks)
        {
            var exceptions = await CaptureTaskExceptions(streamTasks);

            var nonNullExceptions = exceptions.TakeWhile(x => x != null).ToList();
            return nonNullExceptions.Count == 1
                ? nonNullExceptions.Single()
                : nonNullExceptions.Any() ? new AggregateException(nonNullExceptions) : null;
        }

        private async Task<IEnumerable<Exception>> CaptureTaskExceptions(IEnumerable<Task> streamTasks)
        {
            var exceptions = await Task<IEnumerable<Exception>>.Factory
                .ContinueWhenAll(streamTasks.ToArray(),
                    tasks => tasks.Select(t => t.IsFaulted ? TryUnwrapException(t.Exception) : null));
            return exceptions;
        }

        private async Task AppendToStreamAsync(IGrouping<string, IPersistentRepresentation> representations)
        {
            var stream = representations.Key;
            var events = representations.Select(x => TransformRepresentationIntoEventData(stream, x));
            var connection = await GetConnection();
            var expectedVersion = (int) representations.First().SequenceNr - 2;
            expectedVersion = expectedVersion < 0 ? ExpectedVersion.NoStream : expectedVersion;
            await connection.AppendToStreamAsync(stream, expectedVersion, events);
            connection.Close();
        }

        private EventData TransformRepresentationIntoEventData(string stream, IPersistentRepresentation x)
        {
            var eventId = GuidUtility.Create(GuidUtility.IsoOidNamespace,
                string.Concat(stream, x.SequenceNr));
            var json = JsonConvert.SerializeObject(x, _serializerSettings);
            var data = Encoding.UTF8.GetBytes(json);
            var meta = new byte[0];
            var payload = x.Payload;
            if (payload.GetType().GetProperty("Metadata") != null)
            {
                var propType = payload.GetType().GetProperty("Metadata").PropertyType;
                var metaJson =
                    JsonConvert.SerializeObject(
                        payload.GetType().GetProperty("Metadata").GetValue(x.Payload), propType,
                        _serializerSettings);
                meta = Encoding.UTF8.GetBytes(metaJson);
            }
            return new EventData(eventId, x.GetType().FullName, true, data, meta);
        }

        private Exception TryUnwrapException(Exception e)
        {
            var aggregateException = e as AggregateException;
            if (aggregateException != null)
            {
                aggregateException = aggregateException.Flatten();
                if (aggregateException.InnerExceptions.Count == 1)
                    return aggregateException.InnerExceptions[0];
            }
            return e;
        }

        /// <summary>
        /// Delete is not supported in Event Store
        /// </summary>
        /// <param name="persistenceId"></param>
        /// <param name="toSequenceNr"></param>
        /// <returns></returns>
        protected override Task DeleteMessagesToAsync(string persistenceId, long toSequenceNr)
        {
            return Task.FromResult<object>(null);
        }

        class ActorRefConverter : JsonConverter
        {
            private readonly IActorContext _context;

            public ActorRefConverter(IActorContext context)
            {
                _context = context;
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                var formattedValue = ((IActorRef) value).Path.ToStringWithAddress();

                writer.WriteValue(formattedValue);
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                if (reader.Value == null)
                    return null;

                var value = reader.Value.ToString();

                ActorSelection selection = _context.ActorSelection(value);
                return selection.Anchor;
            }

            public override bool CanConvert(Type objectType)
            {
                return typeof (IActorRef).IsAssignableFrom(objectType);
            }
        }
    }
}