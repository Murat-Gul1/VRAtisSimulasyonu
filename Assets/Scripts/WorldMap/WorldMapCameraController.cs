using System;
using UnityEngine;
using UnityEngine.EventSystems;
using VRAtis.Hardware;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
#endif

namespace VRAtis.WorldMap
{
    /// <summary>
    /// Mount & Blade: Warband-inspired angled top-down campaign map camera and ground cursor.
    /// Supports ESP32-S3 rifle HW-504 analog joystick + center button, standard Gamepad sticks,
    /// mobile touch (1-finger pan, 2-finger pinch zoom & twist orbit), and PC keyboard/mouse.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public class WorldMapCameraController : MonoBehaviour
    {
        [Header("Donanım ve İmleç Referansları")]
        [SerializeField] private Esp32RifleReceiver rifleReceiver;
        [SerializeField] private Transform cursorRingTransform;
        [SerializeField] private float cursorGroundHeight = 0.04f;
        [SerializeField] private float cursorBaseScale = 3.2f;

        [Header("Harita Sınırları")]
        [SerializeField] private Vector2 mapBoundsMin = new Vector2(-100f, -100f);
        [SerializeField] private Vector2 mapBoundsMax = new Vector2(100f, 100f);

        [Header("Kamera Mesafe ve Açı (Warband Tarzı)")]
        [SerializeField] private float defaultDistance = 42f;
        [SerializeField] private float minDistance = 16f;
        [SerializeField] private float maxDistance = 90f;
        [SerializeField] private float minPitchDeg = 38f;
        [SerializeField] private float maxPitchDeg = 66f;
        [SerializeField] private float defaultYawDeg = 0f;

        [Header("Joystick Ayarları (ESP32 & Gamepad)")]
        [SerializeField] private float joystickPanSpeed = 38f;
        [SerializeField] private float gamepadOrbitSpeed = 85f;
        [SerializeField] private float gamepadZoomSpeed = 36f;
        [Range(0f, 0.35f)]
        [SerializeField] private float joystickDeadzone = 0.08f;
        [Range(1f, 2.5f)]
        [SerializeField] private float joystickResponseExponent = 1.35f;
        [SerializeField] private bool invertJoystickX = false;
        [SerializeField] private bool invertJoystickY = false;
        [SerializeField] private bool swapJoystickAxes = false;

        [Header("Klavye / Fare / Dokunmatik Ayarları")]
        [SerializeField] private float keyboardPanSpeed = 34f;
        [SerializeField] private float keyboardOrbitSpeed = 75f;
        [SerializeField] private float mouseDragPanFactor = 0.00145f;
        [SerializeField] private float touchDragPanFactor = 0.00155f;
        [SerializeField] private float mouseOrbitSensitivity = 0.24f;
        [SerializeField] private float scrollZoomSpeed = 4.5f;
        [SerializeField] private float pinchZoomSpeed = 0.08f;
        [SerializeField] private float twistOrbitSpeed = 1.0f;
        [SerializeField] private float smoothSharpness = 12f;

        /// <summary>
        /// Fired when the player presses the joystick confirm button (or Space/Enter on PC),
        /// passing the current ground cursor position on the world map.
        /// </summary>
        public event Action<Vector3> OnCursorConfirmPressed;

        private Vector3 _targetFocus;
        private Vector3 _currentFocus;
        private float _targetDistance;
        private float _currentDistance;
        private float _targetYawDeg;
        private float _currentYawDeg;

        private bool _isMousePanning;
        private bool _isMouseOrbiting;
        private bool _isSingleTouchPanning;
        private bool _isMultiTouchActive;
        private float _previousPinchDistance;
        private float _previousTwistAngleDeg;

        private bool _wasEsp32JoyButtonPressed;
        private float _cursorPulseTimer;
        private const float CursorPulseDuration = 0.22f;

        public Vector3 FocusPosition => _currentFocus;
        public Vector3 CursorWorldPosition => _currentFocus;
        public float CurrentDistance => _currentDistance;
        public bool IsEsp32Connected => rifleReceiver != null && rifleReceiver.IsConnected;

        private void Awake()
        {
            Time.timeScale = 1f;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            if (rifleReceiver == null)
                rifleReceiver = FindAnyObjectByType<Esp32RifleReceiver>();

            _targetFocus = Vector3.zero;
            _currentFocus = _targetFocus;
            _targetDistance = Mathf.Clamp(defaultDistance, minDistance, maxDistance);
            _currentDistance = _targetDistance;
            _targetYawDeg = defaultYawDeg;
            _currentYawDeg = _targetYawDeg;

            ApplyCameraAndCursorImmediate();
        }

        private void OnEnable()
        {
            if (rifleReceiver == null)
                rifleReceiver = FindAnyObjectByType<Esp32RifleReceiver>();

            if (rifleReceiver != null)
                rifleReceiver.OnRecenterButtonPressed += HandleRecenterPressed;
        }

        private void OnDisable()
        {
            if (rifleReceiver != null)
                rifleReceiver.OnRecenterButtonPressed -= HandleRecenterPressed;
        }

        private void Update()
        {
            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0.0001f, 0.05f);

            bool confirmPressed = false;
            HandleEsp32JoystickInput(dt, ref confirmPressed);

#if ENABLE_INPUT_SYSTEM
            HandleGamepadInput(dt, ref confirmPressed);

            bool handledTouch = HandleTouchInput();
            if (!handledTouch)
            {
                HandleKeyboardInput(dt, ref confirmPressed);
                HandleMouseInput();
            }
#endif

            ClampTargets();

            float lerpT = 1f - Mathf.Exp(-smoothSharpness * dt);
            _currentFocus = Vector3.Lerp(_currentFocus, _targetFocus, lerpT);
            _currentDistance = Mathf.Lerp(_currentDistance, _targetDistance, lerpT);
            _currentYawDeg = Mathf.LerpAngle(_currentYawDeg, _targetYawDeg, lerpT);

            if (_cursorPulseTimer > 0f)
                _cursorPulseTimer = Mathf.Max(0f, _cursorPulseTimer - dt);

            if (confirmPressed)
                TriggerCursorConfirm();

            ApplyCameraAndCursorImmediate();
        }

        public void FocusOn(Vector3 worldPosition)
        {
            _targetFocus = new Vector3(worldPosition.x, 0f, worldPosition.z);
            ClampTargets();
        }

        public void ResetView()
        {
            _targetFocus = Vector3.zero;
            _targetDistance = Mathf.Clamp(defaultDistance, minDistance, maxDistance);
            _targetYawDeg = defaultYawDeg;
            ClampTargets();
        }

        public void ZoomBy(float deltaDistance)
        {
            _targetDistance = Mathf.Clamp(_targetDistance + deltaDistance, minDistance, maxDistance);
        }

        public void RotateBy(float deltaYawDeg)
        {
            _targetYawDeg += deltaYawDeg;
        }

        public void TriggerCursorConfirm()
        {
            _cursorPulseTimer = CursorPulseDuration;
            OnCursorConfirmPressed?.Invoke(_currentFocus);
        }

        private void HandleRecenterPressed(uint _)
        {
            ResetView();
        }

        private void HandleEsp32JoystickInput(float dt, ref bool confirmPressed)
        {
            if (rifleReceiver == null || !rifleReceiver.IsConnected || !rifleReceiver.HasJoystickData)
            {
                _wasEsp32JoyButtonPressed = false;
                return;
            }

            Vector2 raw = rifleReceiver.JoystickAxis;
            if (swapJoystickAxes)
                raw = new Vector2(raw.y, raw.x);
            if (invertJoystickX)
                raw.x = -raw.x;
            if (invertJoystickY)
                raw.y = -raw.y;

            Vector2 shaped = ShapeStickInput(raw);
            if (shaped.sqrMagnitude > 1e-6f)
                MoveFocusAlongStick(shaped, joystickPanSpeed, dt);

            bool pressedNow = rifleReceiver.IsJoystickButtonPressed;
            if (pressedNow && !_wasEsp32JoyButtonPressed)
                confirmPressed = true;
            _wasEsp32JoyButtonPressed = pressedNow;
        }

#if ENABLE_INPUT_SYSTEM
        private void HandleGamepadInput(float dt, ref bool confirmPressed)
        {
            Gamepad pad = Gamepad.current;
            if (pad == null)
                return;

            Vector2 left = ShapeStickInput(pad.leftStick.ReadValue());
            if (left.sqrMagnitude > 1e-6f)
                MoveFocusAlongStick(left, joystickPanSpeed, dt);

            Vector2 right = ShapeStickInput(pad.rightStick.ReadValue());
            if (Mathf.Abs(right.x) > 0.01f)
                _targetYawDeg += right.x * gamepadOrbitSpeed * dt;
            if (Mathf.Abs(right.y) > 0.01f)
                _targetDistance -= right.y * gamepadZoomSpeed * dt;

            if (pad.buttonSouth.wasPressedThisFrame || pad.leftStickButton.wasPressedThisFrame)
                confirmPressed = true;
        }

        private bool HandleTouchInput()
        {
            Touchscreen touchscreen = Touchscreen.current;
            if (touchscreen == null)
                return false;

            TouchControl first = null;
            TouchControl second = null;
            var touches = touchscreen.touches;
            for (int i = 0; i < touches.Count; i++)
            {
                TouchControl t = touches[i];
                if (!t.press.isPressed)
                    continue;

                if (first == null)
                    first = t;
                else if (second == null)
                {
                    second = t;
                    break;
                }
            }

            if (first == null)
            {
                _isSingleTouchPanning = false;
                _isMultiTouchActive = false;
                return false;
            }

            if (second != null)
            {
                _isSingleTouchPanning = false;
                Vector2 p0 = first.position.ReadValue();
                Vector2 p1 = second.position.ReadValue();
                float dist = Vector2.Distance(p0, p1);
                float angleDeg = Mathf.Atan2(p1.y - p0.y, p1.x - p0.x) * Mathf.Rad2Deg;

                if (!_isMultiTouchActive)
                {
                    _isMultiTouchActive = true;
                    _previousPinchDistance = dist;
                    _previousTwistAngleDeg = angleDeg;
                    return true;
                }

                float pinchDelta = dist - _previousPinchDistance;
                _targetDistance -= pinchDelta * pinchZoomSpeed;

                float twistDelta = Mathf.DeltaAngle(_previousTwistAngleDeg, angleDeg);
                if (Mathf.Abs(twistDelta) < 25f)
                    _targetYawDeg -= twistDelta * twistOrbitSpeed;

                _previousPinchDistance = dist;
                _previousTwistAngleDeg = angleDeg;
                return true;
            }

            _isMultiTouchActive = false;

            if (first.press.wasPressedThisFrame)
            {
                int touchId = first.touchId.ReadValue();
                _isSingleTouchPanning = !IsPointerOverUi(touchId);
            }

            if (_isSingleTouchPanning)
            {
                Vector2 delta = first.delta.ReadValue();
                PanByScreenDelta(delta, touchDragPanFactor);
            }

            return true;
        }

        private void HandleKeyboardInput(float dt, ref bool confirmPressed)
        {
            Keyboard kb = Keyboard.current;
            if (kb == null)
                return;

            float moveX = 0f;
            float moveZ = 0f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) moveX -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) moveX += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) moveZ -= 1f;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) moveZ += 1f;

            if (moveX != 0f || moveZ != 0f)
            {
                Vector2 moveVec = new Vector2(moveX, moveZ);
                if (moveVec.sqrMagnitude > 1f)
                    moveVec.Normalize();
                MoveFocusAlongStick(moveVec, keyboardPanSpeed, dt);
            }

            if (kb.qKey.isPressed)
                _targetYawDeg -= keyboardOrbitSpeed * dt;
            if (kb.eKey.isPressed)
                _targetYawDeg += keyboardOrbitSpeed * dt;

            if (kb.homeKey.wasPressedThisFrame)
                ResetView();

            if (kb.spaceKey.wasPressedThisFrame || kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)
                confirmPressed = true;
        }

        private void HandleMouseInput()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null)
                return;

            bool overUi = IsPointerOverUi(-1);

            if (mouse.leftButton.wasPressedThisFrame)
                _isMousePanning = !overUi;
            if (!mouse.leftButton.isPressed)
                _isMousePanning = false;

            if (mouse.rightButton.wasPressedThisFrame || mouse.middleButton.wasPressedThisFrame)
                _isMouseOrbiting = !overUi;
            if (!mouse.rightButton.isPressed && !mouse.middleButton.isPressed)
                _isMouseOrbiting = false;

            Vector2 delta = mouse.delta.ReadValue();
            if (_isMousePanning && delta.sqrMagnitude > 0.0001f)
                PanByScreenDelta(delta, mouseDragPanFactor);

            if (_isMouseOrbiting && Mathf.Abs(delta.x) > 0.0001f)
                _targetYawDeg += delta.x * mouseOrbitSensitivity;

            if (!overUi)
            {
                float scrollY = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scrollY) > 0.01f)
                {
                    float normalizedScroll = Mathf.Sign(scrollY) * Mathf.Clamp(Mathf.Abs(scrollY) / 120f, 0.25f, 2f);
                    _targetDistance -= normalizedScroll * scrollZoomSpeed;
                }
            }
        }
#endif

        private Vector2 ShapeStickInput(Vector2 raw)
        {
            float mag = raw.magnitude;
            if (mag <= joystickDeadzone)
                return Vector2.zero;

            float remapped = Mathf.Clamp01((mag - joystickDeadzone) / Mathf.Max(0.001f, 1f - joystickDeadzone));
            float curved = Mathf.Pow(remapped, joystickResponseExponent);
            return (raw / mag) * curved;
        }

        private void MoveFocusAlongStick(Vector2 stick, float baseSpeed, float dt)
        {
            GetFlatCameraBasis(out Vector3 forward, out Vector3 right);
            Vector3 move = right * stick.x + forward * stick.y;
            float zoomScale = Mathf.Lerp(0.65f, 1.85f, GetZoomNormalized());
            _targetFocus += move * (baseSpeed * zoomScale * dt);
        }

        private void PanByScreenDelta(Vector2 screenDelta, float panFactor)
        {
            if (screenDelta.sqrMagnitude <= 0.0001f)
                return;

            GetFlatCameraBasis(out Vector3 forward, out Vector3 right);
            float scale = _currentDistance * panFactor;
            _targetFocus -= right * (screenDelta.x * scale);
            _targetFocus -= forward * (screenDelta.y * scale);
        }

        private void GetFlatCameraBasis(out Vector3 forward, out Vector3 right)
        {
            Quaternion yawRot = Quaternion.Euler(0f, _currentYawDeg, 0f);
            forward = yawRot * Vector3.forward;
            right = yawRot * Vector3.right;
        }

        private float GetZoomNormalized()
        {
            return Mathf.InverseLerp(minDistance, maxDistance, _currentDistance);
        }

        private void ClampTargets()
        {
            _targetFocus.x = Mathf.Clamp(_targetFocus.x, mapBoundsMin.x, mapBoundsMax.x);
            _targetFocus.y = 0f;
            _targetFocus.z = Mathf.Clamp(_targetFocus.z, mapBoundsMin.y, mapBoundsMax.y);
            _targetDistance = Mathf.Clamp(_targetDistance, minDistance, maxDistance);
        }

        private void ApplyCameraAndCursorImmediate()
        {
            float pitchDeg = Mathf.Lerp(minPitchDeg, maxPitchDeg, GetZoomNormalized());
            Quaternion camRotation = Quaternion.Euler(pitchDeg, _currentYawDeg, 0f);
            Vector3 offset = camRotation * new Vector3(0f, 0f, -_currentDistance);

            transform.position = _currentFocus + offset;
            transform.rotation = camRotation;

            if (cursorRingTransform != null)
            {
                cursorRingTransform.position = new Vector3(_currentFocus.x, cursorGroundHeight, _currentFocus.z);

                float zoomScale = Mathf.Lerp(0.70f, 1.65f, GetZoomNormalized());
                float pulseBoost = 1f;
                if (_cursorPulseTimer > 0f)
                {
                    float t = _cursorPulseTimer / CursorPulseDuration;
                    pulseBoost = 1f + Mathf.Sin(t * Mathf.PI) * 0.35f;
                }

                float diameter = cursorBaseScale * zoomScale * pulseBoost;
                cursorRingTransform.localScale = new Vector3(diameter, 0.03f, diameter);
            }
        }

        private static bool IsPointerOverUi(int pointerId)
        {
            if (EventSystem.current == null)
                return false;

            return pointerId >= 0
                ? EventSystem.current.IsPointerOverGameObject(pointerId)
                : EventSystem.current.IsPointerOverGameObject();
        }
    }
}
