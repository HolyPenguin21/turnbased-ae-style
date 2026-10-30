using System;
using System.Collections.Generic;
using Game.HexGrid;
using Game.Players;

namespace Game.Map
{
    public static partial class VisionSystem
    {
        private static MutationNotifications pendingMutation;
        private static int mutationEpoch;

        private static void ResetMutationNotifications()
        {
            mutationEpoch++;
            pendingMutation = null;
        }

        internal static MutationNotifications BeginMutation() => new MutationNotifications();

        // A committed operation stays successful if one presentation/knowledge subscriber
        // fails. Isolate multicast handlers too, so later witnesses still receive the result.
        internal static void PublishAfterMutation(Action publish, bool beforeVision = false)
        {
            if (publish == null || Game.Setup.MatchRestoreScope.IsRestoring) return;
            int epoch = mutationEpoch;
            foreach (Action handler in publish.GetInvocationList())
            {
                if (epoch != mutationEpoch) break;
                if (pendingMutation == null) InvokeNotification(handler);
                else (beforeVision ? pendingMutation.BeforeVision : pendingMutation.AfterVision).Add(handler);
            }
        }

        internal static void PublishAfterMutation<T>(Action<T> publish, T value, bool beforeVision = false)
        {
            if (publish == null) return;
            int epoch = mutationEpoch;
            foreach (Action<T> handler in publish.GetInvocationList())
                if (epoch == mutationEpoch) PublishAfterMutation(() => handler(value), beforeVision);
        }

        internal static void PublishAfterMutation<T, U>(Action<T, U> publish, T first, U second, bool beforeVision = false)
        {
            if (publish == null) return;
            int epoch = mutationEpoch;
            foreach (Action<T, U> handler in publish.GetInvocationList())
                if (epoch == mutationEpoch) PublishAfterMutation(() => handler(first, second), beforeVision);
        }

        internal static void PublishAfterMutation<T, U, V>(Action<T, U, V> publish, T first, U second, V third)
        {
            if (publish == null) return;
            int epoch = mutationEpoch;
            foreach (Action<T, U, V> handler in publish.GetInvocationList())
                if (epoch == mutationEpoch) PublishAfterMutation(() => handler(first, second, third));
        }

        private static void InvokeNotification(Action handler)
        {
            try { handler(); }
            catch (Exception error)
            {
                // Logging is diagnostic, never another opportunity to fail the committed command.
                try { UnityEngine.Debug.LogException(error); }
                catch { }
            }
        }

        // Synchronous batching over the existing notification helpers. State compensation
        // remains with the operation owner; this does not roll back or schedule gameplay.
        internal sealed class MutationNotifications : IDisposable
        {
            private readonly MutationNotifications parent;
            private readonly int epoch;
            internal readonly HashSet<PlayerSetupData> Owners = new HashSet<PlayerSetupData>();
            internal readonly HashSet<HexCoord> Hexes = new HashSet<HexCoord>();
            internal readonly List<Action> BeforeVision = new List<Action>();
            internal readonly List<Action> AfterVision = new List<Action>();
            private bool disposed, discarded;

            internal MutationNotifications()
            {
                epoch = mutationEpoch;
                parent = pendingMutation;
                pendingMutation = this;
            }

            internal void Discard() => discarded = true;

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                if (epoch != mutationEpoch) return;
                pendingMutation = parent;
                if (discarded || Game.Setup.MatchRestoreScope.IsRestoring) return;
                if (parent != null)
                {
                    parent.Owners.UnionWith(Owners);
                    parent.Hexes.UnionWith(Hexes);
                    parent.BeforeVision.AddRange(BeforeVision);
                    parent.AfterVision.AddRange(AfterVision);
                    return;
                }
                // World/hand/containers are final, while the legitimate prior witnesses
                // still have their old vision for capture/destruction observations.
                foreach (Action publish in BeforeVision)
                    if (epoch == mutationEpoch) InvokeNotification(publish);
                foreach (PlayerSetupData owner in Owners)
                    if (epoch == mutationEpoch) RecomputeFor(owner);
                foreach (HexCoord hex in Hexes)
                    if (epoch == mutationEpoch) NotifyContentChanged(hex);
                foreach (Action publish in AfterVision)
                    if (epoch == mutationEpoch) InvokeNotification(publish);
            }
        }
    }
}
