using System.Collections.Generic;
using UnityEngine;

namespace VRAtis.WorldMap
{
    /// <summary>
    /// Holds the continent-wide strategic hierarchy of Factions, Capitals, Cities, Military Bases,
    /// and Linked Villages on StoryWorldMap for runtime queries, UI labels, and future garrison dispatches.
    /// </summary>
    [DisallowMultipleComponent]
    public class WorldMapSettlementRegistry : MonoBehaviour
    {
        [SerializeField] private List<WorldMapSettlementRecord> settlements = new List<WorldMapSettlementRecord>(220);

        public IReadOnlyList<WorldMapSettlementRecord> Settlements => settlements;

        public void SetRecords(List<WorldMapSettlementRecord> records)
        {
            settlements.Clear();
            if (records != null)
                settlements.AddRange(records);
        }

        public bool TryGetById(string settlementId, out WorldMapSettlementRecord record)
        {
            for (int i = 0; i < settlements.Count; i++)
            {
                if (settlements[i].id == settlementId)
                {
                    record = settlements[i];
                    return true;
                }
            }
            record = default;
            return false;
        }

        public bool TryGetFactionCapital(WorldMapFaction faction, out WorldMapSettlementRecord capital)
        {
            for (int i = 0; i < settlements.Count; i++)
            {
                if (settlements[i].faction == faction && settlements[i].kind == WorldMapSettlementKind.Capital)
                {
                    capital = settlements[i];
                    return true;
                }
            }
            capital = default;
            return false;
        }

        public void GetLinkedVillages(string parentHubId, List<WorldMapSettlementRecord> results)
        {
            results.Clear();
            for (int i = 0; i < settlements.Count; i++)
            {
                if (settlements[i].kind == WorldMapSettlementKind.Village && settlements[i].parentHubId == parentHubId)
                    results.Add(settlements[i]);
            }
        }

        public bool TryFindNearest(Vector3 worldPos, float maxRadius, out WorldMapSettlementRecord nearest)
        {
            float bestSq = maxRadius * maxRadius;
            int bestIdx = -1;
            for (int i = 0; i < settlements.Count; i++)
            {
                Vector3 p = settlements[i].worldPosition;
                float dx = p.x - worldPos.x;
                float dz = p.z - worldPos.z;
                float dSq = dx * dx + dz * dz;
                if (dSq <= bestSq)
                {
                    bestSq = dSq;
                    bestIdx = i;
                }
            }

            if (bestIdx >= 0)
            {
                nearest = settlements[bestIdx];
                return true;
            }

            nearest = default;
            return false;
        }
    }
}
