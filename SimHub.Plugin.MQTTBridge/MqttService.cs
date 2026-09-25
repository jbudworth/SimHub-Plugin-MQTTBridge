using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using SimHub.Plugin.MQTTBridge.Models;

namespace SimHub.Plugin.MQTTBridge
{
    public class MqttMessageReceivedEventArgs : EventArgs
    {
        public string Topic { get; }
        public string Payload { get; }

        public MqttMessageReceivedEventArgs(string topic, string payload)
        {
            Topic = topic;
            Payload = payload;
        }
    }

    /// <summary>
    /// Thin wrapper around an MQTTnet client: connect, auto-reconnect, publish, subscribe.
    /// Kept independent of the SimHub SDK so it can be unit-tested / reused on its own.
    /// </summary>
    public class MqttService : IDisposable
    {
        /// <summary>SimHub's shared log4net logger (SimHub.Logging.dll).</summary>
        private static readonly log4net.ILog Log = SimHub.Logging.Current;

        private IMqttClient _client;
        private MqttBridgeSettings _settings;
        private Timer _reconnectTimer;
        private bool _disposed;
        private bool _wantConnected;

        // Serializes client creation/disposal and connect attempts, so a pending
        // auto-reconnect timer callback can never race a manual Connect click into
        // a concurrent ConnectAsync on the same client, or use a client that is
        // being disposed and swapped out.
        private readonly SemaphoreSlim _connectLock = new SemaphoreSlim(1, 1);

        public event EventHandler<MqttMessageReceivedEventArgs> MessageReceived;
        public event Action<string> StatusChanged;

        /// <summary>Raised every time a broker connection is established, including
        /// automatic reconnects. Subscriptions must be re-applied on each firing:
        /// the client connects with a clean session, so the broker forgets them
        /// whenever the connection drops.</summary>
        public event Action Connected;

        public bool IsConnected => _client?.IsConnected == true;

        public async Task ConnectAsync(MqttBridgeSettings settings)
        {
            _settings = settings;
            _wantConnected = true;

            await _connectLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_client != null)
                {
                    // Reconnecting (e.g. after editing broker settings): drop the old client's
                    // handlers and dispose it instead of leaking it when _client is reassigned below.
                    _client.ApplicationMessageReceivedAsync -= OnApplicationMessageReceivedAsync;
                    _client.DisconnectedAsync -= OnDisconnectedAsync;
                    _client.ConnectedAsync -= OnConnectedAsync;
                    _client.Dispose();
                }

                var factory = new MqttFactory();
                _client = factory.CreateMqttClient();

                _client.ApplicationMessageReceivedAsync += OnApplicationMessageReceivedAsync;
                _client.DisconnectedAsync += OnDisconnectedAsync;
                _client.ConnectedAsync += OnConnectedAsync;
            }
            finally
            {
                _connectLock.Release();
            }

            await DoConnectAsync().ConfigureAwait(false);
        }

        /// <summary>True if <paramref name="topic"/> matches <paramref name="filter"/> under MQTT
        /// matching rules, including # and + wildcards (a filter without wildcards is an exact match).</summary>
        public static bool TopicMatchesFilter(string topic, string filter)
        {
            if (string.IsNullOrEmpty(topic) || string.IsNullOrEmpty(filter)) return false;
            return MqttTopicFilterComparer.Compare(topic, filter) == MqttTopicFilterCompareResult.IsMatch;
        }

        private MqttClientOptions BuildOptions()
        {
            var builder = new MqttClientOptionsBuilder()
                .WithClientId(_settings.ClientId)
                .WithTcpServer(_settings.Host, _settings.Port)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(Math.Max(5, _settings.KeepAliveSeconds)))
                .WithCleanSession();

            if (!string.IsNullOrEmpty(_settings.Username))
            {
                builder = builder.WithCredentials(_settings.Username, _settings.Password);
            }

            if (_settings.UseTls)
            {
                builder = builder.WithTlsOptions(o => o.UseTls());
            }

            return builder.Build();
        }

        private async Task DoConnectAsync()
        {
            await _connectLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // A concurrent attempt (manual Connect vs. reconnect timer) may have
                // already finished, or the service may have been torn down meanwhile.
                if (_disposed || !_wantConnected || IsConnected) return;

                StatusChanged?.Invoke("Connecting...");
                var options = BuildOptions();
                await _client.ConnectAsync(options, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("MQTT Bridge: connect attempt failed.", ex);
                StatusChanged?.Invoke($"Connect failed: {ex.Message}");
                ScheduleReconnect();
            }
            finally
            {
                _connectLock.Release();
            }
        }

        private Task OnConnectedAsync(MqttClientConnectedEventArgs args)
        {
            Log.Info("MQTT Bridge: broker connection established.");
            StatusChanged?.Invoke("Connected");
            Connected?.Invoke();
            return Task.CompletedTask;
        }

        private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
        {
            if (args.Exception != null)
            {
                Log.Warn($"MQTT Bridge: broker connection lost.", args.Exception);
            }
            else
            {
                Log.Info("MQTT Bridge: broker connection closed.");
            }

            StatusChanged?.Invoke("Disconnected" + (args.Exception != null ? $": {args.Exception.Message}" : ""));
            if (_wantConnected)
            {
                ScheduleReconnect();
            }
            return Task.CompletedTask;
        }

        private void ScheduleReconnect()
        {
            // An in-flight disconnect callback can land here after Dispose/DisconnectAsync;
            // don't recreate a timer that nothing would ever clean up again.
            if (_disposed || !_wantConnected) return;

            _reconnectTimer?.Dispose();
            _reconnectTimer = new Timer(async _ =>
            {
                if (_wantConnected && !IsConnected)
                {
                    await DoConnectAsync().ConfigureAwait(false);
                }
            }, null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
        }

        private Task OnApplicationMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
        {
            try
            {
                var topic = args.ApplicationMessage.Topic;
                var payloadBytes = args.ApplicationMessage.PayloadSegment;
                var payload = payloadBytes.Count > 0
                    ? Encoding.UTF8.GetString(payloadBytes.Array, payloadBytes.Offset, payloadBytes.Count)
                    : string.Empty;

                MessageReceived?.Invoke(this, new MqttMessageReceivedEventArgs(topic, payload));
            }
            catch (Exception ex)
            {
                Log.Error("MQTT Bridge: error handling an incoming message.", ex);
                StatusChanged?.Invoke($"Message handling error: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        public async Task SubscribeAsync(string topic, int qos)
        {
            if (_client == null || string.IsNullOrWhiteSpace(topic)) return;

            try
            {
                var filter = new MqttTopicFilterBuilder()
                    .WithTopic(topic)
                    .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)qos)
                    .Build();

                await _client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter(filter).Build()).ConfigureAwait(false);
                Log.Info($"MQTT Bridge: subscribed to '{topic}' (QoS {qos}).");
            }
            catch (Exception ex)
            {
                Log.Warn($"MQTT Bridge: subscribe failed for '{topic}'.", ex);
                StatusChanged?.Invoke($"Subscribe failed for '{topic}': {ex.Message}");
            }
        }

        public async Task UnsubscribeAsync(string topic)
        {
            if (_client == null || string.IsNullOrWhiteSpace(topic)) return;

            try
            {
                await _client.UnsubscribeAsync(new MqttClientUnsubscribeOptionsBuilder().WithTopicFilter(topic).Build()).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort; ignore unsubscribe errors (e.g. already disconnected).
            }
        }

        public async Task PublishAsync(string topic, string payload, int qos, bool retain)
        {
            if (_client == null || !_client.IsConnected || string.IsNullOrWhiteSpace(topic)) return;

            try
            {
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)qos)
                    .WithRetainFlag(retain)
                    .Build();

                await _client.PublishAsync(message, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"MQTT Bridge: publish failed for '{topic}'.", ex);
                StatusChanged?.Invoke($"Publish failed for '{topic}': {ex.Message}");
            }
        }

        public async Task DisconnectAsync()
        {
            _wantConnected = false;
            _reconnectTimer?.Dispose();
            _reconnectTimer = null;

            if (_client != null && _client.IsConnected)
            {
                try
                {
                    // ConfigureAwait(false) also lets End() block on this from the UI
                    // thread at shutdown without deadlocking on the WPF context.
                    await _client.DisconnectAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Ignore errors on shutdown.
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _reconnectTimer?.Dispose();
            _client?.Dispose();
        }
    }
}
