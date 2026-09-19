# SimHub MQTT Bridge

A SimHub plugin that connects to an MQTT broker and:

- **Publishes** any SimHub property to an MQTT topic (one row per property -> topic mapping).
- **Subscribes** to MQTT topics and exposes the latest payload of each as a SimHub property, usable in dashboards, NCalc expressions, or other plugins.

It is designed to run **alongside** the existing [SHWotever MQTT Publisher](https://github.com/SHWotever/SimHub-MQTT-Publisher) plugin without conflict. See "Why this doesn't conflict" below.

## What's in this project

```
SimHub.Plugin.MQTTBridge/
  SimHub.Plugin.MQTTBridge.csproj  Project file (net48, WPF, MQTTnet + Newtonsoft.Json)
  MqttBridgePlugin.cs          Main plugin: IPlugin / IDataPlugin / IWPFSettingsV2
  MqttService.cs                MQTTnet wrapper: connect/reconnect/publish/subscribe
  Models/
    MqttBridgeSettings.cs       Persisted settings (connection + mapping lists)
    PublishMapping.cs           One "SimHub property -> MQTT topic" row
    SubscribeMapping.cs         One "MQTT topic -> SimHub property" row
  UI/
    SettingsControl.xaml(.cs)   The settings panel shown inside SimHub
```

## Prerequisites

- Visual Studio 2022 (or `dotnet` SDK 6+/8+ command line - the project itself still
  targets `net48` because that's what SimHub's plugin host loads, but modern SDKs
  can build net48 class libraries fine as long as the .NET Framework 4.8 targeting
  pack is installed).
- A working SimHub installation (this is where `SimHub.Plugins.dll`,
  `GameReaderCommon.dll`, `SimHub.Logging.dll`, and `log4net.dll` come from -
  they are **not** included in this project and must not be redistributed).

## Build steps

1. Open `SimHub.Plugin.MQTTBridge.slnx` in Visual Studio 2022 (17.13+, which supports the
   `.slnx` format natively), or run `dotnet build SimHub.Plugin.MQTTBridge.slnx` from this
   folder. If your Visual Studio version doesn't support `.slnx` yet, open
   `SimHub.Plugin.MQTTBridge/SimHub.Plugin.MQTTBridge.csproj` directly instead.
2. Fix the four `HintPath` entries in the `.csproj` to point at your actual SimHub
   install folder if it isn't the default `C:\Program Files (x86)\SimHub`:
   ```xml
   <Reference Include="SimHub.Plugins">
     <HintPath>C:\Program Files (x86)\SimHub\SimHub.Plugins.dll</HintPath>
   </Reference>
   <Reference Include="GameReaderCommon">
     <HintPath>C:\Program Files (x86)\SimHub\GameReaderCommon.dll</HintPath>
   </Reference>
   <Reference Include="SimHub.Logging">
     <HintPath>C:\Program Files (x86)\SimHub\SimHub.Logging.dll</HintPath>
   </Reference>
   <Reference Include="log4net">
     <HintPath>C:\Program Files (x86)\SimHub\log4net.dll</HintPath>
   </Reference>
   ```
3. Build in `Release`. NuGet will pull down `MQTTnet` and `Newtonsoft.Json` automatically
   (this container's network policy blocks nuget.org, so these packages could not be
   restored or the build test-compiled here - do this step on a machine with normal
   internet access).
4. After a successful build, `bin\Release\net48\` will contain:
   - `SimHub.Plugin.MQTTBridge.dll` (the plugin itself)
   - `MQTTnet.dll`
   - `Newtonsoft.Json.dll` (skip this one if SimHub already ships a compatible version -
     check the SimHub install folder first; if a `Newtonsoft.Json.dll` is already there,
     don't overwrite it with a different version, that's a real way to break other plugins)

## Install into SimHub

1. Close SimHub.
2. Copy `SimHub.Plugin.MQTTBridge.dll` and `MQTTnet.dll` into the SimHub install folder
   (same folder as `SimHub.exe`, e.g. `C:\Program Files (x86)\SimHub\`).
3. Start SimHub.
4. Go to **Settings > Plugins**, find **MQTT Bridge** in the list, and enable it.
   Restart SimHub if prompted.
5. Open the new **MQTT Bridge** page from the left menu to configure the broker
   connection and your publish/subscribe mappings.

## Using it

### Publish (SimHub -> MQTT)

Add a row, enter the exact SimHub property name (e.g. `DataCorePlugin.GameData.SpeedKmh`)
and a topic (e.g. `simhub/speed`). To find exact property names, use SimHub's dashboard
editor property picker, or any existing dash/overlay that already displays the value you want.

Each row has:
- **QoS** (0/1/2)
- **Retain**
- **Min interval (ms)** - throttle for chatty properties like speed or RPM
- **Only on change** - skip publishing if the value hasn't moved

### Subscribe (MQTT -> SimHub)

Add a row, enter the topic to subscribe to, a property name, and the expected data
type (String / Double / Integer / Boolean). The value becomes available in SimHub as
`<prefix>.<PropertyName>` - by default `MqttBridge.<PropertyName>` - as soon as a
message arrives on that topic. Click **Apply subscriptions** after editing this list
without reconnecting.

Note: wildcard topics (`#`, `+`) will subscribe fine, but since each row maps to a
single property, all matching messages update the same property (whichever message
came in most recently), rather than fanning out into separate properties per topic.

## Why this doesn't conflict with SHWotever's MQTT Publisher

- **Separate MQTT client, separate client ID.** Each plugin opens its own connection
  to the broker with its own MQTT client ID (this plugin generates a random one on
  first run, stored in its settings). MQTT brokers require unique client IDs per
  connection, and this plugin never touches the other plugin's ID or settings.
- **Separate property namespace.** All properties this plugin exposes live under a
  configurable prefix (default `MqttBridge.*`). As long as you don't rename that
  prefix to collide with names the other plugin already uses, there's no property
  clash - SimHub properties are just a shared dictionary keyed by name, and two
  different names never collide.
- **Separate settings storage and separate DLL.** This plugin persists its settings
  under its own key (`MqttBridgeSettings`) and ships as its own assembly
  (`SimHub.Plugin.MQTTBridge.dll`), so installing or removing it doesn't touch the other
  plugin's files or configuration.
- Both plugins can publish to and subscribe from the same broker at the same time
  with no special coordination needed, exactly like two independent MQTT clients
  from any other application.

## Exporting and importing configuration

Use **Export config** / **Import config** on the settings page to move your whole
setup (broker connection, publish mappings, subscribe mappings) between installs, or
just keep a backup.

- **Export config** writes everything to a `.json` file you choose.
- **Import config** reads a `.json` file back in, replaces the current settings,
  saves immediately, and refreshes the settings page. Click **Connect** afterwards
  to reach the broker with the imported settings, and **Apply subscriptions** to
  register any imported subscribe mappings without restarting SimHub.

Two things worth knowing before you import onto a different machine:

- The broker **password is deliberately left out** of the exported file. Re-enter
  it on the settings page after importing, then click Save/Connect.
- The exported file includes the **Client ID** from the machine it was exported
  from. If you import it onto a second install that connects to the *same* broker
  at the *same time* as the original, both installs will try to use the same
  client ID, which most brokers won't allow (the older connection typically gets
  kicked). Give the imported config a different Client ID on the settings page if
  you plan to run both at once.

## Logging

Connection lifecycle events, subscribe/publish failures, property registration,
and settings import/export all go through SimHub's own logger
(`SimHub.Logging.Current`, a `log4net.ILog` from `SimHub.Logging.dll`), so
they show up in SimHub's own log file/log viewer alongside SimHub's own
messages, prefixed `MQTT Bridge:`. Nothing here writes to its own separate log
file. Per-message publish successes are deliberately **not** logged (that
would be one log line per property per `DataUpdate` tick); only publish/
subscribe *failures* and connection state changes are.

## Download

Prebuilt releases (with `SimHub.Plugin.MQTTBridge.dll` and `MQTTnet.dll`
attached, ready to drop into your SimHub folder per the install steps above)
are published on the [Releases page](https://github.com/jbudworth/SimHub-Plugin-MQTTBridge/releases).

## License

[MIT](LICENSE)

