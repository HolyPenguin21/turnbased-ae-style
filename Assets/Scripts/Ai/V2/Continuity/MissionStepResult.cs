using System;
using System.Collections.Generic;

namespace Game.Ai.V2
{
    public interface IMissionStepPayload { }

    public enum MissionStepDisposition { Progress, Completed, Waiting, Replan, Invalidated, PermanentFailure }

    // Common step lifecycle. A completed sub-leg is never itself authority to retire a campaign.
    // Domain facts are typed and stored once. Legacy outcomes project into the same payloads.
    public abstract class MissionStepResult
    {
        public MissionIntentKey IntentKey;
        public MissionStepDisposition Disposition = MissionStepDisposition.Completed;
        public float ApSpent;
        public int? MoverArmyId;
        private readonly Dictionary<Type, IMissionStepPayload> _payloads =
            new Dictionary<Type, IMissionStepPayload>();

        // Read access never creates a payload or changes provisioned-presence evidence.
        public T GetPayload<T>() where T : class, IMissionStepPayload =>
            _payloads.TryGetValue(typeof(T), out var payload) ? (T)payload : null;
        public void SetPayload<T>(T payload) where T : class, IMissionStepPayload
        {
            if (payload == null) _payloads.Remove(typeof(T));
            else _payloads[typeof(T)] = payload;
        }
        internal T PayloadForWrite<T>() where T : class, IMissionStepPayload, new()
        {
            T payload = GetPayload<T>();
            if (payload == null) SetPayload(payload = new T());
            return payload;
        }
    }
    // New domains can return a typed payload without editing the common result schema.
    public sealed class MissionStepResult<TPayload> : MissionStepResult
        where TPayload : class, IMissionStepPayload
    {
        public TPayload Payload => GetPayload<TPayload>();
        public MissionStepResult(MissionIntentKey operation, MissionStepDisposition disposition, TPayload payload)
        { IntentKey = operation; Disposition = disposition; SetPayload(payload); }
    }
}
