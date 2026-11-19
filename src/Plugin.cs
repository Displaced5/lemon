using System.Collections.Concurrent;
using System.Diagnostics;
using ClothingPairing.Core;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace ClothingPairing;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState Client { get; private set; } = null!;
    [PluginService] internal static IPlayerState Player { get; private set; } = null!;
    [PluginService] internal static IObjectTable Objects { get; private set; } = null!;
    [PluginService] internal static ICondition Conditions { get; private set; } = null!;
    [PluginService] internal static IUnlockState Unlocks { get; private set; } = null!;
    [PluginService] internal static IDataManager Data { get; private set; } = null!;
    [PluginService] internal static ITextureProvider Textures { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly ConcurrentQueue<Action> actions = new();
    private readonly WindowSystem windows = new("ClothingPairing");
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly GameAdapter adapter;
    private readonly SettingsWindow settings;
    private readonly ConcurrentQueue<string> history = new();
    private readonly SubmissionArbiter arbiter = new();
    internal AccessoryChannel[] Channels { get; } =
    [
        new(AccessoryKind.Fashion, 0, "时尚配饰"),
        new(AccessoryKind.Facewear, 0, "面部配饰"),
    ];
    private double nextSample;
    private bool disposed;
    private bool capabilityFault;
    private string saveError = "";
    private string runtimeError = "";
    private double retrySaveAfter;
    private bool configurationDirty;
    private string command = "/cpair";

    internal Configuration Configuration { get; }
    internal string Error => capabilityFault ? runtimeError : saveError;
    internal string[] History => history.ToArray();
    internal AccessoryInfo? Accessory(uint id, AccessoryKind kind) => adapter.Find(id, kind);

    public Plugin()
    {
        // A bad/future config fails loading visibly rather than silently overwriting user data.
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Normalize();
        adapter = new GameAdapter(Client, Player, Objects, Conditions, Unlocks, Data);
        settings = new SettingsWindow(this);
        windows.AddWindow(settings);
        if (!Commands.AddHandler(command, new CommandInfo(OnCommand)
            { HelpMessage = "Clothing pairing 设置；pause 暂停，resume 继续，restore 恢复一次，clear 清除记忆" }))
        {
            command = "/clothingpairing";
            if (!Commands.AddHandler(command, new CommandInfo(OnCommand) { HelpMessage = "打开 Clothing pairing 设置" }))
                throw new InvalidOperationException("插件命令已被占用，无法注册 /cpair 或 /clothingpairing");
        }
        Framework.Update += OnUpdate;
        Client.TerritoryChanged += OnTerritoryChanged;
        Client.Logout += OnLogout;
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenSettings;
        PluginInterface.UiBuilder.OpenMainUi += OpenSettings;
        Log.Information("Clothing pairing 1.1.3 loaded; separate fashion/facewear memories, mode B, command {Command}", command);
    }

    internal void Enqueue(Action action)
    {
        if (!disposed) actions.Enqueue(action);
    }

    internal void ChangeSetting(Action<Configuration> change) => Enqueue(() =>
    {
        change(Configuration);
        Save();
    });

    internal void Pause(AccessoryChannel channel) => Enqueue(() => channel.Controller?.Pause());
    internal void Restore(AccessoryChannel channel) => Enqueue(() =>
    {
        if (Configuration.ToOptions(channel.Kind).Enabled) channel.Controller?.RequestRestore();
    });
    internal void ClearMemory(AccessoryChannel channel) => Enqueue(() => channel.Controller?.ClearMemory());

    private void OnTerritoryChanged(uint _) => Enqueue(() => adapter.NotifyZoneChange(clock.Elapsed.TotalSeconds));
    private void OnLogout(int type, int code) => Enqueue(() =>
    {
        SaveControllerChanges();
        adapter.ResetSession();
        foreach (var channel in Channels)
        {
            channel.Controller = null;
            channel.Snapshot = default;
            channel.LastDiagnostic = "";
        }
        AddHistory("已登出，取消本次会话的恢复请求");
    });

    private void OnUpdate(IFramework _)
    {
        if (disposed) return;
        var now = clock.Elapsed.TotalSeconds;
        try
        {
            // UI/command callbacks never touch native pointers or mutate the controller directly.
            var processed = 0;
            while (processed++ < 100 && actions.TryDequeue(out var action)) action();
            SaveControllerChanges();
            if (now < nextSample || capabilityFault) return;
            nextSample = now + 0.1;
            var requests = new RestoreRequest?[Channels.Length];
            for (var i = 0; i < Channels.Length; i++)
            {
                var channel = Channels[i];
                if (channel.Error.Length > 0) continue;
                try
                {
                    var controller = channel.Controller;
                    var snapshot = adapter.Capture(now, controller?.Memory.LastAccessoryId ?? 0,
                        controller?.HasPendingRequest == true, Configuration.RespectAutoUmbrella, channel.Kind, channel.Slot);
                    if (snapshot.CharacterId != 0 && controller?.CharacterId != snapshot.CharacterId)
                    {
                        SaveControllerChanges();
                        var memory = Configuration.GetMemory(snapshot.CharacterId, channel.Kind, channel.Slot);
                        controller = channel.Controller = new RestoreController(snapshot.CharacterId, memory);
                        snapshot = adapter.Capture(now, memory.LastAccessoryId, false,
                            Configuration.RespectAutoUmbrella, channel.Kind, channel.Slot);
                        Save();
                        AddHistory($"{channel.Name}：已切换角色记录，登录不会恢复历史配饰");
                    }
                    channel.Snapshot = snapshot;
                    if (controller is null) continue;
                    requests[i] = controller.Update(snapshot, Configuration.ToOptions(channel.Kind), now);
                    var diagnostic = $"{controller.Phase}: {controller.Status}";
                    if (diagnostic != channel.LastDiagnostic)
                    {
                        channel.LastDiagnostic = diagnostic;
                        AddHistory($"{channel.Name}：{controller.Status}");
                    }
                }
                catch (Exception ex) { StopChannel(channel, ex); }
            }
            var selected = arbiter.Select(requests.Select(r => r.HasValue).ToArray(), now);
            if (selected is { } index)
            {
                var channel = Channels[index];
                try
                {
                    var request = requests[index]!.Value;
                    var result = adapter.TrySubmit(now, request, Configuration.RespectAutoUmbrella, channel.Kind, channel.Slot);
                    if (result.Attempted)
                    {
                        arbiter.Submitted(now);
                        channel.Controller!.Submitted(now, result.Accepted);
                        AddHistory($"{channel.Name}：{result.Message}；ID {request.AccessoryId}，第 {channel.Controller.Attempts} 次");
                    }
                    else channel.Controller!.Defer(result.Message);
                }
                catch (Exception ex) { StopChannel(channel, ex); }
            }
            SaveControllerChanges();
        }
        catch (Exception ex)
        {
            capabilityFault = true;
            foreach (var channel in Channels)
                channel.Controller?.StopForError("游戏接口发生异常，已停止自动功能；请重载插件后检查");
            runtimeError = "游戏接口发生异常，自动功能已停止；详情见 /xllog";
            Log.Error(ex, "Clothing pairing stopped after an unexpected error");
            AddHistory(runtimeError);
        }
    }

    private void StopChannel(AccessoryChannel channel, Exception ex)
    {
        channel.Error = "此类配饰接口发生异常，已暂停；请重载插件后检查 /xllog";
        channel.Controller?.StopForError(channel.Error);
        Log.Error(ex, "Clothing pairing {Category} slot {Slot} stopped", channel.Kind, channel.Slot);
        AddHistory($"{channel.Name}：{channel.Error}");
    }

    private void SaveControllerChanges()
    {
        if (!Channels.Any(c => c.Controller?.ConfigurationChanged == true) && !configurationDirty) return;
        if (Save()) foreach (var channel in Channels) channel.Controller?.AcknowledgeConfiguration();
    }

    private bool Save()
    {
        configurationDirty = true;
        if (clock.Elapsed.TotalSeconds < retrySaveAfter) return false;
        try
        {
            PluginInterface.SavePluginConfig(Configuration);
            saveError = "";
            configurationDirty = false;
            retrySaveAfter = 0;
            return true;
        }
        catch (Exception ex)
        {
            // Keep in-memory preferences and expose failure. Do not replace the config with defaults.
            if (saveError.Length == 0) Log.Error(ex, "Could not save Clothing pairing configuration");
            saveError = "配置保存失败，当前会话设置仍有效；详情见 /xllog";
            retrySaveAfter = clock.Elapsed.TotalSeconds + 5;
            return false;
        }
    }

    private void AddHistory(string message)
    {
        history.Enqueue($"{DateTime.Now:HH:mm:ss}  {message}");
        while (history.Count > 80) history.TryDequeue(out _);
    }

    private void OnCommand(string name, string args)
    {
        var parts = args.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<AccessoryChannel> targets = Channels;
        var action = parts.FirstOrDefault() ?? "";
        if (action is "face" or "fashion")
        {
            var kind = action == "face" ? AccessoryKind.Facewear : AccessoryKind.Fashion;
            targets = Channels.Where(c => c.Kind == kind);
            action = parts.ElementAtOrDefault(1) ?? "";
        }
        switch (action)
        {
            case "pause": foreach (var channel in targets) Pause(channel); break;
            case "resume":
            case "restore": foreach (var channel in targets) Restore(channel); break;
            case "clear": foreach (var channel in targets) ClearMemory(channel); break;
            default: OpenSettings(); break;
        }
    }

    private void OpenSettings() => settings.IsOpen = true;

    public void Dispose()
    {
        disposed = true;
        Framework.Update -= OnUpdate;
        Client.TerritoryChanged -= OnTerritoryChanged;
        Client.Logout -= OnLogout;
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;
        PluginInterface.UiBuilder.OpenMainUi -= OpenSettings;
        Commands.RemoveHandler(command);
        windows.RemoveAllWindows();
        while (actions.TryDequeue(out _)) { }
        // Config mutations are saved during update; no native access or pending actions survive unload.
        SaveControllerChanges();
        foreach (var channel in Channels) channel.Controller = null;
    }
}
