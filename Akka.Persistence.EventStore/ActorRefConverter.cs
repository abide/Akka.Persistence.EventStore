using System;
using Akka.Actor;
using Newtonsoft.Json;

namespace Akka.Persistence.EventStore
{
    class ActorRefConverter : JsonConverter
    {
        private readonly IActorContext _context;

        public ActorRefConverter(IActorContext context)
        {
            _context = context;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var address = ((IActorRef) value).Path.ToStringWithAddress();
            writer.WriteValue(address);
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
            return typeof(IActorRef).IsAssignableFrom(objectType);
        }
    }
}
