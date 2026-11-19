namespace ClothingPairing.Core;

public enum AccessoryKind { Fashion, Facewear }

[Flags]
public enum Interruption
{
    None = 0,
    Duty = 1,
    Mount = 2,
    Combat = 4,
    Zone = 8,
    Manual = 16,
    ClassJob = 32,
    Gearset = 64,
}

public sealed class CharacterMemory
{
    public uint LastAccessoryId { get; set; }
    public bool PausedByUser { get; set; }
}

public sealed class FacewearMemory
{
    // Keep the legacy property name for config compatibility, but only retain
    // the first, player-usable position. The old second entry was unsupported.
    public CharacterMemory[] Slots { get; set; } = [new()];

    public void Normalize()
    {
        var previous = Slots ?? [];
        Slots = [previous.Length > 0 && previous[0] is not null ? previous[0] : new CharacterMemory()];
    }
}

public readonly record struct RestoreOptions(bool Enabled, Interruption Triggers);

// null AccessoryId means unavailable or inconsistent data, never "collected"/"none".
public readonly record struct GameSnapshot(
    ulong CharacterId,
    uint? AccessoryId,
    Interruption Interruptions,
    string BlockReason = "",
    string UnavailableReason = "",
    uint? NativeStatus = null,
    bool AutoUmbrellaConfigured = false,
    uint RawAccessoryId = 0,
    bool UsingAccessory = false,
    bool Transitioning = false);

public readonly record struct RestoreRequest(ulong CharacterId, uint AccessoryId);

public enum RestorePhase
{
    Observing,
    Suspended,
    Stabilizing,
    WaitingForResult,
    Paused,
    Failed,
    Disabled,
}
