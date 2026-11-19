using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRAtis.Targets
{
    public enum TacticalDrillId
    {
        None = 0,
        ZDrill = 1,
        XDrill = 2,
        Mozambique = 3,
        BoxDrill = 4,
        BillDrill = 5,
        OneToFiveDrill = 6,
        ElPresidente = 7,
        MovingThreatLead = 8
    }

    [Serializable]
    public struct TargetPlacementSpec
    {
        public TargetKind Kind;
        public float DistanceMeters;
        public float LateralOffsetMeters;
        public float PatrolHalfWidthMeters;
        public bool AssaultMode;

        public TargetPlacementSpec(
            TargetKind kind,
            float distanceMeters,
            float lateralOffsetMeters,
            float patrolHalfWidthMeters = 3.0f,
            bool assaultMode = false)
        {
            Kind = kind;
            DistanceMeters = distanceMeters;
            LateralOffsetMeters = lateralOffsetMeters;
            PatrolHalfWidthMeters = patrolHalfWidthMeters;
            AssaultMode = assaultMode;
        }
    }

    public sealed class TacticalDrillDefinition
    {
        public readonly TacticalDrillId Id;
        public readonly string ButtonId;
        public readonly string Title;
        public readonly string ShortSubtitle;
        public readonly string SummaryTag;
        public readonly TargetPlacementSpec[] Targets;
        public readonly string[] BriefingSteps;

        public TacticalDrillDefinition(
            TacticalDrillId id,
            string buttonId,
            string title,
            string shortSubtitle,
            string summaryTag,
            TargetPlacementSpec[] targets,
            string[] briefingSteps)
        {
            Id = id;
            ButtonId = buttonId;
            Title = title;
            ShortSubtitle = shortSubtitle;
            SummaryTag = summaryTag;
            Targets = targets;
            BriefingSteps = briefingSteps;
        }
    }

    /// <summary>
    /// Built-in tactical shooting training drills with real-world multi-depth range layouts
    /// and concise Turkish coaching steps for VR on-screen briefings.
    /// </summary>
    public static class TacticalDrillCatalog
    {
        public static readonly TacticalDrillDefinition[] AllDrills =
        {
            new TacticalDrillDefinition(
                TacticalDrillId.ZDrill,
                "DrillBtn_ZDrill",
                "Z-Drill (Yakın-Uzak 4 Hedef)",
                "4 Metal Siluet • 15 m yakın + 45 m uzak Z rotası",
                "Z-Drill • 4 Hedef (15m & 45m)",
                new[]
                {
                    new TargetPlacementSpec(TargetKind.MetalHumanSilhouette, 45f, -5.0f), // Üst-Sol (Uzak)
                    new TargetPlacementSpec(TargetKind.MetalHumanSilhouette, 45f, +5.0f), // Üst-Sağ (Uzak)
                    new TargetPlacementSpec(TargetKind.MetalHumanSilhouette, 15f, -2.6f), // Alt-Sol (Yakın)
                    new TargetPlacementSpec(TargetKind.MetalHumanSilhouette, 15f, +2.6f)  // Alt-Sağ (Yakın)
                },
                new[]
                {
                    "Z-Drill: Farklı mesafelerdeki 4 hedefi havada hayali bir 'Z' harfi çizecek sırayla vurun.",
                    "Önce 45 m sol uzak hedefe, ardından 45 m sağ uzak hedefe geçip ikişer kontrollü atış yapın.",
                    "Sonra göz odağını hızla yakınlaştırarak 15 m sol yakın hedefe, ardından 15 m sağ yakın hedefe geçin.",
                    "Bu çalışma yatay hedef geçiş hızınızı ve yakın-uzak derinlik odak değişiminizi geliştirir."
                }),

            new TacticalDrillDefinition(
                TacticalDrillId.XDrill,
                "DrillBtn_XDrill",
                "X-Drill (Çapraz 4 Hedef)",
                "4 Metal Siluet • 18 m yakın + 50 m uzak çapraz X",
                "X-Drill • 4 Hedef (18m & 50m)",
                new[]
                {
                    new TargetPlacementSpec(TargetKind.MetalHumanSilhouette, 18f, -3.0f), // Yakın Sol
                    new TargetPlacementSpec(TargetKind.MetalHumanSilhouette, 50f, +6.0f), // Uzak Sağ
                    new TargetPlacementSpec(TargetKind.MetalHumanSilhouette, 18f, +3.0f), // Yakın Sağ
                    new TargetPlacementSpec(TargetKind.MetalHumanSilhouette, 50f, -6.0f)  // Uzak Sol
                },
                new[]
                {
                    "X-Drill: Yakın ve uzak 4 hedef arasında çapraz bir 'X' rotası izleyerek nişan geçişi yapın.",
                    "18 m sol yakın hedefi vurduktan sonra doğrudan çaprazdaki 50 m sağ uzak hedefe geçin.",
                    "Ardından 18 m sağ yakın hedefe inip son olarak çaprazdaki 50 m sol uzak hedefi vurun.",
                    "Her geçişte önce gözünüzle hedefi kilitleyin, ardından tüfeği keskin ama sarsıntısız hedefe oturtun."
                }),

            new TacticalDrillDefinition(
                TacticalDrillId.Mozambique,
                "DrillBtn_Mozambique",
                "Mozambique Drill (2 Gövde + 1 Kafa)",
                "1 Metal Siluet • 15 m • Hızlı ritimden hassas vuruşa",
                "Mozambique • 1 Hedef (15m)",
                new[]
                {
                    new TargetPlacementSpec(TargetKind.MetalHumanSilhouette, 15f, 0f)
                },
                new[]
                {
                    "Mozambique Drill (Failure to Stop): 15 m mesafedeki hedefin göğüs merkezine 2 hızlı atış yapın.",
                    "Hedefin çelik yelek nedeniyle etkisiz kalmadığını varsayarak ritmi anında yavaşlatın.",
                    "Nişan noktasını dikkatle baş bölgesine kaldırıp 1 hassas kafa atışıyla seriyi tamamlayın.",
                    "Amaç, seri atış hızından bir anda yüksek hassasiyetli nişancılığa geçiş kontrolünü kazanmaktır."
                }),

            new TacticalDrillDefinition(
                TacticalDrillId.BoxDrill,
                "DrillBtn_BoxDrill",
                "Box Drill (Kutu Drili — 2 Hedef)",
                "2 Metal Siluet • 15 m • 2.4 m aralıkla kutu rotası",
                "Box Drill • 2 Hedef (15m)",
                new[]
                {
                    new TargetPlacementSpec(TargetKind.MetalHumanSilhouette, 15f, -1.2f),
                    new TargetPlacementSpec(TargetKind.MetalHumanSilhouette, 15f, +1.2f)
                },
                new[]
                {
                    "Box Drill (Kutu Drili): Yan yana duran 2 hedef üzerinde dikdörtgen bir atış rotası izleyin.",
                    "Önce sol hedefin göğsüne 2 hızlı atış, hemen ardından sağ hedefin göğsüne 2 hızlı atış yapın.",
                    "Sonra sağ hedefin kafasına 1 hassas atış yapıp sol hedefin kafasına 1 hassas atışla kutuyu kapatın.",
                    "Gövde vuruşlarında seri tempoyu, kafa vuruşlarında ise sakin tetik ezme disiplinini koruyun."
                }),

            new TacticalDrillDefinition(
                TacticalDrillId.BillDrill,
                "DrillBtn_BillDrill",
                "Bill Drill (6 Seri Atış)",
                "1 Metal Siluet • 12 m • Geri tepme & tetik kontrolü",
                "Bill Drill • 1 Hedef (12m)",
                new[]
                {
                    new TargetPlacementSpec(TargetKind.MetalHumanSilhouette, 12f, 0f)
                },
                new[]
                {
                    "Bill Drill: 12 m mesafedeki tek hedefe hazır duruştan mümkün olan en kısa sürede 6 el ateş edin.",
                    "Tüfeği omuzda sıkıca sabitleyip her atış sonrası Red Dot'un hedef merkezine dönüşünü izleyin.",
                    "6 merminin tamamını göğüs merkezinde toplamak için tetik sıfırlama ve geri tepme kontrolüne odaklanın."
                }),

            new TacticalDrillDefinition(
                TacticalDrillId.OneToFiveDrill,
                "DrillBtn_1to5",
                "1-5 Drill (3 Hedef Ritim)",
                "3 Metal Siluet • 20 m • 1-2-3-4-5 ritim (15 mermi)",
                "1-5 Drill • 3 Hedef (20m)",
                new[]
                {
                    new TargetPlacementSpec(TargetKind.MetalHumanSilhouette, 20f, -2.8f),
                    new TargetPlacementSpec(TargetKind.MetalHumanSilhouette, 20f,  0.0f),
                    new TargetPlacementSpec(TargetKind.MetalHumanSilhouette, 20f, +2.8f)
                },
                new[]
                {
                    "1-5 Drill: 20 m mesafedeki 3 hedefe artan sayılarda toplam 15 mermiyle ritim çalışması yapın.",
                    "Sırasıyla: 1. hedefe 1 atış, 2. hedefe 2 atış ve 3. hedefe 3 atış yapın.",
                    "Hemen geri dönerek 2. hedefe 4 atış ve son olarak 1. hedefe 5 atışla seriyi bitirin.",
                    "Bu dril değişen atış sayılarında ritim disiplinini ve hedef geçişinde mermi sayımını ölçer."
                }),

            new TacticalDrillDefinition(
                TacticalDrillId.ElPresidente,
                "DrillBtn_ElPresidente",
                "El Presidente (3 Hedef + Şarjör)",
                "3 Metal Siluet • 18 m • 2x3 atış + şarjör + 2x3 atış",
                "El Presidente • 3 Hedef (18m)",
                new[]
                {
                    new TargetPlacementSpec(TargetKind.MetalHumanSilhouette, 18f, -2.5f),
                    new TargetPlacementSpec(TargetKind.MetalHumanSilhouette, 18f,  0.0f),
                    new TargetPlacementSpec(TargetKind.MetalHumanSilhouette, 18f, +2.5f)
                },
                new[]
                {
                    "El Presidente: İsterseniz hedeflere sırtınız dönük başlayıp 180 derece dönerek nişan alın.",
                    "18 m mesafedeki 3 hedefin her birine soldan sağa 2'şer hızlı atış yapın (ilk 6 mermi).",
                    "Ardından şarjör yenileyip tekrar her hedefe 2'şer atış yaparak toplam 12 atışı tamamlayın.",
                    "Bu dril dönüş sonrası ilk hedefi yakalama, şarjör değişimi ve çoklu hedef akıcılığını test eder."
                }),

            new TacticalDrillDefinition(
                TacticalDrillId.MovingThreatLead,
                "DrillBtn_MovingThreat",
                "Hareketli Tehdit Takibi (Asker)",
                "2 Hareketli KSK Askeri • 25 m & 45 m • Önleme atışı",
                "Hareketli Tehdit • 2 Asker (25m & 45m)",
                new[]
                {
                    new TargetPlacementSpec(TargetKind.MovingEnemySoldier, 25f, -3.0f, patrolHalfWidthMeters: 4.5f, assaultMode: false),
                    new TargetPlacementSpec(TargetKind.MovingEnemySoldier, 45f, +3.5f, patrolHalfWidthMeters: 6.5f, assaultMode: false)
                },
                new[]
                {
                    "Hareketli Tehdit Takibi: 25 m ve 45 m mesafede ani yön değiştiren 2 hareketli askeri etkisiz hale getirin.",
                    "Askerin koşu yönüne doğru hafif önleme (lead) verin ve hız değişim anlarını yakalayın.",
                    "Önce yakın tehdit olan 25 m'deki askeri, ardından 45 m'deki uzak askeri temizleyin."
                })
        };

        public static TacticalDrillDefinition GetById(TacticalDrillId id)
        {
            for (int i = 0; i < AllDrills.Length; i++)
            {
                if (AllDrills[i].Id == id)
                    return AllDrills[i];
            }
            return null;
        }

        /// <summary>
        /// Splits long instructional steps automatically into crisp, easy-to-read VR subtitle chunks
        /// while preserving natural Turkish sentence boundaries.
        /// </summary>
        public static List<string> ExpandIntoReadableChunks(string[] rawSteps, int maxCharsPerChunk = 128)
        {
            List<string> chunks = new List<string>(8);
            if (rawSteps == null)
                return chunks;

            for (int i = 0; i < rawSteps.Length; i++)
            {
                string step = rawSteps[i];
                if (string.IsNullOrWhiteSpace(step))
                    continue;

                string trimmed = step.Trim();
                if (trimmed.Length <= maxCharsPerChunk)
                {
                    chunks.Add(trimmed);
                    continue;
                }

                // Split by sentence punctuation (. ! ?) first, then fall back to word wrap if still long
                int start = 0;
                while (start < trimmed.Length)
                {
                    int remaining = trimmed.Length - start;
                    if (remaining <= maxCharsPerChunk)
                    {
                        chunks.Add(trimmed.Substring(start).Trim());
                        break;
                    }

                    int splitAt = -1;
                    int searchEnd = Mathf.Min(trimmed.Length - 1, start + maxCharsPerChunk);
                    for (int p = searchEnd; p > start + 24; p--)
                    {
                        char ch = trimmed[p];
                        if (ch == '.' || ch == '!' || ch == '?' || ch == ';')
                        {
                            splitAt = p + 1;
                            break;
                        }
                    }

                    if (splitAt < 0)
                    {
                        for (int p = searchEnd; p > start + 24; p--)
                        {
                            if (trimmed[p] == ' ')
                            {
                                splitAt = p;
                                break;
                            }
                        }
                    }

                    if (splitAt <= start)
                        splitAt = start + maxCharsPerChunk;

                    string piece = trimmed.Substring(start, splitAt - start).Trim();
                    if (!string.IsNullOrEmpty(piece))
                        chunks.Add(piece);

                    start = splitAt;
                    while (start < trimmed.Length && trimmed[start] == ' ')
                        start++;
                }
            }

            return chunks;
        }

        /// <summary>
        /// Computes comfortable reading duration (>= 4.0s) scaled gently to word count so VR users never miss a line.
        /// </summary>
        public static float ComputeReadingDurationSeconds(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 4.0f;

            int words = 1;
            bool inSpace = false;
            for (int i = 0; i < text.Length; i++)
            {
                bool sp = char.IsWhiteSpace(text[i]);
                if (sp && !inSpace)
                    words++;
                inSpace = sp;
            }

            // Minimum 4.0s; ~0.28s per word beyond 10 words, capped at 7.2s
            return Mathf.Clamp(4.0f + Mathf.Max(0, words - 10) * 0.28f, 4.0f, 7.2f);
        }
    }
}
