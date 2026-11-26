using System.Collections.Generic;
using UnityEngine;

namespace VRAtis.Soldiers
{
    /// <summary>
    /// Dynamically configures any of the 5 nations' modular soldiers at runtime from a SoldierAppearanceProfile:
    /// - Blends 3 Body Builds (Standard / Heavy / Lean) + 26 distinct regional Face Archetypes (zero neck seam)
    /// - Applies skin, hair, iris, and base shorts colors via MaterialPropertyBlock (zero GC / zero material cloning!)
    /// - Toggles 6 modular Hair Styles & 4 Beard Styles
    /// - Supports dynamic runtime equipment mounting onto 7 skeleton sockets (Helmet, ChestRig, Weapon, etc.)
    /// </summary>
    [DisallowMultipleComponent]
    public class ModularSoldierAssembler : MonoBehaviour
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private static readonly string[] AllFaceBlendShapeNames =
        {
            // USA (0..5)
            "Face_US_01_EuroSquare", "Face_US_02_EuroLong", "Face_US_03_AfroBroad",
            "Face_US_04_AfroLean", "Face_US_05_Hispanic", "Face_US_06_AsianAmerican",
            // Turkey (6..10)
            "Face_TR_01_AnatolianStrong", "Face_TR_02_AegeanLean", "Face_TR_03_BlackSeaSharp",
            "Face_TR_04_EasternBroad", "Face_TR_05_CentralClassic",
            // Soviet (11..15)
            "Face_SU_01_SlavSquare", "Face_SU_02_SlavHighCheek", "Face_SU_03_UralBroad",
            "Face_SU_04_CaucasusSharp", "Face_SU_05_SiberianMix",
            // China (16..20)
            "Face_CN_01_NorthStrong", "Face_CN_02_SouthLean", "Face_CN_03_BroadRound",
            "Face_CN_04_HighlandAngular", "Face_CN_05_ClassicOfficer",
            // Rebels (21..25)
            "Face_RB_01_DesertGaunt", "Face_RB_02_BruteHeavy", "Face_RB_03_CorsairSharp",
            "Face_RB_04_MercenaryWide", "Face_RB_05_NomadLean"
        };

        [Header("Otomatik Bağlanan Parçalar")]
        [SerializeField] private SkinnedMeshRenderer bodyRenderer;
        [SerializeField] private SkinnedMeshRenderer ptShortsRenderer;
        [SerializeField] private SkinnedMeshRenderer eyesRenderer;
        [SerializeField] private SkinnedMeshRenderer eyebrowsRenderer;
        [SerializeField] private SkinnedMeshRenderer hairBuzzRenderer;
        [SerializeField] private SkinnedMeshRenderer hairHighAndTightRenderer;
        [SerializeField] private SkinnedMeshRenderer hairShortPartRenderer;
        [SerializeField] private SkinnedMeshRenderer hairFullThickRenderer;
        [SerializeField] private SkinnedMeshRenderer hairShaggyRoughRenderer;
        [SerializeField] private SkinnedMeshRenderer beardShortRenderer;
        [SerializeField] private SkinnedMeshRenderer beardMustacheRenderer;
        [SerializeField] private SkinnedMeshRenderer beardFullRuggedRenderer;

        [Header("Dinamik Teçhizat Soketleri")]
        [SerializeField] private Transform socketHelmet;
        [SerializeField] private Transform socketEyeWear;
        [SerializeField] private Transform socketChestRig;
        [SerializeField] private Transform socketBackPack;
        [SerializeField] private Transform socketWeaponRightHand;
        [SerializeField] private Transform socketHolsterHip;

        [Header("Aktif Profil")]
        [SerializeField] private SoldierAppearanceProfile currentProfile;

        private readonly Dictionary<SoldierEquipmentSlot, GameObject> _equippedObjects = new Dictionary<SoldierEquipmentSlot, GameObject>(8);
        private MaterialPropertyBlock _mpb;

        public SoldierAppearanceProfile CurrentProfile => currentProfile;

        private void Awake()
        {
            AutoResolveReferences();
            _mpb = new MaterialPropertyBlock();
            if (!string.IsNullOrEmpty(currentProfile.recruitName))
                ApplyProfile(currentProfile);
        }

        public void AutoResolveReferences()
        {
            if (bodyRenderer == null) bodyRenderer = FindChildRenderer("Soldier_Body");
            if (ptShortsRenderer == null) ptShortsRenderer = FindChildRenderer("Base_PTShorts");
            if (eyesRenderer == null) eyesRenderer = FindChildRenderer("Soldier_Eyes");
            if (eyebrowsRenderer == null) eyebrowsRenderer = FindChildRenderer("Soldier_Eyebrows");
            if (hairBuzzRenderer == null) hairBuzzRenderer = FindChildRenderer("Hair_BuzzCut");
            if (hairHighAndTightRenderer == null) hairHighAndTightRenderer = FindChildRenderer("Hair_HighAndTight");
            if (hairShortPartRenderer == null) hairShortPartRenderer = FindChildRenderer("Hair_ShortSidePart");
            if (hairFullThickRenderer == null) hairFullThickRenderer = FindChildRenderer("Hair_FullThick");
            if (hairShaggyRoughRenderer == null) hairShaggyRoughRenderer = FindChildRenderer("Hair_ShaggyRough");
            if (beardShortRenderer == null) beardShortRenderer = FindChildRenderer("Beard_Short");
            if (beardMustacheRenderer == null) beardMustacheRenderer = FindChildRenderer("Beard_Mustache");
            if (beardFullRuggedRenderer == null) beardFullRuggedRenderer = FindChildRenderer("Beard_FullRugged");

            if (socketHelmet == null) socketHelmet = FindDeepChild("Socket_Helmet");
            if (socketEyeWear == null) socketEyeWear = FindDeepChild("Socket_EyeWear");
            if (socketChestRig == null) socketChestRig = FindDeepChild("Socket_ChestRig");
            if (socketBackPack == null) socketBackPack = FindDeepChild("Socket_BackPack");
            if (socketWeaponRightHand == null) socketWeaponRightHand = FindDeepChild("Socket_WeaponRightHand");
            if (socketHolsterHip == null) socketHolsterHip = FindDeepChild("Socket_HolsterHip");
        }

        public void ApplyProfile(SoldierAppearanceProfile profile)
        {
            AutoResolveReferences();
            if (_mpb == null) _mpb = new MaterialPropertyBlock();
            currentProfile = profile;

            float hScale = Mathf.Clamp(profile.heightScale > 0.1f ? profile.heightScale : 1f, 0.90f, 1.12f);
            transform.localScale = Vector3.one * hScale;

            // 1. Body Build BlendShapes (Standard / Heavy / Lean)
            float heavyWeight = profile.bodyBuild == SoldierBodyBuild.Heavy ? 100f : 0f;
            float leanWeight = profile.bodyBuild == SoldierBodyBuild.Lean ? 100f : 0f;
            SetBlendShape(bodyRenderer, "Build_Heavy", heavyWeight);
            SetBlendShape(bodyRenderer, "Build_Lean", leanWeight);
            SetBlendShape(ptShortsRenderer, "Build_Heavy", heavyWeight);
            SetBlendShape(ptShortsRenderer, "Build_Lean", leanWeight);

            // 2. Regional Face Archetype BlendShape (all 26 faces synced across Body, Eyebrows & all 3 Beards!)
            int faceIdx = Mathf.Clamp((int)profile.faceArchetype, 0, AllFaceBlendShapeNames.Length - 1);
            for (int i = 0; i < AllFaceBlendShapeNames.Length; i++)
            {
                float w = (i == faceIdx) ? 100f : 0f;
                string shapeName = AllFaceBlendShapeNames[i];
                SetBlendShape(bodyRenderer, shapeName, w);
                SetBlendShape(eyebrowsRenderer, shapeName, w);
                SetBlendShape(beardShortRenderer, shapeName, w);
                SetBlendShape(beardMustacheRenderer, shapeName, w);
                SetBlendShape(beardFullRuggedRenderer, shapeName, w);
            }

            // 3. Toggle Modular Hair & Beard Meshes
            ApplyHairVisibility(helmetEquipped: _equippedObjects.ContainsKey(SoldierEquipmentSlot.Helmet));
            SetRendererActive(beardShortRenderer, profile.beardStyle == SoldierBeardStyle.ShortBeard);
            SetRendererActive(beardMustacheRenderer, profile.beardStyle == SoldierBeardStyle.Mustache);
            SetRendererActive(beardFullRuggedRenderer, profile.beardStyle == SoldierBeardStyle.FullRugged);

            // 4. Apply Skin, Hair, Iris & Shorts Colors via MaterialPropertyBlock (zero GC / zero mat clone!)
            ApplyColorToRenderer(bodyRenderer, profile.skinColor, submeshIndex: 0);
            ApplyColorToRenderer(eyebrowsRenderer, profile.hairColor, submeshIndex: 0);
            ApplyColorToRenderer(hairBuzzRenderer, profile.hairColor, submeshIndex: 0);
            ApplyColorToRenderer(hairHighAndTightRenderer, profile.hairColor, submeshIndex: 0);
            ApplyColorToRenderer(hairShortPartRenderer, profile.hairColor, submeshIndex: 0);
            ApplyColorToRenderer(hairFullThickRenderer, profile.hairColor, submeshIndex: 0);
            ApplyColorToRenderer(hairShaggyRoughRenderer, profile.hairColor, submeshIndex: 0);
            ApplyColorToRenderer(beardShortRenderer, profile.hairColor, submeshIndex: 0);
            ApplyColorToRenderer(beardMustacheRenderer, profile.hairColor, submeshIndex: 0);
            ApplyColorToRenderer(beardFullRuggedRenderer, profile.hairColor, submeshIndex: 0);
            ApplyColorToRenderer(eyesRenderer, profile.irisColor, submeshIndex: 1);

            Color shortsCol = profile.shortsColor.a > 0.01f
                ? profile.shortsColor
                : SoldierAppearanceProfile.GetFactionShortsColor(profile.faction);
            ApplyColorToRenderer(ptShortsRenderer, shortsCol, submeshIndex: 0);
        }

        public GameObject EquipToSlot(SoldierEquipmentSlot slot, GameObject gearPrefab)
        {
            ClearSlot(slot);
            if (gearPrefab == null)
                return null;

            Transform socket = ResolveSocket(slot);
            if (socket == null)
                return null;

            GameObject instance = Instantiate(gearPrefab, socket);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;
            _equippedObjects[slot] = instance;

            if (slot == SoldierEquipmentSlot.PantsAndBoots && ptShortsRenderer != null)
                ptShortsRenderer.gameObject.SetActive(false);

            if (slot == SoldierEquipmentSlot.Helmet)
                ApplyHairVisibility(helmetEquipped: true);

            return instance;
        }

        public void ClearSlot(SoldierEquipmentSlot slot)
        {
            if (_equippedObjects.TryGetValue(slot, out GameObject existing))
            {
                if (existing != null)
                    Destroy(existing);
                _equippedObjects.Remove(slot);
            }

            if (slot == SoldierEquipmentSlot.PantsAndBoots && ptShortsRenderer != null)
                ptShortsRenderer.gameObject.SetActive(true);

            if (slot == SoldierEquipmentSlot.Helmet)
                ApplyHairVisibility(helmetEquipped: false);
        }

        private void ApplyHairVisibility(bool helmetEquipped)
        {
            // Under helmets, hide tall/full hair caps cleanly while buzz/shaggy nape stays tidy
            SetRendererActive(hairBuzzRenderer, currentProfile.hairStyle == SoldierHairStyle.BuzzCut);
            SetRendererActive(hairHighAndTightRenderer, !helmetEquipped && currentProfile.hairStyle == SoldierHairStyle.HighAndTight);
            SetRendererActive(hairShortPartRenderer, !helmetEquipped && currentProfile.hairStyle == SoldierHairStyle.ShortSidePart);
            SetRendererActive(hairFullThickRenderer, !helmetEquipped && currentProfile.hairStyle == SoldierHairStyle.FullThick);
            SetRendererActive(hairShaggyRoughRenderer, !helmetEquipped && currentProfile.hairStyle == SoldierHairStyle.ShaggyRough);
        }

        private Transform ResolveSocket(SoldierEquipmentSlot slot)
        {
            switch (slot)
            {
                case SoldierEquipmentSlot.Helmet:        return socketHelmet;
                case SoldierEquipmentSlot.EyeWear:       return socketEyeWear;
                case SoldierEquipmentSlot.ChestRig:      return socketChestRig;
                case SoldierEquipmentSlot.BackPack:      return socketBackPack;
                case SoldierEquipmentSlot.PrimaryWeapon: return socketWeaponRightHand;
                case SoldierEquipmentSlot.HolsterSidearm:return socketHolsterHip;
                default:                                 return transform;
            }
        }

        private void ApplyColorToRenderer(Renderer r, Color color, int submeshIndex)
        {
            if (r == null) return;
            r.GetPropertyBlock(_mpb, submeshIndex);
            _mpb.SetColor(BaseColorId, color);
            _mpb.SetColor(ColorId, color);
            r.SetPropertyBlock(_mpb, submeshIndex);
        }

        private static void SetBlendShape(SkinnedMeshRenderer smr, string shapeName, float weight)
        {
            if (smr == null || smr.sharedMesh == null) return;
            int idx = smr.sharedMesh.GetBlendShapeIndex(shapeName);
            if (idx >= 0)
                smr.SetBlendShapeWeight(idx, weight);
        }

        private static void SetRendererActive(SkinnedMeshRenderer smr, bool active)
        {
            if (smr != null && smr.gameObject.activeSelf != active)
                smr.gameObject.SetActive(active);
        }

        private SkinnedMeshRenderer FindChildRenderer(string exactName)
        {
            Transform t = FindDeepChild(exactName);
            return t != null ? t.GetComponent<SkinnedMeshRenderer>() : null;
        }

        private Transform FindDeepChild(string exactName)
        {
            Transform[] all = GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].name == exactName)
                    return all[i];
            }
            return null;
        }
    }
}
