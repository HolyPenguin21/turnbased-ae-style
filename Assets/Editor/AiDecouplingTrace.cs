#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Game.Ai.V2;
using Game.Players;

namespace Game.EditorTests
{
    // Test-side recorder for the decoupling acceptance tools (Tools/ai-v2-decoupling-verify).
    // It is NOT a game registry: fixtures call it explicitly at the points they control, and it
    // only READS the ledger. With AI_V2_TRACE_DIR set it writes <name>.jsonl, one object per event
    // (schema: Tools/ai-v2-decoupling-verify/README.md); without it, records stay in memory.
    internal sealed class AiDecouplingTrace
    {
        private readonly string _name;
        private readonly PlayerSetupData _player;
        private readonly int _playerId;
        private readonly List<string> _lines = new List<string>();
        private int _ordinal;

        internal AiDecouplingTrace(string name, PlayerSetupData player, int playerId = 1)
        {
            _name = name;
            _player = player;
            _playerId = playerId;
        }

        internal IReadOnlyList<string> Lines => _lines;

        // ap is the caller's value at that point (the physical spend authority is not the ledger).
        internal void Record(int turn, string work, string evt, float ap = 0f, int worldRevision = 0,
            int knowledgeVersion = 0, int pathingVersion = 0, int scopeId = 0, string takeId = null,
            string[] pending = null, string[] consumed = null, string[] operationKeys = null,
            int[] actors = null)
        {
            var sb = new StringBuilder();
            sb.Append("{\"player\":").Append(_playerId)
              .Append(",\"turn\":").Append(turn)
              .Append(",\"work\":").Append(Q(work))
              .Append(",\"ordinal\":").Append(++_ordinal)
              .Append(",\"event\":").Append(Q(evt))
              .Append(",\"ap\":").Append(F(ap))
              .Append(",\"h\":0,\"e\":0,\"m\":0,\"t\":0")
              .Append(",\"rows\":[");
            bool first = true;
            foreach (StrategicResourceReservation r in StrategicResourceReservationLedger.Rows(_player, turn))
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"owner\":").Append(Q(r.Owner))
                  .Append(",\"reason\":").Append(Q(r.Reason.ToString()))
                  .Append(",\"resource\":").Append(Q(r.Resource.ToString()))
                  .Append(",\"amount\":").Append(F(r.Amount))
                  .Append(",\"expiry\":").Append(Q(r.ExpirationStage.ToString())).Append('}');
            }
            sb.Append("],\"operation_keys\":").Append(Arr(operationKeys))
              .Append(",\"actors\":").Append(Arr(actors))
              .Append(",\"world_revision\":").Append(worldRevision)
              .Append(",\"knowledge_version\":").Append(knowledgeVersion)
              .Append(",\"pathing_version\":").Append(pathingVersion)
              .Append(",\"scope_id\":").Append(scopeId)
              .Append(",\"consumed\":").Append(Arr(consumed))
              .Append(",\"pending\":").Append(Arr(pending));
            if (takeId != null) sb.Append(",\"take_id\":").Append(Q(takeId));
            sb.Append('}');
            _lines.Add(sb.ToString());
        }

        // Writes <AI_V2_TRACE_DIR>/<name>.jsonl when the variable is set; returns the path or null.
        internal string Flush()
        {
            string dir = System.Environment.GetEnvironmentVariable("AI_V2_TRACE_DIR");
            if (string.IsNullOrEmpty(dir)) return null;
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, _name + ".jsonl");
            File.WriteAllLines(path, _lines, new UTF8Encoding(false));
            return path;
        }

        private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static string Q(string s) =>
            "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private static string Arr(string[] a)
        {
            if (a == null || a.Length == 0) return "[]";
            var parts = new string[a.Length];
            for (int i = 0; i < a.Length; i++) parts[i] = Q(a[i]);
            return "[" + string.Join(",", parts) + "]";
        }

        private static string Arr(int[] a) =>
            a == null || a.Length == 0 ? "[]" : "[" + string.Join(",", a) + "]";
    }
}
#endif
