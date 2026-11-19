namespace ClothingPairing.Core;

/// <summary>Deterministic decision logic; receives monotonic seconds and never accesses native memory.</summary>
public sealed class RestoreController(ulong characterId, CharacterMemory memory)
{
    public const double StableSeconds = 1.2;
    public const double ResultSeconds = 2.0;
    public const double MissingGraceSeconds = 0.8;
    public const int MaximumAttempts = 3;

    private bool wasWearing;
    private bool presenceObserved;
    private bool armed;
    private bool episode;
    private bool awaiting;
    private uint currentId;
    private uint ignoredId;
    private double? missingSince;
    private double? stableSince;
    private double? clearSince;
    private double lastAttempt;
    private string fault = "";

    public ulong CharacterId { get; } = characterId;
    public CharacterMemory Memory { get; } = memory;
    public Interruption Sources { get; private set; }
    public int Attempts { get; private set; }
    public bool HasPendingRequest => episode;
    public bool ConfigurationChanged { get; private set; }
    public RestorePhase Phase { get; private set; }
    public string Status { get; private set; } = "等待玩家佩戴一次配饰";
    public string LastResult { get; private set; } = "尚未请求恢复";

    public void AcknowledgeConfiguration() => ConfigurationChanged = false;

    public void Pause()
    {
        SetPause(true);
        armed = false;
        CancelEpisode();
        Phase = RestorePhase.Paused;
        Status = "已暂停；再次手动佩戴配饰或点击继续可恢复";
    }

    public void RequestRestore()
    {
        SetPause(false);
        fault = "";
        CancelEpisode();
        if (Memory.LastAccessoryId == 0)
        {
            Status = "没有记忆，请先手动佩戴一次配饰";
            return;
        }

        armed = true;
        episode = true;
        Sources = Interruption.Manual;
        Status = "已请求恢复，等待可用";
    }

    public void ClearMemory()
    {
        ignoredId = currentId;
        Memory.LastAccessoryId = 0;
        SetPause(false);
        ConfigurationChanged = true;
        armed = false;
        fault = "";
        missingSince = null;
        CancelEpisode();
        Status = "记忆已清除；下次重新佩戴配饰时记录";
    }

    public void StopForError(string message)
    {
        var attempts = Attempts;
        fault = message;
        CancelEpisode();
        Attempts = attempts;
        armed = false;
        Phase = RestorePhase.Failed;
        Status = message;
        LastResult = message;
    }

    public void Defer(string reason)
    {
        stableSince = null;
        Status = reason;
    }

    // Called only after the adapter actually invokes UseAction, even when it returns false.
    public void Submitted(double now, bool accepted)
    {
        Attempts++;
        lastAttempt = now;
        awaiting = true;
        stableSince = null;
        Phase = RestorePhase.WaitingForResult;
        LastResult = accepted ? "动作请求已接受，等待实际佩戴" : "动作请求返回失败，等待状态确认";
        Status = LastResult;
    }

    public RestoreRequest? Update(GameSnapshot snapshot, RestoreOptions options, double now)
    {
        if (snapshot.CharacterId != CharacterId)
        {
            stableSince = null;
            Phase = RestorePhase.Suspended;
            Status = "等待当前角色数据就绪";
            return null;
        }

        var active = snapshot.Interruptions;
        // Observe the interruption while the accessory is still visible: it can disappear
        // before the next valid local player object exists.
        if (options.Enabled && !Memory.PausedByUser && armed && fault.Length == 0 && active != 0)
        {
            episode = true;
            Sources |= active;
        }

        if (snapshot.AccessoryId is > 0)
        {
            var id = snapshot.AccessoryId.Value;
            currentId = id;
            var appeared = !wasWearing;
            var previouslyObserved = presenceObserved;
            var changed = id != Memory.LastAccessoryId;
            presenceObserved = true;
            wasWearing = true;
            missingSince = null;
            if (id != ignoredId)
            {
                if (changed)
                {
                    Memory.LastAccessoryId = id;
                    ConfigurationChanged = true;
                }
                if ((appeared && previouslyObserved) || changed)
                {
                    SetPause(false);
                    fault = "";
                }
                armed = !Memory.PausedByUser;
                // Existing/new actual accessories always win over queued preferences.
                // During a live interruption retain intent until it has really ended.
                if (changed || appeared || active == Interruption.None)
                {
                    if (episode || awaiting)
                        LastResult = changed ? "已采用新佩戴的配饰，取消旧请求" : "已确认配饰实际佩戴";
                    CancelEpisode();
                }
                if (options.Enabled && armed && active != 0 && fault.Length == 0)
                {
                    episode = true;
                    Sources |= active;
                }
            }
            return ShowPassiveStatus(options);
        }

        if (snapshot.AccessoryId == 0)
        {
            presenceObserved = true;
            currentId = 0;
            ignoredId = 0;
            if (wasWearing)
            {
                wasWearing = false;
                missingSince = now;
            }
        }

        if (!options.Enabled)
        {
            CancelEpisode();
            armed = false;
            missingSince = null;
            Phase = RestorePhase.Disabled;
            Status = "自动恢复已关闭，仍可记录玩家实际使用的配饰";
            return null;
        }
        if (Memory.PausedByUser || fault.Length > 0)
            return ShowPassiveStatus(options);

        if (!episode && armed && active != 0)
        {
            episode = true;
            Sources |= active;
        }
        if (!episode)
        {
            if (armed && missingSince.HasValue && now - missingSince.Value >= MissingGraceSeconds)
            {
                Pause();
                Status = "配饰已移除；未识别到中断，已暂停自动恢复";
            }
            else
            {
                Phase = RestorePhase.Observing;
                Status = Memory.LastAccessoryId == 0 ? "请先手动佩戴一次配饰" : "已记忆，等待配饰使用或有效中断";
            }
            return null;
        }

        if (active != 0 || snapshot.AccessoryId is null || snapshot.BlockReason.Length > 0)
        {
            stableSince = null;
            clearSince = null;
            Phase = awaiting ? RestorePhase.WaitingForResult : RestorePhase.Suspended;
            Status = snapshot.BlockReason.Length > 0 ? snapshot.BlockReason : "等待中断结束与角色状态就绪";
            return null;
        }

        // Allow flags to settle across zone/duty handoffs before rejecting a disabled trigger.
        clearSince ??= now;
        if ((Sources & (options.Triggers | Interruption.Manual)) == 0)
        {
            Phase = RestorePhase.Suspended;
            Status = "此次中断对应的恢复选项未开启";
            if (now - clearSince.Value >= StableSeconds)
            {
                CancelEpisode();
                armed = false;
                missingSince = null;
            }
            return null;
        }

        if (awaiting)
        {
            Phase = RestorePhase.WaitingForResult;
            Status = "等待配饰佩戴结果";
            if (now - lastAttempt < ResultSeconds)
                return null;
            awaiting = false;
            if (Attempts >= MaximumAttempts)
            {
                StopForError("本轮恢复失败三次，已停止；可点击立即恢复重试");
                return null;
            }
        }

        if (snapshot.UnavailableReason.Length > 0)
        {
            stableSince = null;
            Phase = RestorePhase.Suspended;
            Status = snapshot.UnavailableReason;
            return null;
        }

        stableSince ??= now;
        Phase = RestorePhase.Stabilizing;
        Status = "状态已可用，等待稳定后恢复";
        if (now - stableSince.Value < StableSeconds)
            return null;
        return new RestoreRequest(CharacterId, Memory.LastAccessoryId);
    }

    private RestoreRequest? ShowPassiveStatus(RestoreOptions options)
    {
        if (!options.Enabled)
        {
            CancelEpisode();
            armed = false;
            Phase = RestorePhase.Disabled;
            Status = "自动恢复已关闭";
        }
        else if (Memory.PausedByUser)
        {
            Phase = RestorePhase.Paused;
            Status = "已暂停；再次手动佩戴配饰或点击继续可恢复";
        }
        else if (fault.Length > 0)
        {
            Phase = RestorePhase.Failed;
            Status = fault;
        }
        else
        {
            Phase = episode ? RestorePhase.Suspended : RestorePhase.Observing;
            Status = episode ? "已记录中断，等待允许恢复" : "正在观察当前配饰";
        }
        return null;
    }

    private void SetPause(bool paused)
    {
        if (Memory.PausedByUser == paused) return;
        Memory.PausedByUser = paused;
        ConfigurationChanged = true;
    }

    private void CancelEpisode()
    {
        episode = false;
        awaiting = false;
        Sources = Interruption.None;
        Attempts = 0;
        stableSince = null;
        clearSince = null;
    }
}
