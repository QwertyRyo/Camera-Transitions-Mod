using System.Collections.Generic;
using System.Reflection;
using MelonLoader;
using HarmonyLib;
using UnityEngine;
using GHPC;
using GHPC.Player;
using GHPC.Vehicle;
using GHPC.Weapons;
using GHPC.UI.Hud;

[assembly: MelonInfo(typeof(Camera_Rework.CameraReworkMod), "Camera Rework", "1.0.0", "Qwertyryo")]
[assembly: MelonGame("Radian Simulations LLC", "GHPC")]

namespace Camera_Rework {

  public class CameraReworkMod : MelonMod {
    // Transition state
    internal static bool _transitioning;
    internal static float _transitionTimer;
    internal static float _transitionDuration = 1.75f;
    internal static float _unitTransitionDuration = 1.0f;
    internal static Camera _mainCam;
    internal static bool _inTopDown;
    internal static float _lastToggleTime;
    internal static Vector3 _savedPosition;
    internal static Quaternion _savedRotation;
    internal static Vector3 _savedCameraOffset;
    internal static Vector3 _transitionStartPos;
    internal static Quaternion _transitionStartRot;
    internal static Vector3 _transitionEndPos;
    internal static Quaternion _transitionEndRot;
    internal static readonly Quaternion TopDownRotation =
        Quaternion.Euler(75f, 0f, 0f);

    internal static bool _returningToNormal;
    internal static bool _unitTransitioning;
    internal static float _unitTransitionTimer;
    internal static Vector3 _unitTransStartPos;
    internal static Quaternion _unitTransStartRot;
    internal static WeaponHud _weaponHud;
    public override void OnInitializeMelon() {
      try {
        HarmonyInstance.PatchAll();
      } catch (System.Exception ex) {
        MelonLogger.Error($"Patching failed: {ex}");
      }
    }

    public override void OnSceneWasUnloaded(int buildIndex, string sceneName) {
      TopDownUnitMenu.Destroy();
      _inTopDown = false;
      _transitioning = false;
      _returningToNormal = false;
      _unitTransitioning = false;
      _mainCam = null;
      _weaponHud = null;
    }

    internal static void HideGameHUD() {
      if (_weaponHud == null)
        _weaponHud = GameObject.FindObjectOfType<WeaponHud>();
      if (_weaponHud != null)
        _weaponHud.gameObject.SetActive(false);
    }

    internal static void ShowGameHUD() {
      if (_weaponHud != null)
        _weaponHud.gameObject.SetActive(true);
    }

    internal static Vector3 GetPlayerTopDownPosition() {
      if (PlayerInput.Instance == null || PlayerInput.Instance.CurrentPlayerUnit == null)
        return Vector3.zero;
      Vector3 pos = PlayerInput.Instance.CurrentPlayerUnit.transform.position;
      pos.y += 75f;
      return pos;
    }

    internal static void SetBodyVisible(bool visible) {
      var fpba = GameObject.FindObjectOfType<GHPC.Camera.FirstPersonBinocularsAnimator>();
      if (fpba == null) {
         return; 
      }
      try {
        foreach (var fm in fpba.Models) {
          if (fm.Model != null) {
            fm.Model.SetActive(visible);
          }
        }
        foreach (var hm in fpba.HandModels) {
          if (hm != null) {
            hm.SetActive(visible);
          }
        }
      } catch (System.Exception ex) {
        MelonLogger.Msg($"body exception: {ex.GetType().Name}: {ex.Message}");
      }
    }

    internal static string GetPath(Transform t) {
      string path = t.name;
      while (t.parent != null) { t = t.parent; path = t.name + "/" + path; }
      return path;
    }

    static readonly System.Collections.Generic.HashSet<string> _disableTypes = new System.Collections.Generic.HashSet<string> {
      "GHPC.Camera.BufferedCameraFollow",
      "GHPC.Camera.CameraJiggler"
    };

    internal static void DisableCameraScripts() {
      if (_mainCam == null) return;
      foreach (var mb in _mainCam.GetComponentsInParent<MonoBehaviour>()) {
        if (mb == null) continue;
        if (_disableTypes.Contains(mb.GetType().FullName)) {
          mb.enabled = false;
        }
      }
    }

    internal static void EnableCameraScripts() {
      if (_mainCam == null) return;
      foreach (var mb in _mainCam.GetComponentsInParent<MonoBehaviour>()) {
        if (mb == null) continue;
        if (_disableTypes.Contains(mb.GetType().FullName)) {
          mb.enabled = true;
        }
      }
    }

    internal static void DumpCameraHierarchy() {
      if (_mainCam == null) return;
      Transform root = _mainCam.transform;
      while (root.parent != null) root = root.parent;
      DumpTransform(root, 0);
    }

    internal static void DumpTransform(Transform t, int depth) {
      string indent = new string(' ', depth * 2);
      string components = "";
      foreach (var c in t.GetComponents<Component>()) {
        if (c != null) components += c.GetType().Name + ", ";
      }
      for (int i = 0; i < t.childCount; i++) {
        DumpTransform(t.GetChild(i), depth + 1);
      }
    }

    internal static void TryResetBinocularsViaSlotToggle() {
      var camMgr = GHPC.Camera.CameraManager.Instance;
      if (camMgr == null) { MelonLogger.Msg("[Bino] CameraManager.Instance is null"); return; }

      var bcf = _mainCam.GetComponentInParent<GHPC.Camera.BufferedCameraFollow>();
      if (bcf == null) return; 

      var usingBinoProp = bcf.GetType().GetProperty("UsingBinoculars", BindingFlags.Public | BindingFlags.Instance);
      bool usingBino = usingBinoProp != null && (bool)usingBinoProp.GetValue(bcf);

      if (!usingBino) return;

      camMgr.ToggleCameraSlotSet(); 
      camMgr.ToggleCameraSlotSet(); 
    }

    internal static void StartTransitionToTopDown() {
      if (_mainCam == null) {
        _mainCam = Camera.main;
        if (_mainCam == null) return;
      }

      _unitTransitioning = false;
      try {
        TryResetBinocularsViaSlotToggle();
      } catch (System.Exception ex) {
        MelonLogger.Msg($"camera binoculars reset failed: {ex.GetType().Name}: {ex.Message}");
      }

      _savedPosition = _mainCam.transform.position;
      _savedRotation = _mainCam.transform.rotation;
      if (PlayerInput.Instance != null && PlayerInput.Instance.CurrentPlayerUnit != null) {
        Vector3 tankPos = PlayerInput.Instance.CurrentPlayerUnit.transform.position;
        _savedCameraOffset = _savedPosition - tankPos;
      }
      DisableCameraScripts();
      SetBodyVisible(false);
      HideGameHUD();

      Cursor.visible = true;
      Cursor.lockState = CursorLockMode.None;
      _transitionStartPos = _savedPosition;
      _transitionStartRot = _savedRotation;
      _transitionEndPos = GetPlayerTopDownPosition();
      _transitionEndRot = TopDownRotation;

      _transitioning = true;
      _inTopDown = true;
      _transitionTimer = 0f;

      try { TopDownUnitMenu.Create(); } catch (System.Exception ex) {
        MelonLogger.Msg($"[Menu] CreateUnitMenu failed: {ex.Message}");
      }
    }

    internal static void StartTransitionToNormal() {
      TopDownUnitMenu.Destroy();
      if (_mainCam == null) return;

      SetBodyVisible(false);

      _transitionStartPos = _mainCam.transform.position;
      _transitionStartRot = _mainCam.transform.rotation;

      if (PlayerInput.Instance != null && PlayerInput.Instance.CurrentPlayerUnit != null) {
        Vector3 playerPos = PlayerInput.Instance.CurrentPlayerUnit.transform.position;
        _transitionEndPos = playerPos + _savedCameraOffset;
      } else {
        _transitionEndPos = _savedPosition;
      }
      _transitionEndRot = _savedRotation;

      _transitioning = true;
      _returningToNormal = true;
      _transitionTimer = 0f;

    }

    internal static void RestoreNormalCamera() {
      EnableCameraScripts();
      SetBodyVisible(true);
      ShowGameHUD();

      Cursor.visible = false;
      Cursor.lockState = CursorLockMode.Locked;
    }

    public override void OnGUI() {
      TopDownUnitMenu.DrawGUI();
    }

    public override void OnLateUpdate() {
      if (_transitioning && _mainCam != null) {
        _transitionTimer += Time.unscaledDeltaTime;
        float t = Mathf.Clamp01(_transitionTimer / _transitionDuration);

        //When returning to normal, continuously update the end position
        //Possibly causing bugs
        if (_returningToNormal && PlayerInput.Instance != null && PlayerInput.Instance.CurrentPlayerUnit != null) {
          Vector3 currentTankPos = PlayerInput.Instance.CurrentPlayerUnit.transform.position;
          _transitionEndPos = currentTankPos + _savedCameraOffset;
        }

        //Smoothstep for position
        float tPos = t * t * (3f - 2f * t);

        float tRotRaw = Mathf.Clamp01((t - 0.25f) / 0.75f);
        float tRot = tRotRaw * tRotRaw * (3f - 2f * tRotRaw);

        _mainCam.transform.position = Vector3.Lerp(_transitionStartPos, _transitionEndPos, tPos);
        _mainCam.transform.rotation = Quaternion.Slerp(_transitionStartRot, _transitionEndRot, tRot);

        if (t >= 1f) {
          _transitioning = false;
          if (_returningToNormal) {
            _returningToNormal = false;
            _inTopDown = false;
            RestoreNormalCamera();
          }
        }
      }
      if (_inTopDown) {
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
      }

      TopDownUnitMenu.UpdateHoverPan();

      if (_inTopDown && !_transitioning && !TopDownUnitMenu._isHoveringUnit && !TopDownUnitMenu._hoverTransitioning && _mainCam != null) {
        Vector3 target = GetPlayerTopDownPosition();
        if (target != Vector3.zero) {
          _mainCam.transform.position = Vector3.Lerp(
            _mainCam.transform.position, target, Time.unscaledDeltaTime * 2f);
        }
      }

      if (_unitTransitioning && _mainCam != null) {
        _unitTransitionTimer += Time.unscaledDeltaTime;
        float t = Mathf.Clamp01(_unitTransitionTimer / _unitTransitionDuration);
        float tSmooth = t * t * (3f - 2f * t);

        Vector3 targetPos = _mainCam.transform.position;
        Quaternion targetRot = _mainCam.transform.rotation;

        _mainCam.transform.position = Vector3.Lerp(_unitTransStartPos, targetPos, tSmooth);
        _mainCam.transform.rotation = Quaternion.Slerp(_unitTransStartRot, targetRot, tSmooth);

        if (t >= 1f) {
          _unitTransitioning = false;
        }
      }
    }
  }

  [HarmonyPatch(typeof(PlayerInput), "ToggleUnitMenu", new[] { typeof(bool), typeof(bool) })]
  public static class ToggleUnitMenuPatch {
    [HarmonyPrefix]
    static bool Prefix() {
      if (CameraReworkMod._transitioning) return false;
      //minimum time bc one tab press causes two toggles
      if (Time.unscaledTime - CameraReworkMod._lastToggleTime < 1.0f) return false;
      CameraReworkMod._lastToggleTime = Time.unscaledTime;

      Camera cam = Camera.main;
      if (cam == null) return false;
      CameraReworkMod._mainCam = cam;

      try {
        if (!GHPC.Camera.CameraSlot.CurrentSlotIsExterior) {
          GHPC.Camera.CameraManager.Instance?.ToggleCameraSlotSet();
        }
      } catch (System.Exception ex) {
        MelonLogger.Msg($"Slot check failed: {ex.GetType().Name}: {ex.Message}");
      }

      if (CameraReworkMod._inTopDown) {
        CameraReworkMod.StartTransitionToNormal();
      } else {
        CameraReworkMod.StartTransitionToTopDown();
      }

      return false;
    }
  }

  [HarmonyPatch(typeof(GHPC.Camera.CameraManager), "ToggleCameraSlotSet")]
  public static class ToggleCameraSlotSetPatch {
    [HarmonyPrefix]
    static bool Prefix() {
      return !CameraReworkMod._inTopDown;
    }
  }

  [HarmonyPatch(typeof(GHPC.Camera.CameraManager), "CycleCameraSlot")]
  public static class CycleCameraSlotPatch {
    [HarmonyPrefix]
    static bool Prefix() {
      return !CameraReworkMod._inTopDown;
    }
  }

  [HarmonyPatch(typeof(GHPC.Camera.BufferedCameraFollow), "AdjustZoom")]
  public static class AdjustZoomPatch {
    [HarmonyPrefix]
    static bool Prefix() {
      return !CameraReworkMod._inTopDown;
    }
  }

  [HarmonyPatch(typeof(GHPC.Weapons.WeaponSystem), "Fire")]
  public static class WeaponFirePatch {
    [HarmonyPrefix]
    static bool Prefix() {
      return !CameraReworkMod._inTopDown && !CameraReworkMod._returningToNormal;
    }
  }

  [HarmonyPatch(typeof(PlayerInput), "SetPlayerUnit")]
  public static class SetPlayerUnitPatch {
    [HarmonyPrefix]
    static void Prefix() {
      Camera cam = Camera.main;
      if (cam == null) return;

      CameraReworkMod._mainCam = cam;
      CameraReworkMod._unitTransStartPos = cam.transform.position;
      CameraReworkMod._unitTransStartRot = cam.transform.rotation;
    }

    [HarmonyPostfix]
    static void Postfix() {
      if (CameraReworkMod._mainCam == null) return;
      CameraReworkMod._unitTransitioning = true;
      CameraReworkMod._unitTransitionTimer = 0f;
    }
  }
}
