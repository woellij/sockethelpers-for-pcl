using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SocketHelpers.Extensions;
using Sockets.Plugin;
using Splat;

namespace SocketHelpers.Messaging
{
    public class JsonProtocolMessenger<TMessage> : IEnableLogger where TMessage : class
    {
        readonly TcpSocketClient _client;

        private readonly Subject<JsonProtocolQueueItem<TMessage>> _sendSubject = new Subject<JsonProtocolQueueItem<TMessage>>();
        readonly Subject<TMessage> _messageSubject = new Subject<TMessage>();
        public IObservable<TMessage> Messages { get { return _messageSubject.AsObservable(); } }

        private CancellationTokenSource _executeCancellationSource;

        private List<Assembly> _additionalTypeResolutionAssemblies = new List<Assembly>();
        public List<Assembly> AdditionalTypeResolutionAssemblies { get { return _additionalTypeResolutionAssemblies; } set { _additionalTypeResolutionAssemblies = value; } } 

        public JsonProtocolMessenger(TcpSocketClient client)
        {
            _client = client;
        }

        public void Send(TMessage message)
        {
            var wrapper = new JsonProtocolQueueItem<TMessage>
            {
                MessageType = JsonProtocolMessengerMessageType.StandardMessage,
                Payload = message
            };

            _sendSubject.OnNext(wrapper);
        }

        public void StartExecuting()
        {
            if (_executeCancellationSource != null && !_executeCancellationSource.IsCancellationRequested)
                _executeCancellationSource.Cancel();

            _executeCancellationSource = new CancellationTokenSource();

            // json object protocol
            // first byte = messageType { std, disconnect, ... }
            // next 4 typeNameLength - n
            // next 4 = messageLength - m
            // next n+m = type+message

            _sendSubject
                .Subscribe(async queueItem =>
                {
                    var canceller = _executeCancellationSource.Token;

                    if (queueItem.MessageType != JsonProtocolMessengerMessageType.StandardMessage)
                        throw new InvalidOperationException();

                    this.Log().Debug(String.Format("SEND: {0}", queueItem.Payload.AsJson()));

                    var payload = queueItem.Payload;

                    var typeNameBytes = payload.GetType().FullName.AsUTF8ByteArray();
                    var messageBytes = payload.AsJson().AsUTF8ByteArray();

                    var typeNameSize = typeNameBytes.Length.AsByteArray();
                    var messageSize = messageBytes.Length.AsByteArray();

                    var allBytes = new[] 
                    { 
                        new [] { (byte) JsonProtocolMessengerMessageType.StandardMessage }, 
                        typeNameSize,
                        messageSize,
                        typeNameBytes,
                        messageBytes
                    }
                        .SelectMany(b => b)
                        .ToArray();

                    await _client.WriteStream.WriteAsync(allBytes, 0, allBytes.Length, canceller);
                    await _client.WriteStream.FlushAsync(canceller);

                }, err => this.Log().Debug(err.Message));

            Observable.Defer(() =>
                Observable.Start(async () =>
                {
                    var canceller = _executeCancellationSource.Token;

                    while (!canceller.IsCancellationRequested)
                    {
                        byte[] messageTypeBuf = new byte[1];
                        var count = await _client.ReadStream.ReadAsync(messageTypeBuf, 0, 1, canceller);

                        if (count == 0)
                        {
                            // TODO: this seems to indicate other side disconnected unexpectedly
                            this.Log().Debug("nothing to read");
                            continue;
                        }

                        var messageType = (JsonProtocolMessengerMessageType)messageTypeBuf[0];

                        switch (messageType)
                        {
                            case JsonProtocolMessengerMessageType.StandardMessage:

                                var typeNameLength = (await _client.ReadStream.ReadBytesAsync(sizeof(int), canceller)).AsInt32();
                                var messageLength = (await _client.ReadStream.ReadBytesAsync(sizeof(int), canceller)).AsInt32();

                                var typeNameBytes = await _client.ReadStream.ReadBytesAsync(typeNameLength, canceller);
                                var messageBytes = await _client.ReadStream.ReadBytesAsync(messageLength, canceller);

                                var typeName = typeNameBytes.AsUTF8String();
                                var messageJson = messageBytes.AsUTF8String();

                                var type = Type.GetType(typeName) ?? 
                                    AdditionalTypeResolutionAssemblies
                                        .Select(a => Type.GetType(String.Format("{0}, {1}", typeName, a.FullName)))
                                        .FirstOrDefault(t => t != null);
                                
                                if (type == null)
                                {
                                    this.Log().Debug(String.Format("Received a message of type '{0}' but couldn't resolve it using GetType() directly or when qualified by any of the specified AdditionalTypeResolutionAssemblies: [{1}]", typeName, String.Join(",", AdditionalTypeResolutionAssemblies.Select(a=> a.FullName))));
                                    continue;
                                }

                                var msg = JsonConvert.DeserializeObject(messageJson, type) as TMessage;

                                this.Log().Debug(String.Format("RECV: {0}", msg.AsJson()));

                                _messageSubject.OnNext(msg);

                                break;

                            case JsonProtocolMessengerMessageType.DisconnectMessage:
                                //TODO: 
                                break;
                        }

                    }

                }).Catch(Observable.Empty<Task>())
                ).Retry()
                .Subscribe(_ => this.Log().Debug("MessageReader OnNext"),
                    ex => this.Log().Debug("MessageReader OnError - " + ex.Message),
                    () => this.Log().Debug("MessageReader OnCompleted"));
        }

        public void StopExecuting()
        {
            _executeCancellationSource.Cancel();

        }
    }
}