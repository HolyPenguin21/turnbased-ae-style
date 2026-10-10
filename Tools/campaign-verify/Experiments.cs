using System;
using System.Linq;
using Game.Campaign;
using Game.Cards;
public static class Experiments
{
 public class DeckInput { public string Name; public int Faction; public CardDefinition[] Main, Attachments; }
 private static CardDefinition Card(System.Text.Json.JsonElement e)
 {
  int N(string k,int fallback=0)=>e.TryGetProperty(k,out var v)?v.GetInt32():fallback;
  string S(string k)=>e.TryGetProperty(k,out var v)?v.GetString():null;
  return new CardDefinition { authoredKey=S("authoredKey"), displayName=S("displayName"), cardType=(CardType)N("cardType"), faction=(Game.Players.Faction)N("faction"),
   attack=N("attack"),defenseRating=N("defenseRating",1),hitPoints=N("hitPoints",1),range=N("range",1),initiative=N("initiative",1),resistanceRating=N("resistanceRating"),
   commandRating=N("commandRating",2),fate=N("fate"),moveMax=N("moveMax",1),activationApCost=N("activationApCost",1),requiredBuildingAbility=S("requiredBuildingAbility"),
   isAviation=e.TryGetProperty("isAviation",out var air)&&air.GetInt32()!=0, attachmentSlot=(AttachmentSlot)N("attachmentSlot"),
   unitTypeTags=(e.GetProperty("unitTypeTags").ValueKind == System.Text.Json.JsonValueKind.Array ? e.GetProperty("unitTypeTags").EnumerateArray().ToArray() : Array.Empty<System.Text.Json.JsonElement>()).Select(v=>(UnitTypeTag)v.GetInt32()).ToList(),
   grantedAbilities=(e.GetProperty("grantedAbilities").ValueKind == System.Text.Json.JsonValueKind.Array ? e.GetProperty("grantedAbilities").EnumerateArray().ToArray() : Array.Empty<System.Text.Json.JsonElement>()).Select(v=>v.GetString()).ToList(),
   equipment=e.TryGetProperty("equipment",out var grant)?JsonUtility.FromJson<EquipmentGrant>(grant.GetRawText()):new EquipmentGrant() };
 }
 public static void Run(string input)
 {
  using var document=System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(input));
  var decks=document.RootElement.EnumerateArray().Select(d=>new DeckInput { Name=d.GetProperty("Name").GetString(), Faction=d.GetProperty("Faction").GetInt32(),
      Main=d.GetProperty("Main").EnumerateArray().Select(Card).ToArray(), Attachments=d.GetProperty("Attachments").EnumerateArray().Select(Card).ToArray() }).ToArray();
  var resolver=new CampaignBattleResolver();
  var variants=decks.SelectMany(d=>new[] {
   new CampaignDeck(d.Name,d.Main,d.Attachments),
   new CampaignDeck(d.Name+" — no aviation",d.Main.Where(c=>!c.isAviation),d.Attachments),
   new CampaignDeck(d.Name+" — no blueprints",d.Main)
  }).ToList();
  foreach(var d in decks)
  {
   var original=new CampaignDeck(d.Name,d.Main,d.Attachments);double total=resolver.Evaluate(original).Total;
   for(int i=0;i<d.Main.Length;i++) if(d.Main[i].cardType==CardType.Unit)
   {
    foreach(string stat in new[]{"attack","defenseRating","hitPoints","initiative"})
    {
     var copy=(CardDefinition)typeof(object).GetMethod("MemberwiseClone",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(d.Main[i],null);
     if(stat=="attack")copy.attack++;else if(stat=="defenseRating")copy.defenseRating++;else if(stat=="hitPoints")copy.hitPoints++;else copy.initiative++;
     var pool=(CardDefinition[])d.Main.Clone();pool[i]=copy;
     double increased=resolver.Evaluate(new CampaignDeck(d.Name,pool,d.Attachments)).Total;
     if(resolver.Compare(new CampaignDeck(d.Name,pool,d.Attachments),original).WinChance+1e-7<.5)throw new Exception("Stat improvement reduced win chance.");
     if(increased+1e-5<total)throw new Exception($"Nonmonotone deck score: {d.Name}/{copy.authoredKey}/{stat}: {total} -> {increased}");
    }
   }
  }
  Console.WriteLine("DECK SCORES (heuristic, not measured match strength)");
  foreach(var d in variants){var s=resolver.Evaluate(d);Console.WriteLine($"{d.Name}: combat={s.CombatPotential:F2}; commanders={s.CommanderPotential:F2}; strategic={s.StrategicPotential:F2}; attachments={s.AttachmentPotential:F2}; total={s.Total:F2}");}
  for(int i=0;i<decks.Length;i++)for(int j=i+1;j<decks.Length;j++)
  {
   var a=new CampaignDeck(decks[i].Name,decks[i].Main,decks[i].Attachments);var b=new CampaignDeck(decks[j].Name,decks[j].Main,decks[j].Attachments);
   double p=resolver.Compare(a,b).WinChance;int wins=Enumerable.Range(0,1000).Count(k=>resolver.Resolve(a,b,CampaignRandom.Derive(419,k)).Outcome==CampaignOutcome.AttackerVictory);
   Console.WriteLine($"{a.Name} vs {b.Name}: predicted={p:P2}; fast wins={wins}/1000; full-match wins=NOT MEASURED");
  }
 }
}
