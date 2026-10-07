using System;
using System.Reflection;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(OrbusFlatControls.FlatControlsMod), "Orbus Flat Controls", "0.1.0", "Arena")]

namespace OrbusFlatControls
{
    /// <summary>
    /// Desktop keyboard/mouse bridge for the IL2CPP Orbus controller objects.
    /// Game objects are found by metadata name so this project does not depend on
    /// a particular generated Assembly-CSharp proxy version.
    /// </summary>
    public sealed class FlatControlsMod : MelonMod
    {
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Vector3 DefaultLeftOffset = new Vector3(-0.30f, -0.23f, 0.48f);
        private static readonly Vector3 DefaultRightOffset = new Vector3(0.30f, -0.23f, 0.48f);
        private static readonly Rect PanelRect = new Rect(12f, 12f, 390f, 292f);

        private readonly HandPose _left = new HandPose(DefaultLeftOffset);
        private readonly HandPose _right = new HandPose(DefaultRightOffset);

        private MonoBehaviour _localAvatar;
        private Transform _viewTransform;
        private HandSide _activeHand = HandSide.Right;

        private bool _enabled = true;
        private bool _showUi;
        private bool _mouseCaptured;
        private bool _anglesInitialized;
        private float _yaw;
        private float _pitch;
        private float _nextBindAttempt;
        private float _nextXrAttempt;
        private float _moveSpeed = 0.85f;
        private float _lookSensitivity = 2.2f;
        private string _bindingStatus = "Waiting for the local player...";
        private bool _loggedBound;
        private bool _loggedScanFailure;
        private bool _loggedInputFallback;

        // XR is disabled by default for a desktop session. F9 can turn it back on.
        private bool _disableXrForFlatScreen = true;
        private PropertyInfo _xrEnabledProperty;
        private bool _xrOriginalStateKnown;
        private bool _xrOriginalEnabled;
        private bool _xrSettingApplied;
        private bool _loggedXrFailure;

        public override void OnInitializeMelon()
        {
            MelonLogger.Msg("[OrbusFlatControls] Loaded. F1 opens controls, F6 captures the mouse, F8 toggles the mod, F9 toggles XR.");
        }

        public override void OnUpdate()
        {
            try
            {
                HandleHotkeys();

                if (!_enabled)
                    return;

                if (Time.unscaledTime >= _nextBindAttempt && NeedsBinding())
                {
                    _nextBindAttempt = Time.unscaledTime + 1f;
                    TryBindLocalPlayer();
                }

                if (_localAvatar != null && Time.unscaledTime >= _nextXrAttempt && !_xrSettingApplied)
                {
                    _nextXrAttempt = Time.unscaledTime + 2f;
                    TryApplyXrSetting();
                }

                if (!HasUsableBinding())
                    return;

                UpdateMouseLook();
                UpdateHandMovement();
                UpdateControllerButtons();
                ApplyVirtualHands();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[OrbusFlatControls] Update recovered from an exception: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public override void OnLateUpdate()
        {
            if (!_enabled || !HasUsableBinding())
                return;

            try
            {
                // Re-apply after the game's tracking scripts so the virtual pose wins for this frame.
                if (_mouseCaptured && !_showUi)
                    ApplyMouseLookRotation();
                UpdateControllerButtons();
                ApplyVirtualHands();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[OrbusFlatControls] LateUpdate recovered from an exception: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public override void OnGUI()
        {
            if (!_showUi)
                return;

            try
            {
                GUI.Box(PanelRect, "Orbus Flat Controls");
                GUILayout.BeginArea(new Rect(PanelRect.x + 12f, PanelRect.y + 28f, PanelRect.width - 24f, PanelRect.height - 38f));

                GUILayout.Label("Status: " + _bindingStatus);
                GUILayout.Label("XR: " + (_disableXrForFlatScreen ? "disabled for flatscreen" : "allowed"));

                GUILayout.BeginHorizontal();
                if (GUILayout.Button(_enabled ? "Disable controls (F8)" : "Enable controls (F8)"))
                    ToggleEnabled();
                if (GUILayout.Button("Active hand: " + _activeHand + " (Tab)"))
                    ToggleActiveHand();
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                if (GUILayout.Button(_mouseCaptured ? "Release mouse (F6)" : "Capture mouse (F6)"))
                    SetMouseCaptured(!_mouseCaptured);
                if (GUILayout.Button("Reset both hands"))
                    ResetHands();
                GUILayout.EndHorizontal();

                if (GUILayout.Button("Toggle XR on/off (F9)"))
                    ToggleXrPreference();

                GUILayout.Label("Right hand: WASD + Space/Ctrl; E trigger, Q grip; R reset.");
                GUILayout.Label("Left hand: IJKL + U/O; T trigger, G grip; Y reset.");
                GUILayout.Label("Mouse: look while captured; LMB trigger / RMB grip for the active hand.");
                GUILayout.Label("Shift slows hand movement. Backspace resets both hands.");

                if (GUILayout.Button("Close help (F1)"))
                    ToggleHelpUi();

                GUILayout.EndArea();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[OrbusFlatControls] UI draw recovered from an exception: " + ex.GetType().Name);
            }
        }

        public override void OnApplicationQuit()
        {
            ReleaseVirtualButtons();
            RestoreXrSetting();
            _mouseCaptured = false;
            _showUi = false;
            ApplyCursorState();
        }

        private void HandleHotkeys()
        {
            if (GetKeyDown(KeyCode.F1))
                ToggleHelpUi();

            if (GetKeyDown(KeyCode.F8))
                ToggleEnabled();

            if (GetKeyDown(KeyCode.F6) && !_showUi)
                SetMouseCaptured(!_mouseCaptured);

            if (GetKeyDown(KeyCode.F9))
                ToggleXrPreference();

            if (GetKeyDown(KeyCode.Escape))
                SetMouseCaptured(false);

            if (GetKeyDown(KeyCode.Tab))
                ToggleActiveHand();

            if (GetKeyDown(KeyCode.R))
                _right.LocalOffset = DefaultRightOffset;
            if (GetKeyDown(KeyCode.Y))
                _left.LocalOffset = DefaultLeftOffset;
            if (GetKeyDown(KeyCode.Backspace))
                ResetHands();
        }

        private void ToggleEnabled()
        {
            if (_enabled)
            {
                ReleaseVirtualButtons();
                _enabled = false;
                _showUi = false;
                _mouseCaptured = false;
                _bindingStatus = "Controls paused.";
            }
            else
            {
                _enabled = true;
                _nextBindAttempt = 0f;
                _bindingStatus = "Looking for the local player...";
            }

            ApplyCursorState();
        }

        private void ToggleHelpUi()
        {
            _showUi = !_showUi;
            if (_showUi)
                _mouseCaptured = false;
            ApplyCursorState();
        }

        private void ToggleActiveHand()
        {
            _activeHand = _activeHand == HandSide.Right ? HandSide.Left : HandSide.Right;
        }

        private void SetMouseCaptured(bool captured)
        {
            _mouseCaptured = captured && _enabled && !_showUi;
            if (_mouseCaptured)
                InitializeLookAngles();
            ApplyCursorState();
        }

        private void ApplyCursorState()
        {
            bool locked = _enabled && _mouseCaptured && !_showUi;
            try
            {
                Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
                Cursor.visible = !locked;
            }
            catch
            {
                // Cursor APIs differ slightly between Unity versions; the controls still work without capture.
            }
        }

        private void ToggleXrPreference()
        {
            _disableXrForFlatScreen = !_disableXrForFlatScreen;
            _xrSettingApplied = false;
            _nextXrAttempt = 0f;
            TryApplyXrSetting();
        }

        private bool NeedsBinding()
        {
            return _localAvatar == null || _viewTransform == null || _left.Transform == null || _right.Transform == null;
        }

        private bool HasUsableBinding()
        {
            return _localAvatar != null && _viewTransform != null && (_left.Transform != null || _right.Transform != null);
        }

        private void TryBindLocalPlayer()
        {
            MonoBehaviour[] components;
            try
            {
                components = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
            }
            catch (Exception ex)
            {
                _bindingStatus = "Could not scan Unity objects: " + ex.GetType().Name;
                if (!_loggedScanFailure)
                {
                    _loggedScanFailure = true;
                    MelonLogger.Warning("[OrbusFlatControls] Could not scan scene components. Verify this is running under MelonLoader's IL2CPP runtime.");
                }
                return;
            }

            if (components == null || components.Length == 0)
            {
                _bindingStatus = "Waiting for the game scene...";
                return;
            }

            MonoBehaviour avatar = FindLocalAvatar(components);
            if (avatar == null)
            {
                _bindingStatus = "Waiting for PlayerAvatar.localControl / a local Grabber...";
                return;
            }

            if (_localAvatar != avatar)
            {
                ReleaseVirtualButtons();
                _left.ClearBinding();
                _right.ClearBinding();
                _localAvatar = avatar;
                _viewTransform = null;
                _anglesInitialized = false;
                _loggedBound = false;
            }

            object leftGrabber = ReadMember(avatar, "grabberLeft");
            object rightGrabber = ReadMember(avatar, "grabberRight");
            object leftController = FindControllerReferenceForSide(avatar, "left", leftGrabber);
            object rightController = FindControllerReferenceForSide(avatar, "right", rightGrabber);

            BindHand(_left, leftController, leftGrabber, DefaultLeftOffset);
            BindHand(_right, rightController, rightGrabber, DefaultRightOffset);

            Transform view = FindViewTransform(components, avatar);
            if (view != null && view != _viewTransform)
            {
                _viewTransform = view;
                _anglesInitialized = false;
            }

            if (_viewTransform != null && !_anglesInitialized)
                InitializeLookAngles();

            if (HasUsableBinding())
            {
                _bindingStatus = "Bound to local avatar; " +
                    (_left.Transform != null ? "left hand " : "") +
                    (_right.Transform != null ? "right hand " : "") +
                    "and view ready.";
                if (!_loggedBound)
                {
                    _loggedBound = true;
                    MelonLogger.Msg("[OrbusFlatControls] " + _bindingStatus);
                }
            }
            else
            {
                _bindingStatus = "Found the local avatar; waiting for hand transforms / camera.";
            }
        }

        private static MonoBehaviour FindLocalAvatar(MonoBehaviour[] components)
        {
            MonoBehaviour firstAvatar = null;
            int avatarCount = 0;

            for (int i = 0; i < components.Length; i++)
            {
                MonoBehaviour component = components[i];
                if (component == null || !HasTypeName(component, "PlayerAvatar"))
                    continue;

                avatarCount++;
                if (firstAvatar == null)
                    firstAvatar = component;

                bool local;
                if (TryReadBoolean(component, "localControl", out local) && local)
                    return component;
            }

            // Fallback supported by the dump: the local Grabber has an isLocalGrabber flag and myAvatar reference.
            for (int i = 0; i < components.Length; i++)
            {
                MonoBehaviour component = components[i];
                if (component == null || !HasTypeName(component, "Grabber"))
                    continue;

                bool localGrabber;
                if (!TryReadBoolean(component, "isLocalGrabber", out localGrabber) || !localGrabber)
                    continue;

                MonoBehaviour avatar = ReadMember(component, "myAvatar") as MonoBehaviour;
                if (avatar != null)
                    return avatar;
            }

            // A single PlayerAvatar in a solo/local scene is unambiguous even if the dump omitted its getter.
            return avatarCount == 1 ? firstAvatar : null;
        }

        private static void BindHand(HandPose hand, object controller, object grabber, Vector3 initialOffset)
        {
            Transform transform = ResolveTransform(controller);
            if (transform == null && grabber != null)
                transform = ResolveTransform(grabber);

            if (transform == null)
                return;

            if (hand.Transform != transform)
            {
                hand.Transform = transform;
                hand.LocalOffset = initialOffset;
                hand.TriggerHeld = false;
                hand.GripHeld = false;
            }

            hand.Controller = controller ?? grabber;
        }

        private static Transform FindViewTransform(MonoBehaviour[] components, MonoBehaviour avatar)
        {
            Transform bestRigCamera = null;
            float bestDistance = float.MaxValue;
            Vector3 avatarPosition = avatar.transform.position;

            for (int i = 0; i < components.Length; i++)
            {
                MonoBehaviour component = components[i];
                if (component == null || !HasTypeName(component, "PlayerRig"))
                    continue;

                Transform eye = ResolveTransform(ReadMember(component, "eyeCamera"));
                if (eye == null)
                    continue;

                float distance = Vector3.Distance(avatarPosition, component.transform.position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestRigCamera = eye;
                }
            }

            if (bestRigCamera != null)
                return bestRigCamera;

            Camera mainCamera = Camera.main;
            if (mainCamera != null && mainCamera.enabled)
                return mainCamera.transform;

            Camera[] cameras = Camera.allCameras;
            if (cameras != null)
            {
                for (int i = 0; i < cameras.Length; i++)
                {
                    Camera camera = cameras[i];
                    if (camera != null && camera.enabled)
                        return camera.transform;
                }
            }

            // Last resort only: some builds expose the player's head instead of the eye camera.
            return ResolveTransform(ReadMember(avatar, "playerHead"));
        }

        private static object FindControllerReferenceForSide(object avatar, string side, object grabber)
        {
            object controller = ReadMember(avatar, side + "Controller");
            object trackedController = ReadMember(grabber, "myController");
            if (trackedController == null)
                trackedController = ReadMember(grabber, "OrbusController");

            if (controller == null || (trackedController != null && !HasTypeName(controller, "OrbusTrackedController")))
                controller = trackedController;
            return controller ?? grabber;
        }

        private void UpdateMouseLook()
        {
            if (_viewTransform == null)
                return;

            if (!_anglesInitialized)
                InitializeLookAngles();

            if (!_mouseCaptured || _showUi)
                return;

            float mouseX = GetMouseAxis("Mouse X");
            float mouseY = GetMouseAxis("Mouse Y");
            _yaw += mouseX * _lookSensitivity;
            _pitch -= mouseY * _lookSensitivity;
            _pitch = Mathf.Clamp(_pitch, -82f, 82f);
            ApplyMouseLookRotation();
        }

        private void InitializeLookAngles()
        {
            if (_viewTransform == null)
                return;

            Vector3 angles = _viewTransform.eulerAngles;
            _yaw = angles.y;
            _pitch = angles.x > 180f ? angles.x - 360f : angles.x;
            _pitch = Mathf.Clamp(_pitch, -82f, 82f);
            _anglesInitialized = true;
        }

        private void ApplyMouseLookRotation()
        {
            if (_viewTransform == null)
                return;

            _viewTransform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        private void UpdateHandMovement()
        {
            float deltaTime = Mathf.Clamp(Time.unscaledDeltaTime, 0f, 0.05f);
            float speed = _moveSpeed * (GetKey(KeyCode.LeftShift) || GetKey(KeyCode.RightShift) ? 0.25f : 1f);

            // Right hand: WASD in camera-local space, Space/Ctrl for height.
            MoveHand(_right,
                Axis(KeyCode.D, KeyCode.A),
                (GetKey(KeyCode.Space) ? 1f : 0f) -
                    ((GetKey(KeyCode.LeftControl) || GetKey(KeyCode.RightControl)) ? 1f : 0f),
                Axis(KeyCode.W, KeyCode.S),
                speed, deltaTime);

            // Left hand: IJKL in camera-local space, U/O for height.
            MoveHand(_left,
                Axis(KeyCode.L, KeyCode.J),
                Axis(KeyCode.U, KeyCode.O),
                Axis(KeyCode.I, KeyCode.K),
                speed, deltaTime);

            if (GetKeyDown(KeyCode.R))
                _right.LocalOffset = DefaultRightOffset;
            if (GetKeyDown(KeyCode.Y))
                _left.LocalOffset = DefaultLeftOffset;
            if (GetKeyDown(KeyCode.Backspace))
                ResetHands();
        }

        private static void MoveHand(HandPose hand, float x, float y, float z, float speed, float deltaTime)
        {
            if (hand.Transform == null)
                return;

            Vector3 input = new Vector3(x, y, z);
            if (input.sqrMagnitude > 1f)
                input.Normalize();

            hand.LocalOffset += input * (speed * deltaTime);
            hand.LocalOffset.x = Mathf.Clamp(hand.LocalOffset.x, -1.25f, 1.25f);
            hand.LocalOffset.y = Mathf.Clamp(hand.LocalOffset.y, -1.25f, 1.15f);
            hand.LocalOffset.z = Mathf.Clamp(hand.LocalOffset.z, -0.80f, 1.40f);
        }

        private void ApplyVirtualHands()
        {
            if (_viewTransform == null)
                return;

            ApplyHandPose(_left);
            ApplyHandPose(_right);
        }

        private void ApplyHandPose(HandPose hand)
        {
            Transform transform = hand.Transform;
            if (transform == null)
                return;

            Quaternion viewRotation = _viewTransform.rotation;
            transform.position = _viewTransform.position + viewRotation * hand.LocalOffset;
            transform.rotation = viewRotation;
        }

        private void UpdateControllerButtons()
        {
            bool mouseOverPanel = _showUi && IsMouseOverPanel();
            bool mouseTrigger = !mouseOverPanel && GetMouseButton(0);
            bool mouseGrip = !mouseOverPanel && GetMouseButton(1);

            bool leftTrigger = GetKey(KeyCode.T) || (_activeHand == HandSide.Left && mouseTrigger);
            bool leftGrip = GetKey(KeyCode.G) || (_activeHand == HandSide.Left && mouseGrip);
            bool rightTrigger = GetKey(KeyCode.E) || (_activeHand == HandSide.Right && mouseTrigger);
            bool rightGrip = GetKey(KeyCode.Q) || (_activeHand == HandSide.Right && mouseGrip);

            SetHandButtons(_left, leftTrigger, leftGrip);
            SetHandButtons(_right, rightTrigger, rightGrip);
        }

        private void SetHandButtons(HandPose hand, bool trigger, bool grip)
        {
            object controller = hand.Controller;
            if (controller == null)
                return;

            if (trigger != hand.TriggerHeld)
            {
                TryInvokeControllerEvent(controller, trigger ? "OnTriggerClicked" : "OnTriggerUnclicked");
                hand.TriggerHeld = trigger;
            }

            if (grip != hand.GripHeld)
            {
                TryInvokeControllerEvent(controller, grip ? "OnGripped" : "OnUngripped");
                hand.GripHeld = grip;
            }

            bool triggerWritten = TryWriteMember(controller, "triggerPressed", trigger);
            bool gripWritten = TryWriteMember(controller, "gripped", grip);
            if ((!triggerWritten || !gripWritten) && !_loggedInputFallback)
            {
                _loggedInputFallback = true;
                MelonLogger.Warning("[OrbusFlatControls] Controller state fields were not writable on this proxy; event calls were still attempted. Check the MelonLoader log if clicks do not register.");
            }
        }

        private void ReleaseVirtualButtons()
        {
            SetHandButtons(_left, false, false);
            SetHandButtons(_right, false, false);
        }

        private void ResetHands()
        {
            _left.LocalOffset = DefaultLeftOffset;
            _right.LocalOffset = DefaultRightOffset;
        }

        private bool IsMouseOverPanel()
        {
            try
            {
                Vector3 mouse = Input.mousePosition;
                Vector2 guiPosition = new Vector2(mouse.x, Screen.height - mouse.y);
                return PanelRect.Contains(guiPosition);
            }
            catch
            {
                return false;
            }
        }

        private void TryApplyXrSetting()
        {
            // This mod is intentionally not game-filtered because app.info wasn't supplied.
            // Never change XR state until the Orbus PlayerAvatar has been identified.
            if (_localAvatar == null)
                return;

            try
            {
                if (_xrEnabledProperty == null)
                    _xrEnabledProperty = FindXrEnabledProperty();

                if (_xrEnabledProperty == null)
                    return;

                if (!_xrOriginalStateKnown)
                {
                    _xrOriginalEnabled = Convert.ToBoolean(_xrEnabledProperty.GetValue(null, null));
                    _xrOriginalStateKnown = true;
                }

                _xrEnabledProperty.SetValue(null, !_disableXrForFlatScreen, null);
                _xrSettingApplied = true;
                _loggedXrFailure = false;
                MelonLogger.Msg("[OrbusFlatControls] XR " + (_disableXrForFlatScreen ? "disabled" : "enabled") + " for this session.");
            }
            catch (Exception ex)
            {
                _xrSettingApplied = true;
                if (!_loggedXrFailure)
                {
                    _loggedXrFailure = true;
                    MelonLogger.Warning("[OrbusFlatControls] Could not change Unity XR state: " + ex.GetType().Name + ". Use the game's desktop launch option if available.");
                }
            }
        }

        private static PropertyInfo FindXrEnabledProperty()
        {
            string[] typeNames = { "UnityEngine.XR.XRSettings", "UnityEngine.VR.VRSettings" };
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

            for (int i = 0; i < assemblies.Length; i++)
            {
                Assembly assembly = assemblies[i];
                if (assembly == null)
                    continue;

                for (int j = 0; j < typeNames.Length; j++)
                {
                    try
                    {
                        Type type = assembly.GetType(typeNames[j], false);
                        if (type == null)
                            continue;

                        PropertyInfo property = type.GetProperty("enabled", StaticFlags);
                        if (property != null && property.CanRead && property.CanWrite)
                            return property;
                    }
                    catch
                    {
                        // Try the next XR API/assembly; older Unity versions use VRSettings.
                    }
                }
            }

            return null;
        }

        private void RestoreXrSetting()
        {
            if (!_xrOriginalStateKnown || _xrEnabledProperty == null)
                return;

            try
            {
                _xrEnabledProperty.SetValue(null, _xrOriginalEnabled, null);
            }
            catch
            {
                // The XR provider may already have been shut down during application exit.
            }
        }

        private static bool TryInvokeControllerEvent(object controller, string methodName)
        {
            if (controller == null)
                return false;

            try
            {
                MethodInfo[] methods = controller.GetType().GetMethods(InstanceFlags);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (method.Name != methodName)
                        continue;

                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length == 0)
                    {
                        method.Invoke(controller, null);
                        return true;
                    }

                    if (parameters.Length != 1)
                        continue;

                    object eventArgs = null;
                    Type argumentType = parameters[0].ParameterType;
                    try
                    {
                        if (!argumentType.IsAbstract && !argumentType.IsInterface)
                            eventArgs = Activator.CreateInstance(argumentType, true);
                    }
                    catch
                    {
                        // The state-field fallback below still provides a useful no-HMD path.
                    }

                    method.Invoke(controller, new object[] { eventArgs });
                    return true;
                }
            }
            catch
            {
                // Reflection over generated IL2CPP wrappers can fail on some loader versions.
            }

            return false;
        }

        private static object ReadMember(object source, string name)
        {
            if (source == null)
                return null;

            Type type;
            try
            {
                type = source.GetType();
            }
            catch
            {
                return null;
            }

            while (type != null)
            {
                try
                {
                    PropertyInfo property = type.GetProperty(name, InstanceFlags | BindingFlags.DeclaredOnly);
                    if (property != null && property.GetIndexParameters().Length == 0)
                        return property.GetValue(source, null);
                }
                catch
                {
                    // Try a field or a base type.
                }

                try
                {
                    FieldInfo field = type.GetField(name, InstanceFlags | BindingFlags.DeclaredOnly);
                    if (field != null)
                        return field.GetValue(source);
                }
                catch
                {
                    // Try a base type.
                }

                type = type.BaseType;
            }

            return null;
        }

        private static bool TryWriteMember(object target, string name, bool value)
        {
            if (target == null)
                return false;

            Type type;
            try
            {
                type = target.GetType();
            }
            catch
            {
                return false;
            }

            while (type != null)
            {
                try
                {
                    PropertyInfo property = type.GetProperty(name, InstanceFlags | BindingFlags.DeclaredOnly);
                    if (property != null && property.CanWrite && property.GetIndexParameters().Length == 0)
                    {
                        property.SetValue(target, value, null);
                        return true;
                    }
                }
                catch
                {
                    // Try a field or a base type.
                }

                try
                {
                    FieldInfo field = type.GetField(name, InstanceFlags | BindingFlags.DeclaredOnly);
                    if (field != null)
                    {
                        field.SetValue(target, value);
                        return true;
                    }
                }
                catch
                {
                    // Try a base type.
                }

                type = type.BaseType;
            }

            return false;
        }

        private static bool TryReadBoolean(object source, string name, out bool value)
        {
            value = false;
            object raw = ReadMember(source, name);
            if (raw == null)
                return false;

            try
            {
                value = Convert.ToBoolean(raw);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static Transform ResolveTransform(object value)
        {
            if (value == null)
                return null;

            try
            {
                Transform transform = value as Transform;
                if (transform != null)
                    return transform;

                Component component = value as Component;
                if (component != null)
                    return component.transform;

                GameObject gameObject = value as GameObject;
                if (gameObject != null)
                    return gameObject.transform;

                object nestedTransform = ReadMember(value, "transform");
                transform = nestedTransform as Transform;
                if (transform != null)
                    return transform;

                object nestedGameObject = ReadMember(value, "gameObject");
                gameObject = nestedGameObject as GameObject;
                if (gameObject != null)
                    return gameObject.transform;
            }
            catch
            {
                // A destroyed Il2Cpp object can throw from its proxy accessors.
            }

            return null;
        }

        private static bool HasTypeName(object value, string expected)
        {
            if (value == null)
                return false;

            try
            {
                string name = value.GetType().Name;
                return name == expected || name.EndsWith("." + expected, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static bool GetKey(KeyCode key)
        {
            try { return Input.GetKey(key); }
            catch { return false; }
        }

        private static bool GetKeyDown(KeyCode key)
        {
            try { return Input.GetKeyDown(key); }
            catch { return false; }
        }

        private static bool GetMouseButton(int button)
        {
            try { return Input.GetMouseButton(button); }
            catch { return false; }
        }

        private static float GetMouseAxis(string axis)
        {
            try { return Input.GetAxisRaw(axis); }
            catch { return 0f; }
        }

        private static float Axis(KeyCode positive, KeyCode negative)
        {
            return (GetKey(positive) ? 1f : 0f) - (GetKey(negative) ? 1f : 0f);
        }

        private enum HandSide
        {
            Left,
            Right
        }

        private sealed class HandPose
        {
            public object Controller;
            public Transform Transform;
            public Vector3 LocalOffset;
            public bool TriggerHeld;
            public bool GripHeld;

            public HandPose(Vector3 initialOffset)
            {
                LocalOffset = initialOffset;
            }

            public void ClearBinding()
            {
                Controller = null;
                Transform = null;
                TriggerHeld = false;
                GripHeld = false;
            }
        }
    }
}
