using System;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace SimHub.Plugin.MQTTBridge.Models
{
    public class MqttBridgeSettings
    {
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 1883;
        public bool UseTls { get; set; } = false;

        // Unique per install by default. Every MQTT client connected to the same
        // broker MUST have a distinct client ID, including the SHWotever MQTT
        // Publisher plugin if you run both — this is generated once and then
        // persisted, so it stays stable across restarts.
        public string ClientId { get; set; } = "SimHub-MqttBridge-" + Guid.NewGuid().ToString("N").Substring(0, 8);

        public string Username { get; set; } = "";

        // The plaintext password, used at runtime for authenticating to the broker.
        // Deliberately excluded from serialization: PasswordProtected below is the
        // only form of the password that ever reaches disk.
        [JsonIgnore]
        public string Password { get; set; } = "";

        // DPAPI-encrypted (current Windows user) form of Password. This is what
        // actually gets persisted to the settings JSON file, so the broker password
        // isn't sitting on disk in plaintext.
        public string PasswordProtected { get; set; } = "";

        public int KeepAliveSeconds { get; set; } = 30;
        public bool AutoConnect { get; set; } = true;

        // Every property this plugin exposes is namespaced under this prefix
        // (default "MqttBridge.*"). The SHWotever MQTT Publisher plugin exposes
        // its own properties under its own name, so as long as this prefix is
        // left at its default (or changed to something else unique), there is
        // no property-name collision between the two plugins.
        public string PropertyPrefix { get; set; } = "MqttBridge";

        public ObservableCollection<PublishMapping> PublishMappings { get; set; } = new ObservableCollection<PublishMapping>();
        public ObservableCollection<SubscribeMapping> SubscribeMappings { get; set; } = new ObservableCollection<SubscribeMapping>();

        /// <summary>Encrypts Password into PasswordProtected. Call right before persisting to disk.</summary>
        public void ProtectPassword()
        {
            if (string.IsNullOrEmpty(Password))
            {
                PasswordProtected = "";
                return;
            }

            var plainBytes = Encoding.UTF8.GetBytes(Password);
            var protectedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            PasswordProtected = Convert.ToBase64String(protectedBytes);
        }

        /// <summary>Decrypts PasswordProtected into Password. Call right after loading from disk.</summary>
        public void UnprotectPassword()
        {
            if (string.IsNullOrEmpty(PasswordProtected))
            {
                Password = "";
                return;
            }

            try
            {
                var protectedBytes = Convert.FromBase64String(PasswordProtected);
                var plainBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                Password = Encoding.UTF8.GetString(plainBytes);
            }
            catch
            {
                // Encrypted under a different Windows account/machine, or corrupted.
                // Fall back to empty rather than throwing during settings load.
                Password = "";
            }
        }
    }
}
