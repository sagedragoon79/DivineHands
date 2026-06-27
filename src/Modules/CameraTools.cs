using System;
using System.Reflection;
using UnityEngine;
using MelonLoader;

namespace DivineHands.Modules
{
    /// <summary>
    /// Two independent camera god-powers, both driven from <see cref="Config"/> like
    /// <see cref="GodTools"/> (sync-on-change each frame; capture-before-change; restore-on-disable).
    ///
    /// =====================================================================================
    /// GOD VIEW — relax FF's RTS camera constraints so you can zoom way out, flatten/overhead the
    /// pitch, and survey the whole map.
    ///
    /// All six constraint fields are PUBLIC on CameraManager (verified Assembly-CSharp decompile
    /// 59170-59180), so we set them directly; shadowDistMin/shadowDistMax are PRIVATE [SerializeField]
    /// floats (59156/59159) reached by cached reflection. We snapshot every field's CURRENT value the
    /// frame we enable (NOT hard-coded vanilla numbers — that way we restore whatever the running map
    /// actually had, version-proof) and copy them back verbatim on disable.
    ///   public float minAngle / maxAngle                        [59170 / 59172]  pitch clamp
    ///   public float minFieldOfView / maxFieldOfView            [59174 / 59176]  FOV clamp
    ///   public float minDistanceFromTarget / maxDistanceFromTarget [59178 / 59180]  zoom distance clamp
    ///   private float shadowDistMin / shadowDistMax             [59156 / 59159]  shadow draw distance
    /// We also disable RenderSettings.fog (Unity's environmental distance haze) while god view is active and
    /// restore it on exit — that's the haze that made god view look murky vs Pangu, which does the same.
    /// This is SEPARATE from FF's fog of war (a FOWImageEffect, what GodTools' Reveal Map manipulates), so
    /// the two don't collide. Everything else is a pure capture-relax-restore of the camera constraints.
    ///
    /// =====================================================================================
    /// FREE CAM — detach from RTS control and fly the camera manually.
    ///
    /// CRITICAL DESIGN CHOICE: we do NOT call CameraManager.BeginFreeLook/EndFreeLook (59549/59560).
    /// Those spawn FF's own FreeLookCamera (60026), which (a) only reads arrow keys via
    /// Input.GetAxis("Vertical"/"Horizontal") with NO Space/Ctrl vertical and NO Shift-fast, and
    /// (b) EndFreeLook does a forward raycast + SetLookLocation reorientation (59571-59580) that would
    /// stomp any position/rotation we try to restore. Instead we:
    ///   on enable : snapshot the camera GameObject's transform (pos/rot) + Camera.fieldOfView +
    ///               clearFlags + cameraManager.enabled, then set cameraManager.enabled = false (stops
    ///               its LateUpdate driving the camera) and LOCK+HIDE the cursor for mouse-look.
    ///   OnUpdate  : while active, continuously drive base camera transform ourselves — WASD horizontal,
    ///               Space/LeftCtrl vertical, Shift fast-multiplier, mouse-look (pitch clamped). Toggled
    ///               by the configurable Ctrl+F hotkey (or the panel control); the keyboard works even
    ///               while the cursor is locked, so Ctrl+F is always the safe escape (no soft-lock).
    ///   on disable: restore transform/FOV/clearFlags FIRST and FORCE the cursor free, THEN re-enable
    ///               (order matters — re-enabling first lets its LateUpdate snap the camera before we
    ///               write the restore). CameraManager.LateUpdate re-syncs its proxyCamera from the
    ///               restored transform, so RTS control resumes exactly where the user left it.
    ///
    /// IMPORTANT (verified 59319 Awake): mainCamera = GetComponent&lt;Camera&gt;() — the CameraManager,
    /// the Unity Camera, and base.transform all live on the SAME GameObject. So the transform we fly
    /// is cameraManager.transform, and Camera.main == cameraManager.mainCamera. We never reparent or
    /// destroy anything (Wicker's ToggleFreeCam spawns a second camera; we don't — fewer moving parts,
    /// cleaner restore).
    ///
    /// Both powers auto-restore on OnSceneExit / OnMapLoaded so leaving a map mid-power never strands
    /// the camera. Every reflection hop is cached and wrapped in try/catch gated on DebugLog.
    /// </summary>
    public static class CameraTools
    {
        /// <summary>Runtime live ON/OFF for God View — toggled in the in-game panel, NOT a saved pref.
        /// Reset to false on every map load / scene exit (see <see cref="ResetActive"/>).</summary>
        public static bool GodViewActive;

        /// <summary>Runtime live ON/OFF for Free Cam — toggled in the in-game panel OR by the
        /// configurable Ctrl+F hotkey, NOT a saved pref. Reset to false on every map load / scene exit.</summary>
        public static bool FreeCamActive;

        /// <summary>Force both camera god-powers OFF. Called on map load and scene exit so a fresh map
        /// always starts with God View / Free Cam inactive regardless of their Enable prefs.</summary>
        public static void ResetActive()
        {
            GodViewActive = false;
            FreeCamActive = false;
        }

        // ============================ shared CameraManager handle ============================
        private static CameraManager? _cam;

        private static CameraManager? ResolveCamera()
        {
            if (_cam != null) return _cam;
            try
            {
                var gm = GameManager.Instance;       // same accessor TerrainElevation uses
                if (gm == null) return null;
                _cam = gm.cameraManager;             // public CameraManager cameraManager { get; } [95616]
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] ResolveCamera failed: {ex.Message}");
                _cam = null;
            }
            return _cam;
        }

        // ============================ cached reflection (all constraint fields) ============================
        // The CameraManager constraint fields' visibility drifts between game versions (some that were
        // public in older builds are non-public in the shipped DLL), so we reach EVERY one via cached
        // reflection — Public|NonPublic — and tolerate any that are missing. Mirrors TerrainElevation's
        // resolve-once-then-cache pattern. Field names verified in the Assembly-CSharp decompile
        // (59156-59180); GetField with both binding flags catches them whether public or [SerializeField].
        private static FieldInfo? _minDistField, _maxDistField, _minAngleField, _maxAngleField,
                                  _minFovField, _maxFovField, _shadowMinField, _shadowMaxField;
        private static bool _constraintFieldsResolved;

        private static void ResolveConstraintFields()
        {
            if (_constraintFieldsResolved) return;
            _constraintFieldsResolved = true;
            const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;
            var t = typeof(CameraManager);
            _minDistField   = t.GetField("minDistanceFromTarget", F);
            _maxDistField   = t.GetField("maxDistanceFromTarget", F);
            _minAngleField  = t.GetField("minAngle", F);
            _maxAngleField  = t.GetField("maxAngle", F);
            _minFovField    = t.GetField("minFieldOfView", F);
            _maxFovField    = t.GetField("maxFieldOfView", F);
            _shadowMinField = t.GetField("shadowDistMin", F);
            _shadowMaxField = t.GetField("shadowDistMax", F);
        }

        private static float ReadField(FieldInfo? f, CameraManager cam, float fallback)
        {
            try { return f != null ? (float)f.GetValue(cam) : fallback; }
            catch { return fallback; }
        }

        private static bool ReadBool(FieldInfo? f, CameraManager cam, bool fallback)
        {
            try { return f != null ? (bool)f.GetValue(cam) : fallback; }
            catch { return fallback; }
        }

        private static void WriteBool(FieldInfo? f, CameraManager cam, bool value)
        {
            try { f?.SetValue(cam, value); }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] camera bool write failed: {ex.Message}");
            }
        }

        private static void WriteField(FieldInfo? f, CameraManager cam, float value)
        {
            try { f?.SetValue(cam, value); }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] camera field write failed: {ex.Message}");
            }
        }

        // =====================================================================================
        // Lifecycle (driven from Plugin)
        // =====================================================================================

        public static void OnMapLoaded()
        {
            // New map: drop the cached handle and force both powers to a known-off baseline so a
            // stale "applied" flag from the previous map can't suppress a fresh apply.
            ForceRestoreAll(refetch: true);
            _cam = null;
            // Field handles are type-level (not instance) so they stay valid across maps; no reset needed.
        }

        public static void OnSceneExit()
        {
            // Leaving the map (to menu / loading). Restore against the handle we still hold so we
            // never strand the camera, then drop everything.
            ForceRestoreAll(refetch: false);
            _cam = null;
        }

        public static void OnUpdate()
        {
            // Ctrl+F (configurable) toggles Free Cam on/off — same runtime flag the panel toggle drives.
            // The keyboard works even while the cursor is locked for mouse-look, so this is always the
            // escape out of Free Cam (no soft-lock).
            if (Config.MasterEnable.Value && Config.EnableFreeCam.Value
                && Hotkey.Pressed(Config.FreeCamHotkey.Value))
                FreeCamActive = !FreeCamActive;

            SyncGodView();
            SyncFreeCam();
            if (_freeCamApplied) DriveFreeCam();   // fly only while active
        }

        /// <summary>Hard-restore both powers (used by scene transitions). Best-effort, never throws.</summary>
        private static void ForceRestoreAll(bool refetch)
        {
            try
            {
                if (refetch) ResolveCamera();
                if (_godViewApplied) RestoreGodView();
                if (_freeCamApplied) RestoreFreeCam();
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] camera force-restore failed: {ex.Message}");
            }
            finally
            {
                // Clear flags regardless — the camera object may already be gone on scene exit.
                _godViewApplied = false;
                _freeCamApplied = false;
            }
        }

        // =====================================================================================
        // GOD VIEW
        // =====================================================================================

        // Relaxed presets. Distances/FOV widen the survey envelope; angles allow flat->overhead.
        private const float GV_MinDistance =   6f;   // keep a sane floor
        private const float GV_MaxDistance = 900f;   // far out — whole-map survey
        private const float GV_MinAngle    =  10f;   // low/flat
        private const float GV_MaxAngle    =  89f;   // near-overhead (avoid exact 90 gimbal)
        private const float GV_MinFOV      =  20f;
        private const float GV_MaxFOV      =  70f;
        private const float GV_ShadowMin   = 150f;
        private const float GV_ShadowMax   = 600f;

        private static bool _godViewApplied;

        // captured originals
        private static float _ovMinDist, _ovMaxDist, _ovMinAngle, _ovMaxAngle,
                             _ovMinFOV, _ovMaxFOV, _ovShadowMin, _ovShadowMax;
        private static bool  _ovRenderFog;   // original RenderSettings.fog (env haze), restored on exit

        private static void SyncGodView()
        {
            bool want = Config.MasterEnable.Value && Config.EnableGodView.Value && GodViewActive;
            if (want == _godViewApplied) return;

            var cam = ResolveCamera();
            if (cam == null) return;

            if (want) ApplyGodView(cam);
            else       RestoreGodView();

            if (Config.DebugLog.Value)
                MelonLogger.Msg($"[DivineHands] God View -> {want}");
        }

        private static void ApplyGodView(CameraManager cam)
        {
            try
            {
                ResolveConstraintFields();

                // Capture the CURRENT values so restore is exact for whatever the map actually had.
                // Any field that doesn't exist in this game version reads its fallback and is skipped on write.
                _ovMinDist   = ReadField(_minDistField,   cam,   6f);
                _ovMaxDist   = ReadField(_maxDistField,   cam, 260f);
                _ovMinAngle  = ReadField(_minAngleField,  cam,  35f);
                _ovMaxAngle  = ReadField(_maxAngleField,  cam,  58f);
                _ovMinFOV    = ReadField(_minFovField,    cam,  35f);
                _ovMaxFOV    = ReadField(_maxFovField,    cam,  50f);
                _ovShadowMin = ReadField(_shadowMinField, cam, 100f);
                _ovShadowMax = ReadField(_shadowMaxField, cam, 350f);
                _ovRenderFog = RenderSettings.fog;

                // Apply relaxed envelope (never narrow below what the map already allows).
                WriteField(_minDistField,   cam, Mathf.Min(_ovMinDist, GV_MinDistance));
                WriteField(_maxDistField,   cam, Mathf.Max(_ovMaxDist, GV_MaxDistance));
                WriteField(_minAngleField,  cam, Mathf.Min(_ovMinAngle, GV_MinAngle));
                WriteField(_maxAngleField,  cam, Mathf.Max(_ovMaxAngle, GV_MaxAngle));
                WriteField(_minFovField,    cam, Mathf.Min(_ovMinFOV, GV_MinFOV));
                WriteField(_maxFovField,    cam, Mathf.Max(_ovMaxFOV, GV_MaxFOV));
                WriteField(_shadowMinField, cam, Mathf.Max(_ovShadowMin, GV_ShadowMin));
                WriteField(_shadowMaxField, cam, Mathf.Max(_ovShadowMax, GV_ShadowMax));

                // Kill the environmental distance fog while surveying from altitude — that's the haze that
                // made god view look murky vs Pangu. This is Unity's RenderSettings.fog (atmospheric), which
                // is SEPARATE from FF's fog-of-war (FOWImageEffect, what Reveal Map touches), so there's no
                // collision. Restored exactly on exit.
                RenderSettings.fog = false;

                // (We no longer touch CameraManager.zoomUnlocked — its reflection was unreliable across
                // game versions, so DH's proportional-zoom patch now owns the god-view zoom damping and
                // gates on GodViewActive instead. See Patches/CameraZoomPatch.)

                _godViewApplied = true;
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] God View apply failed: {ex.Message}");
                // Leave _godViewApplied false so we don't try to restore values we never captured.
            }
        }

        private static void RestoreGodView()
        {
            var cam = _cam;
            if (cam != null)
            {
                try
                {
                    ResolveConstraintFields();
                    WriteField(_minDistField,   cam, _ovMinDist);
                    WriteField(_maxDistField,   cam, _ovMaxDist);
                    WriteField(_minAngleField,  cam, _ovMinAngle);
                    WriteField(_maxAngleField,  cam, _ovMaxAngle);
                    WriteField(_minFovField,    cam, _ovMinFOV);
                    WriteField(_maxFovField,    cam, _ovMaxFOV);
                    WriteField(_shadowMinField, cam, _ovShadowMin);
                    WriteField(_shadowMaxField, cam, _ovShadowMax);
                    RenderSettings.fog = _ovRenderFog;
                }
                catch (Exception ex)
                {
                    if (Config.DebugLog.Value)
                        MelonLogger.Warning($"[DivineHands] God View restore failed: {ex.Message}");
                }
            }
            _godViewApplied = false;
        }

        // =====================================================================================
        // FREE CAM
        // =====================================================================================

        private static bool _freeCamApplied;

        // captured originals
        private static Vector3 _fcPosition;
        private static Quaternion _fcRotation;
        private static float _fcFieldOfView;
        private static CameraClearFlags _fcClearFlags;
        private static bool _fcCamManagerEnabled;
        private static bool _fcHaveCameraComponent;

        // live fly state (mouse-look accumulators, seeded from current rotation on enable)
        private static float _fcYaw;
        private static float _fcPitch;

        private static void SyncFreeCam()
        {
            bool want = Config.MasterEnable.Value && Config.EnableFreeCam.Value && FreeCamActive;
            if (want == _freeCamApplied) return;

            var cam = ResolveCamera();
            if (cam == null) return;

            if (want) ApplyFreeCam(cam);
            else       RestoreFreeCam();

            if (Config.DebugLog.Value)
                MelonLogger.Msg($"[DivineHands] Free Cam -> {want}");
        }

        private static void ApplyFreeCam(CameraManager cam)
        {
            try
            {
                var tr = cam.transform;                 // same GameObject as the Camera [59319]
                Camera? unityCam = cam.mainCamera;      // public Camera mainCamera { get; } [59296]

                // ---- capture EVERYTHING before we touch anything ----
                _fcPosition          = tr.position;
                _fcRotation          = tr.rotation;
                _fcCamManagerEnabled = cam.enabled;
                _fcHaveCameraComponent = unityCam != null;
                if (unityCam != null)
                {
                    _fcFieldOfView = unityCam.fieldOfView;
                    _fcClearFlags  = unityCam.clearFlags;
                }

                // ---- seed mouse-look from current orientation ----
                Vector3 e = tr.eulerAngles;
                _fcYaw   = e.y;
                _fcPitch = e.x;
                if (_fcPitch > 180f) _fcPitch -= 360f;     // normalise to [-180,180] for clamping

                // ---- take control: stop FF's controller AND lock+hide the cursor for mouse-look ----
                // Ctrl+F (configurable) is the toggle, and the keyboard works even while the cursor is
                // locked, so locking is SAFE here — no cursor trap. Mouse-look + WASD fly are continuous
                // while active (see DriveFreeCam); RestoreFreeCam always force-frees the cursor on exit.
                cam.enabled = false;                        // halts CameraManager LateUpdate drive
                if (unityCam != null)
                    unityCam.clearFlags = CameraClearFlags.Skybox; // avoid smear off the map edge
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible   = false;

                _freeCamApplied = true;
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] Free Cam apply failed: {ex.Message}");
                // Best-effort undo of partial enable so we don't leave a half-detached camera.
                try { if (cam != null) cam.enabled = true; } catch { /* ignore */ }
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible   = true;
                _freeCamApplied = false;
            }
        }

        private static void DriveFreeCam()
        {
            var cam = _cam;
            if (cam == null) { _freeCamApplied = false; return; }
            try
            {
                var tr = cam.transform;

                // Continuous mouse-look + WASD fly while Free Cam is active (cursor stays locked/hidden).
                // Ctrl+F exits — the keyboard works even with the cursor locked, so this never traps.

                // --- mouse-look (pitch clamped like FF's FreeLookCamera [60062]) ---
                float sens = Mathf.Max(0.01f, Config.FreeCamSensitivity.Value);
                _fcYaw   += Input.GetAxis("Mouse X") * sens;
                _fcPitch -= Input.GetAxis("Mouse Y") * sens;
                _fcPitch  = Mathf.Clamp(_fcPitch, -89f, 89f);
                tr.rotation = Quaternion.Euler(_fcPitch, _fcYaw, 0f);

                // --- movement: WASD horizontal, Space/LeftCtrl vertical, Shift fast ---
                float speed = Mathf.Max(0.1f, Config.FreeCamMoveSpeed.Value);
                if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                    speed *= Mathf.Max(1f, Config.FreeCamFastMultiplier.Value);

                Vector3 move = Vector3.zero;
                if (Input.GetKey(KeyCode.W)) move += tr.forward;
                if (Input.GetKey(KeyCode.S)) move -= tr.forward;
                if (Input.GetKey(KeyCode.D)) move += tr.right;
                if (Input.GetKey(KeyCode.A)) move -= tr.right;
                if (Input.GetKey(KeyCode.Space))    move += Vector3.up;     // world-up, not cam-up
                if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                    move -= Vector3.up;

                // unscaledDeltaTime so the camera still flies while the game is PAUSED (timeScale 0,
                // where Time.deltaTime is 0). Surveying a frozen scene is a primary Free Cam use case.
                if (move.sqrMagnitude > 0f)
                    tr.position += move.normalized * speed * Time.unscaledDeltaTime;

                // Ground floor: keep the camera above the terrain surface (+ clearance) so it can't clip
                // through into the backface/sky void. A SMALL clearance still allows true ground-level
                // shots (skim just above the grass) — it only blocks going under the world. Toggle off
                // for deliberate under-the-map shots. No-ops until terrain resolves (safe in menus).
                if (Config.FreeCamGroundFloor.Value
                    && DivineHands.Modules.TerrainElevation.TryGetGroundHeight(
                           tr.position.x, tr.position.z, out float groundY))
                {
                    float minY = groundY + Mathf.Max(0f, Config.FreeCamFloorClearance.Value);
                    if (tr.position.y < minY)
                    {
                        Vector3 p = tr.position;
                        p.y = minY;
                        tr.position = p;
                    }
                }
            }
            catch (Exception ex)
            {
                if (Config.DebugLog.Value)
                    MelonLogger.Warning($"[DivineHands] Free Cam drive failed: {ex.Message}");
            }
        }

        private static void RestoreFreeCam()
        {
            var cam = _cam;
            if (cam != null)
            {
                try
                {
                    var tr = cam.transform;
                    Camera? unityCam = cam.mainCamera;

                    // 1) restore transform + camera props FIRST, while the controller is still off.
                    tr.position = _fcPosition;
                    tr.rotation = _fcRotation;
                    if (_fcHaveCameraComponent && unityCam != null)
                    {
                        unityCam.fieldOfView = _fcFieldOfView;
                        unityCam.clearFlags  = _fcClearFlags;
                    }

                    // 2) ALWAYS free the cursor on exit (never restore a locked state — that's the
                    //    soft-lock we're guarding against). FF manages its own cursor in RTS mode.
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible   = true;

                    // 3) re-enable FF's RTS controller LAST. Its LateUpdate re-syncs proxyCamera from
                    //    the (now restored) transform, so RTS control resumes exactly where we left it.
                    cam.enabled = _fcCamManagerEnabled;
                }
                catch (Exception ex)
                {
                    if (Config.DebugLog.Value)
                        MelonLogger.Warning($"[DivineHands] Free Cam restore failed: {ex.Message}");
                    // Last-ditch: make sure the controller is back on and the cursor is usable.
                    try { cam.enabled = true; } catch { /* ignore */ }
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible   = true;
                }
            }
            else
            {
                // Camera gone (scene exit): at least free the cursor so menus are usable.
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible   = true;
            }
            _freeCamApplied = false;
        }
    }
}
