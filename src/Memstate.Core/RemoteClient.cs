using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Memstate.Tcp;
using Microsoft.Extensions.Logging;

namespace Memstate
{
    public class RemoteClient<TModel> : Client<TModel> where TModel : class
    {
        private readonly MemstateSettings _config;

        private readonly ILogger _logger;

        /// <summary>
        /// The tcp connection to the server
        /// </summary>
        private TcpClient _tcpClient;

        /// <summary>
        /// Serializer used to serialize and deserialize messages
        /// </summary>
        private readonly ISerializer _serializer;

        /// <summary>
        /// Stream that we read and write messages to/from
        /// </summary>
        private NetworkStream _stream;

        /// <summary>
        /// Requests that have been sent awaiting responses
        /// </summary>
        private readonly Dictionary<Guid, TaskCompletionSource<Message>> _pendingRequests;

        /// <summary>
        /// Queue of messages to be sent to the server
        /// </summary>
        private MessageProcessor<Message> _messageDispatcher;

        /// <summary>
        /// Task that processes incoming messages from the server
        /// </summary>
        private Task _messageHandler;

        private readonly Counter _counter = new Counter();

        private readonly CancellationTokenSource _cancellationSource;

        private readonly Dictionary<Type, Action<Event>> _eventHandlers;

        public RemoteClient(MemstateSettings config)
        {
            _config = config;
            _serializer = config.CreateSerializer();
            _pendingRequests = new Dictionary<Guid, TaskCompletionSource<Message>>();
            _logger = _config.LoggerFactory.CreateLogger<RemoteClient<TModel>>();
            _cancellationSource = new CancellationTokenSource();

            _eventHandlers = new Dictionary<Type, Action<Event>>();
        }


        public async Task ConnectAsync(string host = "localhost", int port = 3001)
        {
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(host, port);
            _stream = _tcpClient.GetStream();
            _logger.LogInformation($"Connected to {host}:{port}");
            _messageDispatcher = new MessageProcessor<Message>(WriteMessage);
            _messageHandler = Task.Run(ReceiveMessages);
        }

        private void Handle(Message message)
        {
            switch (message)
            {
                case CommandResponse respose:
                    Handle(respose);
                    break;

                case QueryResponse response:
                    Handle(response);
                    break;

                case EventsRaised response:
                    Handle(response);
                    break;

                case SubscribeResponse response:
                    Handle(response);
                    break;

                case UnsubscribeResponse response:
                    Handle(response);
                    break;

                default:
                    _logger.LogError("No handler for message " + message);
                    break;
            }
        }

        private void Handle(Response response)
        {
            var requestId = response.ResponseTo;

            CompleteRequest(requestId, response);
        }

        private void Handle(EventsRaised eventsRaised)
        {
            foreach(var @event in eventsRaised.Events)
            {
                if (_eventHandlers.TryGetValue(@event.GetType(), out var handler))
                {
                    handler.Invoke(@event);
                }
                else
                {
                    _logger.LogError("No handler for event type {0}", @event);
                }
            }
        }

        private void CompleteRequest(Guid requestId, Response response)
        {
            if (_pendingRequests.TryGetValue(requestId, out var completionSource))
            {
                completionSource.SetResult(response);

                _pendingRequests.Remove(requestId);
            }
            else
            {
                _logger.LogError($"No completion source for {response}, id {response.ResponseTo}");
            }
        }

        private async Task ReceiveMessages()
        {
            _logger.LogTrace("Starting ReceiveMessages task");

            var serializer = _config.CreateSerializer();

            var cancellationToken = _cancellationSource.Token;

            while (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogTrace("awaiting NetworkMessage");

                var message = await Message.ReadAsync(_stream, serializer, cancellationToken);

                _logger.LogDebug("message received " + message);

                if (message == null)
                {
                    break;
                }

                Handle(message);
            }
        }

        /// <summary>
        /// This method is called by the message processor never call it directly!
        /// </summary>
        /// <param name="message"></param>
        /// <returns></returns>
        private async Task WriteMessage(Message message)
        {
            _logger.LogDebug("WriteMessage: invoked with " + message);

            var bytes = _serializer.Serialize(message);

            _logger.LogDebug("WriteMessage: serialized message size: " + bytes.Length);

            var messageId = _counter.Next();
            var packet = Packet.Create(bytes, messageId);

            await packet.WriteToAsync(_stream);

            _logger.LogTrace("Packet written");

            await _stream.FlushAsync();
        }

        private async Task<Message> SendAndReceive(Request request)
        {
            var completionSource = new TaskCompletionSource<Message>();

            _pendingRequests[request.Id] = completionSource;

            _logger.LogTrace("SendAndReceive: queueing request id {0}, type {1}", request.Id, request.GetType());

            _messageDispatcher.Enqueue(request);

            return await completionSource.Task;
        }

        internal override object Execute(Query query)
        {
            // NOTE: Not sure if we need this method.
            throw new NotImplementedException();
        }

        public override void Execute(Command<TModel> command)
        {
            ExecuteAsync(command).Wait();
        }

        public override TResult Execute<TResult>(Command<TModel, TResult> command)
        {
            return ExecuteAsync(command).Result;
        }

        public override TResult Execute<TResult>(Query<TModel, TResult> query)
        {
            return ExecuteAsync(query).Result;
        }


        public override async Task ExecuteAsync(Command<TModel> command)
        {
            var request = new CommandRequest(command);

            await SendAndReceive(request);
        }

        public override async Task<TResult> ExecuteAsync<TResult>(Command<TModel, TResult> command)
        {
            var request = new CommandRequest(command);

            var response = (CommandResponse) await SendAndReceive(request);

            return (TResult) response.Result;
        }

        public override async Task<TResult> ExecuteAsync<TResult>(Query<TModel, TResult> query)
        {
            var request = new QueryRequest(query);

            var response = (QueryResponse) await SendAndReceive(request);

            return (TResult) response.Result;
        }

        public override async Task UnsubscribeAsync<T>()
        {
            await SendAndReceive(new UnsubscribeRequest(typeof(T)));
        }

        public override async Task SubscribeAsync<T>(Action<T> handler, IEventFilter filter = null)
        {
            
            await SendAndReceive(new SubscribeRequest(typeof(T), filter));
        }

        public void Dispose()
        {
            _messageDispatcher.Dispose();
            _tcpClient.Dispose();
        }
    }
}