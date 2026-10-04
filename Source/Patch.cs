using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using TakeCover;
using UnityEngine;
using Verse;

namespace TakeCoverTweak;

[StaticConstructorOnStartup]
public static class TakeCoverTweakMod
{
    static TakeCoverTweakMod()
    {
        new Harmony("ifchen0.takecovertweak").PatchAll();
    }
}

/// <summary>
/// Shared state and helpers for the tweaked TakeCover controls:
/// plain right-drag issues a take-cover order, Ctrl + right-click falls back to vanilla.
/// The press cell is the rally point, the drag direction faces the enemy and the drag length is the total formation width.
/// </summary>
internal static class Tweak
{
    /// <summary>Drags shorter than this (in cells) are treated as a plain vanilla right-click.</summary>
    public const float MinDragCells = 2f;

    private const int ThreatProjectionCells = 40;

    private const int MinThreatProjectionCells = 4;

    public static readonly AccessTools.FieldRef<TakeCoverController, IntVec3> StartRef = AccessTools.FieldRefAccess<TakeCoverController, IntVec3>("start");

    public static readonly AccessTools.FieldRef<TakeCoverController, IntVec3> EndRef = AccessTools.FieldRefAccess<TakeCoverController, IntVec3>("end");

    private static readonly MethodInfo HandleMultiselectGotoMethod = AccessTools.Method(typeof(Selector), "HandleMultiselectGoto");

    /// <summary>Map position of the right-click that started the current interaction.</summary>
    public static Vector3 ClickPos;

    /// <summary>Lateral slot spacing for the planner call in progress.</summary>
    public static float CurrentSpacing = 1f;

    /// <summary>Half-width (in cells) of the row for the planner call in progress.</summary>
    public static float CurrentHalfWidth = 1f;

    private static bool CtrlHeld => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

    /// <summary>Replaces TakeCoverInput.ActivationKeyHeld: TakeCover runs only while Ctrl is NOT held.</summary>
    public static bool ActivationKeyHeld() => !CtrlHeld;

    /// <summary>Replaces TakeCoverPlanner.LateralSpacingForOffset so slots spread over the dragged width.</summary>
    public static float LateralSpacing(int manualLateralRangeOffset) => CurrentSpacing;

    /// <summary>
    /// Ideal lateral position of a slot: all slots spread evenly over [-half, +half], handed out center-out
    /// (left before right, like TakeCover). With more pawns than columns the step drops below one cell, so every
    /// column gets a similar share instead of the surplus piling up at the edges.
    /// </summary>
    public static float IdealLateral(int slotIndex, int slotCount)
    {
        if (slotCount <= 1)
        {
            return 0f;
        }
        float step = 2f * CurrentHalfWidth / (slotCount - 1);
        if (slotCount % 2 == 1)
        {
            return TakeCoverPlanner.CenterOutLateralOffset(slotIndex) * step;
        }
        float offset = (slotIndex / 2 + 0.5f) * step;
        return slotIndex % 2 == 0 ? -offset : offset;
    }

    public static float DragLength(IntVec3 start, IntVec3 end) => (end - start).LengthHorizontal;

    /// <summary>
    /// Projects the threat cell along the drag direction, pulled back toward the start until it is on the map.
    /// </summary>
    public static IntVec3 ProjectThreatCell(IntVec3 start, IntVec3 end, Map map)
    {
        Vector3 forward = end.ToVector3() - start.ToVector3();
        forward.y = 0f;
        forward.Normalize();
        IntVec3 fallback = start + ToCell(forward * ThreatProjectionCells);
        for (int d = ThreatProjectionCells; d >= MinThreatProjectionCells; d--)
        {
            IntVec3 cell = start + ToCell(forward * d);
            if (cell.InBounds(map))
            {
                return cell;
            }
        }
        return fallback;
    }

    /// <summary>
    /// Reproduces the vanilla right-click handling of Selector.HandleMapClicks for a click at the given position.
    /// </summary>
    public static void VanillaRightClick(Vector3 clickPos)
    {
        Selector selector = Find.Selector;
        List<Pawn> selectedPawns = selector.SelectedPawns.ToList();
        if (selectedPawns.Count == 0)
        {
            return;
        }
        List<FloatMenuOption> options = null;
        FloatMenuContext context = null;
        try
        {
            options = FloatMenuMakerMap.GetOptions(selectedPawns, clickPos, out context);
        }
        catch (System.Exception ex)
        {
            Log.Error("[TakeCoverTweak] Error trying to make float menu: " + ex);
        }
        if (!options.NullOrEmpty())
        {
            FloatMenuOption autoTakeOption = FloatMenuMakerMap.GetAutoTakeOption(options);
            if (context.IsMultiselect && context.ValidSelectedPawns.Count() > 1 && options.Count == 1 && options[0].isGoto)
            {
                MultiselectGoto(selector, context);
            }
            else if (autoTakeOption != null)
            {
                autoTakeOption.Chosen(colonistOrdering: true, null);
            }
            else
            {
                string title = context.IsMultiselect ? null : context.FirstSelectedPawn.LabelCap;
                Find.WindowStack.Add(new FloatMenuMap(options, title, clickPos) { givesColonistOrders = true });
            }
        }
        else if (context != null && context.IsMultiselect)
        {
            MultiselectGoto(selector, context);
        }
    }

    private static void MultiselectGoto(Selector selector, FloatMenuContext context)
    {
        HandleMultiselectGotoMethod.Invoke(selector, new object[] { context });
        // The mouse button is already up, so finish the vanilla group goto right away.
        if (selector.gotoController.Active)
        {
            selector.gotoController.FinalizeInteraction();
        }
    }

    private static IntVec3 ToCell(Vector3 v) => new IntVec3(Mathf.RoundToInt(v.x), 0, Mathf.RoundToInt(v.z));
}

/// <summary>
/// Swaps the Ctrl check that TakeCover injects into Selector.HandleMapClicks, so plain right-click starts TakeCover
/// and Ctrl + right-click falls through to vanilla.
/// </summary>
[HarmonyPatch(typeof(Selector), "HandleMapClicks")]
[HarmonyAfter("rabiosus.TakeCover")]
public static class HandleMapClicksPatch
{
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        return ActivationKeySwap.Swap(instructions, "Selector.HandleMapClicks");
    }
}

/// <summary>
/// Replaces TakeCover's start check. The original yields to vanilla when the pressed cell is not standable
/// (sandbags, trees, furniture) or the right-click menu has non-goto options (items, corpses, downed or hostile
/// pawns), which made plain right-drag randomly fall back to the vanilla formation. Every press without Ctrl is now
/// taken; a release without a drag replays the vanilla right-click, so menus and item actions still work.
/// </summary>
[HarmonyPatch(typeof(TakeCoverInputRouter), nameof(TakeCoverInputRouter.TryStartInteraction))]
public static class TryStartInteractionPatch
{
    private static readonly List<Pawn> DraftedPawns = new List<Pawn>();

    public static bool Prefix(Selector selector, Event ev, Vector3 clickPos, ref bool __result)
    {
        __result = false;
        bool mouseDown = ev.type == EventType.MouseDown || ev.rawType == EventType.MouseDown;
        if (!mouseDown || ev.button != 1 || !Tweak.ActivationKeyHeld())
        {
            return false;
        }
        Map map = Find.CurrentMap;
        IntVec3 clickCell = IntVec3.FromVector3(clickPos);
        if (map == null || !clickCell.InBounds(map))
        {
            return false;
        }
        DraftedPawns.Clear();
        List<object> selected = selector.SelectedObjectsListForReading;
        for (int i = 0; i < selected.Count; i++)
        {
            if (selected[i] is Pawn pawn && pawn.Spawned && pawn.Drafted && pawn.Map == map)
            {
                DraftedPawns.Add(pawn);
            }
        }
        if (DraftedPawns.Count == 0)
        {
            return false;
        }
        IntVec3 rallyCell = CellFinder.StandableCellNear(clickCell, map, 2.9f);
        if (!rallyCell.IsValid)
        {
            DraftedPawns.Clear();
            return false;
        }
        selector.gotoController.Deactivate();
        TakeCoverController.Instance.StartInteraction(rallyCell, DraftedPawns);
        DraftedPawns.Clear();
        Tweak.ClickPos = clickPos;
        ev.Use();
        __result = true;
        return false;
    }
}

internal static class ActivationKeySwap
{
    private static readonly MethodInfo Original = AccessTools.PropertyGetter(typeof(TakeCoverInput), nameof(TakeCoverInput.ActivationKeyHeld));

    private static readonly MethodInfo Replacement = AccessTools.Method(typeof(Tweak), nameof(Tweak.ActivationKeyHeld));

    public static IEnumerable<CodeInstruction> Swap(IEnumerable<CodeInstruction> instructions, string target)
    {
        int count = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(Original))
            {
                instruction.operand = Replacement;
                count++;
            }
            yield return instruction;
        }
        if (count == 0)
        {
            Log.Warning("[TakeCoverTweak] No TakeCover activation key check found in " + target + "; controls unchanged there.");
        }
    }
}

/// <summary>
/// Feeds the planner the tweaked geometry: threat projected along the drag, search width equal to the drag length.
/// Drags shorter than the minimum produce no destinations, so a plain click shows no preview.
/// </summary>
[HarmonyPatch(typeof(TakeCoverPlanner), nameof(TakeCoverPlanner.AssignDestinations))]
public static class AssignDestinationsPatch
{
    private static readonly MethodInfo SpacingOriginal = AccessTools.Method(typeof(TakeCoverPlanner), "LateralSpacingForOffset");

    private static readonly MethodInfo SpacingReplacement = AccessTools.Method(typeof(Tweak), nameof(Tweak.LateralSpacing));

    public static bool Prefix(List<Pawn> pawns, IntVec3 start, ref IntVec3 end, ref int manualLateralRangeOffset, List<IntVec3> dests, ref TakeCoverSearchArea searchArea)
    {
        if (pawns.Count == 0)
        {
            return true;
        }
        float length = Tweak.DragLength(start, end);
        if (length < Tweak.MinDragCells)
        {
            for (int i = 0; i < dests.Count; i++)
            {
                dests[i] = IntVec3.Invalid;
            }
            searchArea = TakeCoverSearchArea.Invalid;
            return false;
        }
        end = Tweak.ProjectThreatCell(start, end, pawns[0].Map);

        // Mirror TakeCover's SearchDimensions.ForPawnCount base width, then offset it to the dragged half-width.
        float t = (Mathf.Clamp(pawns.Count, 1, 12) - 1) / 11f;
        int baseLateralRange = Mathf.RoundToInt(Mathf.Lerp(5f, 13f, t));
        int lateralRange = Mathf.Clamp(Mathf.RoundToInt(length / 2f), 1, 24);
        manualLateralRangeOffset = lateralRange - baseLateralRange;

        // Spread slots over the searched columns; the spacing is only used by the original (debug-logged) path.
        Tweak.CurrentHalfWidth = lateralRange;
        Tweak.CurrentSpacing = pawns.Count > 1 ? 2f * lateralRange / (pawns.Count - 1) : 1f;
        return true;
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        int count = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(SpacingOriginal))
            {
                instruction.operand = SpacingReplacement;
                count++;
            }
            yield return instruction;
        }
        if (count == 0)
        {
            Log.Warning("[TakeCoverTweak] LateralSpacingForOffset call not found in TakeCoverPlanner.AssignDestinations; formation spacing unchanged.");
        }
    }
}

/// <summary>Releasing after a too-short drag behaves like a vanilla right-click at the press position.</summary>
[HarmonyPatch(typeof(TakeCoverController), nameof(TakeCoverController.FinalizeInteraction))]
public static class FinalizeInteractionPatch
{
    public static bool Prefix(TakeCoverController __instance)
    {
        if (!__instance.Active || Tweak.DragLength(Tweak.StartRef(__instance), Tweak.EndRef(__instance)) >= Tweak.MinDragCells)
        {
            return true;
        }
        __instance.Deactivate();
        Tweak.VanillaRightClick(Tweak.ClickPos);
        return false;
    }
}

/// <summary>Width now comes from the drag length, so Shift + scroll no longer adjusts it (and no longer eats scroll events).</summary>
[HarmonyPatch(typeof(TakeCoverController), "HandleRangeScroll")]
public static class HandleRangeScrollPatch
{
    public static bool Prefix() => false;
}

/// <summary>Removes the start-to-end drag line; destination ghosts and circles are still drawn.</summary>
[HarmonyPatch(typeof(TakeCoverController), nameof(TakeCoverController.Draw))]
public static class DrawPatch
{
    private static readonly MethodInfo DrawLineOriginal = AccessTools.Method(typeof(GenDraw), nameof(GenDraw.DrawLineBetween), new[] { typeof(Vector3), typeof(Vector3), typeof(Material), typeof(float) });

    private static readonly MethodInfo DrawLineReplacement = AccessTools.Method(typeof(DrawPatch), nameof(SkipLine));

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        int count = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(DrawLineOriginal))
            {
                instruction.operand = DrawLineReplacement;
                count++;
            }
            yield return instruction;
        }
        if (count == 0)
        {
            Log.Warning("[TakeCoverTweak] Drag line draw call not found in TakeCoverController.Draw; line still shown.");
        }
    }

    public static void SkipLine(Vector3 a, Vector3 b, Material mat, float lineWidth)
    {
    }
}

/// <summary>
/// Keeps the formation a single row: only candidates within <see cref="MaxDepth"/> cells in front of or behind the
/// rally line survive. TakeCover's fallback tiers then fill pawns without cover onto the row.
/// </summary>
[HarmonyPatch(typeof(TakeCoverPlanner), nameof(TakeCoverPlanner.BuildCandidates))]
public static class RowBandPatch
{
    public const int MaxDepth = 2;

    public static void Postfix(List<TakeCoverPlanner.CoverCandidate> __result)
    {
        __result.RemoveAll(candidate => candidate.Depth > MaxDepth || candidate.Depth < -MaxDepth);
    }
}
