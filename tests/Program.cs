using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ClothingPairing.Core;
using Newtonsoft.Json;

// All character identifiers and accessory IDs below are synthetic test fixtures.
// No player configuration or captured session data is included.
var failures = new List<string>();
var passed = 0;
void Test(string name, Action body)
{
    try { body(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures.Add(name + ": " + ex.Message); Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
void Check(bool value, string message = "assertion failed")
{
    if (!value) throw new InvalidOperationException(message);
}
void No(RestoreRequest? request) => Check(request is null, "unexpected action request");
void Yes(RestoreRequest? request, uint id = 10) => Check(request is { AccessoryId: var actual } && actual == id, "missing/wrong action request");
RestoreController New(uint remembered = 0, bool paused = false) => new(101,
    new CharacterMemory { LastAccessoryId = remembered, PausedByUser = paused });
GameSnapshot S(uint? accessory, Interruption active = 0, string block = "", string gate = "", ulong character = 101) =>
    new(character, accessory, active, block, gate);
var dutyOnly = new RestoreOptions(true, Interruption.Duty);
var all = new RestoreOptions(true, Interruption.Duty | Interruption.Mount | Interruption.Combat | Interruption.Zone);
RestoreRequest? Tick(RestoreController c, double time, uint? id = 0, Interruption active = 0,
    RestoreOptions? options = null, string block = "", string gate = "", ulong character = 101) =>
    c.Update(S(id, active, block, gate, character), options ?? dutyOnly, time);
RestoreController Interrupted(Interruption source = Interruption.Duty, RestoreOptions? options = null)
{
    var c = New(); Tick(c, 0, 10, options: options); Tick(c, 1, null, source, options);
    Tick(c, 100, 0, source, options); return c;
}

Test("fresh load does not choose an accessory", () => { var c = New(); No(Tick(c, 0)); No(Tick(c, 600)); Check(c.Memory.LastAccessoryId == 0); });
Test("only real nonzero accessories update memory", () =>
{
    var c = New(); Tick(c, 0, 10); Tick(c, 1, null); Tick(c, 2, 0);
    Check(c.Memory.LastAccessoryId == 10); Check(c.ConfigurationChanged);
    c.AcknowledgeConfiguration(); Tick(c, 2.1, 0); Check(!c.ConfigurationChanged);
});
Test("duty exit waits for stability", () =>
{
    var c = Interrupted(); No(Tick(c, 101)); No(Tick(c, 102)); Yes(Tick(c, 102.3));
});
Test("long duty preserves intent without attempts", () =>
{
    var c = Interrupted(); No(Tick(c, 15000, 0, Interruption.Duty)); Check(c.Attempts == 0 && c.HasPendingRequest);
    No(Tick(c, 15001)); Yes(Tick(c, 15002.3));
});
Test("zone before duty does not lose the pre-entry accessory", () =>
{
    var c = New(); Tick(c, 0, 10); Tick(c, 1, null, Interruption.Zone); Tick(c, 2, 0, Interruption.Zone | Interruption.Duty);
    Tick(c, 3, 0, Interruption.Duty); No(Tick(c, 500)); Yes(Tick(c, 501.3));
});
Test("duty flag arriving during disappearance grace is attributed", () =>
{
    var c = New(); Tick(c, 0, 10); Tick(c, 1); Tick(c, 1.3, 0, Interruption.Duty);
    No(Tick(c, 100)); Yes(Tick(c, 101.3)); Check(!c.Memory.PausedByUser);
});
Test("unknown unequip pauses and keeps history", () =>
{
    var c = New(); Tick(c, 0, 10); No(Tick(c, 1)); No(Tick(c, 1.9));
    Check(c.Memory.PausedByUser && c.Memory.LastAccessoryId == 10); No(Tick(c, 5, 0, Interruption.Duty)); No(Tick(c, 50));
});
Test("manual unequip before duty never produces a duty restore", () =>
{
    var c = New(); Tick(c, 0, 10); Tick(c, 1); Tick(c, 2); Tick(c, 3, 0, Interruption.Duty);
    No(Tick(c, 100)); No(Tick(c, 103)); Check(!c.HasPendingRequest);
});
Test("manual equip resumes and changes preference", () =>
{
    var c = New(10, true); Tick(c, 0); Tick(c, 1, 20);
    Check(c.Memory.LastAccessoryId == 20 && !c.Memory.PausedByUser);
    Tick(c, 2, 0, Interruption.Duty); Tick(c, 100); Yes(Tick(c, 101.3), 20);
});
Test("new accessory wins over old queued request", () =>
{
    var c = Interrupted(); Tick(c, 101, 20); No(Tick(c, 102, 20));
    Check(!c.HasPendingRequest && c.Memory.LastAccessoryId == 20);
});
Test("disabled mount trigger does not recover later from stale memory", () =>
{
    var c = Interrupted(Interruption.Mount); No(Tick(c, 101)); No(Tick(c, 102.3));
    No(Tick(c, 103, 0, Interruption.Duty)); No(Tick(c, 200)); No(Tick(c, 202));
    Check(!c.HasPendingRequest);
});
foreach (var source in new[] { Interruption.Mount, Interruption.Combat, Interruption.Zone })
    Test($"optional {source} trigger restores when enabled", () =>
    {
        var c = Interrupted(source, all); No(Tick(c, 101, options: all)); Yes(Tick(c, 102.3, options: all));
    });
Test("overlapping sources restore when any selected source matches", () =>
{
    var c = Interrupted(Interruption.Zone); Tick(c, 110, 0, Interruption.Mount | Interruption.Duty);
    No(Tick(c, 120)); Yes(Tick(c, 121.3));
});
Test("all interruptions must end before restoration", () =>
{
    var c = Interrupted(Interruption.Duty | Interruption.Combat); No(Tick(c, 101, 0, Interruption.Combat));
    No(Tick(c, 200)); Yes(Tick(c, 201.3));
});
Test("renewed blocker resets stability timer", () =>
{
    var c = Interrupted(); Tick(c, 101); No(Tick(c, 102, block: "剧情"));
    No(Tick(c, 103)); No(Tick(c, 104)); Yes(Tick(c, 104.3));
});
Test("unknown local player samples never clear history or submit", () =>
{
    var c = Interrupted(); No(Tick(c, 101, null)); No(Tick(c, 300, null));
    Check(c.Memory.LastAccessoryId == 10); No(Tick(c, 301)); Yes(Tick(c, 302.3));
});
Test("native denial does not consume attempt budget", () =>
{
    var c = Interrupted(); No(Tick(c, 101, gate: "不可用")); No(Tick(c, 900, gate: "不可用"));
    Check(c.Attempts == 0); No(Tick(c, 901)); Yes(Tick(c, 902.3));
});
Test("request waits for actual confirmation and never repeats immediately", () =>
{
    var c = Interrupted(); Tick(c, 101); Yes(Tick(c, 102.3)); c.Submitted(102.3, true);
    No(Tick(c, 102.4)); No(Tick(c, 104)); Tick(c, 104.1, 10); Check(!c.HasPendingRequest);
    No(Tick(c, 105, 10)); No(Tick(c, 106)); No(Tick(c, 107)); Check(c.Memory.PausedByUser);
});
Test("false action return also waits to avoid toggle spam", () =>
{
    var c = Interrupted(); Tick(c, 101); Yes(Tick(c, 102.3)); c.Submitted(102.3, false);
    No(Tick(c, 102.4)); No(Tick(c, 103)); Tick(c, 103.5, 10); Check(!c.HasPendingRequest);
});
Test("three actual attempts exhaust one episode", () =>
{
    var c = Interrupted(); Tick(c, 101); Yes(Tick(c, 102.3)); c.Submitted(102.3, true);
    No(Tick(c, 104.4)); Yes(Tick(c, 105.7)); c.Submitted(105.7, false);
    No(Tick(c, 107.8)); Yes(Tick(c, 109.1)); c.Submitted(109.1, true);
    No(Tick(c, 111.2)); Check(c.Phase == RestorePhase.Failed); No(Tick(c, 500)); Check(!c.HasPendingRequest);
});
Test("manual retry clears episode failure", () =>
{
    var c = New(10); c.StopForError("test fault"); c.RequestRestore(); No(Tick(c, 1)); Yes(Tick(c, 2.3));
});
Test("master disable cancels pending and reenabling does not use stale request", () =>
{
    var c = Interrupted(); No(Tick(c, 101, options: new(false, Interruption.Duty)));
    No(Tick(c, 102)); No(Tick(c, 105)); Check(!c.HasPendingRequest);
});
Test("pause cancels pending and is sticky across later interruptions", () =>
{
    var c = Interrupted(); c.Pause(); No(Tick(c, 101)); No(Tick(c, 102, 0, Interruption.Duty));
    No(Tick(c, 1000)); Check(c.Memory.PausedByUser && !c.HasPendingRequest);
});
Test("pause while visibly equipped does not resume on identical samples", () =>
{
    var c = New(); Tick(c, 0, 10); c.Pause(); Tick(c, 1, 10); Check(c.Memory.PausedByUser);
    Tick(c, 2); Tick(c, 3, 10); Check(!c.Memory.PausedByUser);
});
Test("persistent pause survives reload while the same accessory remains visible", () =>
{
    var c = New(10, true); No(Tick(c, 0, 10)); Check(c.Memory.PausedByUser);
    No(Tick(c, 1, 0, Interruption.Duty)); No(Tick(c, 100)); No(Tick(c, 102));
    Check(c.Memory.PausedByUser && !c.HasPendingRequest);
});
Test("clear while equipped waits for actual re-equip to remember again", () =>
{
    var c = New(); Tick(c, 0, 10); c.ClearMemory(); Tick(c, 1, 10); Tick(c, 2, 10);
    Check(c.Memory.LastAccessoryId == 0); Tick(c, 3); Tick(c, 4, 10); Check(c.Memory.LastAccessoryId == 10);
});
Test("new session never restores history on login", () =>
{
    var c = New(10); No(Tick(c, 0)); No(Tick(c, 60)); No(Tick(c, 61, 0, Interruption.Duty)); No(Tick(c, 200));
    Check(!c.HasPendingRequest);
});
Test("manual request restores remembered ID", () =>
{
    var c = New(10, true); c.RequestRestore(); No(Tick(c, 0)); Yes(Tick(c, 1.3)); Check(!c.Memory.PausedByUser);
});
Test("manual request still obeys all blockers", () =>
{
    var c = New(10); c.RequestRestore(); No(Tick(c, 0, block: "死亡")); No(Tick(c, 100, block: "死亡"));
    No(Tick(c, 101)); Yes(Tick(c, 102.3));
});
Test("no history means no manual action", () => { var c = New(); c.RequestRestore(); No(Tick(c, 0)); No(Tick(c, 5)); });
Test("cross-character samples cannot record or submit", () =>
{
    var c = Interrupted(); No(Tick(c, 101, 20, character: 202)); No(Tick(c, 103, character: 202));
    Check(c.Memory.LastAccessoryId == 10);
});
Test("another character has independent history", () =>
{
    var c = Interrupted(); var d = new RestoreController(202, new CharacterMemory());
    No(d.Update(S(20, character: 202), all, 0)); Check(d.Memory.LastAccessoryId == 20 && c.Memory.LastAccessoryId == 10);
});
Test("disabling selected source cancels pending episode", () =>
{
    var c = Interrupted(); No(Tick(c, 101, options: new(true, 0))); No(Tick(c, 103, options: new(true, 0)));
    Check(!c.HasPendingRequest);
});
Test("same accessory returning without action completes interruption", () =>
{
    var c = Interrupted(); No(Tick(c, 101, 10)); Check(!c.HasPendingRequest && c.Attempts == 0);
});
Test("flag flutter does not erase duty source", () =>
{
    var c = Interrupted(); Tick(c, 101); Tick(c, 101.5, 0, Interruption.Zone);
    Check((c.Sources & Interruption.Duty) != 0); Tick(c, 102); Yes(Tick(c, 103.3));
});
Test("master off still records actual accessory", () =>
{
    var c = New(); No(Tick(c, 0, 20, options: new(false, 0))); Check(c.Memory.LastAccessoryId == 20);
    Tick(c, 1, options: new(false, 0)); No(Tick(c, 2)); No(Tick(c, 5));
});
Test("readiness recheck failure restarts stabilization", () =>
{
    var c = Interrupted(); Tick(c, 101); Yes(Tick(c, 102.3)); c.Defer("状态改变");
    No(Tick(c, 102.4)); Yes(Tick(c, 103.7)); Check(c.Attempts == 0);
});

Test("v1 JSON migrates without changing fashion IDs, pause or triggers", () =>
{
    var config = JsonConvert.DeserializeObject<SettingsData>("""
        {"Version":1,"Enabled":false,"RestoreAfterDuty":false,"RestoreAfterZone":true,
         "RespectAutoUmbrella":false,"Characters":{"101":{"LastAccessoryId":10,"PausedByUser":true}}}
        """)!;
    config.Normalize();
    Check(config.Version == 2 && !config.Enabled && !config.RestoreAfterDuty && config.RestoreAfterZone);
    Check(!config.RespectAutoUmbrella && config.FacewearEnabled && config.FashionEnabled);
    Check(config.Characters[101].LastAccessoryId == 10 && config.Characters[101].PausedByUser);
    Check(config.GetMemory(101, AccessoryKind.Facewear).LastAccessoryId == 0);
});
Test("JSON roundtrip keeps fashion and one facewear memory independent", () =>
{
    var config = new SettingsData();
    config.GetMemory(101, AccessoryKind.Fashion).LastAccessoryId = 10;
    config.GetMemory(101, AccessoryKind.Facewear, 0).LastAccessoryId = 10;
    config.GetMemory(101, AccessoryKind.Facewear, 0).PausedByUser = true;
    config.FacewearEnabled = false;
    var loaded = JsonConvert.DeserializeObject<SettingsData>(JsonConvert.SerializeObject(config))!;
    loaded.Normalize();
    Check(loaded.Characters[101].LastAccessoryId == 10 && !loaded.Characters[101].PausedByUser);
    Check(loaded.FacewearCharacters[101].Slots[0].LastAccessoryId == 10 && loaded.FacewearCharacters[101].Slots[0].PausedByUser);
    Check(loaded.FacewearCharacters[101].Slots.Length == 1 && !loaded.FacewearEnabled);
});
Test("null or incomplete facewear config is repaired without losing existing first slot", () =>
{
    var config = JsonConvert.DeserializeObject<SettingsData>("""
        {"Version":2,"Characters":null,"FacewearCharacters":{
        "0":null,"101":{"Slots":[{"LastAccessoryId":77,"PausedByUser":true}]},
        "202":{"Slots":null},"303":{"Slots":[null,null]}}}
        """)!;
    config.Normalize();
    Check(!config.FacewearCharacters.ContainsKey(0));
    Check(config.FacewearCharacters[101].Slots[0].LastAccessoryId == 77 && config.FacewearCharacters[101].Slots[0].PausedByUser);
    Check(config.FacewearCharacters.Values.All(x => x.Slots.Length == 1 && x.Slots.All(s => s is not null)));
});
Test("future config is rejected before normalization", () =>
{
    var config = new SettingsData { Version = 99 }; var rejected = false;
    try { config.Normalize(); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected && config.Version == 99);
});
Test("category toggles are independent and master toggle gates both", () =>
{
    var config = new SettingsData { FacewearEnabled = false };
    Check(config.ToOptions(AccessoryKind.Fashion).Enabled && !config.ToOptions(AccessoryKind.Facewear).Enabled);
    config.FacewearEnabled = true; config.FashionEnabled = false;
    Check(!config.ToOptions(AccessoryKind.Fashion).Enabled && config.ToOptions(AccessoryKind.Facewear).Enabled);
    config.Enabled = false;
    Check(!config.ToOptions(AccessoryKind.Fashion).Enabled && !config.ToOptions(AccessoryKind.Facewear).Enabled);
});
Test("pausing facewear with same numeric ID leaves fashion restoration armed", () =>
{
    var config = new SettingsData();
    var fashion = new RestoreController(101, config.GetMemory(101, AccessoryKind.Fashion));
    var face = new RestoreController(101, config.GetMemory(101, AccessoryKind.Facewear));
    Tick(fashion, 0, 10); Tick(face, 0, 10); face.Pause();
    Tick(fashion, 1, 0, Interruption.Duty); Tick(face, 1, 0, Interruption.Duty);
    Tick(fashion, 100); No(Tick(face, 100)); Yes(Tick(fashion, 101.3)); No(Tick(face, 101.3));
    Check(face.Memory.PausedByUser && !fashion.Memory.PausedByUser);
});
Test("clearing facewear preserves fashion history", () =>
{
    var config = new SettingsData();
    var fashion = config.GetMemory(101, AccessoryKind.Fashion); fashion.LastAccessoryId = 10;
    var face0 = config.GetMemory(101, AccessoryKind.Facewear, 0); face0.LastAccessoryId = 20;
    new RestoreController(101, face0).ClearMemory();
    Check(face0.LastAccessoryId == 0 && fashion.LastAccessoryId == 10);
});
Test("legacy second position is discarded without changing real facewear colour or pause", () =>
{
    var config = JsonConvert.DeserializeObject<SettingsData>("""
        {"Version":2,"FacewearCharacters":{"101":{"Slots":[
        {"LastAccessoryId":77,"PausedByUser":true},{"LastAccessoryId":300,"PausedByUser":false}]}}}
        """)!;
    config.Normalize();
    var face = config.GetMemory(101, AccessoryKind.Facewear);
    Check(face.LastAccessoryId == 77 && face.PausedByUser && config.FacewearCharacters[101].Slots.Length == 1);
    var rejected = false;
    try { config.GetMemory(101, AccessoryKind.Facewear, 1); }
    catch (ArgumentOutOfRangeException) { rejected = true; }
    Check(rejected);
});
Test("facewear remembers full variant ID after colour change", () =>
{
    var face = New(); Tick(face, 0, 101); Tick(face, 1, 102);
    Tick(face, 2, 0, Interruption.Duty); Tick(face, 100); Yes(Tick(face, 101.3), 102);
});
Test("facewear retained throughout duty is never redundantly re-equipped", () =>
{
    var face = New(); Tick(face, 0, 101); No(Tick(face, 1, 101, Interruption.Duty));
    No(Tick(face, 400, 101, Interruption.Duty)); No(Tick(face, 500, 101)); No(Tick(face, 502, 101));
    Check(!face.HasPendingRequest && face.Attempts == 0);
});
Test("facewear command preserves variant and rejects second position and invalid inputs", () =>
{
    var command = FacewearEquipCommand.Create(0, 345);
    Check(command.Slot == 0 && command.GlassesId == 345);
    foreach (var input in new[] { (-1, 10u), (1, 10u), (2, 10u), (0, 0u), (0, 65536u) })
    {
        var rejected = false;
        try { FacewearEquipCommand.Create(input.Item1, input.Item2); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected);
    }
});
Test("both categories become eligible but only one request is selected per interval", () =>
{
    var fashion = Interrupted(); var face = Interrupted(); Tick(fashion, 101); Tick(face, 101);
    var requests = new[] { Tick(fashion, 102.3), Tick(face, 102.3) }; Check(requests.All(r => r.HasValue));
    var arbiter = new SubmissionArbiter(); Check(arbiter.Select([true, true], 102.3) == 0);
    arbiter.Submitted(102.3); Check(arbiter.Select([false, true], 102.4) is null);
    Check(arbiter.Select([false, true], 103) == 1);
});
Test("request arbitration rotates all slots and skips failed/unavailable categories", () =>
{
    var arbiter = new SubmissionArbiter();
    Check(arbiter.Select([true, true, true], 0) == 0); arbiter.Submitted(0);
    Check(arbiter.Select([true, true, true], 1) == 1); arbiter.Submitted(1);
    Check(arbiter.Select([true, true, true], 2) == 2); arbiter.Submitted(2);
    Check(arbiter.Select([false, true, false], 3) == 1);
    Check(arbiter.Select([false, false, false], 4) is null);
});
Test("facewear retry failure does not cancel another category's request", () =>
{
    var fashion = Interrupted(); var face = Interrupted(); face.StopForError("facewear unavailable");
    Tick(fashion, 101); Yes(Tick(fashion, 102.3)); No(Tick(face, 102.3));
    Check(fashion.HasPendingRequest && face.Phase == RestorePhase.Failed);
});
Test("facewear memories are also isolated by character", () =>
{
    var config = new SettingsData(); config.GetMemory(101, AccessoryKind.Facewear).LastAccessoryId = 77;
    Check(config.GetMemory(202, AccessoryKind.Facewear).LastAccessoryId == 0);
    Check(config.GetMemory(101, AccessoryKind.Facewear).LastAccessoryId == 77);
});

Test("v2 config enables new facewear triggers without changing memories or pause", () =>
{
    var config = JsonConvert.DeserializeObject<SettingsData>("""
        {"Version":2,"RestoreAfterDuty":false,"FacewearCharacters":{
        "101":{"Slots":[{"LastAccessoryId":77,"PausedByUser":true}]}}}
        """)!;
    config.Normalize();
    Check(config.RestoreFacewearAfterClassJob && config.RestoreFacewearAfterGearset && !config.RestoreAfterDuty);
    Check(config.GetMemory(101, AccessoryKind.Facewear).LastAccessoryId == 77 &&
        config.GetMemory(101, AccessoryKind.Facewear).PausedByUser);
});
Test("equipment triggers only apply to facewear and persist explicit disabled preferences", () =>
{
    var config = new SettingsData();
    var equipment = Interruption.ClassJob | Interruption.Gearset;
    Check((config.ToOptions(AccessoryKind.Facewear).Triggers & equipment) == equipment);
    Check((config.ToOptions(AccessoryKind.Fashion).Triggers & equipment) == 0);
    config.RestoreFacewearAfterClassJob = false;
    var loaded = JsonConvert.DeserializeObject<SettingsData>(JsonConvert.SerializeObject(config))!;
    Check(!loaded.RestoreFacewearAfterClassJob && loaded.RestoreFacewearAfterGearset);
    Check((loaded.ToOptions(AccessoryKind.Facewear).Triggers & equipment) == Interruption.Gearset);
});
Test("first equipment observation and repeated identical context do not trigger", () =>
{
    var tracker = new EquipmentChangeTracker();
    Check(tracker.Observe(101, 24, 0, 0) == 0);
    Check(tracker.Observe(101, 24, 0, 50) == 0);
});
Test("same job different gearset is detected independently of class job", () =>
{
    var tracker = new EquipmentChangeTracker(); tracker.Observe(101, 24, 0, 0);
    Check(tracker.Observe(101, 24, 1, 1) == Interruption.Gearset);
    Check(tracker.Observe(101, 24, 1, 2.4) == Interruption.Gearset);
    Check(tracker.Observe(101, 24, 1, 2.6) == 0);
});
Test("manual weapon job change is detected without a gearset change", () =>
{
    var tracker = new EquipmentChangeTracker(); tracker.Observe(101, 24, 0, 0);
    Check(tracker.Observe(101, 25, 0, 1) == Interruption.ClassJob);
});
Test("rapid gearset and delayed job changes extend settling and preserve both sources", () =>
{
    var tracker = new EquipmentChangeTracker(); tracker.Observe(101, 24, 0, 0);
    Check(tracker.Observe(101, 24, 1, 1) == Interruption.Gearset);
    Check(tracker.Observe(101, 25, 1, 2) == (Interruption.Gearset | Interruption.ClassJob));
    Check(tracker.Observe(101, 25, 1, 3) != 0);
    Check(tracker.Observe(101, 25, 1, 3.6) == 0);
});
Test("temporary missing equipment data neither triggers nor loses known context", () =>
{
    var tracker = new EquipmentChangeTracker(); tracker.Observe(101, 24, 0, 0);
    Check(tracker.Observe(101, 0, null, 1) == 0);
    Check(tracker.Observe(101, 24, -1, 2) == 0);
    Check(tracker.Observe(101, 24, 0, 3) == 0);
    Check(tracker.Observe(101, 25, 1, 4) == (Interruption.ClassJob | Interruption.Gearset));
});
Test("logout character change and explicit reset cannot manufacture equipment triggers", () =>
{
    var tracker = new EquipmentChangeTracker(); tracker.Observe(101, 24, 0, 0);
    Check(tracker.Observe(202, 25, 1, 1) == 0);
    tracker.Observe(202, 26, 2, 2); Check(tracker.Observe(0, 0, null, 2.1) == 0);
    Check(tracker.Observe(202, 27, 3, 3) == 0);
    tracker.Observe(202, 28, 4, 4); tracker.Reset();
    Check(tracker.Observe(202, 29, 5, 4.1) == 0);
});
foreach (var source in new[] { Interruption.ClassJob, Interruption.Gearset })
{
    var faceOptions = new RestoreOptions(true, source);
    Test($"{source} restores missing facewear only after context and availability settle", () =>
    {
        var c = Interrupted(source, faceOptions);
        No(Tick(c, 101, options: faceOptions, block: "busy"));
        No(Tick(c, 102, options: faceOptions)); Yes(Tick(c, 103.3, options: faceOptions));
    });
    Test($"{source} respects manual removal pause and never arms stale login memories", () =>
    {
        var c = New(); Tick(c, 0, 10, options: faceOptions); Tick(c, 1, options: faceOptions);
        Tick(c, 2, options: faceOptions); Check(c.Memory.PausedByUser);
        No(Tick(c, 3, active: source, options: faceOptions)); No(Tick(c, 6, options: faceOptions));
        var reloaded = New(10); Tick(reloaded, 0, options: faceOptions);
        Tick(reloaded, 1, active: source, options: faceOptions); No(Tick(reloaded, 5, options: faceOptions));
        Check(!reloaded.HasPendingRequest);
    });
    Test($"{source} retained or newly equipped facewear wins without duplicate submission", () =>
    {
        var c = New(); Tick(c, 0, 10, options: faceOptions);
        No(Tick(c, 1, 10, source, faceOptions)); No(Tick(c, 3, 10, options: faceOptions));
        Check(!c.HasPendingRequest);
        Tick(c, 4, 0, source, faceOptions); Tick(c, 5, 20, source, faceOptions);
        No(Tick(c, 7, 20, options: faceOptions)); Check(c.Memory.LastAccessoryId == 20 && !c.HasPendingRequest);
    });
    Test($"disabled {source} cancels pending restoration and does not leak into later duty", () =>
    {
        var c = Interrupted(source, faceOptions); No(Tick(c, 101)); No(Tick(c, 103));
        No(Tick(c, 104, active: Interruption.Duty)); No(Tick(c, 110)); No(Tick(c, 112));
        Check(!c.HasPendingRequest);
    });
}
Test("gearset observation after facewear disappears still restores within disappearance grace", () =>
{
    var config = new SettingsData(); var options = config.ToOptions(AccessoryKind.Facewear);
    var tracker = new EquipmentChangeTracker(); var c = New();
    Tick(c, 0, 10, tracker.Observe(101, 24, 0, 0), options);
    Tick(c, 1, 0, tracker.Observe(101, 24, 0, 1), options);
    Tick(c, 1.3, 0, tracker.Observe(101, 24, 1, 1.3), options);
    No(Tick(c, 2, 0, tracker.Observe(101, 24, 1, 2), options));
    No(Tick(c, 3, 0, tracker.Observe(101, 24, 1, 3), options));
    Yes(Tick(c, 4.3, 0, tracker.Observe(101, 24, 1, 4.3), options));
    Check(!c.Memory.PausedByUser);
});

// Read the actual compiled assembly without executing a plugin or loading native functions.
if (args.Length == 1)
{
    Test("compiled plugin metadata matches required identity and API contracts", () =>
    {
        using var file = File.OpenRead(args[0]); using var pe = new PEReader(file);
        var metadata = pe.GetMetadataReader(); var assembly = metadata.GetAssemblyDefinition();
        Check(metadata.GetString(assembly.Name) == "ClothingPairing"); Check(assembly.Version == new Version(1, 1, 3, 0));
        var plugin = metadata.TypeDefinitions.Select(metadata.GetTypeDefinition)
            .Single(t => metadata.GetString(t.Name) == "Plugin");
        Check(plugin.GetInterfaceImplementations().Any());
        var refs = metadata.AssemblyReferences.Select(h => metadata.GetString(metadata.GetAssemblyReference(h).Name)).ToArray();
        Check(refs.Contains("Dalamud") && refs.Contains("FFXIVClientStructs") && refs.Contains("Lumina.Excel"));
        Check(!refs.Contains("OmenTools") && !refs.Contains("ECommons"));
    });
}

Console.WriteLine($"{passed} passed; {failures.Count} failed.");
foreach (var failure in failures) Console.Error.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;
