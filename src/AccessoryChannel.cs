using ClothingPairing.Core;

namespace ClothingPairing;

internal sealed class AccessoryChannel(AccessoryKind kind, int slot, string name)
{
    public AccessoryKind Kind { get; } = kind;
    public int Slot { get; } = slot;
    public string Name { get; } = name;
    public RestoreController? Controller { get; set; }
    public GameSnapshot Snapshot { get; set; }
    public string Error { get; set; } = "";
    public string LastDiagnostic { get; set; } = "";
}
