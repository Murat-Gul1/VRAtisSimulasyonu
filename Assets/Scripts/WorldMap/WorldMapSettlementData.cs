using System;
using UnityEngine;

namespace VRAtis.WorldMap
{
    public enum WorldMapFaction
    {
        Turkey = 0,
        Soviet = 1,
        USA = 2,
        China = 3,
        Rebels = 4
    }

    public enum WorldMapSettlementKind
    {
        Capital = 0,
        City = 1,
        MilitaryBase = 2,
        Village = 3
    }

    /// <summary>
    /// Serializable strategic record for every Capital, City, Military Base, and Village on the world map.
    /// Stores parent-child links (Village -> City/Capital/MilitaryBase -> Faction Capital) so future
    /// garrison reinforcement dispatch, raids, and territory capture work cleanly from day one.
    /// </summary>
    [Serializable]
    public struct WorldMapSettlementRecord
    {
        public string id;
        public string displayName;
        public WorldMapFaction faction;
        public WorldMapSettlementKind kind;
        public string parentHubId;
        public Vector3 worldPosition;
        public float yawDeg;
        public float clearanceRadius;
        public bool isVisuallyBuilt;
    }
}
