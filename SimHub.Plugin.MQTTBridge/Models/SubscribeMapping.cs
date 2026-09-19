using System;
using System.ComponentModel;
using Newtonsoft.Json;

namespace SimHub.Plugin.MQTTBridge.Models
{
    public enum SubscribeDataType
    {
        String,
        Double,
        Integer,
        Boolean
    }

    /// <summary>
    /// One "subscribe" row: subscribes to an MQTT topic and exposes the latest
    /// payload as a SimHub property, usable in dashboards / NCalc / other plugins.
    /// </summary>
    public class SubscribeMapping : INotifyPropertyChanged
    {
        private bool _enabled = true;
        private string _topic = "";
        private int _qos = 0;
        private string _propertyName = "";
        private SubscribeDataType _dataType = SubscribeDataType.String;

        public bool Enabled
        {
            get => _enabled;
            set { _enabled = value; OnPropertyChanged(nameof(Enabled)); }
        }

        /// <summary>MQTT topic to subscribe to. Wildcards (# and +) are not expanded into
        /// separate properties; a wildcard subscription will only populate the property
        /// with the payload of whichever matching message arrived most recently.</summary>
        public string Topic
        {
            get => _topic;
            set { _topic = value; OnPropertyChanged(nameof(Topic)); }
        }

        /// <summary>0 = At most once, 1 = At least once, 2 = Exactly once. Clamped to that range
        /// so an out-of-range value typed in the grid can never reach MQTTnet's QoS enum cast.</summary>
        public int Qos
        {
            get => _qos;
            set { _qos = Math.Max(0, Math.Min(2, value)); OnPropertyChanged(nameof(Qos)); }
        }

        /// <summary>
        /// Name to expose this under, relative to the plugin's property prefix.
        /// e.g. "PitLimiterOverride" becomes "MqttBridge.PitLimiterOverride".
        /// </summary>
        public string PropertyName
        {
            get => _propertyName;
            set { _propertyName = value; OnPropertyChanged(nameof(PropertyName)); }
        }

        public SubscribeDataType DataType
        {
            get => _dataType;
            set { _dataType = value; OnPropertyChanged(nameof(DataType)); }
        }

        [JsonIgnore]
        public bool IsRegistered { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
