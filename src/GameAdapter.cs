using ClothingPairing.Core;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using Ornament = Lumina.Excel.Sheets.Ornament;

namespace ClothingPairing;

public sealed record AccessoryInfo(uint Id, string Name, uint Icon, int Slot = 0);
public readonly record struct Submission(bool Attempted, bool Accepted, string Message);

/// <summary>Only accessed by the framework update callback. Never retains player/native pointers.</summary>
public sealed unsafe class GameAdapter
{
    private readonly IClientState client;
    private readonly IPlayerState player;
    private readonly IObjectTable objects;
    private readonly ICondition conditions;
    private readonly IUnlockState unlocks;
    private readonly IDataManager data;
    private readonly Dictionary<uint, AccessoryInfo> catalog = [];
    private readonly Dictionary<uint, AccessoryInfo> facewearCatalog = [];
    private readonly HashSet<uint> dutyTerritories = [];
    private readonly HashSet<uint> umbrellaIds = [];
    private double zoneUntil;
    private readonly EquipmentChangeTracker equipmentChanges = new();

    private static readonly (ConditionFlag Flag, string Reason)[] BusyConditions =
    [
        (ConditionFlag.Unconscious, "玩家已死亡"),
        (ConditionFlag.Emoting, "正在使用情感动作"),
        (ConditionFlag.Casting, "正在施法"),
        (ConditionFlag.Casting87, "正在施法"),
        (ConditionFlag.Occupied, "玩家忙碌中"),
        (ConditionFlag.Occupied30, "玩家忙碌中"),
        (ConditionFlag.OccupiedInEvent, "正在进行交互"),
        (ConditionFlag.OccupiedInQuestEvent, "正在进行任务交互"),
        (ConditionFlag.Occupied33, "玩家忙碌中"),
        (ConditionFlag.OccupiedInCutSceneEvent, "正在观看剧情"),
        (ConditionFlag.WatchingCutscene, "正在观看剧情"),
        (ConditionFlag.WatchingCutscene78, "正在观看剧情"),
        (ConditionFlag.Occupied38, "玩家忙碌中"),
        (ConditionFlag.Occupied39, "玩家忙碌中"),
        (ConditionFlag.OccupiedSummoningBell, "正在使用传唤铃"),
        (ConditionFlag.Crafting, "正在制作"),
        (ConditionFlag.PreparingToCraft, "正在准备制作"),
        (ConditionFlag.ExecutingCraftingAction, "正在制作"),
        (ConditionFlag.Gathering, "正在采集"),
        (ConditionFlag.ExecutingGatheringAction, "正在采集"),
        (ConditionFlag.Fishing, "正在钓鱼"),
        (ConditionFlag.Jumping, "正在跳跃"),
        (ConditionFlag.Jumping61, "正在跳跃"),
        (ConditionFlag.Diving, "正在潜水"),
        (ConditionFlag.UsingChocoboTaxi, "正在乘坐陆行鸟出租车"),
        (ConditionFlag.LoggingOut, "正在登出"),
        (ConditionFlag.TradeOpen, "正在交易"),
        (ConditionFlag.Performing, "正在演奏"),
        (ConditionFlag.EditingPortrait, "正在编辑肖像"),
        (ConditionFlag.DutyRecorderPlayback, "正在回放任务"),
        (ConditionFlag.RolePlaying, "正在使用剧情角色"),
        (ConditionFlag.Transformed, "玩家处于变身状态"),
        (ConditionFlag.PilotingMech, "正在驾驶机甲"),
    ];

    public GameAdapter(IClientState client, IPlayerState player, IObjectTable objects,
        ICondition conditions, IUnlockState unlocks, IDataManager data)
    {
        this.client = client;
        this.player = player;
        this.objects = objects;
        this.conditions = conditions;
        this.unlocks = unlocks;
        this.data = data;

        foreach (var row in data.GetExcelSheet<Ornament>())
        {
            if (row.RowId == 0 || row.Model == 0) continue;
            catalog[row.RowId] = new(row.RowId, row.Singular.ExtractText(), row.Icon);
        }
        foreach (var row in data.GetExcelSheet<TerritoryType>())
            if (row.ContentFinderCondition.RowId != 0)
                dutyTerritories.Add(row.RowId);
        foreach (var row in data.GetExcelSheet<Glasses>())
        {
            if (row.RowId == 0 || row.RowId > ushort.MaxValue || row.Model == 0 || !row.Style.IsValid) continue;
            var name = row.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name)) name = row.Singular.ExtractText();
            var slot = row.Style.Value.GlassesSlot;
            if (slot != 0) continue;
            facewearCatalog[row.RowId] = new(row.RowId, name, (uint)Math.Max(0, row.Icon), slot);
        }
    }

    public AccessoryInfo? Find(uint id, AccessoryKind kind = AccessoryKind.Fashion) =>
        (kind == AccessoryKind.Fashion ? catalog : facewearCatalog).GetValueOrDefault(id);
    public void NotifyZoneChange(double now) => zoneUntil = now + 1.5;
    public void ResetSession() => equipmentChanges.Reset();

    public GameSnapshot Capture(double now, uint rememberedId, bool checkAction, bool respectUmbrella,
        AccessoryKind kind = AccessoryKind.Fashion, int slot = 0)
    {
        if (slot != 0) throw new ArgumentOutOfRangeException(nameof(slot));
        var activeCatalog = kind == AccessoryKind.Fashion ? catalog : facewearCatalog;
        var id = player.IsLoaded ? player.ContentId : 0;
        var interruptions = Interruption.None;
        if (conditions[ConditionFlag.BoundByDuty] || conditions[ConditionFlag.BoundByDuty56] ||
            conditions[ConditionFlag.BoundByDuty95] || dutyTerritories.Contains(client.TerritoryType))
            interruptions |= Interruption.Duty;
        if (conditions[ConditionFlag.Mounted] || conditions[ConditionFlag.RidingPillion] ||
            conditions[ConditionFlag.Mounting] || conditions[ConditionFlag.Mounting71] ||
            conditions[ConditionFlag.InFlight])
            interruptions |= Interruption.Mount;
        if (conditions[ConditionFlag.InCombat]) interruptions |= Interruption.Combat;
        if (conditions[ConditionFlag.BetweenAreas] || conditions[ConditionFlag.BetweenAreas51] || now < zoneUntil)
            interruptions |= Interruption.Zone;

        if (!client.IsLoggedIn || id == 0)
        {
            equipmentChanges.Reset();
            return new(id, null, interruptions, "等待登录和角色数据就绪");
        }
        var local = objects.LocalPlayer;
        if (local is null || local.Address == 0)
            return new(id, null, interruptions, "等待玩家对象就绪");
        if (local.EntityId != player.EntityId)
            return new(id, null, interruptions, "等待角色与玩家对象同步");

        var character = (Character*)local.Address;
        if (kind == AccessoryKind.Facewear)
        {
            var gearsets = RaptureGearsetModule.Instance();
            interruptions |= equipmentChanges.Observe(id, local.ClassJob.RowId,
                gearsets == null ? null : gearsets->CurrentGearsetIndex, now);
        }
        var rawId = kind == AccessoryKind.Fashion
            ? (uint)character->OrnamentData.OrnamentId : character->DrawData.GlassesIds[slot];
        var hasAccessory = kind == AccessoryKind.Fashion ? conditions[ConditionFlag.UsingFashionAccessory] : rawId > 0;
        var transition = conditions[ConditionFlag.MountOrOrnamentTransition];
        uint? accessoryId = null;
        // Require condition and ID to agree. An inconsistent transitional sample must
        // neither clear memory nor be interpreted as an intentional unequip.
        if (!transition && hasAccessory && rawId > 0 && activeCatalog.ContainsKey(rawId)) accessoryId = rawId;
        else if (!transition && !hasAccessory && rawId == 0) accessoryId = 0;

        var autoUmbrella = (uint)character->OrnamentData.AutoUmbrellaOrnamentId;
        if (autoUmbrella != 0) umbrellaIds.Add(autoUmbrella);
        var umbrellaConflict = kind == AccessoryKind.Fashion && autoUmbrella != 0 && umbrellaIds.Contains(rememberedId);

        var block = "";
        if ((interruptions & Interruption.Zone) != 0) block = "等待切区结束";
        else if ((interruptions & Interruption.Duty) != 0) block = "等待离开副本";
        else if ((interruptions & Interruption.Mount) != 0) block = "等待下坐骑";
        else if ((interruptions & Interruption.Combat) != 0) block = "等待脱离战斗";
        else if ((interruptions & (Interruption.ClassJob | Interruption.Gearset)) != 0) block = "等待职业与套装切换稳定";
        else if (client.IsPvP) block = "PvP 区域暂停恢复";
        else if (client.IsGPosing) block = "集体姿势模式暂停恢复";
        else if (transition) block = "等待配饰或坐骑动作结束";
        else if (accessoryId is null) block = "等待配饰状态同步";
        else
            foreach (var busy in BusyConditions)
                if (conditions[busy.Flag]) { block = busy.Reason; break; }

        var unavailable = "";
        uint? status = null;
        if (checkAction && rememberedId != 0 && block.Length == 0 && accessoryId == 0)
        {
            if (!activeCatalog.ContainsKey(rememberedId))
                unavailable = "记忆中的配饰不存在于当前客户端";
            else if (kind == AccessoryKind.Facewear)
            {
                var row = data.GetExcelSheet<Glasses>().GetRow(rememberedId);
                if (facewearCatalog[rememberedId].Slot != slot)
                    unavailable = "记忆中的面部配饰与当前槽位不匹配";
                else if (!unlocks.IsGlassesUnlocked(row))
                    unavailable = "当前角色尚未解锁记忆中的面部配饰";
                else if (GameMain.MemberFunctionPointers.ExecuteCommand == null)
                    throw new InvalidOperationException("面部配饰佩戴接口不可用，已停止此槽位自动恢复");
                // Facewear uses a game command, not an ActionType. Do not fabricate an action status.
            }
            else if (!unlocks.IsOrnamentUnlocked(data.GetExcelSheet<Ornament>().GetRow(rememberedId)))
                unavailable = "当前角色尚未解锁记忆中的配饰";
            else if (respectUmbrella && umbrellaConflict)
                unavailable = "已交由游戏自动雨伞控制；可在设置中关闭优先游戏自动雨伞";
            else
            {
                var manager = ActionManager.Instance();
                if (manager is null || ActionManager.MemberFunctionPointers.GetActionStatus == null ||
                    ActionManager.MemberFunctionPointers.UseAction == null)
                    throw new InvalidOperationException("配饰动作接口不可用；已停止自动恢复");
                status = manager->GetActionStatus(ActionType.Ornament, rememberedId);
                if (status != 0) unavailable = "游戏当前不允许使用此配饰（状态码见诊断）";
            }
        }
        return new(id, accessoryId, interruptions, block, unavailable, status, autoUmbrella != 0,
            rawId, hasAccessory, transition);
    }

    public Submission TrySubmit(double now, RestoreRequest request, bool respectUmbrella,
        AccessoryKind kind = AccessoryKind.Fashion, int slot = 0)
    {
        var snapshot = Capture(now, request.AccessoryId, true, respectUmbrella, kind, slot);
        if (snapshot.CharacterId != request.CharacterId || snapshot.AccessoryId != 0 ||
            snapshot.Interruptions != 0 || snapshot.BlockReason.Length > 0 || snapshot.UnavailableReason.Length > 0 ||
            (kind == AccessoryKind.Fashion && snapshot.NativeStatus != 0))
            return new(false, false, "提交前状态改变，继续等待");
        if (kind == AccessoryKind.Facewear)
        {
            var command = FacewearEquipCommand.Create(slot, request.AccessoryId);
            var accepted = GameMain.ExecuteCommand(FacewearEquipCommand.CommandId, command.Slot, command.GlassesId);
            return new(true, accepted, accepted ? "游戏已接受面部配饰佩戴请求" : "游戏拒绝了面部配饰佩戴请求");
        }
        var manager = ActionManager.Instance();
        if (manager is null) return new(false, false, "动作管理器尚未就绪");
        // Regular game action dispatch: no memory writes, unlock changes or forced visuals.
        var result = manager->UseAction(ActionType.Ornament, request.AccessoryId);
        return new(true, result, result ? "游戏已接受展开请求" : "游戏拒绝了展开请求");
    }
}
