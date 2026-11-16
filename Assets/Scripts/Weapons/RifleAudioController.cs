using UnityEngine;

namespace VRAtis.Weapons
{
    /// <summary>
    /// Plays realistic AR-15 gunshot audio with pooled polyphonic AudioSources at the muzzle,
    /// subtle pitch/volume variation per shot, zero trigger latency, and gentle mountain-valley reverb.
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

        [Header("Dağ Vadisi Yankısı")]
        [SerializeField] private bool enableMountainValleyReverb = false;

        private AudioSource[] _shotPool;
        private int _poolIndex;
        private int _lastClipIndex = -1;

        private void Awake()
        {
            if (muzzleTransform == null)
                muzzleTransform = transform;

            BuildShotAudioPool();

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

            AudioClip clip = SelectRandomClip();
            if (clip == null)
                return;

            AudioSource src = _shotPool[_poolIndex];
            _poolIndex = (_poolIndex + 1) % _shotPool.Length;

            src.clip = clip;
            src.pitch = Random.Range(pitchVariation.x, pitchVariation.y);
            src.volume = shotVolume * Random.Range(volumeVariation.x, volumeVariation.y);
            src.Play();
        }

        private AudioClip SelectRandomClip()
        {
            if (gunshotClips.Length == 1)
                return gunshotClips[0];

            int idx = Random.Range(0, gunshotClips.Length);
            if (idx == _lastClipIndex)
                idx = (idx + 1) % gunshotClips.Length;
            _lastClipIndex = idx;
            return gunshotClips[idx];
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
