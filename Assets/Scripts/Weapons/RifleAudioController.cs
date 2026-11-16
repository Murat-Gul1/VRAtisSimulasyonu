using UnityEngine;

namespace VRAtis.Weapons
{
    /// <summary>
    /// Plays realistic AR-15 gunshot audio with pooled polyphonic AudioSources at the muzzle,
    /// 3D spatialized brass casing bounce audio at ground impact points,
    /// and separate magazine eject/insert mechanical sounds.
    /// </summary>
    public class RifleAudioController : MonoBehaviour
    {
        [Header("Referanslar")]
        [SerializeField] private Transform muzzleTransform;

        [Header("AR-15 Ateş Sesi (5.56x45mm)")]
        [SerializeField] private AudioClip[] gunshotClips;
        [SerializeField] private int shotPoolSize = 6;
        [Range(0f, 1f)]
        [SerializeField] private float shotVolume = 1.0f;
        [SerializeField] private Vector2 volumeVariation = new Vector2(0.96f, 1.0f);
        [SerializeField] private Vector2 pitchVariation = new Vector2(0.96f, 1.04f);
        [Range(0f, 1f)]
        [SerializeField] private float spatialBlend = 0.25f;

        [Header("Boş Kovan Yere Düşme Sesi")]
        [SerializeField] private AudioClip[] casingBounceClips;
        [SerializeField] private int casingPoolSize = 4;
        [Range(0f, 1f)]
        [SerializeField] private float casingVolume = 0.45f;
        [SerializeField] private Vector2 casingPitchVariation = new Vector2(0.92f, 1.08f);
        [Range(0f, 1f)]
        [SerializeField] private float casingSpatialBlend = 0.85f;

        [Header("Şarjör Çıkarma ve Takma Sesleri")]
        [SerializeField] private AudioClip[] magEjectClips;
        [SerializeField] private AudioClip[] magInsertClips;
        [Range(0f, 1f)]
        [SerializeField] private float magVolume = 0.72f;
        [SerializeField] private Vector2 magPitchVariation = new Vector2(0.96f, 1.04f);
        [Range(0f, 1f)]
        [SerializeField] private float magSpatialBlend = 0.35f;

        [Header("Dağ Vadisi Yankısı")]
        [SerializeField] private bool enableMountainValleyReverb = false;

        private AudioSource[] _shotPool;
        private int _shotPoolIndex;
        private int _lastShotClipIndex = -1;

        private AudioSource[] _casingPool;
        private int _casingPoolIndex;
        private int _lastCasingClipIndex = -1;

        private AudioSource _mechSource;
        private int _lastMagEjectIndex = -1;
        private int _lastMagInsertIndex = -1;

        private void Awake()
        {
            if (muzzleTransform == null)
                muzzleTransform = transform;

            BuildShotAudioPool();
            BuildCasingAudioPool();
            BuildMechanismSource();

            if (enableMountainValleyReverb)
                EnsureValleyReverbZone();
            else
                CleanupValleyReverbZone();
        }

        /// <summary>
        /// Call from RifleWeapon.onWeaponFired UnityEvent.
        /// </summary>
        public void PlayGunshot()
        {
            if (gunshotClips == null || gunshotClips.Length == 0 || _shotPool == null || _shotPool.Length == 0)
                return;

            AudioClip clip = SelectRandomClip(gunshotClips, ref _lastShotClipIndex);
            if (clip == null)
                return;

            AudioSource src = _shotPool[_shotPoolIndex];
            _shotPoolIndex = (_shotPoolIndex + 1) % _shotPool.Length;

            src.clip = clip;
            src.pitch = Random.Range(pitchVariation.x, pitchVariation.y);
            src.volume = shotVolume * Random.Range(volumeVariation.x, volumeVariation.y);
            src.Play();
        }

        /// <summary>
        /// Plays a 3D brass casing clink at the world impact point when an ejected shell hits the ground.
        /// </summary>
        public void PlayCasingBounce(Vector3 worldPos, float impactStrength = 1f)
        {
            if (casingBounceClips == null || casingBounceClips.Length == 0 || _casingPool == null || _casingPool.Length == 0)
                return;

            AudioClip clip = SelectRandomClip(casingBounceClips, ref _lastCasingClipIndex);
            if (clip == null)
                return;

            AudioSource src = _casingPool[_casingPoolIndex];
            _casingPoolIndex = (_casingPoolIndex + 1) % _casingPool.Length;

            src.transform.position = worldPos;
            src.clip = clip;
            src.pitch = Random.Range(casingPitchVariation.x, casingPitchVariation.y);
            src.volume = casingVolume * Mathf.Clamp(impactStrength, 0.35f, 1.0f);
            src.Play();
        }

        /// <summary>
        /// Call from RifleWeapon.onMagazineEjected UnityEvent (or physical mag-eject sensor).
        /// </summary>
        public void PlayMagEject()
        {
            PlayMechanismClip(magEjectClips, ref _lastMagEjectIndex, magVolume, magPitchVariation);
        }

        /// <summary>
        /// Call from RifleWeapon.onMagazineInserted UnityEvent (or physical mag-insert sensor).
        /// </summary>
        public void PlayMagInsert()
        {
            PlayMechanismClip(magInsertClips, ref _lastMagInsertIndex, magVolume, magPitchVariation);
        }

        private void PlayMechanismClip(AudioClip[] clips, ref int lastIndex, float volume, Vector2 pitchRange)
        {
            if (_mechSource == null || clips == null || clips.Length == 0)
                return;

            AudioClip clip = SelectRandomClip(clips, ref lastIndex);
            if (clip == null)
                return;

            _mechSource.pitch = Random.Range(pitchRange.x, pitchRange.y);
            _mechSource.PlayOneShot(clip, volume);
        }

        private static AudioClip SelectRandomClip(AudioClip[] clips, ref int lastIndex)
        {
            if (clips.Length == 1)
                return clips[0];

            int idx = Random.Range(0, clips.Length);
            if (idx == lastIndex)
                idx = (idx + 1) % clips.Length;
            lastIndex = idx;
            return clips[idx];
        }

        private void BuildShotAudioPool()
        {
            int count = Mathf.Max(2, shotPoolSize);
            _shotPool = new AudioSource[count];

            GameObject poolRoot = new GameObject("GunshotAudioPool");
            poolRoot.transform.SetParent(muzzleTransform, false);

            for (int i = 0; i < count; i++)
            {
                AudioSource src = poolRoot.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.loop = false;
                src.spatialBlend = spatialBlend;
                src.dopplerLevel = 0f;
                src.minDistance = 1.5f;
                src.maxDistance = 200f;
                src.rolloffMode = AudioRolloffMode.Logarithmic;
                _shotPool[i] = src;
            }
        }

        private void BuildCasingAudioPool()
        {
            int count = Mathf.Max(2, casingPoolSize);
            _casingPool = new AudioSource[count];

            GameObject poolRoot = new GameObject("CasingAudioPool");
            poolRoot.transform.SetParent(transform, false);

            for (int i = 0; i < count; i++)
            {
                GameObject voiceGo = new GameObject($"CasingVoice_{i}");
                voiceGo.transform.SetParent(poolRoot.transform, false);
                AudioSource src = voiceGo.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.loop = false;
                src.spatialBlend = casingSpatialBlend;
                src.dopplerLevel = 0f;
                src.minDistance = 0.4f;
                src.maxDistance = 25f;
                src.rolloffMode = AudioRolloffMode.Logarithmic;
                _casingPool[i] = src;
            }
        }

        private void BuildMechanismSource()
        {
            GameObject mechGo = new GameObject("MechanismAudioSource");
            mechGo.transform.SetParent(transform, false);
            _mechSource = mechGo.AddComponent<AudioSource>();
            _mechSource.playOnAwake = false;
            _mechSource.loop = false;
            _mechSource.spatialBlend = magSpatialBlend;
            _mechSource.dopplerLevel = 0f;
            _mechSource.minDistance = 0.4f;
            _mechSource.maxDistance = 18f;
            _mechSource.rolloffMode = AudioRolloffMode.Logarithmic;
        }

        private void EnsureValleyReverbZone()
        {
            if (FindAnyObjectByType<AudioReverbZone>() != null)
                return;

            GameObject zoneGo = new GameObject("MountainValley_ReverbZone");
            zoneGo.transform.position = new Vector3(0f, 1.5f, 200f);
            AudioReverbZone zone = zoneGo.AddComponent<AudioReverbZone>();
            zone.reverbPreset = AudioReverbPreset.Mountains;
            zone.minDistance = 400f;
            zone.maxDistance = 750f;
        }

        private static void CleanupValleyReverbZone()
        {
            GameObject existing = GameObject.Find("MountainValley_ReverbZone");
            if (existing != null)
                Destroy(existing);
        }
    }
}
