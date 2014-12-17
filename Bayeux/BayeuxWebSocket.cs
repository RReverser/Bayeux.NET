﻿using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Windows.Networking.Sockets;

namespace Bayeux
{
    public enum Reconnect { Retry, Handshake, None }

    public class Advice
    {
        [JsonConverter(typeof(StringEnumConverter))]
        public Reconnect Reconnect { get; set; }
        public int Interval { get; set; }
    }

    public class Message
    {
        public string Id { get; set; }
        public virtual string Channel { get; set; }
        public string ClientId { get; set; }
        public dynamic Ext { get; set; }
    }

    public class ResponseError : Exception
    {
        public HttpStatusCode Code { get; private set; }
        public IReadOnlyList<string> Args { get; private set; }
        public string Description { get; private set; }

        public ResponseError(string errorString) : base(errorString)
        {
            var parts = errorString.Split(new[] { ':' }, 3);
            Code = (HttpStatusCode)int.Parse(parts[0]);
            Args = parts[1].Split(',');
            Description = parts[2];
        }
    }

    internal class ResponseErrorConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return false;
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (objectType != typeof(string)) throw new NotSupportedException();
            return new ResponseError((string)reader.Value);
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            throw new NotSupportedException();
        }
    }

    public class ResponseMessage : Message
    {
        public bool Successful { get; set; }

        [JsonConverter(typeof(ResponseErrorConverter))]
        public ResponseError Error { get; set; }

        public Advice Advice { get; set; }
    }

    public class HandshakeRequest : Message
    {
        public override string Channel
        {
            get { return "/meta/handshake"; }
        }

        public string Version
        {
            get { return "1.0"; }
        }

        public string[] SupportedConnectionTypes
        {
            get { return new[] { "websocket" }; }
        }
    }

    public class HandshakeResponse : ResponseMessage
    {
        public string Version { get; set; }
        public string[] SupportedConnectionTypes { get; set; }
    }

    public class ConnectRequest : Message
    {
        public override string Channel
        {
            get { return "/meta/connect"; }
        }

        public string ConnectionType
        {
            get { return "websocket"; }
        }
    }

    public class ConnectResponse : ResponseMessage { }

    public class DisconnectRequest : ConnectRequest
    {
        public override string Channel
        {
            get { return "/meta/disconnect"; }
        }
    }

    public class DisconnectResponse : ConnectResponse { }

    public class SubscribeRequest : Message
    {
        public override string Channel
        {
            get { return "/meta/subscribe"; }
        }

        public string Subscription { get; set; }
    }

    public class SubscribeResponse : ResponseMessage
    {
        public string Subscription { get; set; }
    }

    public class UnsubscribeRequest : SubscribeRequest
    {
        public override string Channel
        {
            get { return "/meta/unsubscribe"; }
        }
    }

    public class UnsubscribeResponse : SubscribeResponse { }

    public class DataMessage<TData> : Message
    {
        public TData Data { get; set; }
    }

    public class PublishRequest<TData> : DataMessage<TData> { }

    public class PublishResponse : ResponseMessage { }

    public class BayeuxWebSocket : StatefulWebSocket
    {
        private Advice advice = new Advice() { Interval = 1000, Reconnect = Reconnect.Retry };
        private readonly Dictionary<string, Action<JToken>> subscriptionHandlers = new Dictionary<string, Action<JToken>>();
        private readonly Dictionary<string, TaskCompletionSource<JToken>> responseHandlers = new Dictionary<string, TaskCompletionSource<JToken>>();
        public readonly JsonSerializerSettings SerializerSettings = new JsonSerializerSettings() { ContractResolver = new CamelCasePropertyNamesContractResolver() };

        public BayeuxWebSocket(string url) : base(url)
        {
            subscriptionHandlers.Add("/meta/connect", HandleConnect);
            MessageReceived += BayeuxWebSocket_MessageReceived;
        }

        private void BayeuxWebSocket_MessageReceived(StatefulWebSocket sender, string args)
        {
            var messages = JsonConvert.DeserializeObject<JObject[]>(args, SerializerSettings);
            foreach (var message in messages)
            {
                var newAdvice = message["advice"]?.ToObject<Advice>();
                if (newAdvice != null) advice = newAdvice;
                var id = message.Value<string>("id");
                TaskCompletionSource<JToken> responseHandler;
                if (responseHandlers.TryGetValue(id, out responseHandler))
                {
                    responseHandlers.Remove(id);
                    responseHandler.SetResult(message);
                    continue;
                }
                var channel = message.Value<string>("channel");
                Action<JToken> subscriptionHandler;
                if (subscriptionHandlers.TryGetValue(channel, out subscriptionHandler))
                {
                    subscriptionHandler(message);
                    continue;
                }
                throw new KeyNotFoundException("Could not find appropriate handler for message: \{message}");
            }
        }

        private int idCounter;
        private string clientId;

        private async void HandleConnect(JToken token)
        {
            if (advice.Reconnect != Reconnect.Retry) return;
            var message = token.ToObject<ConnectResponse>();
            await Task.Delay(advice.Interval);
            SendConnect();
        }

        public virtual void Send(Message message)
        {
            message.Id = (++idCounter).ToString();
            message.ClientId = clientId;
            Send(JsonConvert.SerializeObject(new[] { message }, SerializerSettings));
        }

        public async Task<TResponse> SendAsync<TResponse>(Message message)
            where TResponse : ResponseMessage
        {
            var tsc = new TaskCompletionSource<JToken>();
            Send(message);
            responseHandlers[message.Id] = tsc;
            var response = (await tsc.Task).ToObject<TResponse>();
            if (!response.Successful)
            {
                throw response.Error;
            }
            return response;
        }

        private void SendConnect()
        {
            Send(new ConnectRequest());
        }

        protected override async Task ReconnectAsync()
        {
            if (advice.Reconnect == Reconnect.None) return;
            await base.ReconnectAsync();
        }

        public override async Task ConnectAsync()
        {
            await base.ConnectAsync();
            idCounter = 0;
            clientId = null;
            clientId = (await SendAsync<HandshakeResponse>(new HandshakeRequest())).ClientId;
            SendConnect();
        }

        public override async Task CloseAsync(ushort code, string reason)
        {
            if (!IsConnected) return;
            await SendAsync<DisconnectResponse>(new DisconnectRequest());
            await base.CloseAsync(code, reason);
        }

        public async Task SubscribeAsync<T>(string path, Action<T> processMessage)
        {
            await SendAsync<SubscribeResponse>(new SubscribeRequest() { Subscription = path });
            subscriptionHandlers.Add(path, token => processMessage(token.ToObject<DataMessage<T>>().Data));
        }

        public Task UnsubscribeAsync(string path)
        {
            subscriptionHandlers.Remove(path);
            return SendAsync<UnsubscribeResponse>(new UnsubscribeRequest() { Subscription = path });
        }
    }
}