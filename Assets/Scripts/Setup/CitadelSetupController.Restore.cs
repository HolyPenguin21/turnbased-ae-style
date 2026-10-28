using System;
using Game.Core;
using UnityEngine;

namespace Game.Setup
{
    public partial class CitadelSetupController
    {
        // The scene coroutine owns navigation/input activation. Keep the restore transaction
        // and its failure cleanup together so every entry follows the same owner lifecycle.
        internal bool TryRestorePreparedMatch()
        {
            try
            {
                if (!string.IsNullOrEmpty(GameSession.SetupError))
                    throw new InvalidOperationException(GameSession.SetupError);
                ConfigureWorldSystems();
                MatchRestoreCoordinator.RestorePending(gameConfig, map, turnController, hexSelectionController);
                return true;
            }
            catch (Exception ex)
            {
                FailMatchRestore(ex);
                return false;
            }
        }

        private void FailMatchRestore(Exception exception)
        {
            Debug.LogException(exception);
            MatchStateInventory.EndMatch(turnController);
            GameSession.SetupError = "Could not restore the saved match: " + exception.Message;
        }
    }
}
