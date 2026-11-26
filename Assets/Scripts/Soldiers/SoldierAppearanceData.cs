using System;
using UnityEngine;
using VRAtis.WorldMap;

namespace VRAtis.Soldiers
{
    public enum SoldierBodyBuild
    {
        Standard = 0,
        Heavy = 1,
        Lean = 2
    }

    public enum SoldierFaceArchetype
    {
        // USA (6)
        US_01_EuroSquare = 0,
        US_02_EuroLong = 1,
        US_03_AfroBroad = 2,
        US_04_AfroLean = 3,
        US_05_Hispanic = 4,
        US_06_AsianAmerican = 5,

        // Turkey (5)
        TR_01_AnatolianStrong = 6,
        TR_02_AegeanLean = 7,
        TR_03_BlackSeaSharp = 8,
        TR_04_EasternBroad = 9,
        TR_05_CentralClassic = 10,

        // Soviet / Russia (5)
        SU_01_SlavSquare = 11,
        SU_02_SlavHighCheek = 12,
        SU_03_UralBroad = 13,
        SU_04_CaucasusSharp = 14,
        SU_05_SiberianMix = 15,

        // China (5)
        CN_01_NorthStrong = 16,
        CN_02_SouthLean = 17,
        CN_03_BroadRound = 18,
        CN_04_HighlandAngular = 19,
        CN_05_ClassicOfficer = 20,

        // Rebels / Pirates (5)
        RB_01_DesertGaunt = 21,
        RB_02_BruteHeavy = 22,
        RB_03_CorsairSharp = 23,
        RB_04_MercenaryWide = 24,
        RB_05_NomadLean = 25
    }

    public enum SoldierHairStyle
    {
        Bald = 0,
        BuzzCut = 1,
        HighAndTight = 2,
        ShortSidePart = 3,
        FullThick = 4,
        ShaggyRough = 5
    }

    public enum SoldierBeardStyle
    {
        CleanShaven = 0,
        ShortBeard = 1,
        Mustache = 2,
        FullRugged = 3
    }

    public enum SoldierTier
    {
        Tier1_Recruit = 1,
        Tier2_Infantry = 2,
        Tier3_Veteran = 3,
        Tier4_Commando = 4,
        Tier5_Elite = 5
    }

    public enum SoldierEquipmentSlot
    {
        Helmet = 0,
        EyeWear = 1,
        UniformTop = 2,
        ChestRig = 3,
        PantsAndBoots = 4,
        PrimaryWeapon = 5,
        BackPack = 6,
        HolsterSidearm = 7
    }

    [Serializable]
    public struct SoldierAppearanceProfile
    {
        public string recruitName;
        public WorldMapFaction faction;
        public SoldierTier tier;
        public SoldierBodyBuild bodyBuild;
        public SoldierFaceArchetype faceArchetype;
        public SoldierHairStyle hairStyle;
        public SoldierBeardStyle beardStyle;
        public Color skinColor;
        public Color hairColor;
        public Color irisColor;
        public Color shortsColor;
        public float heightScale;

        // Dynamic equipment requirement stats (allows equipping better gear on qualified lower-tier recruits!)
        public int strength;
        public int endurance;
        public int marksmanship;

        private static readonly string[] UsFirst = { "James", "Marcus", "Carlos", "David", "Daniel", "Tyler", "Derrick", "Mateo", "Kevin", "Brandon", "Andre", "Nathan", "Ryan", "Ethan" };
        private static readonly string[] UsLast  = { "Miller", "Jackson", "Ramirez", "Chen", "Anderson", "Brooks", "Walker", "Morales", "Kim", "Carter", "Hayes", "Washington", "Cooper", "Park" };

        private static readonly string[] TrFirst = { "Mehmet", "Ahmet", "Mustafa", "Emre", "Kerem", "Burak", "Yusuf", "Oğuz", "Hakan", "Serkan", "Volkan", "Tarik", "Mert", "Kadir" };
        private static readonly string[] TrLast  = { "Yılmaz", "Kaya", "Demir", "Aydın", "Arslan", "Şahin", "Çelik", "Karahan", "Öztürk", "Yıldız", "Korkmaz", "Aslan", "Koç", "Kurt" };

        private static readonly string[] SuFirst = { "Ivan", "Dmitri", "Alexei", "Sergei", "Nikolai", "Viktor", "Yuri", "Mikhail", "Andrei", "Pavel", "Boris", "Anton", "Oleg", "Igor" };
        private static readonly string[] SuLast  = { "Petrov", "Volkov", "Sokolov", "Morozov", "Orlov", "Lebedev", "Smirnov", "Kuznetsov", "Popov", "Vasiliev", "Kozlov", "Novikov", "Fedorov", "Zaitsev" };

        private static readonly string[] CnFirst = { "Wei", "Jun", "Hao", "Ming", "Lei", "Tao", "Feng", "Long", "Bo", "Jian", "Qiang", "Peng", "Gang", "Chao" };
        private static readonly string[] CnLast  = { "Zhang", "Li", "Wang", "Chen", "Liu", "Yang", "Wu", "Zhao", "Huang", "Zhou", "Xu", "Sun", "Ma", "Zhu" };

        private static readonly string[] RbNames = { "Kara Kemal", "Paslı Tarık", "Akrep Rıza", "Çöl Kurdu", "Kızıl Vahap", "Demir Hasan", "Kırık Diş", "Gölge Sami", "Barut Selim", "Yırtık Orhan", "Kargı", "Sürgün Salih", "Korsan Ziya", "Tozlu Bekir" };

        public static string GetFactionRankTitle(WorldMapFaction faction, SoldierTier tier)
        {
            switch (faction)
            {
                case WorldMapFaction.USA:
                    switch (tier)
                    {
                        case SoldierTier.Tier1_Recruit:  return "Recruit";
                        case SoldierTier.Tier2_Infantry: return "Private";
                        case SoldierTier.Tier3_Veteran:  return "Corporal";
                        case SoldierTier.Tier4_Commando: return "US Ranger";
                        default:                         return "Delta Force";
                    }
                case WorldMapFaction.Turkey:
                    switch (tier)
                    {
                        case SoldierTier.Tier1_Recruit:  return "Acemi Er";
                        case SoldierTier.Tier2_Infantry: return "Piyade Er";
                        case SoldierTier.Tier3_Veteran:  return "Uzman Çavuş";
                        case SoldierTier.Tier4_Commando: return "Komando";
                        default:                         return "Bordo Bereli";
                    }
                case WorldMapFaction.Soviet:
                    switch (tier)
                    {
                        case SoldierTier.Tier1_Recruit:  return "Conscript";
                        case SoldierTier.Tier2_Infantry: return "Kızıl Piyade";
                        case SoldierTier.Tier3_Veteran:  return "Muhafız Eri";
                        case SoldierTier.Tier4_Commando: return "VDV Paraşütçü";
                        default:                         return "Spetsnaz";
                    }
                case WorldMapFaction.China:
                    switch (tier)
                    {
                        case SoldierTier.Tier1_Recruit:  return "Milis";
                        case SoldierTier.Tier2_Infantry: return "PLA Piyade";
                        case SoldierTier.Tier3_Veteran:  return "Hücum Piyadesi";
                        case SoldierTier.Tier4_Commando: return "Deniz Komandosu";
                        default:                         return "Kar Leoparı";
                    }
                default: // Rebels / Pirates
                    switch (tier)
                    {
                        case SoldierTier.Tier1_Recruit:  return "Çapulcu";
                        case SoldierTier.Tier2_Infantry: return "Silahlı Yağmacı";
                        case SoldierTier.Tier3_Veteran:  return "Çöl Korsanı";
                        case SoldierTier.Tier4_Commando: return "Ağır Baskıncı";
                        default:                         return "Korsan Kaptanı";
                    }
            }
        }

        public static SoldierAppearanceProfile GenerateForFaction(WorldMapFaction faction, int seed, SoldierTier tier)
        {
            var rng = new System.Random(seed);
            var build = (SoldierBodyBuild)rng.Next(0, 3);
            SoldierFaceArchetype face = PickFaceForFaction(faction, rng);
            SoldierHairStyle hair = PickHairForFaction(faction, rng);
            SoldierBeardStyle beard = PickBeardForFaction(faction, rng);
            Color skin = PickSkinTone(faction, face, rng);
            Color hairCol = PickHairColor(faction, face, rng);
            Color irisCol = PickIrisColor(faction, face, rng);
            Color shortsCol = GetFactionShortsColor(faction);
            string fullName = PickNameForFaction(faction, rng);

            int baseStat = (int)tier * 2;
            float height = 0.95f + (float)rng.NextDouble() * 0.10f;
            if (build == SoldierBodyBuild.Heavy) height += 0.02f;
            if (faction == WorldMapFaction.China) height -= 0.02f;

            return new SoldierAppearanceProfile
            {
                recruitName = fullName,
                faction = faction,
                tier = tier,
                bodyBuild = build,
                faceArchetype = face,
                hairStyle = hair,
                beardStyle = beard,
                skinColor = skin,
                hairColor = hairCol,
                irisColor = irisCol,
                shortsColor = shortsCol,
                heightScale = height,
                strength = baseStat + rng.Next(0, 3) + (build == SoldierBodyBuild.Heavy ? 2 : 0),
                endurance = baseStat + rng.Next(0, 3) + (build == SoldierBodyBuild.Lean ? 1 : 0),
                marksmanship = baseStat + rng.Next(0, 4)
            };
        }

        private static SoldierFaceArchetype PickFaceForFaction(WorldMapFaction faction, System.Random rng)
        {
            switch (faction)
            {
                case WorldMapFaction.USA:    return (SoldierFaceArchetype)rng.Next(0, 6);
                case WorldMapFaction.Turkey: return (SoldierFaceArchetype)rng.Next(6, 11);
                case WorldMapFaction.Soviet: return (SoldierFaceArchetype)rng.Next(11, 16);
                case WorldMapFaction.China:  return (SoldierFaceArchetype)rng.Next(16, 21);
                default:                     return (SoldierFaceArchetype)rng.Next(21, 26);
            }
        }

        private static SoldierHairStyle PickHairForFaction(WorldMapFaction faction, System.Random rng)
        {
            int r = rng.Next(0, 100);
            switch (faction)
            {
                case WorldMapFaction.USA:
                    if (r < 32) return SoldierHairStyle.HighAndTight;
                    if (r < 64) return SoldierHairStyle.BuzzCut;
                    if (r < 85) return SoldierHairStyle.ShortSidePart;
                    return SoldierHairStyle.Bald;
                case WorldMapFaction.Turkey:
                    if (r < 32) return SoldierHairStyle.BuzzCut;
                    if (r < 64) return SoldierHairStyle.ShortSidePart;
                    if (r < 88) return SoldierHairStyle.FullThick;
                    return SoldierHairStyle.Bald;
                case WorldMapFaction.Soviet:
                    if (r < 42) return SoldierHairStyle.BuzzCut;
                    if (r < 70) return SoldierHairStyle.ShortSidePart;
                    if (r < 88) return SoldierHairStyle.FullThick;
                    return SoldierHairStyle.Bald;
                case WorldMapFaction.China:
                    if (r < 45) return SoldierHairStyle.ShortSidePart;
                    if (r < 80) return SoldierHairStyle.BuzzCut;
                    return SoldierHairStyle.FullThick;
                default: // Rebels / Pirates
                    if (r < 40) return SoldierHairStyle.ShaggyRough;
                    if (r < 64) return SoldierHairStyle.Bald;
                    if (r < 84) return SoldierHairStyle.FullThick;
                    return SoldierHairStyle.BuzzCut;
            }
        }

        private static SoldierBeardStyle PickBeardForFaction(WorldMapFaction faction, System.Random rng)
        {
            int r = rng.Next(0, 100);
            switch (faction)
            {
                case WorldMapFaction.USA:
                    if (r < 52) return SoldierBeardStyle.CleanShaven;
                    if (r < 78) return SoldierBeardStyle.ShortBeard;
                    return SoldierBeardStyle.Mustache;
                case WorldMapFaction.Turkey:
                    if (r < 28) return SoldierBeardStyle.CleanShaven;
                    if (r < 58) return SoldierBeardStyle.ShortBeard;
                    if (r < 85) return SoldierBeardStyle.Mustache;
                    return SoldierBeardStyle.FullRugged;
                case WorldMapFaction.Soviet:
                    if (r < 45) return SoldierBeardStyle.CleanShaven;
                    if (r < 72) return SoldierBeardStyle.Mustache;
                    if (r < 90) return SoldierBeardStyle.ShortBeard;
                    return SoldierBeardStyle.FullRugged;
                case WorldMapFaction.China:
                    if (r < 78) return SoldierBeardStyle.CleanShaven;
                    if (r < 93) return SoldierBeardStyle.Mustache;
                    return SoldierBeardStyle.ShortBeard;
                default: // Rebels / Pirates
                    if (r < 46) return SoldierBeardStyle.FullRugged;
                    if (r < 78) return SoldierBeardStyle.ShortBeard;
                    if (r < 92) return SoldierBeardStyle.Mustache;
                    return SoldierBeardStyle.CleanShaven;
            }
        }

        private static Color PickSkinTone(WorldMapFaction faction, SoldierFaceArchetype face, System.Random rng)
        {
            float v = (float)rng.NextDouble();
            if (faction == WorldMapFaction.USA)
            {
                if (face == SoldierFaceArchetype.US_03_AfroBroad || face == SoldierFaceArchetype.US_04_AfroLean)
                    return Color.Lerp(new Color(0.32f, 0.21f, 0.15f), new Color(0.45f, 0.30f, 0.21f), v);
                if (face == SoldierFaceArchetype.US_05_Hispanic)
                    return Color.Lerp(new Color(0.67f, 0.49f, 0.36f), new Color(0.56f, 0.39f, 0.28f), v);
                if (face == SoldierFaceArchetype.US_06_AsianAmerican)
                    return Color.Lerp(new Color(0.81f, 0.65f, 0.50f), new Color(0.72f, 0.56f, 0.41f), v);
                return Color.Lerp(new Color(0.84f, 0.67f, 0.56f), new Color(0.75f, 0.57f, 0.45f), v);
            }
            if (faction == WorldMapFaction.Turkey)
            {
                // Realistic Anatolian / Aegean / Eastern Mediterranean wheat-to-olive tones
                if (face == SoldierFaceArchetype.TR_04_EasternBroad)
                    return Color.Lerp(new Color(0.66f, 0.48f, 0.35f), new Color(0.56f, 0.39f, 0.28f), v);
                if (face == SoldierFaceArchetype.TR_02_AegeanLean || face == SoldierFaceArchetype.TR_03_BlackSeaSharp)
                    return Color.Lerp(new Color(0.82f, 0.65f, 0.53f), new Color(0.73f, 0.56f, 0.44f), v);
                return Color.Lerp(new Color(0.76f, 0.58f, 0.45f), new Color(0.65f, 0.48f, 0.36f), v);
            }
            if (faction == WorldMapFaction.Soviet)
            {
                if (face == SoldierFaceArchetype.SU_04_CaucasusSharp)
                    return Color.Lerp(new Color(0.74f, 0.57f, 0.45f), new Color(0.64f, 0.47f, 0.36f), v);
                if (face == SoldierFaceArchetype.SU_05_SiberianMix)
                    return Color.Lerp(new Color(0.82f, 0.66f, 0.52f), new Color(0.73f, 0.58f, 0.44f), v);
                return Color.Lerp(new Color(0.88f, 0.72f, 0.62f), new Color(0.80f, 0.63f, 0.53f), v);
            }
            if (faction == WorldMapFaction.China)
            {
                return Color.Lerp(new Color(0.83f, 0.68f, 0.52f), new Color(0.71f, 0.55f, 0.40f), v);
            }
            // Rebels: sun-weathered desert / coastal diverse raider tones
            return Color.Lerp(new Color(0.66f, 0.48f, 0.34f), new Color(0.46f, 0.32f, 0.22f), v);
        }

        private static Color PickHairColor(WorldMapFaction faction, SoldierFaceArchetype face, System.Random rng)
        {
            int p = rng.Next(0, 4);
            switch (faction)
            {
                case WorldMapFaction.China:
                    return p == 0 ? new Color(0.08f, 0.07f, 0.06f) : new Color(0.05f, 0.05f, 0.05f);
                case WorldMapFaction.Turkey:
                    if (p == 0) return new Color(0.06f, 0.05f, 0.05f);
                    if (p == 1) return new Color(0.11f, 0.08f, 0.06f);
                    if (p == 2) return new Color(0.19f, 0.13f, 0.09f);
                    return new Color(0.26f, 0.19f, 0.12f);
                case WorldMapFaction.Soviet:
                    if (face == SoldierFaceArchetype.SU_04_CaucasusSharp) return new Color(0.07f, 0.06f, 0.05f);
                    if (p == 0) return new Color(0.52f, 0.42f, 0.26f); // Slavic blonde
                    if (p == 1) return new Color(0.32f, 0.23f, 0.14f); // Light brown
                    if (p == 2) return new Color(0.18f, 0.13f, 0.09f); // Brown
                    return new Color(0.10f, 0.08f, 0.06f);
                case WorldMapFaction.USA:
                    if (face == SoldierFaceArchetype.US_03_AfroBroad || face == SoldierFaceArchetype.US_04_AfroLean || face == SoldierFaceArchetype.US_06_AsianAmerican)
                        return new Color(0.05f, 0.05f, 0.05f);
                    if (p == 0) return new Color(0.48f, 0.38f, 0.22f);
                    if (p == 1) return new Color(0.26f, 0.18f, 0.11f);
                    return new Color(0.12f, 0.09f, 0.06f);
                default: // Rebels
                    if (p == 0) return new Color(0.06f, 0.05f, 0.05f);
                    if (p == 1) return new Color(0.14f, 0.10f, 0.07f);
                    if (p == 2) return new Color(0.28f, 0.26f, 0.24f); // weathered salt-and-pepper
                    return new Color(0.18f, 0.12f, 0.08f);
            }
        }

        private static Color PickIrisColor(WorldMapFaction faction, SoldierFaceArchetype face, System.Random rng)
        {
            int p = rng.Next(0, 4);
            if (faction == WorldMapFaction.Soviet && face != SoldierFaceArchetype.SU_04_CaucasusSharp && face != SoldierFaceArchetype.SU_05_SiberianMix)
            {
                if (p < 2) return new Color(0.17f, 0.30f, 0.42f); // Ice blue
                if (p == 2) return new Color(0.24f, 0.30f, 0.22f); // Gray-green
            }
            if (faction == WorldMapFaction.USA && (face == SoldierFaceArchetype.US_01_EuroSquare || face == SoldierFaceArchetype.US_02_EuroLong))
            {
                if (p == 0) return new Color(0.16f, 0.28f, 0.38f);
                if (p == 1) return new Color(0.22f, 0.27f, 0.16f);
            }
            if (faction == WorldMapFaction.Turkey && p == 0)
                return new Color(0.22f, 0.24f, 0.14f); // Hazel

            return new Color(0.13f, 0.09f, 0.06f);
        }

        public static Color GetFactionShortsColor(WorldMapFaction faction)
        {
            switch (faction)
            {
                case WorldMapFaction.USA:    return new Color(0.20f, 0.25f, 0.18f); // US Army Olive PT
                case WorldMapFaction.Turkey: return new Color(0.28f, 0.26f, 0.20f); // Khaki / Earth PT
                case WorldMapFaction.Soviet: return new Color(0.24f, 0.20f, 0.18f); // Soviet Dark Brown-Gray
                case WorldMapFaction.China:  return new Color(0.16f, 0.24f, 0.21f); // PLA Deep Pine
                default:                     return new Color(0.17f, 0.16f, 0.16f); // Rebel Charcoal
            }
        }

        private static string PickNameForFaction(WorldMapFaction faction, System.Random rng)
        {
            switch (faction)
            {
                case WorldMapFaction.USA:    return $"{UsFirst[rng.Next(UsFirst.Length)]} {UsLast[rng.Next(UsLast.Length)]}";
                case WorldMapFaction.Turkey: return $"{TrFirst[rng.Next(TrFirst.Length)]} {TrLast[rng.Next(TrLast.Length)]}";
                case WorldMapFaction.Soviet: return $"{SuFirst[rng.Next(SuFirst.Length)]} {SuLast[rng.Next(SuLast.Length)]}";
                case WorldMapFaction.China:  return $"{CnFirst[rng.Next(CnFirst.Length)]} {CnLast[rng.Next(CnLast.Length)]}";
                default:                     return RbNames[rng.Next(RbNames.Length)];
            }
        }
    }
}
