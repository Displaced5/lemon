namespace ClothingPairing.Core;

// Argument order verified against pinned OmenTools GlassesCommand / ExecuteCommandFlag.
public readonly record struct FacewearEquipCommand(int Slot, int GlassesId)
{
    public const int CommandId = 455;
    public static FacewearEquipCommand Create(int slot, uint glassesId)
    {
        // CN exposes one usable facewear position. A second native array element is
        // not evidence of a player-usable slot, and is never a separate colour slot.
        if (slot != 0) throw new ArgumentOutOfRangeException(nameof(slot));
        if (glassesId is 0 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(glassesId));
        return new(slot, (int)glassesId);
    }
}
