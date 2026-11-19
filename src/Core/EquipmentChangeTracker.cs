namespace ClothingPairing.Core;

/// <summary>Observes confirmed context changes, never treats the first session sample as a change.</summary>
public sealed class EquipmentChangeTracker
{
    public const double SettleSeconds = 1.5;
    private ulong characterId;
    private uint? classJobId;
    private int? gearsetIndex;
    private Interruption sources;
    private double until;

    public void Reset()
    {
        characterId = 0;
        classJobId = null;
        gearsetIndex = null;
        sources = Interruption.None;
        until = 0;
    }

    public Interruption Observe(ulong character, uint job, int? gearset, double now)
    {
        if (character == 0) { Reset(); return Interruption.None; }
        if (characterId != character)
        {
            Reset();
            characterId = character;
        }
        if (now >= until) sources = Interruption.None;
        var changed = Interruption.None;
        if (job != 0)
        {
            if (classJobId.HasValue && classJobId != job) changed |= Interruption.ClassJob;
            classJobId = job;
        }
        // null means temporarily unavailable; -1 means no selected gearset.
        // Both may occur during transitions and cannot manufacture a gearset switch.
        if (gearset is >= 0 and < 100)
        {
            if (gearsetIndex.HasValue && gearsetIndex != gearset) changed |= Interruption.Gearset;
            gearsetIndex = gearset;
        }
        if (changed != Interruption.None)
        {
            sources |= changed;
            until = now + SettleSeconds;
        }
        return now < until ? sources : Interruption.None;
    }
}
