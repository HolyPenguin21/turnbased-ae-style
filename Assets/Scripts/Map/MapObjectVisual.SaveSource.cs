namespace Game.Map
{
    public partial class MapObjectVisual
    {
        // Runtime authored template identity survives ownership changes. A captured base keeps
        // its original silhouette; the new owner changes only its tint and flag.
        internal MapObjectVisual PersistenceSourcePrefab { get; set; }
        internal UnityEngine.Sprite PersistenceIcon { get; set; }
    }
}
