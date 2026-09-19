using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Newtonsoft.Json;
using SimHub.Plugin.MQTTBridge.Models;

namespace SimHub.Plugin.MQTTBridge.UI
{
    public partial class SettingsControl : UserControl
    {
        private readonly MqttBridgePlugin _plugin;

        public SettingsControl(MqttBridgePlugin plugin)
        {
            InitializeComponent();
            _plugin = plugin;

            LoadFieldsFromSettings();

            StatusText.Text = _plugin.ConnectionStatus;
            _plugin.StatusChanged += OnStatusChanged;
            Unloaded += (s, e) => _plugin.StatusChanged -= OnStatusChanged;
        }

        private void LoadFieldsFromSettings()
        {
            DataContext = _plugin.Settings;

            HostBox.Text = _plugin.Settings.Host;
            PortBox.Text = _plugin.Settings.Port.ToString();
            UsernameBox.Text = _plugin.Settings.Username;
            PasswordBox.Password = _plugin.Settings.Password;
            ClientIdBox.Text = _plugin.Settings.ClientId;
            PropertyPrefixBox.Text = _plugin.Settings.PropertyPrefix;
            KeepAliveBox.Text = _plugin.Settings.KeepAliveSeconds.ToString();
            UseTlsCheck.IsChecked = _plugin.Settings.UseTls;
            AutoConnectCheck.IsChecked = _plugin.Settings.AutoConnect;
        }

        private void OnStatusChanged(string status)
        {
            Dispatcher.Invoke(() => StatusText.Text = status);
        }

        private void ReadFieldsIntoSettings()
        {
            _plugin.Settings.Host = HostBox.Text.Trim();
            _plugin.Settings.Port = int.TryParse(PortBox.Text, out var p) ? p : 1883;
            _plugin.Settings.Username = UsernameBox.Text.Trim();
            _plugin.Settings.Password = PasswordBox.Password;
            _plugin.Settings.ClientId = ClientIdBox.Text.Trim();
            _plugin.Settings.PropertyPrefix = string.IsNullOrWhiteSpace(PropertyPrefixBox.Text) ? "MqttBridge" : PropertyPrefixBox.Text.Trim();
            _plugin.Settings.KeepAliveSeconds = int.TryParse(KeepAliveBox.Text, out var k) ? k : 30;
            _plugin.Settings.UseTls = UseTlsCheck.IsChecked == true;
            _plugin.Settings.AutoConnect = AutoConnectCheck.IsChecked == true;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            ReadFieldsIntoSettings();
            _plugin.PersistSettingsNow();
            MessageBox.Show("Settings saved.", "MQTT Bridge", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            ReadFieldsIntoSettings();

            var dialog = new SaveFileDialog
            {
                Title = "Export MQTT Bridge configuration",
                Filter = "MQTT Bridge config (*.json)|*.json|All files (*.*)|*.*",
                FileName = "SimHub.Plugin.MQTTBridge.json"
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                // Round-trip through JSON to get an independent copy, then strip the
                // encrypted password so it's never written to the exported file (it's
                // DPAPI-encrypted to this Windows account anyway, so it wouldn't import
                // usefully elsewhere).
                var exportSettings = JsonConvert.DeserializeObject<MqttBridgeSettings>(
                    JsonConvert.SerializeObject(_plugin.Settings));
                exportSettings.PasswordProtected = "";

                var json = JsonConvert.SerializeObject(exportSettings, Formatting.Indented);
                File.WriteAllText(dialog.FileName, json);

                MessageBox.Show(
                    "Configuration Exported.\n\nPasswords are not included in exports for security.  After import all passwords will need to be re-entered.",
                    "MQTT Bridge", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "MQTT Bridge", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Import MQTT Bridge configuration",
                Filter = "MQTT Bridge config (*.json)|*.json|All files (*.*)|*.*"
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                var json = File.ReadAllText(dialog.FileName);
                var imported = JsonConvert.DeserializeObject<MqttBridgeSettings>(json);

                if (imported == null)
                {
                    MessageBox.Show("That file doesn't look like a valid MQTT Bridge config.", "MQTT Bridge", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _plugin.ReplaceSettings(imported);
                LoadFieldsFromSettings();

                MessageBox.Show(
                    "Configuration imported and saved. The broker password isn't included in exported configs, so re-enter it here if needed. Click Connect to apply the new broker settings, and Apply subscriptions to pick up any imported subscribe mappings.",
                    "MQTT Bridge", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Import failed: {ex.Message}", "MQTT Bridge", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            ReadFieldsIntoSettings();
            _plugin.ConnectAsync();
        }

        private void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            _plugin.DisconnectAsync();
        }

        private void AddPublishRow_Click(object sender, RoutedEventArgs e)
        {
            _plugin.AddPublishMapping(new PublishMapping());
        }

        private void RemovePublishRow_Click(object sender, RoutedEventArgs e)
        {
            if (PublishGrid.SelectedItem is PublishMapping selected)
            {
                _plugin.RemovePublishMapping(selected);
            }
        }

        private void AddSubscribeRow_Click(object sender, RoutedEventArgs e)
        {
            _plugin.AddSubscribeMapping(new SubscribeMapping());
        }

        private void RemoveSubscribeRow_Click(object sender, RoutedEventArgs e)
        {
            if (SubscribeGrid.SelectedItem is SubscribeMapping selected)
            {
                _plugin.RemoveSubscribeMapping(selected);
            }
        }

        private void ApplySubscriptions_Click(object sender, RoutedEventArgs e)
        {
            _plugin.ResubscribeAll();
        }
    }
}
