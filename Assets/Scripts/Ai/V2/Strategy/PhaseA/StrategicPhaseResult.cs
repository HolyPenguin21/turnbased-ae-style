using Game.Players;

namespace Game.Ai.V2
{
    public sealed class StrategicPhaseResult
    {
        public bool StateChanged;
        public int CardsPlayed;
        public int CardsDrawn;

        // Production telemetry (Step 8B/8C — spec §12). Attempts/successes for each chain stage,
        // kept separate so a Generate → Attach → Deploy chain reads as one materialization with
        // one generated card and one equipment assignment. Scoring is untouched.
        public int MaterializationAttempts;
        public int MaterializationsSucceeded;
        public int GeneratedCardAttempts;
        public int GeneratedCardsSucceeded;
        public int EquipmentAssignmentAttempts;
        public int EquipmentAssignmentsSucceeded;
        public int InfrastructureAttempts;
        public int InfrastructureBuilt;
        // Stand-alone Base levels bought by Development CapacityUnlock: a settled admission baseline taken
        // after such a step no longer describes the world its Facility demand must be judged on.
        public int CapacityUnlocks;
        public int CapabilityDeliveries;   // operational capability actually delivered to a demand

        public MaterializationReservation Reservation;

        // One executed materialization chain, Phase A or Phase B alike: the per-stage
        // attempt/success telemetry, the generation attempt charged to the reservation and to the
        // turn's tempo budget, and the world change it made.
        internal void RecordMaterialization(PlayerSetupData player, int turn, MaterializationPlan plan,
            MaterializationResult play)
        {
            MaterializationAttempts++;
            if (play.Deployed) MaterializationsSucceeded++;
            if (play.GenerationAttempted)
                RecordGenerationAttempt(player, turn, plan.Generation, play, play.Generated);
            if (plan.UsesEquipment)
            {
                EquipmentAssignmentAttempts++;
                if (play.Attached) EquipmentAssignmentsSucceeded++;
            }
            if (play.StateChanged)
                StateChanged = true;
        }

        // One paid Challenge, whichever lane ran it (a materialization chain, an infrastructure
        // operator step, a Development upgrade): its telemetry, the reservation's generator claims
        // and the turn's shared tempo budget.
        internal void RecordGenerationAttempt(PlayerSetupData player, int turn, GenerationStep generation,
            MaterializationResult play, bool generated)
        {
            GeneratedCardAttempts++;
            if (generated) GeneratedCardsSucceeded++;
            Reservation.RecordGenerationAttempt(generation, play);
            StrategicTempoBudget.RecordGenerationAttempt(player, turn);
        }

        // Multiple bounded local admissions in one turn report through one phase aggregate.
        // The shared ledgers/budgets remain the authorities; this only combines telemetry and
        // carries the newest reservation forward.
        public void Accumulate(StrategicPhaseResult other)
        {
            if (other == null) return;
            StateChanged |= other.StateChanged;
            CardsPlayed += other.CardsPlayed;
            CardsDrawn += other.CardsDrawn;
            MaterializationAttempts += other.MaterializationAttempts;
            MaterializationsSucceeded += other.MaterializationsSucceeded;
            GeneratedCardAttempts += other.GeneratedCardAttempts;
            GeneratedCardsSucceeded += other.GeneratedCardsSucceeded;
            EquipmentAssignmentAttempts += other.EquipmentAssignmentAttempts;
            EquipmentAssignmentsSucceeded += other.EquipmentAssignmentsSucceeded;
            InfrastructureAttempts += other.InfrastructureAttempts;
            InfrastructureBuilt += other.InfrastructureBuilt;
            CapacityUnlocks += other.CapacityUnlocks;
            CapabilityDeliveries += other.CapabilityDeliveries;
            if (other.Reservation != null)
                Reservation = other.Reservation;
        }
    }
}
