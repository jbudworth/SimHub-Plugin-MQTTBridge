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

        public event EventHandler<MqttMessageReceivedEventArgs> MessageReceived;
        public event Action<string> StatusChanged;

        public bool IsConnected => _client?.IsConnected == true;

        public async Task ConnectAsync(MqttBridgeSettings settings)
        {
            _settings = settings;
            _wantConnected = true;

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

            await DoConnectAsync();
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
            try
            {
                StatusChanged?.Invoke("Connecting...");
                var options = BuildOptions();
                await _client.ConnectAsync(options, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Warn("MQTT Bridge: connect attempt failed.", ex);
                StatusChanged?.Invoke($"Connect failed: {ex.Message}");
                ScheduleReconnect();
            }
        }

        private Task OnConnectedAsync(MqttClientConnectedEventArgs args)
        {
            Log.Info("MQTT Bridge: broker connection established.");
            StatusChanged?.Invoke("Connected");
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
            _reconnectTimer?.Dispose();
            _reconnectTimer = new Timer(async _ =>
            {
                if (_wantConnected && !IsConnected)
                {
                    await DoConnectAsync();
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

                await _client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter(filter).Build());
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
                await _client.UnsubscribeAsync(new MqttClientUnsubscribeOptionsBuilder().WithTopicFilter(topic).Build());
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

                await _client.PublishAsync(message, CancellationToken.None);
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
                    await _client.DisconnectAsync();
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
