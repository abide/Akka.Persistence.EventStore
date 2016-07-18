﻿using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Akka.Event;
using Akka.Persistence.Snapshot;
using Akka.Serialization;
using EventStore.ClientAPI;
using Newtonsoft.Json;

namespace Akka.Persistence.EventStore
{
    public class EventStoreSnapshotStore : SnapshotStore
    {
        private readonly IEventStoreConnection _connection;

        private readonly Serializer _serializer;
        private readonly ILoggingAdapter _log;

        public EventStoreSnapshotStore()
        {
            _log = Context.GetLogger();

            var serialization = Context.System.Serialization;
            _serializer = serialization.FindSerializerForType(typeof(SelectedSnapshot));

            var extension = EventStorePersistence.Instance.Apply(Context.System);
            _connection = extension.SnapshotStoreSettings.Connection;
        }

        protected override async Task<SelectedSnapshot> LoadAsync(string persistenceId, SnapshotSelectionCriteria criteria)
        {            
            var streamName = GetStreamName(persistenceId);
            var requestedSnapVersion = (int)criteria.MaxSequenceNr;
            StreamEventsSlice slice = null;
            if (criteria.MaxSequenceNr == long.MaxValue)
            {
                requestedSnapVersion = StreamPosition.End;
                slice = await _connection.ReadStreamEventsBackwardAsync(streamName, requestedSnapVersion, 1, false);
            }
            else
            {
                slice = await _connection.ReadStreamEventsBackwardAsync(streamName, StreamPosition.End, requestedSnapVersion, false);
            }

            if (slice.Status == SliceReadStatus.StreamNotFound)
            {
                await _connection.SetStreamMetadataAsync(streamName, ExpectedVersion.Any, StreamMetadata.Data);
                return null;
            }

            if (slice.Events.Any())
            {
                _log.Debug("Found snapshot of {0}", persistenceId);
                if (requestedSnapVersion == StreamPosition.End)
                {
                    var @event = slice.Events.First().OriginalEvent;
                    return (SelectedSnapshot)_serializer.FromBinary(@event.Data, typeof(SelectedSnapshot));
                }
                else
                {
                    var @event = slice.Events.Where(t => t.OriginalEvent.EventNumber == requestedSnapVersion).First().OriginalEvent;
                    return (SelectedSnapshot)_serializer.FromBinary(@event.Data, typeof(SelectedSnapshot));
                }
            }

            return null;
        }

        private static string GetStreamName(string persistenceId)
        {
            return $"snapshot-{persistenceId}";
        }

        protected override Task SaveAsync(SnapshotMetadata metadata, object snapshot)
        {
            var streamName = GetStreamName(metadata.PersistenceId);
            var data = _serializer.ToBinary(new SelectedSnapshot(metadata, snapshot));
            var eventData = new EventData(Guid.NewGuid(), typeof(Serialization.Snapshot).Name, false, data, new byte[0]);

            return _connection.AppendToStreamAsync(streamName, ExpectedVersion.Any, eventData);
        }

        protected override Task DeleteAsync(SnapshotMetadata metadata)
        {
            return Task.FromResult<object>(null);
        }

        protected override Task DeleteAsync(string persistenceId, SnapshotSelectionCriteria criteria)
        {
            return Task.FromResult<object>(null);
        }

        public class StreamMetadata
        {
            [JsonProperty("$maxCount")]
            public int MaxCount = 1;

            private StreamMetadata()
            {
            }

            private static readonly StreamMetadata Instance = new StreamMetadata();

            public static byte[] Data => Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(Instance));
        }
    }
}