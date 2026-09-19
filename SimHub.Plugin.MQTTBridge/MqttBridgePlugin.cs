using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using GameReaderCommon;
using SimHub.Plugins;
using SimHub.Plugin.MQTTBridge.Models;
using SimHub.Plugin.MQTTBridge.UI;

namespace SimHub.Plugin.MQTTBridge
{
    [PluginDescription("Publishes SimHub properties to MQTT and exposes subscribed MQTT topics as SimHub properties.")]
    [PluginAuthor("Claude.ai")]
    [PluginName("MQTT Bridge")]
    public class MqttBridgePlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        private const string SettingsKey = "MqttBridgeSettings";

        /// <summary>SimHub's shared log4net logger (SimHub.Logging.dll), so plugin messages land in SimHub's own log.</summary>
        private static readonly log4net.ILog Log = SimHub.Logging.Current;

        // Guards all reads/writes of Settings.PublishMappings and Settings.SubscribeMappings.
        // DataUpdate and the MQTT message-received callback read these on SimHub's data thread
        // and MQTTnet's callback thread respectively, while the settings UI adds/removes rows
        // on the WPF UI thread; ObservableCollection<T> is not safe under that kind of concurrent
        // read/write without this.
        private readonly object _mappingsLock = new object();

        // Full property names already registered with PluginManager, tracked ourselves instead
        // of relying on AddProperty's "throws if already registered" behavior to distinguish
        // that case from a genuine registration failure.
        private readonly HashSet<string> _registeredPropertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public MqttBridgeSettings Settings { get; private set; }
        public MqttService MqttService { get; private set; }
        public string ConnectionStatus { get; private set; } = "Not connected";

        public event Action<string> StatusChanged;

        public PluginManager PluginManager { get; set; }

        public string LeftMenuTitle => "MQTT Bridge";

        public ImageSource PictureIcon => MqttRippleIcon.Get();

        public void Init(PluginManager pluginManager)
        {
            Log.Info("MQTT Bridge: Init starting.");
            PluginManager = pluginManager;

            Settings = this.ReadCommonSettings<MqttBridgeSettings>(SettingsKey, () => new MqttBridgeSettings());
            Settings.UnprotectPassword();

            // Pre-register every configured "subscribe" row as a SimHub property,
            // so it exists (with a default value) even before the broker connects
            // or before the first message for that topic has arrived.
            List<SubscribeMapping> initialSubs;
            lock (_mappingsLock)
            {
                initialSubs = Settings.SubscribeMappings.ToList();
            }
            foreach (var sub in initialSubs)
            {
                RegisterSubscribeProperty(sub);
            }

            MqttService = new MqttService();
            MqttService.MessageReceived += OnMqttMessageReceived;
            MqttService.StatusChanged += OnMqttStatusChanged;

            Log.Info($"MQTT Bridge: Init complete. {Settings.PublishMappings.Count} publish mapping(s), {Settings.SubscribeMappings.Count} subscribe mapping(s) loaded.");

            if (Settings.AutoConnect)
            {
                ConnectAsync();
            }
        }

        public async void ConnectAsync()
        {
            try
            {
                Log.Info($"MQTT Bridge: connecting to {Settings.Host}:{Settings.Port} as client '{Settings.ClientId}'.");
                await MqttService.DisconnectAsync();
                await MqttService.ConnectAsync(Settings);

                List<SubscribeMapping> subsToSubscribe;
                lock (_mappingsLock)
                {
                    subsToSubscribe = Settings.SubscribeMappings.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Topic)).ToList();
                }

                foreach (var sub in subsToSubscribe)
                {
                    await MqttService.SubscribeAsync(sub.Topic, sub.Qos);
                }
            }
            catch (Exception ex)
            {
                Log.Error("MQTT Bridge: connect error.", ex);
                OnMqttStatusChanged($"Connect error: {ex.Message}");
            }
        }

        /// <summary>Lets the settings UI persist immediately on "Save", instead of waiting for plugin shutdown.</summary>
        public void PersistSettingsNow()
        {
            Settings.ProtectPassword();
            this.SaveCommonSettings(SettingsKey, Settings);
        }

        /// <summary>Adds a new publish-mapping row. Goes through this method (rather than the UI
        /// touching Settings.PublishMappings directly) so the mutation is covered by the same lock
        /// that guards DataUpdate's read of this collection.</summary>
        public void AddPublishMapping(PublishMapping mapping)
        {
            lock (_mappingsLock)
            {
                Settings.PublishMappings.Add(mapping);
            }
        }

        public void RemovePublishMapping(PublishMapping mapping)
        {
            lock (_mappingsLock)
            {
                Settings.PublishMappings.Remove(mapping);
            }
        }

        /// <summary>Adds a new subscribe-mapping row. Goes through this method (rather than the UI
        /// touching Settings.SubscribeMappings directly) so the mutation is covered by the same lock
        /// that guards OnMqttMessageReceived's read of this collection.</summary>
        public void AddSubscribeMapping(SubscribeMapping mapping)
        {
            lock (_mappingsLock)
            {
                Settings.SubscribeMappings.Add(mapping);
            }
        }

        public void RemoveSubscribeMapping(SubscribeMapping mapping)
        {
            lock (_mappingsLock)
            {
                Settings.SubscribeMappings.Remove(mapping);
            }
        }

        /// <summary>
        /// Swaps in a whole new settings object, e.g. after importing a config exported from
        /// another install. Persists immediately and re-registers subscribe properties so the
        /// imported mappings work without requiring a SimHub restart. The caller is still
        /// responsible for refreshing its own UI fields and for calling ConnectAsync/ResubscribeAll
        /// if the broker should be reached with the new settings right away.
        /// </summary>
        public void ReplaceSettings(MqttBridgeSettings newSettings)
        {
            Log.Info("MQTT Bridge: settings replaced (import).");
            Settings = newSettings ?? new MqttBridgeSettings();
            Settings.UnprotectPassword();
            PersistSettingsNow();

            List<SubscribeMapping> subs;
            lock (_mappingsLock)
            {
                subs = Settings.SubscribeMappings.ToList();
            }
            foreach (var sub in subs)
            {
                RegisterSubscribeProperty(sub);
            }
        }

        public async void DisconnectAsync()
        {
            if (MqttService != null)
            {
                Log.Info("MQTT Bridge: disconnecting.");
                await MqttService.DisconnectAsync();
            }
        }

        /// <summary>
        /// Called by the settings UI after edits to the subscribe list, so newly
        /// added/changed topics take effect without a full plugin restart.
        /// </summary>
        public async void ResubscribeAll()
        {
            List<SubscribeMapping> subs;
            lock (_mappingsLock)
            {
                subs = Settings.SubscribeMappings.ToList();
            }

            foreach (var sub in subs)
            {
                RegisterSubscribeProperty(sub);
                if (MqttService != null && MqttService.IsConnected && sub.Enabled && !string.IsNullOrWhiteSpace(sub.Topic))
                {
                    await MqttService.SubscribeAsync(sub.Topic, sub.Qos);
                }
            }
        }

        private void OnMqttStatusChanged(string status)
        {
            ConnectionStatus = status;
            if (status != null && (status.StartsWith("Connect failed") || status.StartsWith("Connect error") ||
                                    status.Contains("failed") || status.Contains("error")))
            {
                Log.Warn($"MQTT Bridge: {status}");
            }
            else
            {
                Log.Info($"MQTT Bridge: {status}");
            }

            StatusChanged?.Invoke(status);
        }

        private void RegisterSubscribeProperty(SubscribeMapping sub)
        {
            if (string.IsNullOrWhiteSpace(sub.PropertyName)) return;

            var fullName = GetFullPropertyName(sub.PropertyName);

            lock (_registeredPropertyNames)
            {
                if (_registeredPropertyNames.Contains(fullName))
                {
                    sub.IsRegistered = true;
                    return;
                }

                Type propertyType = GetClrType(sub.DataType);

                try
                {
                    PluginManager.AddProperty(fullName, this.GetType(), propertyType);
                    _registeredPropertyNames.Add(fullName);
                    sub.IsRegistered = true;
                    Log.Info($"MQTT Bridge: registered property '{fullName}' ({propertyType.Name}) for topic '{sub.Topic}'.");
                }
                catch (Exception ex)
                {
                    // A genuine registration failure (e.g. an invalid property name). Leave
                    // IsRegistered false so it's retried on the next message/reload instead of
                    // being silently and permanently treated as done.
                    Log.Warn($"MQTT Bridge: failed to register property '{fullName}'.", ex);
                }
            }
        }

        private string GetFullPropertyName(string name)
        {
            var prefix = string.IsNullOrWhiteSpace(Settings.PropertyPrefix) ? "MqttBridge" : Settings.PropertyPrefix;
            return $"{prefix}.{name}";
        }

        private static Type GetClrType(SubscribeDataType type)
        {
            switch (type)
            {
                case SubscribeDataType.Double: return typeof(double);
                case SubscribeDataType.Integer: return typeof(int);
                case SubscribeDataType.Boolean: return typeof(bool);
                default: return typeof(string);
            }
        }

        private void OnMqttMessageReceived(object sender, MqttMessageReceivedEventArgs e)
        {
            List<SubscribeMapping> matches;
            lock (_mappingsLock)
            {
                matches = Settings.SubscribeMappings.Where(s => s.Enabled && s.Topic == e.Topic).ToList();
            }

            foreach (var sub in matches)
            {
                if (!sub.IsRegistered)
                {
                    RegisterSubscribeProperty(sub);
                }

                object value = ParsePayload(e.Payload, sub.DataType);
                if (value == null) continue;

                try
                {
                    PluginManager.SetPropertyValue(GetFullPropertyName(sub.PropertyName), this.GetType(), value);
                }
                catch (Exception ex)
                {
                    Log.Error($"MQTT Bridge: failed to set property '{sub.PropertyName}'.", ex);
                    OnMqttStatusChanged($"Failed to set property '{sub.PropertyName}': {ex.Message}");
                }
            }
        }

        private static object ParsePayload(string payload, SubscribeDataType type)
        {
            switch (type)
            {
                case SubscribeDataType.Double:
                    return double.TryParse(payload, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : (object)null;
                case SubscribeDataType.Integer:
                    return int.TryParse(payload, NumberStyles.Any, CultureInfo.InvariantCulture, out var i) ? i : (object)null;
                case SubscribeDataType.Boolean:
                    if (bool.TryParse(payload, out var b)) return b;
                    if (payload == "1") return true;
                    if (payload == "0") return false;
                    return null;
                default:
                    return payload ?? "";
            }
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            if (MqttService == null || !MqttService.IsConnected) return;

            var now = DateTime.UtcNow;

            PublishMapping[] mappings;
            lock (_mappingsLock)
            {
                mappings = Settings.PublishMappings.ToArray();
            }

            foreach (var mapping in mappings)
            {
                if (!mapping.Enabled) continue;
                if (string.IsNullOrWhiteSpace(mapping.SourceProperty) || string.IsNullOrWhiteSpace(mapping.Topic)) continue;
                if ((now - mapping.LastPublishedAt).TotalMilliseconds < mapping.MinIntervalMs) continue;

                object value;
                try
                {
                    value = pluginManager.GetPropertyValue(mapping.SourceProperty);
                }
                catch
                {
                    continue; // property name typo or not yet available this tick
                }

                if (value == null) continue;
                if (mapping.OnlyOnChange && Equals(value, mapping.LastPublishedValue)) continue;

                mapping.LastPublishedValue = value;
                mapping.LastPublishedAt = now;

                var payload = FormatValue(value);
                _ = MqttService.PublishAsync(mapping.Topic, payload, mapping.Qos, mapping.Retain);
            }
        }

        private static string FormatValue(object value)
        {
            switch (value)
            {
                case double d: return d.ToString(CultureInfo.InvariantCulture);
                case float f: return f.ToString(CultureInfo.InvariantCulture);
                case bool b: return b ? "true" : "false";
                case DateTime dt: return dt.ToString("o", CultureInfo.InvariantCulture);
                default: return value.ToString();
            }
        }

        public Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return new SettingsControl(this);
        }

        public void End(PluginManager pluginManager)
        {
            Log.Info("MQTT Bridge: End - saving settings and shutting down.");
            Settings.ProtectPassword();
            this.SaveCommonSettings(SettingsKey, Settings);
            DisconnectAsync();
            MqttService?.Dispose();
        }
    }
}
