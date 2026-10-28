using System;
using System.Linq;
using System.Reflection;
using Game.Map;
using Game.Saves;
using UnityEngine;
static class Program {
    const BindingFlags Fields=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Set(MapObjectVisual visual,string role,object value)=>typeof(MapObjectVisual).GetField(role,Fields).SetValue(visual,value);
    static T Codec<T>(string method,object value)=>(T)typeof(MatchPresentationCodec).GetMethod(method,BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new[]{value});
    static void Check(bool result,string label){if(!result)throw new Exception(label);Console.WriteLine("PASS "+label);}
    static SpriteRenderer Renderer(GameObject owner,string name){var child=new GameObject(name);child.transform.SetParent(owner.transform,false);var result=child.AddComponent<SpriteRenderer>();result.sprite=new Sprite{name=name};return result;}
    static void Main(){
        var live=new GameObject("observed army");var marker=live.AddComponent<MapObjectVisual>();
        var circle=Renderer(live,"circle-before");var icon=Renderer(live,"ground-before");var accent=Renderer(live,"accent-before");var auxiliary=Renderer(live,"aux-before");
        Set(marker,"innerCircle",circle);Set(marker,"objectImage",icon);Set(marker,"factionAccent",accent);Set(marker,"auxiliaryRenderers",new SpriteRenderer[]{null,auxiliary});
        marker.FreezeRememberedAppearance();
        var wire=Codec<MatchFrozenVisualData>("CaptureVisual",marker);
        wire=MatchSaveJson.Deserialize<MatchFrozenVisualData>(MatchSaveJson.Serialize(wire));
        var restored=Codec<MapObjectVisual>("RestoreVisual",wire);
        icon.sprite=new Sprite{name="air-after"};circle.color=new Color(.2f,.3f,.4f);accent.sprite=new Sprite{name="accent-after"};auxiliary.sprite=new Sprite{name="aux-after"};
        // Exact production operation on an imported army's next real observation.
        restored.CopyAppearanceFrom(marker);
        var renderers=restored.GetComponentsInChildren<SpriteRenderer>(true);
        Check(renderers[1].sprite.name=="air-after","re-observed icon replaces imported ground appearance");
        Check(renderers[0].color.r==.2f,"re-observed primary tint updates imported renderer");
        Check(renderers[2].sprite.name=="accent-after","re-observed accent updates its own renderer");
        Check(renderers[3].sprite.name=="aux-after","ordered auxiliary slots, including null, survive");
        restored.SetVisible(true);Check(renderers.All(r=>r.enabled),"all persisted visibility roles remain controlled");
        wire.InnerCircle=wire.Renderers.Count;
        try{Codec<MapObjectVisual>("RestoreVisual",wire);throw new Exception("accepted missing renderer role");}catch(TargetInvocationException e) when(e.InnerException is ArgumentException){Console.WriteLine("PASS invalid role rejected before reconstruction");}
        wire.InnerCircle=0;wire.Renderers[0].ControlsVisibility=false;
        try{Codec<MapObjectVisual>("RestoreVisual",wire);throw new Exception("accepted inconsistent renderer role");}catch(TargetInvocationException e) when(e.InnerException is ArgumentException){Console.WriteLine("PASS inconsistent visibility role rejected before reconstruction");}
    }
}
