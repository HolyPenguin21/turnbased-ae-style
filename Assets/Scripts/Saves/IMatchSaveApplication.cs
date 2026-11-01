using System;
using System.Collections.Generic;
namespace Game.Saves
{
    public sealed class MatchSaveSlot
    {
        public int Id {get;} public bool Occupied {get;} public string Summary {get;}
        public DateTimeOffset? SavedUtc {get;} public string Notice {get;} public string Error {get;}
        internal MatchSaveSlot(int id,bool occupied,string summary,DateTimeOffset? date,string notice,string error)
        {Id=id;Occupied=occupied;Summary=summary;SavedUtc=date;Notice=notice;Error=error;}
    }
    public interface IMatchSaveApplication
    {
        IReadOnlyList<MatchSaveSlot> ListSlots(); bool Exists(int slot);
        void Save(int slot); void Delete(int slot); void Load(int slot);
    }
}
