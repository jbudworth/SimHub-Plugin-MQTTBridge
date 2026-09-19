using System;
using System.ComponentModel;
using Newtonsoft.Json;

namespace SimHub.Plugin.MQTTBridge.Models
{
    /// <summary>
    /// One "publish" row: reads a SimHub property and forwards its value to an MQTT topic.
    /// </summary>
    public class PublishMapping : INotifyPropertyChanged
    {
        private bool _enabled = true;
        private string _sourceProperty = "";
        private string _topic = "";
        private int _qos = 0;
        private bool _retain = false;
        private int _minIntervalMs = 100;
        private bool _onlyOnChange = true;

        /// <summary>If false, this row is skipped entirely.</summary>
        public bool Enabled
        {
            get => _enabled;
            set { _enabled = value; OnPropertyChanged(nameof(Enabled)); }
        }

        /// <summary>
        /// Full SimHub property name, e.g. "DataCorePlugin.GameData.SpeedKmh".
        /// Find exact names via SimHub's dash editor property picker or the
        /// "Additional properties" debug view (F12 in the SimHub UI shows
        /// a live property browser in most recent builds).
        /// </summary>
        public string SourceProperty
        {
            get => _sourceProperty;
            set { _sourceProperty = value; OnPropertyChanged(nameof(SourceProperty)); }
        }

        /// <summary>MQTT topic to publish to, e.g. "simhub/speed".</summary>
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

        public bool Retain
        {
            get => _retain;
            set { _retain = value; OnPropertyChanged(nameof(Retain)); }
        }

        /// <summary>Minimum time between publishes for this mapping, to avoid flooding the broker.</summary>
        public int MinIntervalMs
        {
            get => _minIntervalMs;
            set { _minIntervalMs = value; OnPropertyChanged(nameof(MinIntervalMs)); }
        }

        /// <summary>If true, only publish when the value actually changed since the last publish.</summary>
        public bool OnlyOnChange
        {
            get => _onlyOnChange;
            set { _onlyOnChange = value; OnPropertyChanged(nameof(OnlyOnChange)); }
        }

        // Runtime-only bookkeeping, not persisted to settings.json.
        [JsonIgnore]
        public object LastPublishedValue { get; set; }

        [JsonIgnore]
        public DateTime LastPublishedAt { get; set; } = DateTime.MinValue;

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
