using System;
using System.Collections.Generic;

#nullable enable

namespace Uplink2.Runtime.Workspace.Ui;

/// <summary>Identifies which workspace snapshot ratio drives one split in the renderer layout tree.</summary>
internal enum WorkspaceSplitRatioBinding
{
    /// <summary>The top-level left/right column split.</summary>
    LeftColumn = 0,

    /// <summary>The right-column top/bottom split.</summary>
    RightTop = 1,
}

/// <summary>Identifies the axis of one renderer layout split.</summary>
internal enum WorkspaceSplitAxis
{
    /// <summary>Splits children left/right.</summary>
    Horizontal = 0,

    /// <summary>Splits children top/bottom.</summary>
    Vertical = 1,
}

/// <summary>Base node type for the alpha shell renderer layout tree.</summary>
internal abstract class WorkspaceLayoutNodeDefinition
{
}

/// <summary>Leaf renderer-layout node that hosts one dock slot.</summary>
internal sealed class WorkspaceSlotLeafDefinition : WorkspaceLayoutNodeDefinition
{
    /// <summary>Initializes a new slot leaf definition.</summary>
    /// <param name="slot">The slot rendered by the leaf.</param>
    internal WorkspaceSlotLeafDefinition(DockSlot slot)
    {
        Slot = slot;
    }

    /// <summary>Gets the slot rendered by the leaf.</summary>
    internal DockSlot Slot { get; }
}

/// <summary>Split renderer-layout node that arranges two child nodes along one axis.</summary>
internal sealed class WorkspaceSplitNodeDefinition : WorkspaceLayoutNodeDefinition
{
    /// <summary>Initializes a new split definition.</summary>
    /// <param name="axis">Axis of the split.</param>
    /// <param name="ratioBinding">Snapshot ratio used to size the split.</param>
    /// <param name="firstChild">First child node.</param>
    /// <param name="secondChild">Second child node.</param>
    internal WorkspaceSplitNodeDefinition(
        WorkspaceSplitAxis axis,
        WorkspaceSplitRatioBinding ratioBinding,
        WorkspaceLayoutNodeDefinition firstChild,
        WorkspaceLayoutNodeDefinition secondChild)
    {
        Axis = axis;
        RatioBinding = ratioBinding;
        FirstChild = firstChild ?? throw new ArgumentNullException(nameof(firstChild));
        SecondChild = secondChild ?? throw new ArgumentNullException(nameof(secondChild));
    }

    /// <summary>Gets the axis of the split.</summary>
    internal WorkspaceSplitAxis Axis { get; }

    /// <summary>Gets the snapshot ratio binding that sizes the split.</summary>
    internal WorkspaceSplitRatioBinding RatioBinding { get; }

    /// <summary>Gets the first child node.</summary>
    internal WorkspaceLayoutNodeDefinition FirstChild { get; }

    /// <summary>Gets the second child node.</summary>
    internal WorkspaceLayoutNodeDefinition SecondChild { get; }
}

/// <summary>Defines the shell renderer layout tree independently from the current alpha slot count.</summary>
internal sealed class WorkspaceLayoutDefinition
{
    private static readonly DockSlot[] AlphaSlotOrder =
    {
        DockSlot.Left,
        DockSlot.RightTop,
        DockSlot.RightBottom,
    };

    /// <summary>Initializes a new renderer layout definition.</summary>
    /// <param name="root">Root node of the layout tree.</param>
    /// <param name="slotOrder">Stable alpha slot order exposed by the definition.</param>
    internal WorkspaceLayoutDefinition(
        WorkspaceLayoutNodeDefinition root,
        IReadOnlyList<DockSlot> slotOrder)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        SlotOrder = slotOrder ?? throw new ArgumentNullException(nameof(slotOrder));
    }

    /// <summary>Gets the root layout node.</summary>
    internal WorkspaceLayoutNodeDefinition Root { get; }

    /// <summary>Gets the stable slot ordering exposed by the definition.</summary>
    internal IReadOnlyList<DockSlot> SlotOrder { get; }

    /// <summary>Creates the current alpha layout tree.</summary>
    internal static WorkspaceLayoutDefinition CreateAlpha()
    {
        return new WorkspaceLayoutDefinition(
            new WorkspaceSplitNodeDefinition(
                WorkspaceSplitAxis.Horizontal,
                WorkspaceSplitRatioBinding.LeftColumn,
                new WorkspaceSlotLeafDefinition(DockSlot.Left),
                new WorkspaceSplitNodeDefinition(
                    WorkspaceSplitAxis.Vertical,
                    WorkspaceSplitRatioBinding.RightTop,
                    new WorkspaceSlotLeafDefinition(DockSlot.RightTop),
                    new WorkspaceSlotLeafDefinition(DockSlot.RightBottom))),
            AlphaSlotOrder);
    }
}
