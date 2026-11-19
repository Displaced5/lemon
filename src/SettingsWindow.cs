using System.Numerics;
using ClothingPairing.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;

namespace ClothingPairing;

public sealed class SettingsWindow : Window
{
    private readonly Plugin plugin;
    private AccessoryChannel? clearChannel;
    private ulong clearCharacter;

    public SettingsWindow(Plugin plugin) : base("Clothing pairing###ClothingPairing.Settings")
    {
        this.plugin = plugin;
        Size = new Vector2(650, 720);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(490, 420), MaximumSize = new Vector2(1400, 1300) };
    }

    public override void Draw()
    {
        var config = plugin.Configuration;
        ImGui.TextUnformatted("Clothing pairing  ·  Displaced  ·  1.1.2");
        Checkbox("启用自动恢复", config.Enabled, (c, v) => c.Enabled = v);
        Checkbox("恢复时尚配饰", config.FashionEnabled, (c, v) => c.FashionEnabled = v);
        ImGui.SameLine();
        Checkbox("恢复面部配饰", config.FacewearEnabled, (c, v) => c.FacewearEnabled = v);
        ImGui.TextWrapped("两类配饰分别记忆和暂停。主动移除后暂停对应配饰的恢复；仍在佩戴时不会重复操作。");
        ImGui.Separator();
        ImGui.TextUnformatted("恢复时机（B 方案）");
        Checkbox("出本后恢复", config.RestoreAfterDuty, (c, v) => c.RestoreAfterDuty = v);
        ImGui.SameLine();
        Checkbox("下坐骑后恢复", config.RestoreAfterMount, (c, v) => c.RestoreAfterMount = v);
        Checkbox("脱战后恢复", config.RestoreAfterCombat, (c, v) => c.RestoreAfterCombat = v);
        ImGui.SameLine();
        Checkbox("普通切区后恢复", config.RestoreAfterZone, (c, v) => c.RestoreAfterZone = v);
        Checkbox("切换职业后恢复面部配饰", config.RestoreFacewearAfterClassJob, (c, v) => c.RestoreFacewearAfterClassJob = v);
        Checkbox("切换套装后恢复面部配饰", config.RestoreFacewearAfterGearset, (c, v) => c.RestoreFacewearAfterGearset = v);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("包括同职业切换到其他套装。若新套装已佩戴面部配饰，则采用当前款式；仅缺失时恢复上一件。");
        Checkbox("优先游戏自动雨伞", config.RespectAutoUmbrella, (c, v) => c.RespectAutoUmbrella = v);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("仅影响时尚配饰中的雨伞，面部配饰独立处理。");
        if (plugin.Error.Length > 0)
            ImGui.TextColored(new Vector4(1, 0.6f, 0.3f, 1), plugin.Error);

        foreach (var channel in plugin.Channels) DrawChannel(channel);
        if (ImGui.CollapsingHeader("最近记录"))
        {
            ImGui.TextUnformatted("仅保留本次加载最近 80 条，不自动写入文件。");
            foreach (var line in plugin.History.Reverse()) ImGui.TextWrapped(line);
        }
    }

    private void DrawChannel(AccessoryChannel channel)
    {
        ImGui.PushID($"{channel.Kind}.{channel.Slot}");
        ImGui.Separator();
        ImGui.TextUnformatted(channel.Name);
        DrawAccessory("当前", channel.Snapshot.AccessoryId, channel.Kind);
        DrawAccessory("记忆", channel.Controller?.Memory.LastAccessoryId ?? 0, channel.Kind);
        ImGui.TextWrapped("状态：" + (channel.Controller?.Status ?? "等待角色登录"));
        if (channel.Error.Length > 0)
            ImGui.TextColored(new Vector4(1, 0.6f, 0.3f, 1), channel.Error);

        ImGui.BeginDisabled(channel.Controller is null || channel.Error.Length > 0);
        if (ImGui.Button("暂停恢复")) plugin.Pause(channel);
        ImGui.SameLine();
        ImGui.BeginDisabled(!plugin.Configuration.ToOptions(channel.Kind).Enabled);
        if (ImGui.Button("继续 / 立即恢复")) plugin.Restore(channel);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("清除这项记忆"))
        {
            clearChannel = channel;
            clearCharacter = channel.Controller?.CharacterId ?? 0;
        }
        if (clearChannel == channel && clearCharacter == channel.Controller?.CharacterId)
        {
            ImGui.TextWrapped("只清除此项记忆，下次重新佩戴时记录。");
            if (ImGui.Button("确认清除")) { plugin.ClearMemory(channel); clearChannel = null; }
            ImGui.SameLine();
            if (ImGui.Button("取消")) clearChannel = null;
        }
        else if (clearChannel == channel) clearChannel = null;
        ImGui.EndDisabled();

        if (ImGui.CollapsingHeader("诊断"))
        {
            ImGui.TextUnformatted($"阶段：{channel.Controller?.Phase.ToString() ?? "未登录"}；中断：{channel.Controller?.Sources.ToString() ?? "None"}");
            ImGui.TextUnformatted($"本轮尝试：{channel.Controller?.Attempts ?? 0} / {RestoreController.MaximumAttempts}");
            ImGui.TextUnformatted($"原始 ID：{channel.Snapshot.RawAccessoryId}；过渡：{channel.Snapshot.Transitioning}");
            if (channel.Kind == AccessoryKind.Fashion)
            {
                ImGui.TextUnformatted($"游戏状态码：{channel.Snapshot.NativeStatus?.ToString() ?? "未查询"}；使用标记：{channel.Snapshot.UsingAccessory}");
                ImGui.TextUnformatted($"自动雨伞配置：{(channel.Snapshot.AutoUmbrellaConfigured ? "检测到" : "未检测到")}");
            }
            else ImGui.TextUnformatted("面部配饰使用游戏佩戴命令，并核对槽位、解锁与实际结果。");
            ImGui.TextWrapped("最近结果：" + (channel.Controller?.LastResult ?? "无"));
        }
        ImGui.PopID();
    }

    private void Checkbox(string label, bool value, Action<Configuration, bool> update)
    {
        if (ImGui.Checkbox(label, ref value)) plugin.ChangeSetting(c => update(c, value));
    }

    private void DrawAccessory(string label, uint? id, AccessoryKind kind)
    {
        var info = id is > 0 ? plugin.Accessory(id.Value, kind) : null;
        if (info?.Icon > 0)
        {
            var texture = Plugin.Textures.GetFromGameIcon(new GameIconLookup(info.Icon));
            if (texture.TryGetWrap(out var wrap, out _))
            {
                ImGui.Image(wrap.Handle, new Vector2(28, 28));
                ImGui.SameLine();
            }
        }
        var name = id is null ? "状态同步中" : id == 0 ? "无" : info?.Name ?? $"未知配饰（{id}）";
        ImGui.TextUnformatted($"{label}：{name}");
    }
}
