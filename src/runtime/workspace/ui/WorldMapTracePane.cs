using Godot;
using System;
using System.Collections.Generic;
using Uplink2.Runtime;

#nullable enable

namespace Uplink2.Runtime.Workspace.Ui;

/// <summary>Content-only world map trace pane rendered inside the shell workspace or legacy host windows.</summary>
internal sealed partial class WorldMapTracePane : Control, IWorkspaceConstraintAwarePaneContent, IWorkspacePaneStateParticipant
{
    internal static readonly Vector2I DefaultMapViewportSize = new(512, 256);
    internal static readonly Vector2I ReferenceTextureFallbackSize = new(2048, 1024);

    private const string InternetNetId = "internet";
    private const string WorldMapTraceTexturePath = "res://gui/images/min_world_map_subtle_glow.png";
    private const float WorldMapNodeFillRadius = 3.5f;
    private const float WorldMapNodeHaloRadius = 7.0f;
    private const float WorldMapNodeOutlineRadius = 5.25f;
    private const float WorldMapNodeOutlineWidth = 1.5f;
    private const float WorldMapNodeBaseStrokeWidth = 1.0f;
    private const int WorldMapNodeOutlineArcPointCount = 32;
    private const float WorldMapSshLineWidth = 1.25f;
    private const float WorldMapSshStartArcRadiusOffset = 2.0f;
    private const float WorldMapSshStartArcSweepDegrees = 72.0f;
    private const float WorldMapSshStartArcHalfSweepDegrees = WorldMapSshStartArcSweepDegrees * 0.5f;
    private const float WorldMapSshStartArcWidth = 1.5f;
    private const float WorldMapSshTargetMarkerRadius = 7.0f;
    private const float WorldMapSshTargetMarkerGapFromNode = 9.0f;
    private const float WorldMapSshTargetMarkerPerpendicularScale = 0.7f;
    private const float WorldMapSshTargetMarkerRearScale = 0.6f;
    private const float WorldMapSshTargetMarkerOutlineWidth = 1.0f;
    private const float WorldMapSshMinVectorLengthSquared = 0.0001f;
    private const float WorldMapSshAngleMergeEpsilonDegrees = 0.5f;
    private const float WorldMapSshAngleFullCoverageEpsilonDegrees = 1.0f;
    private const string DefaultTabId = "map";
    private const int OuterPadding = 8;
    private const int RowColumnGap = 4;
    private const int TopBarHeight = 24;
    private const int LeftRailWidth = 24;
    private const string GlowHBlurShaderCode = @"shader_type canvas_item;

uniform float blur_radius : hint_range(1.0, 50.0) = 50.0;
uniform float threshold : hint_range(0.0, 1.0) = 0.08;

void fragment() {
    float px = TEXTURE_PIXEL_SIZE.x;
    float sigma = blur_radius * 0.4;
    float inv_s2 = 1.0 / (2.0 * sigma * sigma);
    float accum = 0.0;
    float wt = 0.0;
    for (int i = -16; i <= 16; i++) {
        float offs = float(i) / 16.0 * blur_radius;
        float sx = UV.x + offs * px;
        float mask = step(0.0, sx) * step(sx, 1.0);
        float w = exp(-(offs * offs) * inv_s2) * mask;
        vec3 sc = texture(TEXTURE, vec2(clamp(sx, 0.0, 1.0), UV.y)).rgb;
        float b = max(max(sc.r, sc.g), sc.b);
        accum += smoothstep(threshold, threshold + 0.25, b) * b * w;
        wt += w;
    }
    COLOR = vec4(vec3(accum / max(wt, 0.001)), 1.0);
}";
    private const string GlowVBlurShaderCode = @"shader_type canvas_item;
render_mode unshaded, blend_add;

uniform vec4 glow_tint : source_color = vec4(0.35, 0.93, 1.0, 1.0);
uniform float glow_strength : hint_range(0.0, 5.0) = 2.5;
uniform float blur_radius : hint_range(1.0, 50.0) = 50.0;
uniform float compress : hint_range(0.05, 1.0) = 0.7;

void fragment() {
    float px = TEXTURE_PIXEL_SIZE.y;
    float sigma = blur_radius * 0.4;
    float inv_s2 = 1.0 / (2.0 * sigma * sigma);
    float accum = 0.0;
    float wt = 0.0;
    for (int i = -16; i <= 16; i++) {
        float offs = float(i) / 16.0 * blur_radius;
        float sy = UV.y + offs * px;
        float mask = step(0.0, sy) * step(sy, 1.0);
        float w = exp(-(offs * offs) * inv_s2) * mask;
        accum += texture(TEXTURE, vec2(UV.x, clamp(sy, 0.0, 1.0))).r * w;
        wt += w;
    }
    float raw = accum / max(wt, 0.001);
    float tone = raw / (raw + compress);
    float intensity = clamp(tone * glow_strength, 0.0, 1.0);
    COLOR = vec4(glow_tint.rgb * intensity, intensity);
}";

    private static readonly Color WorldMapNodeFillOnlineColor = new(1f, 1f, 1f, 1f);
    private static readonly Color WorldMapNodeFillWorkstationColor = new(0.18f, 0.9f, 0.18f, 1f);
    private static readonly Color WorldMapNodeFillOfflineColor = new(0.5f, 0.5f, 0.5f, 1f);
    private static readonly Color WorldMapNodeBaseStrokeColor = new(0.03f, 0.11f, 0.16f, 0.78f);
    private static readonly Color WorldMapNodeOutlineRedColor = new(1f, 0.2f, 0.2f, 1f);
    private static readonly Color WorldMapNodeHaloYellowColor = new(1f, 0.9f, 0.15f, 0.45f);
    private static readonly Color WorldMapSshLineColor = new(0.35f, 0.93f, 1.0f, 0.65f);
    private static readonly Color WorldMapSshStartArcColor = new(0.20f, 0.84f, 0.58f, 0.95f);
    private static readonly Color WorldMapSshTargetMarkerColor = new(0.95f, 0.42f, 0.40f, 0.95f);
    private static readonly Color WorldMapSshTargetMarkerOutlineColor = new(0.13f, 0.07f, 0.08f, 0.7f);

    private static readonly IReadOnlyList<WorldMapTraceTabDefinition> TabDefinitions =
    [
        new WorldMapTraceTabDefinition("map", "map", DefaultSelected: true, IncludeInPhaseOneUi: true),
        new WorldMapTraceTabDefinition("nodes", "nodes", DefaultSelected: false, IncludeInPhaseOneUi: false),
    ];

    private static readonly IReadOnlyList<WorldMapTraceToggleDefinition> ToggleDefinitions =
    [
        new WorldMapTraceToggleDefinition("hot", "hot", DefaultEnabled: true, IncludeInPhaseOneUi: true),
        new WorldMapTraceToggleDefinition("forensic", "forensic", DefaultEnabled: true, IncludeInPhaseOneUi: false),
        new WorldMapTraceToggleDefinition("lock-on", "lock-on", DefaultEnabled: true, IncludeInPhaseOneUi: false),
    ];

    private readonly Dictionary<string, Button> tabButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> toggleButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> toggleStatesById = new(StringComparer.Ordinal);

    private Control? mapViewportHost;
    private ScrollContainer? mapViewportScroll;
    private Control? mapViewportScrollContentRoot;
    private TextureRect? mapTextureRect;
    private HBoxContainer? topTabsContainer;
    private VBoxContainer? leftTogglesContainer;
    private WorldMapNodeOverlay? nodeOverlay;
    private ButtonGroup? tabGroup;
    private string activeTabId = DefaultTabId;
    private bool isUiBuilt;
    private bool suppressPaneStateChanged;
    private WorkspacePaneConstraintRenderState? activeConstraintState;

    event Action? IWorkspacePaneStateParticipant.WorkspacePaneStateChanged
    {
        add => workspacePaneStateChanged += value;
        remove => workspacePaneStateChanged -= value;
    }

    private event Action? workspacePaneStateChanged;

    /// <inheritdoc/>
    public override void _Ready()
    {
        BuildUiIfNeeded();
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        UpdateNodeOverlay();
    }

    internal static List<string> CollectWorldMapTraceNodeIds(
        IReadOnlyDictionary<string, HashSet<string>> knownNodesByNet,
        string? workstationNodeId)
    {
        var nodeIdSet = new HashSet<string>(StringComparer.Ordinal);
        if (knownNodesByNet.TryGetValue(InternetNetId, out var internetKnownNodeIds))
        {
            foreach (var rawNodeId in internetKnownNodeIds)
            {
                if (string.IsNullOrWhiteSpace(rawNodeId))
                {
                    continue;
                }

                nodeIdSet.Add(rawNodeId.Trim());
            }
        }

        if (!string.IsNullOrWhiteSpace(workstationNodeId))
        {
            nodeIdSet.Add(workstationNodeId.Trim());
        }

        var orderedNodeIds = new List<string>(nodeIdSet);
        orderedNodeIds.Sort(StringComparer.Ordinal);
        return orderedNodeIds;
    }

    internal static Vector2 ProjectWorldMapLocation(double lat, double lng, Vector2 viewportSize)
    {
        var width = Math.Max(1f, viewportSize.X);
        var height = Math.Max(1f, viewportSize.Y);
        var clampedLat = Math.Clamp(lat, -90d, 90d);
        var clampedLng = Math.Clamp(lng, -180d, 180d);
        var projectedX = (float)(((clampedLng + 180d) / 360d) * width);
        var projectedY = (float)(((90d - clampedLat) / 180d) * height);
        return new Vector2(projectedX, projectedY);
    }

    internal static Rect2 ResolveDisplayedMapRect(Vector2 containerSize, Vector2 textureSize)
    {
        var safeContainerSize = new Vector2(
            Math.Max(1f, containerSize.X),
            Math.Max(1f, containerSize.Y));
        var safeTextureSize = new Vector2(
            Math.Max(1f, textureSize.X),
            Math.Max(1f, textureSize.Y));
        var scale = Math.Min(
            safeContainerSize.X / safeTextureSize.X,
            safeContainerSize.Y / safeTextureSize.Y);
        var drawSize = safeTextureSize * scale;
        var drawPosition = (safeContainerSize - drawSize) * 0.5f;
        return new Rect2(drawPosition, drawSize);
    }

    internal static Vector2I GetReferenceTextureSize()
    {
        // The workspace contract owns a canonical WORLD_MAP_TRACE reference texture size.
        // The shipped UI asset may be downscaled, but constraint thresholds follow the contract value.
        return ReferenceTextureFallbackSize;
    }

    internal static Vector2I ResolveMinUsableViewportSize(Vector2I textureSize)
    {
        return new Vector2I(
            Mathf.Max(1, Mathf.CeilToInt(textureSize.X * 0.1f)),
            Mathf.Max(1, Mathf.CeilToInt(textureSize.Y * 0.1f)));
    }


    internal static Color ResolveWorldMapNodeFillColor(bool isOffline, bool isWorkstation)
    {
        if (isOffline)
        {
            return WorldMapNodeFillOfflineColor;
        }

        if (isWorkstation)
        {
            return WorldMapNodeFillWorkstationColor;
        }

        return WorldMapNodeFillOnlineColor;
    }

    internal static WorldMapNodeOutlineMode ResolveWorldMapNodeOutlineMode(ServerReason reason)
    {
        return reason switch
        {
            ServerReason.Reboot => WorldMapNodeOutlineMode.PulseRed,
            ServerReason.Disabled => WorldMapNodeOutlineMode.SolidRed,
            ServerReason.Crashed => WorldMapNodeOutlineMode.SolidRed,
            _ => WorldMapNodeOutlineMode.None,
        };
    }

    internal static WorldMapRenderBundle BuildWorldMapRenderBundle(
        WorldRuntime runtime,
        Vector2 viewportSize,
        bool includeHotOverlay)
    {
        var nodes = new List<WorldMapNodeRenderState>();
        var projectedNodePositions = new Dictionary<string, Vector2>(StringComparer.Ordinal);
        var workstationNodeId = runtime.PlayerWorkstationServer?.NodeId ?? string.Empty;
        var nodeIds = CollectWorldMapTraceNodeIds(runtime.KnownNodesByNet, workstationNodeId);
        foreach (var nodeId in nodeIds)
        {
            if (!runtime.TryGetServer(nodeId, out var server))
            {
                continue;
            }

            var iconInfo = server.Icon ?? RuntimeServerIconInfo.CreateDefault();
            var fillColor = ResolveWorldMapNodeFillColor(
                isOffline: server.Status == ServerStatus.Offline,
                isWorkstation: string.Equals(nodeId, workstationNodeId, StringComparison.Ordinal));
            var outlineMode = ResolveWorldMapNodeOutlineMode(server.Reason);
            var projectedPosition = ProjectWorldMapLocation(server.Location.Lat, server.Location.Lng, viewportSize);
            nodes.Add(new WorldMapNodeRenderState(
                projectedPosition,
                iconInfo.IconType,
                iconInfo.HaloType,
                fillColor,
                outlineMode));
            projectedNodePositions[nodeId] = projectedPosition;
        }

        var sshLines = new List<WorldMapSshLineRenderState>();
        var sshStartArcs = new List<WorldMapSshStartArcRenderState>();
        var sshTargetMarkers = new List<WorldMapSshTargetMarkerRenderState>();
        if (includeHotOverlay)
        {
            BuildWorldMapSshRenderStates(
                runtime.GetActiveSshSessionEdgeSnapshots(),
                projectedNodePositions,
                out sshLines,
                out sshStartArcs,
                out sshTargetMarkers);
        }

        return new WorldMapRenderBundle(nodes, sshLines, sshStartArcs, sshTargetMarkers);
    }

    internal static void BuildWorldMapSshRenderStates(
        IReadOnlyList<WorldRuntime.ActiveSshSessionEdgeSnapshot> sshEdges,
        IReadOnlyDictionary<string, Vector2> projectedNodePositions,
        out List<WorldMapSshLineRenderState> sshLines,
        out List<WorldMapSshStartArcRenderState> sshStartArcs,
        out List<WorldMapSshTargetMarkerRenderState> sshTargetMarkers)
    {
        sshLines = new List<WorldMapSshLineRenderState>();
        sshStartArcs = new List<WorldMapSshStartArcRenderState>();
        sshTargetMarkers = new List<WorldMapSshTargetMarkerRenderState>();
        if (sshEdges.Count == 0 || projectedNodePositions.Count == 0)
        {
            return;
        }

        var dedupedEdgeKeys = new HashSet<WorldMapSshEdgeKey>();
        var dedupedEdges = new List<WorldMapSshEdgeGeometry>();
        var incomingEdgeCountByNodeId = new Dictionary<string, int>(StringComparer.Ordinal);
        var outgoingEdgeCountByNodeId = new Dictionary<string, int>(StringComparer.Ordinal);
        var sourceAngleRanges = new Dictionary<string, List<WorldMapAngleRange>>(StringComparer.Ordinal);
        var startArcRadius = WorldMapNodeOutlineRadius + WorldMapSshStartArcRadiusOffset;
        foreach (var sshEdge in sshEdges)
        {
            var sourceNodeId = sshEdge.SourceNodeId?.Trim() ?? string.Empty;
            var targetNodeId = sshEdge.TargetNodeId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(sourceNodeId) ||
                string.IsNullOrWhiteSpace(targetNodeId) ||
                string.Equals(sourceNodeId, targetNodeId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!projectedNodePositions.TryGetValue(sourceNodeId, out var sourcePosition) ||
                !projectedNodePositions.TryGetValue(targetNodeId, out var targetPosition))
            {
                continue;
            }

            var edgeKey = new WorldMapSshEdgeKey(sourceNodeId, targetNodeId);
            if (!dedupedEdgeKeys.Add(edgeKey))
            {
                continue;
            }

            var directionVector = targetPosition - sourcePosition;
            if (directionVector.LengthSquared() <= WorldMapSshMinVectorLengthSquared)
            {
                continue;
            }

            var direction = directionVector.Normalized();
            var theta = Mathf.Atan2(direction.Y, direction.X);
            dedupedEdges.Add(new WorldMapSshEdgeGeometry(
                sourceNodeId,
                targetNodeId,
                sourcePosition,
                targetPosition,
                direction,
                theta));
            outgoingEdgeCountByNodeId[sourceNodeId] = outgoingEdgeCountByNodeId.TryGetValue(sourceNodeId, out var outgoingCount)
                ? outgoingCount + 1
                : 1;
            incomingEdgeCountByNodeId[targetNodeId] = incomingEdgeCountByNodeId.TryGetValue(targetNodeId, out var incomingCount)
                ? incomingCount + 1
                : 1;
        }

        foreach (var edge in dedupedEdges)
        {
            var sourceHasPreviousEdge = incomingEdgeCountByNodeId.TryGetValue(edge.SourceNodeId, out var sourceIncomingCount) &&
                                        sourceIncomingCount > 0;
            var targetHasNextEdge = outgoingEdgeCountByNodeId.TryGetValue(edge.TargetNodeId, out var targetOutgoingCount) &&
                                    targetOutgoingCount > 0;

            var startAnchor = sourceHasPreviousEdge
                ? edge.SourcePosition
                : edge.SourcePosition + (edge.Direction * startArcRadius);
            var endAnchor = targetHasNextEdge
                ? edge.TargetPosition
                : edge.TargetPosition - (edge.Direction * (WorldMapSshTargetMarkerRadius + WorldMapSshTargetMarkerGapFromNode));
            if ((endAnchor - startAnchor).LengthSquared() <= WorldMapSshMinVectorLengthSquared)
            {
                continue;
            }

            sshLines.Add(new WorldMapSshLineRenderState(startAnchor, endAnchor));
            if (!sourceHasPreviousEdge)
            {
                AddSshStartArcAngleRange(edge.SourceNodeId, edge.DirectionAngle, sourceAngleRanges);
            }

            if (!targetHasNextEdge)
            {
                sshTargetMarkers.Add(new WorldMapSshTargetMarkerRenderState(endAnchor, edge.DirectionAngle));
            }
        }

        var fullCoverageEpsilon = Mathf.DegToRad(WorldMapSshAngleFullCoverageEpsilonDegrees);
        foreach (var pair in sourceAngleRanges)
        {
            if (!projectedNodePositions.TryGetValue(pair.Key, out var sourceCenter))
            {
                continue;
            }

            var mergedRanges = MergeWorldMapAngleRanges(pair.Value);
            var coverage = 0f;
            foreach (var mergedRange in mergedRanges)
            {
                coverage += Math.Max(0f, mergedRange.End - mergedRange.Start);
            }

            if (coverage >= Mathf.Tau - fullCoverageEpsilon)
            {
                sshStartArcs.Add(new WorldMapSshStartArcRenderState(sourceCenter, 0f, Mathf.Tau, IsFullRing: true));
                continue;
            }

            foreach (var mergedRange in mergedRanges)
            {
                sshStartArcs.Add(new WorldMapSshStartArcRenderState(
                    sourceCenter,
                    mergedRange.Start,
                    mergedRange.End,
                    IsFullRing: false));
            }
        }
    }

    private void BuildUiIfNeeded()
    {
        if (isUiBuilt)
        {
            return;
        }

        isUiBuilt = true;
        SetAnchorsPreset(Control.LayoutPreset.FullRect);
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        var background = new ColorRect
        {
            Name = "Background",
            Color = Colors.Black,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        PrepareFillControl(background);
        AddChild(background);

        var rootMargin = new MarginContainer
        {
            Name = "RootMargin",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        PrepareFillControl(rootMargin);
        rootMargin.AddThemeConstantOverride("margin_left", OuterPadding);
        rootMargin.AddThemeConstantOverride("margin_top", OuterPadding);
        rootMargin.AddThemeConstantOverride("margin_right", OuterPadding);
        rootMargin.AddThemeConstantOverride("margin_bottom", OuterPadding);
        AddChild(rootMargin);

        var rootLayout = new VBoxContainer
        {
            Name = "RootLayout",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        PrepareFillControl(rootLayout);
        rootLayout.AddThemeConstantOverride("separation", RowColumnGap);
        rootMargin.AddChild(rootLayout);

        var topBar = new HBoxContainer
        {
            Name = "TopBar",
            CustomMinimumSize = new Vector2(0, TopBarHeight),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        topBar.AddThemeConstantOverride("separation", RowColumnGap);
        rootLayout.AddChild(topBar);

        var topLeftRailSpacer = new Control
        {
            Name = "TopLeftRailSpacer",
            CustomMinimumSize = new Vector2(LeftRailWidth, 0),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
        };
        topBar.AddChild(topLeftRailSpacer);

        topTabsContainer = new HBoxContainer
        {
            Name = "TopTabs",
            CustomMinimumSize = new Vector2(0, TopBarHeight),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
        };
        topTabsContainer.AddThemeConstantOverride("separation", RowColumnGap);
        topBar.AddChild(topTabsContainer);

        var topSpacer = new Control
        {
            Name = "TopSpacer",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        topBar.AddChild(topSpacer);

        var bodyRow = new HBoxContainer
        {
            Name = "BodyRow",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        bodyRow.AddThemeConstantOverride("separation", RowColumnGap);
        rootLayout.AddChild(bodyRow);

        leftTogglesContainer = new VBoxContainer
        {
            Name = "LeftToggles",
            CustomMinimumSize = new Vector2(LeftRailWidth, 0),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
        };
        leftTogglesContainer.AddThemeConstantOverride("separation", RowColumnGap);
        bodyRow.AddChild(leftTogglesContainer);

        mapViewportScroll = new ScrollContainer
        {
            Name = "MapViewportScroll",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        bodyRow.AddChild(mapViewportScroll);

        mapViewportScrollContentRoot = new Control
        {
            Name = "MapViewportScrollContent",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        mapViewportScroll.AddChild(mapViewportScrollContentRoot);

        mapViewportHost = new ColorRect
        {
            Name = "MapViewport",
            Color = Colors.Black,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        mapViewportHost.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        mapViewportScrollContentRoot.AddChild(mapViewportHost);

        mapTextureRect = new TextureRect
        {
            Name = "MapTexture",
            AnchorRight = 1.0f,
            AnchorBottom = 1.0f,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        };
        var mapTexture = GD.Load<Texture2D>(WorldMapTraceTexturePath);
        if (mapTexture is null)
        {
            GD.PushWarning($"WorldMapTracePane: failed to load map texture '{WorldMapTraceTexturePath}'.");
        }
        else
        {
            mapTextureRect.Texture = mapTexture;
        }

        var glowHalfSize = new Vector2I(DefaultMapViewportSize.X / 2, DefaultMapViewportSize.Y / 2);
        var glowHBlurViewport = new SubViewport
        {
            Name = "GlowHBlurViewport",
            Size = glowHalfSize,
            TransparentBg = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        var glowHBlurSource = new TextureRect
        {
            Name = "GlowHBlurSource",
            Position = Vector2.Zero,
            Size = new Vector2(glowHalfSize.X, glowHalfSize.Y),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            Material = CreateGlowHBlurMaterial(),
        };
        if (mapTexture is not null)
        {
            glowHBlurSource.Texture = mapTexture;
        }
        glowHBlurViewport.AddChild(glowHBlurSource);

        var mapGlowTextureRect = new TextureRect
        {
            Name = "MapGlowOverlay",
            AnchorRight = 1.0f,
            AnchorBottom = 1.0f,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Material = CreateGlowVBlurMaterial(),
        };

        nodeOverlay = new WorldMapNodeOverlay
        {
            Name = "MapNodeOverlay",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        nodeOverlay.SetAnchorsPreset(Control.LayoutPreset.TopLeft);

        mapViewportHost.AddChild(mapTextureRect);
        mapViewportHost.AddChild(mapGlowTextureRect);
        mapViewportHost.AddChild(nodeOverlay);
        AddChild(glowHBlurViewport);
        mapGlowTextureRect.Texture = glowHBlurViewport.GetTexture();

        BuildTabButtons();
        BuildToggleButtons();
        ApplyViewportConstraintState();
    }

    private void UpdateNodeOverlay()
    {
        if (nodeOverlay is null)
        {
            return;
        }

        UpdateOverlayLayout();

        var isMapTabActive = string.Equals(activeTabId, "map", StringComparison.Ordinal);
        nodeOverlay.Visible = isMapTabActive;
        if (!isMapTabActive)
        {
            nodeOverlay.SetRenderData(
                Array.Empty<WorldMapNodeRenderState>(),
                Array.Empty<WorldMapSshLineRenderState>(),
                Array.Empty<WorldMapSshStartArcRenderState>(),
                Array.Empty<WorldMapSshTargetMarkerRenderState>());
            return;
        }

        var runtime = WorldRuntime.Instance;
        if (runtime is null)
        {
            nodeOverlay.SetRenderData(
                Array.Empty<WorldMapNodeRenderState>(),
                Array.Empty<WorldMapSshLineRenderState>(),
                Array.Empty<WorldMapSshStartArcRenderState>(),
                Array.Empty<WorldMapSshTargetMarkerRenderState>());
            return;
        }

        var viewportSize = nodeOverlay.Size;
        if (viewportSize.X <= 0f || viewportSize.Y <= 0f)
        {
            viewportSize = new Vector2(DefaultMapViewportSize.X, DefaultMapViewportSize.Y);
        }

        if (!runtime.TryRunViaWorldLock(
                () => BuildWorldMapRenderBundle(runtime, viewportSize, IsHotToggleEnabled()),
                out var renderBundle,
                out _))
        {
            nodeOverlay.SetRenderData(
                Array.Empty<WorldMapNodeRenderState>(),
                Array.Empty<WorldMapSshLineRenderState>(),
                Array.Empty<WorldMapSshStartArcRenderState>(),
                Array.Empty<WorldMapSshTargetMarkerRenderState>());
            return;
        }

        nodeOverlay.SetRenderData(
            renderBundle.Nodes,
            renderBundle.SshLines,
            renderBundle.SshStartArcs,
            renderBundle.SshTargetMarkers);
    }

    private void UpdateOverlayLayout()
    {
        if (nodeOverlay is null || mapViewportHost is null)
        {
            return;
        }

        UpdateViewportConstraintLayout();
        var textureSize = mapTextureRect?.Texture?.GetSize() ?? new Vector2(DefaultMapViewportSize.X, DefaultMapViewportSize.Y);
        var displayedMapRect = ResolveDisplayedMapRect(mapViewportHost.Size, textureSize);
        nodeOverlay.Position = displayedMapRect.Position;
        nodeOverlay.Size = displayedMapRect.Size;
    }

    void IWorkspaceConstraintAwarePaneContent.ApplyConstraintState(WorkspacePaneConstraintRenderState state)
    {
        activeConstraintState = state ?? throw new ArgumentNullException(nameof(state));
        ApplyViewportConstraintState();
    }

    private void ApplyViewportConstraintState()
    {
        if (!isUiBuilt)
        {
            return;
        }

        UpdateViewportConstraintLayout();
    }

    private void UpdateViewportConstraintLayout()
    {
        if (mapViewportScroll is null || mapViewportScrollContentRoot is null || mapViewportHost is null)
        {
            return;
        }

        var minWidth = 0.0f;
        var minHeight = 0.0f;
        if (activeConstraintState is not null)
        {
            if (activeConstraintState.HorizontalResolvePolicy == WorkspaceConstraintResolvePolicy.Scroll)
            {
                minWidth = activeConstraintState.MinUsableWidthPx;
            }

            if (activeConstraintState.VerticalResolvePolicy == WorkspaceConstraintResolvePolicy.Scroll)
            {
                minHeight = activeConstraintState.MinUsableHeightPx;
            }
        }

        var viewportSize = mapViewportScroll.Size;
        var targetWidth = Mathf.Max(viewportSize.X, minWidth);
        var targetHeight = Mathf.Max(viewportSize.Y, minHeight);
        mapViewportScrollContentRoot.CustomMinimumSize = new Vector2(minWidth, minHeight);
        mapViewportScrollContentRoot.Size = new Vector2(targetWidth, targetHeight);
    }

    private bool IsHotToggleEnabled()
    {
        return !toggleStatesById.TryGetValue("hot", out var enabled) || enabled;
    }

    IReadOnlyDictionary<string, object?> IWorkspacePaneStateParticipant.CaptureWorkspacePaneState()
    {
        return WorldMapTracePaneStateCodec.Capture(ResolvePersistedActiveTabId(), IsHotToggleEnabled());
    }

    void IWorkspacePaneStateParticipant.RestoreWorkspacePaneState(IReadOnlyDictionary<string, object?> state)
    {
        var restoredState = WorldMapTracePaneStateCodec.Restore(state);

        suppressPaneStateChanged = true;
        try
        {
            ApplyTabState(restoredState.ActiveTabId, emitChange: false);
            ApplyToggleState("hot", restoredState.FilterHot, emitChange: false);
        }
        finally
        {
            suppressPaneStateChanged = false;
        }

        UpdateNodeOverlay();
    }

    private void BuildTabButtons()
    {
        if (topTabsContainer is null)
        {
            return;
        }

        tabGroup = new ButtonGroup();
        tabButtons.Clear();

        string? fallbackTabId = null;
        string? defaultTabId = null;
        foreach (var definition in TabDefinitions)
        {
            fallbackTabId ??= definition.Id;
            if (!definition.IncludeInPhaseOneUi)
            {
                continue;
            }

            var tabButton = new Button
            {
                Name = BuildControlName("Tab", definition.Id),
                Text = definition.Label,
                ToggleMode = true,
                ButtonGroup = tabGroup,
                CustomMinimumSize = new Vector2(TopBarHeight, TopBarHeight),
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                ClipText = true,
                TooltipText = definition.Label,
            };
            tabButton.AddThemeFontSizeOverride("font_size", 10);
            tabButton.Pressed += () => HandleTabPressed(definition.Id);
            topTabsContainer.AddChild(tabButton);
            tabButtons[definition.Id] = tabButton;

            if (definition.DefaultSelected)
            {
                defaultTabId = definition.Id;
            }
        }

        var initialTabId = string.IsNullOrWhiteSpace(defaultTabId)
            ? fallbackTabId ?? DefaultTabId
            : defaultTabId;
        ApplyTabState(initialTabId, emitChange: false);
    }

    private void BuildToggleButtons()
    {
        if (leftTogglesContainer is null)
        {
            return;
        }

        toggleButtons.Clear();
        toggleStatesById.Clear();
        foreach (var definition in ToggleDefinitions)
        {
            if (!definition.IncludeInPhaseOneUi)
            {
                continue;
            }

            var toggleButton = new Button
            {
                Name = BuildControlName("Toggle", definition.Id),
                Text = definition.Label,
                ToggleMode = true,
                ButtonPressed = definition.DefaultEnabled,
                CustomMinimumSize = new Vector2(LeftRailWidth, TopBarHeight),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                ClipText = true,
                TooltipText = definition.Label,
            };
            toggleButton.AddThemeFontSizeOverride("font_size", 10);
            toggleButton.Toggled += enabled => HandleToggleChanged(definition.Id, enabled);
            leftTogglesContainer.AddChild(toggleButton);
            toggleButtons[definition.Id] = toggleButton;
            ApplyToggleState(definition.Id, definition.DefaultEnabled, emitChange: false);
        }
    }

    private void HandleTabPressed(string tabId)
    {
        if (string.IsNullOrWhiteSpace(tabId))
        {
            return;
        }

        ApplyTabState(tabId, emitChange: true);
    }

    private void HandleToggleChanged(string toggleId, bool isEnabled)
    {
        if (string.IsNullOrWhiteSpace(toggleId))
        {
            return;
        }

        ApplyToggleState(toggleId, isEnabled, emitChange: true);
    }

    private void ApplyTabState(string tabId, bool emitChange)
    {
        var nextTabId = ResolveVisibleTabIdOrDefault(tabId);
        var changed = !string.Equals(activeTabId, nextTabId, StringComparison.Ordinal);
        activeTabId = nextTabId;
        foreach (var pair in tabButtons)
        {
            pair.Value.SetPressedNoSignal(string.Equals(pair.Key, activeTabId, StringComparison.Ordinal));
        }

        UpdateNodeOverlay();
        if (changed && emitChange)
        {
            EmitPaneStateChanged();
        }
    }

    private void ApplyToggleState(string toggleId, bool isEnabled, bool emitChange)
    {
        if (string.IsNullOrWhiteSpace(toggleId))
        {
            return;
        }

        var hadExistingValue = toggleStatesById.TryGetValue(toggleId, out var existingValue);
        var changed = !hadExistingValue || existingValue != isEnabled;
        toggleStatesById[toggleId] = isEnabled;
        if (toggleButtons.TryGetValue(toggleId, out var toggleButton))
        {
            toggleButton.SetPressedNoSignal(isEnabled);
        }

        UpdateNodeOverlay();
        if (changed && emitChange)
        {
            EmitPaneStateChanged();
        }
    }

    private string ResolvePersistedActiveTabId()
    {
        return ResolveVisibleTabIdOrDefault(activeTabId);
    }

    private static string ResolveVisibleTabIdOrDefault(string? tabId)
    {
        foreach (var definition in TabDefinitions)
        {
            if (!definition.IncludeInPhaseOneUi)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(tabId) &&
                string.Equals(definition.Id, tabId.Trim(), StringComparison.Ordinal))
            {
                return definition.Id;
            }
        }

        foreach (var definition in TabDefinitions)
        {
            if (definition.IncludeInPhaseOneUi && definition.DefaultSelected)
            {
                return definition.Id;
            }
        }

        foreach (var definition in TabDefinitions)
        {
            if (definition.IncludeInPhaseOneUi)
            {
                return definition.Id;
            }
        }

        return DefaultTabId;
    }

    private void EmitPaneStateChanged()
    {
        if (suppressPaneStateChanged)
        {
            return;
        }

        workspacePaneStateChanged?.Invoke();
    }

    private static void AddSshStartArcAngleRange(
        string sourceNodeId,
        float directionAngle,
        Dictionary<string, List<WorldMapAngleRange>> sourceAngleRanges)
    {
        if (!sourceAngleRanges.TryGetValue(sourceNodeId, out var ranges))
        {
            ranges = new List<WorldMapAngleRange>();
            sourceAngleRanges[sourceNodeId] = ranges;
        }

        var halfSweepRadians = Mathf.DegToRad(WorldMapSshStartArcHalfSweepDegrees);
        var startAngle = NormalizeWorldMapAngle(directionAngle - halfSweepRadians);
        var endAngle = NormalizeWorldMapAngle(directionAngle + halfSweepRadians);
        if (endAngle < startAngle)
        {
            ranges.Add(new WorldMapAngleRange(startAngle, Mathf.Tau));
            ranges.Add(new WorldMapAngleRange(0f, endAngle));
            return;
        }

        ranges.Add(new WorldMapAngleRange(startAngle, endAngle));
    }

    private static List<WorldMapAngleRange> MergeWorldMapAngleRanges(List<WorldMapAngleRange> ranges)
    {
        var merged = new List<WorldMapAngleRange>();
        if (ranges.Count == 0)
        {
            return merged;
        }

        ranges.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        var epsilon = Mathf.DegToRad(WorldMapSshAngleMergeEpsilonDegrees);
        var currentStart = ranges[0].Start;
        var currentEnd = ranges[0].End;
        for (var index = 1; index < ranges.Count; index++)
        {
            var nextRange = ranges[index];
            if (nextRange.Start <= currentEnd + epsilon)
            {
                currentEnd = Math.Max(currentEnd, nextRange.End);
                continue;
            }

            merged.Add(new WorldMapAngleRange(currentStart, currentEnd));
            currentStart = nextRange.Start;
            currentEnd = nextRange.End;
        }

        merged.Add(new WorldMapAngleRange(currentStart, currentEnd));
        return merged;
    }

    private static float NormalizeWorldMapAngle(float angle)
    {
        var normalized = angle % Mathf.Tau;
        if (normalized < 0f)
        {
            normalized += Mathf.Tau;
        }

        return normalized;
    }

    private static float ResolveWorldMapPulseAlpha(double nowSeconds)
    {
        var cycle = nowSeconds - Math.Floor(nowSeconds);
        var wave = 0.5d + (0.5d * Math.Sin(cycle * (Math.PI * 2d)));
        return (float)(0.35d + (0.65d * wave));
    }

    private static string BuildControlName(string prefix, string id)
    {
        var safeId = string.IsNullOrWhiteSpace(id) ? "unknown" : id.Trim().Replace('-', '_');
        return $"{prefix}_{safeId}";
    }

    private static ShaderMaterial CreateGlowHBlurMaterial()
    {
        var shader = new Shader { Code = GlowHBlurShaderCode };
        return new ShaderMaterial { Shader = shader };
    }

    private static ShaderMaterial CreateGlowVBlurMaterial()
    {
        var shader = new Shader { Code = GlowVBlurShaderCode };
        return new ShaderMaterial { Shader = shader };
    }

    private static void PrepareFillControl(Control control)
    {
        control.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        control.OffsetLeft = 0.0f;
        control.OffsetTop = 0.0f;
        control.OffsetRight = 0.0f;
        control.OffsetBottom = 0.0f;
        control.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        control.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
    }

    internal enum WorldMapNodeOutlineMode
    {
        None = 0,
        PulseRed = 1,
        SolidRed = 2,
    }

    internal readonly record struct WorldMapNodeRenderState(
        Vector2 Position,
        ServerIconType IconType,
        ServerHaloType HaloType,
        Color FillColor,
        WorldMapNodeOutlineMode OutlineMode);

    internal readonly record struct WorldMapSshLineRenderState(
        Vector2 Start,
        Vector2 End);

    internal readonly record struct WorldMapSshStartArcRenderState(
        Vector2 Center,
        float StartAngle,
        float EndAngle,
        bool IsFullRing);

    internal readonly record struct WorldMapSshTargetMarkerRenderState(
        Vector2 Center,
        float DirectionAngle);

    internal readonly record struct WorldMapRenderBundle(
        IReadOnlyList<WorldMapNodeRenderState> Nodes,
        IReadOnlyList<WorldMapSshLineRenderState> SshLines,
        IReadOnlyList<WorldMapSshStartArcRenderState> SshStartArcs,
        IReadOnlyList<WorldMapSshTargetMarkerRenderState> SshTargetMarkers);

    private readonly record struct WorldMapTraceTabDefinition(
        string Id,
        string Label,
        bool DefaultSelected,
        bool IncludeInPhaseOneUi);

    private readonly record struct WorldMapTraceToggleDefinition(
        string Id,
        string Label,
        bool DefaultEnabled,
        bool IncludeInPhaseOneUi);

    private readonly record struct WorldMapSshEdgeKey(
        string SourceNodeId,
        string TargetNodeId);

    private readonly record struct WorldMapSshEdgeGeometry(
        string SourceNodeId,
        string TargetNodeId,
        Vector2 SourcePosition,
        Vector2 TargetPosition,
        Vector2 Direction,
        float DirectionAngle);

    private readonly record struct WorldMapAngleRange(
        float Start,
        float End);

    private sealed partial class WorldMapNodeOverlay : Control
    {
        private IReadOnlyList<WorldMapNodeRenderState> renderNodes = Array.Empty<WorldMapNodeRenderState>();
        private IReadOnlyList<WorldMapSshLineRenderState> renderSshLines = Array.Empty<WorldMapSshLineRenderState>();
        private IReadOnlyList<WorldMapSshStartArcRenderState> renderSshStartArcs = Array.Empty<WorldMapSshStartArcRenderState>();
        private IReadOnlyList<WorldMapSshTargetMarkerRenderState> renderSshTargetMarkers = Array.Empty<WorldMapSshTargetMarkerRenderState>();

        internal void SetRenderData(
            IReadOnlyList<WorldMapNodeRenderState> nodes,
            IReadOnlyList<WorldMapSshLineRenderState> sshLines,
            IReadOnlyList<WorldMapSshStartArcRenderState> sshStartArcs,
            IReadOnlyList<WorldMapSshTargetMarkerRenderState> sshTargetMarkers)
        {
            renderNodes = nodes ?? Array.Empty<WorldMapNodeRenderState>();
            renderSshLines = sshLines ?? Array.Empty<WorldMapSshLineRenderState>();
            renderSshStartArcs = sshStartArcs ?? Array.Empty<WorldMapSshStartArcRenderState>();
            renderSshTargetMarkers = sshTargetMarkers ?? Array.Empty<WorldMapSshTargetMarkerRenderState>();
            QueueRedraw();
        }

        /// <inheritdoc/>
        public override void _Draw()
        {
            if (renderNodes.Count == 0 &&
                renderSshLines.Count == 0 &&
                renderSshStartArcs.Count == 0 &&
                renderSshTargetMarkers.Count == 0)
            {
                return;
            }

            foreach (var state in renderNodes)
            {
                if (state.HaloType != ServerHaloType.Yellow)
                {
                    continue;
                }

                DrawCircle(state.Position, WorldMapNodeHaloRadius, WorldMapNodeHaloYellowColor);
            }

            foreach (var sshLine in renderSshLines)
            {
                DrawLine(
                    sshLine.Start,
                    sshLine.End,
                    WorldMapSshLineColor,
                    WorldMapSshLineWidth,
                    antialiased: true);
            }

            var startArcRadius = WorldMapNodeOutlineRadius + WorldMapSshStartArcRadiusOffset;
            foreach (var startArc in renderSshStartArcs)
            {
                var startAngle = startArc.IsFullRing ? 0f : startArc.StartAngle;
                var endAngle = startArc.IsFullRing ? Mathf.Tau : startArc.EndAngle;
                DrawArc(
                    startArc.Center,
                    startArcRadius,
                    startAngle,
                    endAngle,
                    WorldMapNodeOutlineArcPointCount,
                    WorldMapSshStartArcColor,
                    WorldMapSshStartArcWidth,
                    antialiased: true);
            }

            foreach (var targetMarker in renderSshTargetMarkers)
            {
                DrawSshTargetTriangle(targetMarker.Center, targetMarker.DirectionAngle);
            }

            foreach (var state in renderNodes)
            {
                DrawFillShape(state.Position, state.IconType, state.FillColor);
                DrawBaseShapeOutline(state.Position, state.IconType);
            }

            var nowSeconds = Time.GetTicksMsec() / 1000d;
            foreach (var state in renderNodes)
            {
                var outlineColor = ResolveOutlineColor(state.OutlineMode, nowSeconds);
                if (!outlineColor.HasValue)
                {
                    continue;
                }

                DrawArc(
                    state.Position,
                    WorldMapNodeOutlineRadius,
                    0f,
                    Mathf.Tau,
                    WorldMapNodeOutlineArcPointCount,
                    outlineColor.Value,
                    WorldMapNodeOutlineWidth,
                    antialiased: true);
            }
        }

        private static Color? ResolveOutlineColor(WorldMapNodeOutlineMode mode, double nowSeconds)
        {
            if (mode == WorldMapNodeOutlineMode.None)
            {
                return null;
            }

            var color = WorldMapNodeOutlineRedColor;
            if (mode == WorldMapNodeOutlineMode.PulseRed)
            {
                color.A = ResolveWorldMapPulseAlpha(nowSeconds);
            }

            return color;
        }

        private void DrawFillShape(Vector2 center, ServerIconType iconType, Color fillColor)
        {
            switch (iconType)
            {
                case ServerIconType.Triangle:
                    DrawTriangle(center, fillColor);
                    break;
                case ServerIconType.Square:
                    DrawSquare(center, fillColor);
                    break;
                default:
                    DrawCircle(center, WorldMapNodeFillRadius, fillColor);
                    break;
            }
        }

        private void DrawBaseShapeOutline(Vector2 center, ServerIconType iconType)
        {
            switch (iconType)
            {
                case ServerIconType.Triangle:
                    DrawTriangleOutline(center);
                    break;
                case ServerIconType.Square:
                    DrawSquareOutline(center);
                    break;
                default:
                    DrawArc(
                        center,
                        WorldMapNodeFillRadius,
                        0f,
                        Mathf.Tau,
                        WorldMapNodeOutlineArcPointCount,
                        WorldMapNodeBaseStrokeColor,
                        WorldMapNodeBaseStrokeWidth,
                        antialiased: true);
                    break;
            }
        }

        private void DrawSquare(Vector2 center, Color fillColor)
        {
            var side = WorldMapNodeFillRadius * 2f;
            var topLeft = center - new Vector2(WorldMapNodeFillRadius, WorldMapNodeFillRadius);
            DrawRect(new Rect2(topLeft, new Vector2(side, side)), fillColor, filled: true);
        }

        private void DrawSquareOutline(Vector2 center)
        {
            var side = WorldMapNodeFillRadius * 2f;
            var topLeft = center - new Vector2(WorldMapNodeFillRadius, WorldMapNodeFillRadius);
            DrawRect(
                new Rect2(topLeft, new Vector2(side, side)),
                WorldMapNodeBaseStrokeColor,
                filled: false,
                width: WorldMapNodeBaseStrokeWidth,
                antialiased: true);
        }

        private void DrawTriangle(Vector2 center, Color fillColor)
        {
            var radius = WorldMapNodeFillRadius;
            var halfBase = radius * 0.95f;
            var tipHeight = radius * 1.05f;
            var baseY = center.Y + (radius * 0.75f);
            var points = new Vector2[]
            {
                new(center.X, center.Y - tipHeight),
                new(center.X + halfBase, baseY),
                new(center.X - halfBase, baseY),
            };
            var colors = new Color[] { fillColor, fillColor, fillColor };
            DrawPolygon(points, colors);
        }

        private void DrawTriangleOutline(Vector2 center)
        {
            var radius = WorldMapNodeFillRadius;
            var halfBase = radius * 0.95f;
            var tipHeight = radius * 1.05f;
            var baseY = center.Y + (radius * 0.75f);
            var top = new Vector2(center.X, center.Y - tipHeight);
            var right = new Vector2(center.X + halfBase, baseY);
            var left = new Vector2(center.X - halfBase, baseY);
            DrawLine(top, right, WorldMapNodeBaseStrokeColor, WorldMapNodeBaseStrokeWidth, antialiased: true);
            DrawLine(right, left, WorldMapNodeBaseStrokeColor, WorldMapNodeBaseStrokeWidth, antialiased: true);
            DrawLine(left, top, WorldMapNodeBaseStrokeColor, WorldMapNodeBaseStrokeWidth, antialiased: true);
        }

        private void DrawSshTargetTriangle(Vector2 center, float directionAngle)
        {
            var radius = WorldMapSshTargetMarkerRadius;
            var direction = new Vector2(Mathf.Cos(directionAngle), Mathf.Sin(directionAngle));
            var perpendicular = new Vector2(-direction.Y, direction.X);
            var tip = center + (direction * radius);
            var rearCenter = center - (direction * (radius * WorldMapSshTargetMarkerRearScale));
            var halfWidth = radius * WorldMapSshTargetMarkerPerpendicularScale;
            var points = new Vector2[]
            {
                tip,
                rearCenter + (perpendicular * halfWidth),
                rearCenter - (perpendicular * halfWidth),
            };
            var colors = new Color[]
            {
                WorldMapSshTargetMarkerColor,
                WorldMapSshTargetMarkerColor,
                WorldMapSshTargetMarkerColor,
            };
            DrawPolygon(points, colors);
            DrawLine(points[0], points[1], WorldMapSshTargetMarkerOutlineColor, WorldMapSshTargetMarkerOutlineWidth, antialiased: true);
            DrawLine(points[1], points[2], WorldMapSshTargetMarkerOutlineColor, WorldMapSshTargetMarkerOutlineWidth, antialiased: true);
            DrawLine(points[2], points[0], WorldMapSshTargetMarkerOutlineColor, WorldMapSshTargetMarkerOutlineWidth, antialiased: true);
        }
    }
}
