using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Setup;
using Game.Saves;
using Game.Units;

// Only the native appearance/asset bridge is adapted here. The detached DTO, history
// capture/validation/reconstruction, fog and memory owners, identities and units are real.
namespace UnityEngine { public struct Quaternion { public float x,y,z,w; } }
namespace Game.Saves
{
    internal static class MatchSaveAssets
    {
        internal static string SpriteKey(UnityEngine.Sprite value) => value == null ? null : throw new Exception("Native art outside portable history scope");
        internal static UnityEngine.Sprite ResolveSprite(string key) => string.IsNullOrEmpty(key) ? null : throw new Exception("Native art outside portable history scope");
    }
    internal static partial class MatchPresentationCodec
    {
        private static MatchFrozenVisualData CaptureVisual(MapObjectVisual value) => value.IsFrozenRememberedAppearance ? new MatchFrozenVisualData { IsFrozen = true } : throw new Exception("Expected frozen portable appearance");
        private static void ValidateVisual(MatchFrozenVisualData value) { if (!value.IsFrozen) throw new ArgumentException("Not frozen"); }
    }
}
internal static class PresentationCodecChecks
{
    private static int checks;
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
    private static void Reject(Action action, string label)
    { try { action(); } catch (ArgumentException) { checks++; return; } throw new Exception("Expected rejection: "+label); }
    private static T Call<T>(string method, params object[] arguments) => (T)typeof(MatchPresentationCodec).GetMethod(method, BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,arguments);
    private static int Main()
    {
        var human = new PlayerSetupData { PlayerId="human", IsHuman=true };
        var enemy = new PlayerSetupData { PlayerId="enemy" };
        PlayerSetupData Player(string id) => id=="human"?human:id=="enemy"?enemy:null;
        var config = new GameConfig();
        var live = new ArmyData { Owner=enemy, Name="Observed owner", Hex=new HexCoord(1,0), IsAirfield=true };
        var unit = new UnitData { Owner=enemy, Attack=8, Defense=2, HitPointsCurrent=3, HitPointsMax=9,
            MoveMax=6, MoveCurrent=2, Fate=1, FateMax=4, IsAviation=true, Name="Old roster" };
        live.Members.Add(unit); HumanVisualMemory.ObserveArmy(human,live,live.Hex);
        Check(HumanVisualMemory.TryGetArmySighting(human,live.Id,out var sighting),"initial observation");
        var remembered=Call<MatchRememberedArmyData>("CaptureArmy",live.Id,live.Hex,sighting.Army,config);
        var data=new MatchPresentationData { CurrentViewerId="human" };
        data.Vision.Add(new MatchVisionData { PlayerId="human", EverSeen={new MatchHexData {Q=1},new MatchHexData {Q=2}}, Visible={new MatchHexData {Q=2}} });
        data.Memory.Add(new MatchHumanMemoryData { PlayerId="human", Armies={remembered} });
        data.Visuals.Add(new MatchFrozenVisualData { ViewerId="human",IsArmy=true,SourceArmyId=live.Id,Q=1,IsFrozen=true });
        data=JsonUtility.FromJson<MatchPresentationData>(JsonUtility.ToJson(data));
        var layout=new HexMapLayout { Cells=new[]{new MapLayoutCell {Q=1}} };
        var identity=MatchIdentityState.Capture();
        MatchPresentationCodec.Validate(data,config,Player,layout,identity); checks++;
        live.Owner=human; live.Name="Captured behind fog";unit.Attack=99;unit.Owner=human;live.Members.Clear();HumanVisualMemory.Clear();
        MatchPresentationCodec.RestoreKnowledge(data,config,Player);
        Check(HumanVisualMemory.TryGetArmySighting(human,live.Id,out var restored),"restored historical observation");
        Check(restored.Army.Id==-1 && !ArmyRegistry.IsRegistered(restored.Army),"display snapshot has no live identity");
        Check(restored.Army.Owner==enemy && restored.Army.Name=="Observed owner","old owner and name survive hidden capture");
        Check(restored.Army.IsAirfield && restored.Army.Members.Single().IsAviation,"aviation appearance facts");
        Check(restored.Army.Members.Single().Attack==8 && restored.Army.Members.Single().HitPointsCurrent==3,"old mutable stats remain independent");
        Check(restored.Army.Members.Single().MoveCurrent==2 && restored.Army.VisualSnapshotCurrentMovement==2,"frozen effective movement");
        Check(restored.Army.Members.Single().OriginalResourceCost==null && restored.Army.Members.Single().CapturedFrom==null,"null inline-cost and empty optional-string representation");
        Check(VisionSystem.IsVisible(human,new HexCoord(2,0)),"off-map peripheral visibility survives");
        var appearance=data.Visuals.Single();data.Visuals.Clear();
        Reject(()=>MatchPresentationCodec.Validate(data,config,Player,layout,identity),"missing appearance coverage");
        Check(HumanVisualMemory.TryGetArmySighting(human,live.Id,out _),"failed validation has no knowledge side effect");
        data.Visuals.Add(appearance);data.Visuals.Add(appearance);
        Reject(()=>MatchPresentationCodec.Validate(data,config,Player,layout,identity),"duplicate appearance coverage");
        data.Visuals.RemoveAt(1); data.Memory.Single().Armies.Single().Members.Single().RuntimeId=int.MaxValue;
        Reject(()=>MatchPresentationCodec.Validate(data,config,Player,layout,identity),"unissued historical unit identity");
        HumanVisualMemory.Clear();
        var neutral = new PlayerSetupData { PlayerId="historical-neutral", IsNeutral=true };
        var oldNeutral = new ArmyData { Owner=neutral, Hex=new HexCoord(1,0) };
        oldNeutral.Members.Add(new UnitData { Owner=neutral, CapturedFrom=enemy, Attack=3 });
        HumanVisualMemory.ObserveArmy(human,oldNeutral,oldNeutral.Hex);
        int neutralId=oldNeutral.Id;
        oldNeutral.Owner=human; oldNeutral.Members.Clear(); // only the old display snapshot retains neutral
        var selection=new HexSelectionController();
        var appearances=new RememberedMapVisualState();
        appearances.Armies.Add(new RememberedArmyVisualState { ViewerId="human", SourceArmyId=neutralId,
            Hex=new HexCoord(1,0), Appearance=new MapVisualSnapshotReference { TypeKey="portable-frozen",SnapshotKey="neutral" } });
        selection.RestoreRememberedVisuals(appearances,Player,key=>new MapObjectVisual { IsFrozenRememberedAppearance=true });
        var includedPlayers=new HashSet<PlayerSetupData>();
        var captured=MatchPresentationCodec.Capture(selection,config,
            army=>throw new Exception("A historical display army must not be included as a live entity"),
            member=>throw new Exception("A historical display unit must not be included as a live entity"),
            card=>throw new Exception("Historical display capture has no live card reference"),
            owner=>includedPlayers.Add(owner));
        Check(includedPlayers.Contains(neutral),"capture retains neutral owner referenced only by historical display snapshot");
        Check(includedPlayers.Contains(human) && includedPlayers.Contains(enemy),"capture includes viewer and historical captured-from owner");
        Check(captured.Memory.Single().Armies.Single().OwnerId=="historical-neutral","historical neutral DTO identity survives capture");
        Console.WriteLine("PASS: "+checks+" production detached presentation/history checks; native renderer codec excluded.");
        return 0;
    }
}
