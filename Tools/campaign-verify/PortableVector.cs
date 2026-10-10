// Test-only value vector replacing non-executable Unity reference Vector2 in geometry source copies.
public struct CampaignTestVector2
{
 public float x,y;
 public CampaignTestVector2(float x,float y) {this.x=x;this.y=y;}
 public static CampaignTestVector2 zero => new CampaignTestVector2(0,0);
 public float sqrMagnitude => x*x+y*y;
 public float magnitude => (float)System.Math.Sqrt(sqrMagnitude);
 public static float Dot(CampaignTestVector2 a,CampaignTestVector2 b)=>a.x*b.x+a.y*b.y;
 public static CampaignTestVector2 operator +(CampaignTestVector2 a,CampaignTestVector2 b)=>new CampaignTestVector2(a.x+b.x,a.y+b.y);
 public static CampaignTestVector2 operator -(CampaignTestVector2 a,CampaignTestVector2 b)=>new CampaignTestVector2(a.x-b.x,a.y-b.y);
 public static CampaignTestVector2 operator *(CampaignTestVector2 a,float f)=>new CampaignTestVector2(a.x*f,a.y*f);
 public static CampaignTestVector2 operator /(CampaignTestVector2 a,float f)=>new CampaignTestVector2(a.x/f,a.y/f);
}
namespace Game.Campaign { public static class CampaignUI { public static string FactionName(Game.Players.Faction f)=>f.ToString(); } }
