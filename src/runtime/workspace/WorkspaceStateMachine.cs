using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

#nullable enable

namespace Uplink2.Runtime.Workspace;

/// <summary>Owns the minimal stage-1 shell workspace state and its transitions.</summary>
public sealed class WorkspaceStateMachine
{
    /// <summary>Default normalized left-column ratio for a fresh shell workspace.</summary>
    public const float DefaultLeftRatio = 0.42f;

    /// <summary>Default normalized right-top ratio for a fresh shell workspace.</summary>
    public const float DefaultRightTopRatio = 0.55f;

    private static readonly DockSlot[] SlotOrder =
    {
        DockSlot.Left,
        DockSlot.RightTop,
        DockSlot.RightBottom,
    };

    private readonly Dictionary<DockSlot, List<WorkspacePaneKind>> dockStacks = new();
    private readonly Dictionary<DockSlot, WorkspacePaneKind?> activeDockPaneBySlot = new();
    private readonly HashSet<WorkspacePaneKind> pinnedSet = [];
    private WorkspaceMode mode;
    private WorkspacePaneKind? maximizedPane;
    private float leftRatio;
    private float rightTopRatio;

    /// <summary>Initializes the state machine with the default bootstrap state.</summary>
    public WorkspaceStateMachine()
    {
        foreach (var slot in SlotOrder)
        {
            dockStacks[slot] = [];
            activeDockPaneBySlot[slot] = null;
        }

        ResetToDefaultState();
    }

    /// <summary>Returns an immutable snapshot of the current workspace state.</summary>
    public WorkspaceStateSnapshot GetSnapshot()
    {
        ValidateInvariants();
        return CreateSnapshot();
    }

    /// <summary>Resets the workspace to the stage-1 bootstrap state.</summary>
    public bool ResetToDefaultState()
    {
        var changed = !IsDefaultState();

        mode = WorkspaceMode.Docked;
        maximizedPane = null;
        leftRatio = DefaultLeftRatio;
        rightTopRatio = DefaultRightTopRatio;
        pinnedSet.Clear();
        pinnedSet.Add(WorkspacePaneKind.Terminal);

        foreach (var slot in SlotOrder)
        {
            dockStacks[slot].Clear();
            activeDockPaneBySlot[slot] = null;
        }

        dockStacks[DockSlot.Left].Add(WorkspacePaneKind.Terminal);
        activeDockPaneBySlot[DockSlot.Left] = WorkspacePaneKind.Terminal;

        ValidateInvariants();
        return changed;
    }

    /// <summary>Replaces the current state with a fully sanitized effective workspace snapshot.</summary>
    /// <param name="snapshot">Sanitized effective state to apply.</param>
    /// <returns><see langword="true"/> when the state changed; otherwise, <see langword="false"/>.</returns>
    public bool ReplaceState(WorkspaceStateSnapshot snapshot)
    {
        ValidateSnapshot(snapshot);

        if (SnapshotsEquivalent(CreateSnapshot(), snapshot))
        {
            return false;
        }

        mode = snapshot.Mode;
        maximizedPane = snapshot.MaximizedPane;
        leftRatio = snapshot.LeftRatio;
        rightTopRatio = snapshot.RightTopRatio;

        pinnedSet.Clear();
        foreach (var kind in snapshot.PinnedSet)
        {
            pinnedSet.Add(kind);
        }

        foreach (var slot in SlotOrder)
        {
            dockStacks[slot].Clear();
            var slotState = snapshot.Slots[slot];
            dockStacks[slot].AddRange(slotState.DockStack);
            activeDockPaneBySlot[slot] = slotState.ActivePane;
        }

        ValidateInvariants();
        return true;
    }

    /// <summary>Activates a pane, opening it in its home slot when currently closed.</summary>
    public bool ActivatePane(WorkspacePaneKind kind)
    {
        var changed = false;
        if (!TryGetCurrentDockSlot(kind, out var slot))
        {
            slot = ResolveHomeSlot(kind);
            dockStacks[slot].Add(kind);
            changed = true;
        }

        if (maximizedPane == kind)
        {
            if (mode != WorkspaceMode.Maximized)
            {
                mode = WorkspaceMode.Maximized;
                changed = true;
            }
        }
        else if (mode != WorkspaceMode.Docked)
        {
            mode = WorkspaceMode.Docked;
            changed = true;
        }

        if (SetActivePane(slot, kind))
        {
            changed = true;
        }

        ValidateInvariants();
        return changed;
    }

    /// <summary>Closes a resident pane.</summary>
    public bool ClosePane(WorkspacePaneKind kind)
    {
        if (!TryGetCurrentDockSlot(kind, out var slot))
        {
            return false;
        }

        var stack = dockStacks[slot];
        var removedIndex = stack.IndexOf(kind);
        if (removedIndex < 0)
        {
            return false;
        }

        stack.RemoveAt(removedIndex);
        if (activeDockPaneBySlot[slot] == kind)
        {
            activeDockPaneBySlot[slot] = SelectFallbackPane(stack, removedIndex);
        }

        if (maximizedPane == kind)
        {
            maximizedPane = null;
            mode = WorkspaceMode.Docked;
        }

        ValidateInvariants();
        return true;
    }

    /// <summary>Moves a resident pane to another dock slot.</summary>
    public bool MovePane(WorkspacePaneKind kind, DockSlot targetSlot)
    {
        if (!TryGetCurrentDockSlot(kind, out var sourceSlot))
        {
            return false;
        }

        if (sourceSlot == targetSlot)
        {
            return false;
        }

        var sourceStack = dockStacks[sourceSlot];
        var sourceIndex = sourceStack.IndexOf(kind);
        if (sourceIndex < 0)
        {
            return false;
        }

        sourceStack.RemoveAt(sourceIndex);
        if (activeDockPaneBySlot[sourceSlot] == kind)
        {
            activeDockPaneBySlot[sourceSlot] = SelectFallbackPane(sourceStack, sourceIndex);
        }

        dockStacks[targetSlot].Add(kind);
        activeDockPaneBySlot[targetSlot] = kind;

        ValidateInvariants();
        return true;
    }

    /// <summary>Adds a pane kind to the pinned set.</summary>
    public bool PinPane(WorkspacePaneKind kind)
    {
        var changed = pinnedSet.Add(kind);
        ValidateInvariants();
        return changed;
    }

    /// <summary>Removes a pane kind from the pinned set.</summary>
    public bool UnpinPane(WorkspacePaneKind kind)
    {
        var changed = pinnedSet.Remove(kind);
        ValidateInvariants();
        return changed;
    }

    /// <summary>Stores and enters a maximized pane context for a resident pane.</summary>
    public bool MaximizePane(WorkspacePaneKind kind)
    {
        if (!TryGetCurrentDockSlot(kind, out _))
        {
            return false;
        }

        if (maximizedPane == kind && mode == WorkspaceMode.Maximized)
        {
            return false;
        }

        maximizedPane = kind;
        mode = WorkspaceMode.Maximized;
        ValidateInvariants();
        return true;
    }

    /// <summary>Clears the stored maximized pane context and returns to docked mode.</summary>
    public bool RestoreDocked()
    {
        if (mode == WorkspaceMode.Docked && maximizedPane is null)
        {
            return false;
        }

        mode = WorkspaceMode.Docked;
        maximizedPane = null;
        ValidateInvariants();
        return true;
    }

    /// <summary>Updates the normalized split ratios.</summary>
    public bool SetSplitRatios(float nextLeftRatio, float nextRightTopRatio)
    {
        if (!IsValidRatio(nextLeftRatio) || !IsValidRatio(nextRightTopRatio))
        {
            return false;
        }

        if (leftRatio.Equals(nextLeftRatio) && rightTopRatio.Equals(nextRightTopRatio))
        {
            return false;
        }

        leftRatio = nextLeftRatio;
        rightTopRatio = nextRightTopRatio;
        ValidateInvariants();
        return true;
    }

    /// <summary>Tries to resolve the current dock slot of a resident pane.</summary>
    public bool TryGetCurrentDockSlot(WorkspacePaneKind kind, out DockSlot slot)
    {
        foreach (var candidate in SlotOrder)
        {
            if (dockStacks[candidate].Contains(kind))
            {
                slot = candidate;
                return true;
            }
        }

        slot = default;
        return false;
    }

    private static DockSlot ResolveHomeSlot(WorkspacePaneKind kind)
    {
        return kind switch
        {
            WorkspacePaneKind.Terminal => DockSlot.Left,
            WorkspacePaneKind.WebViewer => DockSlot.Left,
            WorkspacePaneKind.CodeEditor => DockSlot.Left,
            WorkspacePaneKind.WorldMapTrace => DockSlot.RightTop,
            WorkspacePaneKind.Mail => DockSlot.RightBottom,
            WorkspacePaneKind.MissionPanel => DockSlot.RightBottom,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown workspace pane kind."),
        };
    }

    private static bool SnapshotsEquivalent(WorkspaceStateSnapshot left, WorkspaceStateSnapshot right)
    {
        if (left.Mode != right.Mode ||
            left.MaximizedPane != right.MaximizedPane ||
            !left.LeftRatio.Equals(right.LeftRatio) ||
            !left.RightTopRatio.Equals(right.RightTopRatio))
        {
            return false;
        }

        if (!new HashSet<WorkspacePaneKind>(left.PinnedSet).SetEquals(right.PinnedSet))
        {
            return false;
        }

        foreach (var slot in SlotOrder)
        {
            var leftSlot = left.Slots[slot];
            var rightSlot = right.Slots[slot];
            if (leftSlot.ActivePane != rightSlot.ActivePane ||
                leftSlot.DockStack.Count != rightSlot.DockStack.Count)
            {
                return false;
            }

            for (var index = 0; index < leftSlot.DockStack.Count; index++)
            {
                if (leftSlot.DockStack[index] != rightSlot.DockStack[index])
                {
                    return false;
                }
            }
        }

        return true;
    }

    private bool IsDefaultState()
    {
        if (mode != WorkspaceMode.Docked ||
            maximizedPane is not null ||
            !leftRatio.Equals(DefaultLeftRatio) ||
            !rightTopRatio.Equals(DefaultRightTopRatio))
        {
            return false;
        }

        if (pinnedSet.Count != 1 || !pinnedSet.Contains(WorkspacePaneKind.Terminal))
        {
            return false;
        }

        return MatchesSlotState(DockSlot.Left, [WorkspacePaneKind.Terminal], WorkspacePaneKind.Terminal) &&
               MatchesSlotState(DockSlot.RightTop, [], null) &&
               MatchesSlotState(DockSlot.RightBottom, [], null);
    }

    private bool MatchesSlotState(
        DockSlot slot,
        IReadOnlyList<WorkspacePaneKind> expectedStack,
        WorkspacePaneKind? expectedActivePane)
    {
        var actualStack = dockStacks[slot];
        if (actualStack.Count != expectedStack.Count)
        {
            return false;
        }

        for (var i = 0; i < actualStack.Count; i++)
        {
            if (actualStack[i] != expectedStack[i])
            {
                return false;
            }
        }

        return activeDockPaneBySlot[slot] == expectedActivePane;
    }

    private bool SetActivePane(DockSlot slot, WorkspacePaneKind kind)
    {
        if (activeDockPaneBySlot[slot] == kind)
        {
            return false;
        }

        activeDockPaneBySlot[slot] = kind;
        return true;
    }

    private static WorkspacePaneKind? SelectFallbackPane(List<WorkspacePaneKind> stack, int removedIndex)
    {
        if (stack.Count == 0)
        {
            return null;
        }

        var fallbackIndex = Math.Min(removedIndex, stack.Count - 1);
        return stack[fallbackIndex];
    }

    private static bool IsValidRatio(float value)
    {
        return float.IsFinite(value) && value > 0.0f && value < 1.0f;
    }

    private static void ValidateSnapshot(WorkspaceStateSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        if (!IsValidRatio(snapshot.LeftRatio))
        {
            throw new InvalidOperationException($"Left ratio '{snapshot.LeftRatio}' is outside the normalized range.");
        }

        if (!IsValidRatio(snapshot.RightTopRatio))
        {
            throw new InvalidOperationException($"Right-top ratio '{snapshot.RightTopRatio}' is outside the normalized range.");
        }

        if (snapshot.Slots.Count != SlotOrder.Length)
        {
            throw new InvalidOperationException(
                $"Effective workspace snapshot must contain exactly {SlotOrder.Length} slots.");
        }

        var seenPanes = new HashSet<WorkspacePaneKind>();
        foreach (var slot in SlotOrder)
        {
            if (!snapshot.Slots.TryGetValue(slot, out var slotState))
            {
                throw new InvalidOperationException($"Effective workspace snapshot is missing slot '{slot}'.");
            }

            if (slotState.Slot != slot)
            {
                throw new InvalidOperationException(
                    $"Effective workspace snapshot slot '{slot}' contains mismatched state for '{slotState.Slot}'.");
            }

            foreach (var pane in slotState.DockStack)
            {
                if (!seenPanes.Add(pane))
                {
                    throw new InvalidOperationException($"Pane '{pane}' appears in more than one slot.");
                }
            }

            if (slotState.ActivePane.HasValue && !slotState.DockStack.Contains(slotState.ActivePane.Value))
            {
                throw new InvalidOperationException(
                    $"Active pane '{slotState.ActivePane.Value}' is not resident in slot '{slot}'.");
            }
        }

        var uniquePinnedSet = new HashSet<WorkspacePaneKind>();
        foreach (var pane in snapshot.PinnedSet)
        {
            if (!uniquePinnedSet.Add(pane))
            {
                throw new InvalidOperationException($"Pinned set contains duplicate pane '{pane}'.");
            }
        }

        if (snapshot.Mode == WorkspaceMode.Maximized && snapshot.MaximizedPane is null)
        {
            throw new InvalidOperationException("Effective workspace snapshot cannot be maximized without a maximized pane.");
        }

        if (snapshot.MaximizedPane.HasValue && !seenPanes.Contains(snapshot.MaximizedPane.Value))
        {
            throw new InvalidOperationException(
                $"Maximized pane '{snapshot.MaximizedPane.Value}' must be resident in the effective workspace snapshot.");
        }
    }

    private WorkspaceStateSnapshot CreateSnapshot()
    {
        var slotSnapshots = new Dictionary<DockSlot, WorkspaceDockSlotState>(SlotOrder.Length);
        foreach (var slot in SlotOrder)
        {
            var stack = dockStacks[slot].ToArray();
            slotSnapshots.Add(
                slot,
                new WorkspaceDockSlotState(
                    slot,
                    Array.AsReadOnly(stack),
                    activeDockPaneBySlot[slot]));
        }

        var pinnedKinds = pinnedSet
            .OrderBy(static kind => kind)
            .ToArray();

        return new WorkspaceStateSnapshot(
            mode,
            maximizedPane,
            leftRatio,
            rightTopRatio,
            new ReadOnlyDictionary<DockSlot, WorkspaceDockSlotState>(slotSnapshots),
            Array.AsReadOnly(pinnedKinds));
    }

    private void ValidateInvariants()
    {
        var seenPanes = new HashSet<WorkspacePaneKind>();
        foreach (var slot in SlotOrder)
        {
            if (!dockStacks.TryGetValue(slot, out var stack))
            {
                throw new InvalidOperationException($"Workspace state is missing slot stack '{slot}'.");
            }

            if (!activeDockPaneBySlot.TryGetValue(slot, out var activePane))
            {
                throw new InvalidOperationException($"Workspace state is missing active pane state for slot '{slot}'.");
            }

            foreach (var pane in stack)
            {
                if (!seenPanes.Add(pane))
                {
                    throw new InvalidOperationException($"Pane '{pane}' is resident in more than one slot.");
                }
            }

            if (activePane.HasValue && !stack.Contains(activePane.Value))
            {
                throw new InvalidOperationException($"Active pane '{activePane.Value}' is not resident in slot '{slot}'.");
            }
        }

        if (mode == WorkspaceMode.Maximized && maximizedPane is null)
        {
            throw new InvalidOperationException("Workspace cannot be in maximized mode without a maximized pane.");
        }

        if (maximizedPane.HasValue && !seenPanes.Contains(maximizedPane.Value))
        {
            throw new InvalidOperationException($"Maximized pane '{maximizedPane.Value}' must be resident.");
        }
    }
}
