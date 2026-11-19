namespace ClothingPairing.Core;

public class SettingsData
{
    public int Version { get; set; } = 2;
    public bool Enabled { get; set; } = true;
    public bool FashionEnabled { get; set; } = true;
    public bool FacewearEnabled { get; set; } = true;
    public bool RestoreAfterDuty { get; set; } = true;
    public bool RestoreAfterMount { get; set; }
    public bool RestoreAfterCombat { get; set; }
    public bool RestoreAfterZone { get; set; }
    public bool RestoreFacewearAfterClassJob { get; set; } = true;
    public bool RestoreFacewearAfterGearset { get; set; } = true;
    public bool RespectAutoUmbrella { get; set; } = true;
    // Keep original v1 fashion memories and pauses unchanged during migration.
    public Dictionary<ulong, CharacterMemory> Characters { get; set; } = [];
    public Dictionary<ulong, FacewearMemory> FacewearCharacters { get; set; } = [];

    public RestoreOptions ToOptions(AccessoryKind kind) => new(
        Enabled && (kind == AccessoryKind.Fashion ? FashionEnabled : FacewearEnabled),
        (RestoreAfterDuty ? Interruption.Duty : 0) |
        (RestoreAfterMount ? Interruption.Mount : 0) |
        (RestoreAfterCombat ? Interruption.Combat : 0) |
        (RestoreAfterZone ? Interruption.Zone : 0) |
        (kind == AccessoryKind.Facewear && RestoreFacewearAfterClassJob ? Interruption.ClassJob : 0) |
        (kind == AccessoryKind.Facewear && RestoreFacewearAfterGearset ? Interruption.Gearset : 0));

    public CharacterMemory GetMemory(ulong characterId, AccessoryKind kind, int slot = 0)
    {
        if (characterId == 0) throw new ArgumentOutOfRangeException(nameof(characterId));
        if (kind == AccessoryKind.Fashion)
        {
            if (!Characters.TryGetValue(characterId, out var memory))
                Characters[characterId] = memory = new CharacterMemory();
            return memory;
        }
        if (slot != 0) throw new ArgumentOutOfRangeException(nameof(slot));
        if (!FacewearCharacters.TryGetValue(characterId, out var facewear))
            FacewearCharacters[characterId] = facewear = new FacewearMemory();
        return facewear.Slots[slot];
    }

    public void Normalize()
    {
        if (Version > 2)
            throw new InvalidOperationException("配置版本高于本插件支持范围，请使用对应的新版本插件。");
        Characters ??= [];
        FacewearCharacters ??= [];
        foreach (var id in Characters.Where(p => p.Key == 0 || p.Value is null).Select(p => p.Key).ToArray())
            Characters.Remove(id);
        foreach (var id in FacewearCharacters.Where(p => p.Key == 0 || p.Value is null).Select(p => p.Key).ToArray())
            FacewearCharacters.Remove(id);
        foreach (var memory in FacewearCharacters.Values) memory.Normalize();
        Version = 2;
    }
}
