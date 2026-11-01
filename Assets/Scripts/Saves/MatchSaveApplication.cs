using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Game.Core;
using Game.Map;
using Game.Setup;
using Game.Turns;
using UnityEngine;
using UnityEngine.SceneManagement;
namespace Game.Saves
{
    internal sealed class MatchSaveApplication : IMatchSaveApplication
    {
        private readonly MatchSaveStore store;
        private readonly GameConfig config; private readonly HexMap map;
        private readonly GameTurnController turns; private readonly HexSelectionController selection;
        private readonly Action<string> navigate;
        internal MatchSaveApplication(string directory,GameConfig config,HexMap map,GameTurnController turns,
            HexSelectionController selection,Action<string> navigate=null)
        {
            store=new MatchSaveStore(directory);this.config=config;this.map=map;this.turns=turns;this.selection=selection;
            this.navigate=navigate ?? (scene=>SceneManager.LoadScene(scene));
        }
        internal static MatchSaveApplication ForScene(GameConfig config,GameTurnController turns) =>
            new MatchSaveApplication(Path.Combine(Application.persistentDataPath,"Matches"),config,
                UnityEngine.Object.FindAnyObjectByType<HexMap>(),turns,UnityEngine.Object.FindAnyObjectByType<HexSelectionController>());
        internal static bool HasSavedMatches() => new MatchSaveStore(Path.Combine(Application.persistentDataPath,"Matches")).Slots().Length>0;
        public IReadOnlyList<MatchSaveSlot> ListSlots()
        {
            var slots=new List<MatchSaveSlot>();
            foreach(int slot in store.Slots())
            {
                try
                {
                    var header=store.Load(slot,MatchSaveCodec.ReadEnvelope,out var notice,quarantineCorruptFile:false);
                    DateTimeOffset? date=header!=null && DateTimeOffset.TryParse(header.savedUtc,CultureInfo.InvariantCulture,DateTimeStyles.None,out var saved) ? saved : (DateTimeOffset?)null;
                    slots.Add(new MatchSaveSlot(slot,header!=null,header?.summary,date,notice,null));
                }
                catch(Exception ex){slots.Add(new MatchSaveSlot(slot,true,null,null,null,ex.Message));}
            }
            return slots.AsReadOnly();
        }
        public bool Exists(int slot)=>store.Exists(slot);
        public void Save(int slot)=>store.Save(slot,MatchSaveCodec.Capture(config,map,turns,selection));
        public void Delete(int slot)=>store.Delete(slot);
        public void Load(int slot)
        {
            // Preflight is detached and precedes cancellation of the outgoing scene.
            var source=store.Load(slot,bytes=>MatchSaveCodec.Decode(bytes,config),out var notice);
            if(source==null)throw new InvalidOperationException("This save slot is empty.");
            GameSession.ValidateRestore(source,config);
            try
            {
                turns?.PrepareForExit();GameSession.CommitPreparedRestore(source);GameSession.RestoreNotice=notice;
                navigate(SceneNames.Game);
            }
            catch(Exception ex){MatchSceneAssembly.FailReconstruction(ex,turns);navigate(SceneNames.MainMenu);}
        }
    }
}
