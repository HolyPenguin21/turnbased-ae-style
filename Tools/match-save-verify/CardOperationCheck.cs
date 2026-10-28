using System;
using System.Collections.Generic;
namespace Game.UI
{
    // Field-only native UI adapters. The predicate under test is the Assets source.
    public sealed class CardUI { public bool IsDragging; }
    public partial class CardHandUI
    {
        private bool IsAttachMode, _tacticChallengeActive, _tacticFlipping;
        private readonly List<CardUI> _cards = new List<CardUI>(), _tacticCards = new List<CardUI>();
        internal static void VerifyCaptureGate()
        {
            var hand = new CardHandUI();
            if (hand.HasPendingCardOperation) throw new Exception("Idle hand should be settled.");
            hand._tacticChallengeActive = true;
            if (!hand.HasPendingCardOperation) throw new Exception("Active tactical challenge incorrectly permits saving.");
            hand._tacticChallengeActive = false; hand._tacticFlipping = true;
            if (!hand.HasPendingCardOperation) throw new Exception("Hand flip incorrectly permits saving.");
            hand._tacticFlipping = false; hand._tacticCards.Add(new CardUI { IsDragging = true });
            if (!hand.HasPendingCardOperation) throw new Exception("Tactical card drag incorrectly permits saving.");
            hand._tacticCards.Clear(); hand._cards.Add(new CardUI { IsDragging = true });
            if (!hand.HasPendingCardOperation) throw new Exception("Main card drag incorrectly permits saving.");
            hand._cards.Clear(); hand.IsAttachMode = true;
            if (!hand.HasPendingCardOperation) throw new Exception("Attachment incorrectly permits saving.");
            Console.WriteLine("PASS real card-operation predicate: idle, challenge, flip, both drags, attachment");
        }
    }
}
