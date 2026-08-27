using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using MelonLoader;
using UnityEngine;
using GHPC;
using GHPC.Player;
using GHPC.UI.UnitMenu;

namespace Camera_Rework {

  internal static class TopDownUnitMenu {
    // Menu state
    internal static bool _menuOpen;
    internal static List<PlatoonEntry> _platoons;
    internal static bool _hoverTransitioning;
    internal static float _hoverTransitionTimer;
    internal static float _hoverTransitionDuration = 0.45f;
    internal static Vector3 _hoverStartPos;
    internal static Vector3 _hoverEndPos;
    internal static bool _isHoveringUnit;
    internal static Unit _hoveredUnit; // track hovered unit for continuous follow

    // GUI state
    static Vector2 _scrollPos;
    static int _hoveredPlatoonIndex = -1;
    static int _hoveredUnitIndex = -1;
    static GUIStyle _buttonStyle;
    static GUIStyle _buttonHoverStyle;
    static GUIStyle _buttonCurrentStyle;
    static GUIStyle _buttonDisabledStyle;
    static GUIStyle _panelStyle;
    static GUIStyle _headerStyle;
    static bool _stylesInitialized;

    // Arrow textures (keyed by: color+size combo)
    static Dictionary<string, Texture2D> _arrowTexCache = new Dictionary<string, Texture2D>();

    // Platoon grouping data
    internal class PlatoonEntry {
      public string Name;
      public List<Unit> Units = new List<Unit>();
      public Unit Leader;
    }

    internal static void Create() {
      // Refresh valid units list from the game's existing system
      try {
        var umc = UnitMenuController.Instance;
        if (umc != null) umc.RefreshMenu(false);
      } catch (System.Exception ex) {
        MelonLogger.Msg($"[Menu] RefreshMenu failed: {ex.Message}");
      }

      // Get platoon data from UnitMenuController
      List<UnitMenuPlatoonData> platoonData = null;
      try {
        var umc = UnitMenuController.Instance;
        if (umc != null) {
          var field = umc.GetType().GetField("_platoonData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
          if (field != null) platoonData = field.GetValue(umc) as List<UnitMenuPlatoonData>;
        }
      } catch (System.Exception ex) {
        MelonLogger.Msg($"menu failed to get platoon data: {ex.Message}");
      }

      if (platoonData == null || platoonData.Count == 0) {
        // Fallback: get flat unit list
        List<Unit> validUnits = null;
        try {
          var umc = UnitMenuController.Instance;
          if (umc != null) {
            var field = umc.GetType().GetField("_allValidUnits", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) validUnits = field.GetValue(umc) as List<Unit>;
          }
        } catch (System.Exception ex) {
          MelonLogger.Msg($"menu failed to get valid units: {ex.Message}");
        }

        if (validUnits == null || validUnits.Count == 0) {
          MelonLogger.Msg("menu no valid units found");
          return;
        }

        _platoons = new List<PlatoonEntry>();
        var entry = new PlatoonEntry { Name = "All Units" };
        entry.Units.AddRange(validUnits);
        _platoons.Add(entry);
      } else {
        _platoons = new List<PlatoonEntry>();
        foreach (var pd in platoonData) {
          if (pd == null) continue;
          ReadOnlyCollection<Unit> units = null;
          try {
            units = pd.Units;
          } catch { continue; }
          if (units == null || units.Count == 0) continue;

          var entry = new PlatoonEntry { Name = pd.FriendlyName ?? "Unknown Platoon" };
          if (pd.PlatoonObject != null) entry.Leader = pd.PlatoonObject.PlatoonLeaderUnit;
          foreach (var u in units) {
            if (u != null) entry.Units.Add(u);
          }
          if (entry.Units.Count > 0) _platoons.Add(entry);
        }
      }

      int totalUnits = 0;
      foreach (var p in _platoons){
        totalUnits += p.Units.Count;
      } 
      //MelonLogger.Msg($"menu creating unit menu with {_platoons.Count} platoons, {totalUnits} units");

      _menuOpen = true;
      _scrollPos = Vector2.zero;
      _hoveredPlatoonIndex = -1;
      _hoveredUnitIndex = -1;
      _hoveredUnit = null;
      _stylesInitialized = false;
      _arrowTexCache.Clear();
      //MelonLogger.Msg("menu unit menu created (IMGUI mode)");
    }

    internal static void Destroy() {
      _menuOpen = false;
      _platoons = null;
      _isHoveringUnit = false;
      _hoverTransitioning = false;
      _hoveredPlatoonIndex = -1;
      _hoveredUnitIndex = -1;
      _hoveredUnit = null;
    }


    static Texture2D MakeArrowTexture(int width, int height, Color fill, int tipWidth) {
      var tex = new Texture2D(width, height);
      tex.filterMode = FilterMode.Point;
      var transparent = new Color(0f, 0f, 0f, 0f);
      int bodyEnd = width - tipWidth - 1;

      for (int y = 0; y < height; y++) {
        for (int x = 0; x < width; x++) {
          if (x <= bodyEnd) {
            tex.SetPixel(x, y, fill);
          } else {
            float progress = (float)(x - bodyEnd) / tipWidth;
            float halfSpan = (1f - progress) * (height / 2f);
            float center = height / 2f;
            if (y >= center - halfSpan && y <= center + halfSpan) {
              tex.SetPixel(x, y, fill);
            } else {
              tex.SetPixel(x, y, transparent);
            }
          }
        }
      }
      tex.Apply();
      return tex;
    }

    static Texture2D GetArrowTex(int width, int height, Color fill, int tipWidth) {
      string key = $"{width}_{height}_{fill}_{tipWidth}";
      if (!_arrowTexCache.TryGetValue(key, out var tex)) {
        tex = MakeArrowTexture(width, height, fill, tipWidth);
        _arrowTexCache[key] = tex;
      }
      return tex;
    }

    static void InitStyles() {
      if (_stylesInitialized) return;

      _panelStyle = new GUIStyle(GUI.skin.box);
      var panelTex = new Texture2D(1, 1);
      panelTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.4f));
      panelTex.Apply();
      _panelStyle.normal.background = panelTex;

      _headerStyle = new GUIStyle(GUI.skin.label);
      _headerStyle.fontSize = 14;
      _headerStyle.fontStyle = FontStyle.Bold;
      _headerStyle.normal.textColor = new Color(0f, 0f, 0f, 1f);
      _headerStyle.padding = new RectOffset(6, 6, 4, 2);

      int arrowW = 240;
      int arrowH = 40;
      int arrowHoverW = 280;
      int arrowHoverH = 48;
      int tipW = 30;
      int tipHoverW = 36;

      //Normal
      _buttonStyle = new GUIStyle();
      _buttonStyle.alignment = TextAnchor.MiddleLeft;
      _buttonStyle.fontSize = 16;
      _buttonStyle.padding = new RectOffset(12, tipW + 4, 6, 6);
      _buttonStyle.margin = new RectOffset(0, 0, 2, 2);
      _buttonStyle.normal.background = GetArrowTex(arrowW, arrowH, new Color(0.1f, 0.1f, 0.15f, 0.6f), tipW);
      _buttonStyle.normal.textColor = Color.white;

      //Hovered
      _buttonHoverStyle = new GUIStyle();
      _buttonHoverStyle.alignment = TextAnchor.MiddleLeft;
      _buttonHoverStyle.fontSize = 18;
      _buttonHoverStyle.padding = new RectOffset(12, tipHoverW + 6, 6, 6);
      _buttonHoverStyle.margin = new RectOffset(0, 0, 2, 2);
      _buttonHoverStyle.normal.background = GetArrowTex(arrowHoverW, arrowHoverH, new Color(0.2f, 0.2f, 0.3f, 0.8f), tipHoverW);
      _buttonHoverStyle.normal.textColor = Color.white;

      //Current
      _buttonCurrentStyle = new GUIStyle(_buttonStyle);
      _buttonCurrentStyle.normal.background = GetArrowTex(arrowW, arrowH, new Color(0.1f, 0.4f, 0.1f, 0.6f), tipW);
      _buttonCurrentStyle.normal.textColor = Color.white;

      //Disabled
      _buttonDisabledStyle = new GUIStyle(_buttonStyle);
      _buttonDisabledStyle.normal.background = GetArrowTex(arrowW, arrowH, new Color(0.2f, 0.2f, 0.2f, 0.5f), tipW);
      _buttonDisabledStyle.normal.textColor = Color.gray;

      _stylesInitialized = true;
    }

    internal static void DrawGUI() {
      if (!_menuOpen || _platoons == null) return;

      InitStyles();

      Unit currentUnit = PlayerInput.Instance?.CurrentPlayerUnit;

      float panelWidth = 300f;
      float panelX = 10f;
      float panelY = 115f;
      float panelHeight = Screen.height - panelY - 20f;

      //GUI.Box(new Rect(panelX, panelY, panelWidth, panelHeight), "", _panelStyle);

      Rect scrollArea = new Rect(panelX + 4f, panelY + 4f, panelWidth - 8f, panelHeight - 8f);
      float buttonHeight = 40f;
      float hoverButtonHeight = 48f;
      float buttonWidth = 240f;
      float hoverButtonWidth = 280f;
      float spacing = 4f;
      float headerHeight = 24f;
      float platoonSpacing = 8f;

      float contentHeight = 0f;
      for (int p = 0; p < _platoons.Count; p++) {
        contentHeight += headerHeight; // platoon header
        for (int u = 0; u < _platoons[p].Units.Count; u++) {
          bool isHovered = p == _hoveredPlatoonIndex && u == _hoveredUnitIndex;
          contentHeight += (isHovered ? hoverButtonHeight : buttonHeight) + spacing;
        }
        contentHeight += platoonSpacing;
      }
      _scrollPos = GUI.BeginScrollView(scrollArea, _scrollPos, new Rect(0, 0, panelWidth - 28f, contentHeight), GUIStyle.none, GUIStyle.none);

      float yPos = 0f;
      int newHoveredPlatoon = -1;
      int newHoveredUnit = -1;

      for (int p = 0; p < _platoons.Count; p++) {
        var platoon = _platoons[p];

        GUI.Label(new Rect(0, yPos, panelWidth - 28f, headerHeight), platoon.Name, _headerStyle);
        yPos += headerHeight;

        for (int u = 0; u < platoon.Units.Count; u++) {
          Unit unit = platoon.Units[u];
          if (unit == null) continue;

          bool isCurrentUnit = (unit == currentUnit);
          bool isNeutralized = unit.Neutralized;
          bool isHovered = (p == _hoveredPlatoonIndex && u == _hoveredUnitIndex);
          float h = isHovered ? hoverButtonHeight : buttonHeight;
          float w = isHovered ? hoverButtonWidth : buttonWidth;

          Rect btnRect = new Rect(0, yPos, w, h);

          GUIStyle style;
          if (isNeutralized) style = _buttonDisabledStyle;
          else if (isCurrentUnit) style = isHovered ? _buttonHoverStyle : _buttonCurrentStyle;
          else if (isHovered) style = _buttonHoverStyle;
          else style = _buttonStyle;

          string label = (unit == platoon.Leader) ? unit.FriendlyName + " (LEADER)" : unit.FriendlyName;

          if (!isNeutralized && GUI.Button(btnRect, label, style)) {
            _isHoveringUnit = false;
            _hoverTransitioning = false;
            _hoveredUnit = null;
            PlayerInput.Instance.SetPlayerUnit(unit);
            Destroy();
            CameraReworkMod.StartTransitionToNormal();
            GUI.EndScrollView();
            return;
          } else if (isNeutralized) {
            GUI.Label(btnRect, label, style);
          }
          if (btnRect.Contains(Event.current.mousePosition)) {
            newHoveredPlatoon = p;
            newHoveredUnit = u;
          }

          yPos += h + spacing;
        }

        yPos += platoonSpacing;
      }

      GUI.EndScrollView();

      if (newHoveredPlatoon != _hoveredPlatoonIndex || newHoveredUnit != _hoveredUnitIndex) {
        if (newHoveredPlatoon >= 0 && newHoveredUnit >= 0) {
          Unit hoverUnit = _platoons[newHoveredPlatoon].Units[newHoveredUnit];
          if (hoverUnit != null && !hoverUnit.Neutralized) {
            _isHoveringUnit = true;
            _hoveredUnit = hoverUnit;
            _hoverStartPos = CameraReworkMod._mainCam.transform.position;
            _hoverEndPos = hoverUnit.transform.position + Vector3.up * 75f;
            _hoverTransitionTimer = 0f;
            _hoverTransitioning = true;
          }
        } else if (_hoveredPlatoonIndex >= 0) {
          _isHoveringUnit = false;
          _hoveredUnit = null;
          _hoverStartPos = CameraReworkMod._mainCam.transform.position;
          _hoverEndPos = CameraReworkMod.GetPlayerTopDownPosition();
          _hoverTransitionTimer = 0f;
          _hoverTransitioning = true;
        }
        _hoveredPlatoonIndex = newHoveredPlatoon;
        _hoveredUnitIndex = newHoveredUnit;
      }
    }

    internal static void UpdateHoverPan() {
      if (CameraReworkMod._mainCam == null) return;

      if (_hoverTransitioning) {
        _hoverTransitionTimer += Time.unscaledDeltaTime;
        float ht = Mathf.Clamp01(_hoverTransitionTimer / _hoverTransitionDuration);
        float htSmooth = ht * ht * (3f - 2f * ht);
        if (_hoveredUnit != null) {
          _hoverEndPos = _hoveredUnit.transform.position + Vector3.up * 75f;
        }
        CameraReworkMod._mainCam.transform.position = Vector3.Lerp(_hoverStartPos, _hoverEndPos, htSmooth);
        if (ht >= 1f) _hoverTransitioning = false;
        return;
      }
      if (_isHoveringUnit && _hoveredUnit != null) {
        Vector3 target = _hoveredUnit.transform.position + Vector3.up * 75f;
        CameraReworkMod._mainCam.transform.position = Vector3.Lerp(
          CameraReworkMod._mainCam.transform.position, target, Time.unscaledDeltaTime * 4f);
      }
    }
  }
}
