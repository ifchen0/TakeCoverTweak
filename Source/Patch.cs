using System;
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
        // Patch class by class: if a TakeCover update breaks one target, only that feature is skipped.
        Harmony harmony = new Harmony("ifchen0.takecovertweak");
        int skipped = 0;
        foreach (Type type in AccessTools.GetTypesFromAssembly(typeof(TakeCoverTweakMod).Assembly))
        {
            if (!type.GetCustomAttributes(typeof(HarmonyPatch), false).Any())
            {
                continue;
            }
            try
            {
                harmony.CreateClassProcessor(type).Patch();
            }
            catch (Exception ex)
            {
                skipped++;
                Log.Warning($"[TakeCoverTweak] Skipped {type.Name}: {ex.GetBaseException().Message}");
            }
        }
        if (skipped > 0)
        {
            Log.Warning($"[TakeCoverTweak] {skipped} patch(es) could not be applied, probably because TakeCover was updated. The rest of the tweak still works.");
        }
    }
}

/// <summary>
/// Shared helpers for the tweaked TakeCover controls:
/// plain right-drag issues a take-cover order, Ctrl + right-click falls back to vanilla.
/// </summary>
internal static class Tweak
{
    /// <summary>Drags shorter than this (in cells) are treated as a plain vanilla right-click.</summary>
    public const float MinDragCells = 2f;

    private static readonly MethodInfo HandleMultiselectGotoMethod = AccessTools.Method(typeof(Selector), "HandleMultiselectGoto");

    /// <summary>Map position of the right-click that started the current interaction.</summary>
    public static Vector3 ClickPos;

    private static bool CtrlHeld => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

    /// <summary>Replaces TakeCoverInput.ActivationKeyHeld: TakeCover runs only while Ctrl is NOT held.</summary>
    public static bool ActivationKeyHeld() => !CtrlHeld;

    public static float DragLength(IntVec3 start, IntVec3 end) => (end - start).LengthHorizontal;

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
        catch (Exception ex)
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
        if (HandleMultiselectGotoMethod == null)
        {
            return;
        }
        HandleMultiselectGotoMethod.Invoke(selector, new object[] { context });
        // The mouse button is already up, so finish the vanilla group goto right away.
        if (selector.gotoController.Active)
        {
            selector.gotoController.FinalizeInteraction();
        }
    }
}

/// <summary>
/// Geometry of the order being planned, set once per planner call by <see cref="AssignDestinationsPatch"/>:
/// the press cell is the rally point, the drag direction faces the enemy and the drag length is the total width.
/// </summary>
internal static class Formation
{
    private const int ThreatProjectionCells = 40;

    private const int MinThreatProjectionCells = 4;

    public static IntVec3 Rally = IntVec3.Invalid;

    /// <summary>Threat guess 40 cells down the drag direction, pulled back onto the map.</summary>
    public static IntVec3 Threat = IntVec3.Invalid;

    /// <summary>Columns searched on each side of the rally point: half the dragged width, 1 to 24.</summary>
    public static int HalfWidth = 1;

    /// <summary>How far cover may pull pawns past the rally line: a quarter of the dragged width, 1 to 6.</summary>
    public static int FrontDepth = 1;

    /// <summary>Rows searched behind the rally line: half the dragged width, up to 12 (never below TakeCover's own).</summary>
    public static int RearDepth;

    /// <summary>Lateral slot spacing, only used by TakeCover's original (debug-logged) selection.</summary>
    public static float Spacing = 1f;

    public static void Set(IntVec3 rally, IntVec3 dragEnd, float length, int pawnCount, Map map)
    {
        Rally = rally;
        Threat = ProjectThreat(rally, dragEnd, map);
        HalfWidth = Mathf.Clamp(Mathf.RoundToInt(length / 2f), 1, 24);
        FrontDepth = Mathf.Clamp(Mathf.RoundToInt(length / 4f), 1, 6);
        RearDepth = Mathf.Clamp(Mathf.RoundToInt(length / 2f), 0, 12);
        Spacing = pawnCount > 1 ? 2f * HalfWidth / (pawnCount - 1) : 1f;
    }

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
        float step = 2f * HalfWidth / (slotCount - 1);
        if (slotCount % 2 == 1)
        {
            return TakeCoverPlanner.CenterOutLateralOffset(slotIndex) * step;
        }
        float offset = (slotIndex / 2 + 0.5f) * step;
        return slotIndex % 2 == 0 ? -offset : offset;
    }

    private static IntVec3 ProjectThreat(IntVec3 start, IntVec3 end, Map map)
    {
        Vector3 forward = end.ToVector3() - start.ToVector3();
        forward.y = 0f;
        forward.Normalize();
        for (int d = ThreatProjectionCells; d >= MinThreatProjectionCells; d--)
        {
            IntVec3 cell = start + TakeCoverPlanner.ToCell(forward * d);
            if (cell.InBounds(map))
            {
                return cell;
            }
        }
        return start + TakeCoverPlanner.ToCell(forward * ThreatProjectionCells);
    }
}

/// <summary>
/// Swaps the Ctrl check that TakeCover injects into Selector.HandleMapClicks, so plain right-click starts TakeCover
/// and Ctrl + right-click falls through to vanilla.
/// </summary>
[HarmonyPatch(typeof(Selector), "HandleMapClicks")]
[HarmonyAfter("rabiosus.TakeCover")]
public static class HandleMapClicksPatch
{
    private static readonly MethodInfo Original = AccessTools.PropertyGetter(typeof(TakeCoverInput), nameof(TakeCoverInput.ActivationKeyHeld));

    private static readonly MethodInfo Replacement = AccessTools.Method(typeof(Tweak), nameof(Tweak.ActivationKeyHeld));

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
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
            Log.Warning("[TakeCoverTweak] No TakeCover activation key check found in Selector.HandleMapClicks; controls unchanged.");
        }
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
        IntVec3 rallyCell = CellFinder.StandableCellNear(clickCell, map, 2.9f);
        if (!rallyCell.IsValid)
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
        selector.gotoController.Deactivate();
        TakeCoverController.Instance.StartInteraction(rallyCell, DraftedPawns);
        DraftedPawns.Clear();
        Tweak.ClickPos = clickPos;
        ev.Use();
        __result = true;
        return false;
    }
}

/// <summary>
/// Feeds the planner the tweaked geometry (see <see cref="Formation"/>): the threat replaces the drag end, and the
/// search area comes from the drag length instead of the pawn count. Drags shorter than the minimum produce no
/// destinations, so a plain click shows no preview.
/// </summary>
[HarmonyPatch(typeof(TakeCoverPlanner), nameof(TakeCoverPlanner.AssignDestinations))]
public static class AssignDestinationsPatch
{
    private static readonly MethodInfo SpacingOriginal = AccessTools.Method(typeof(TakeCoverPlanner), nameof(TakeCoverPlanner.LateralSpacingForOffset));

    private static readonly MethodInfo SpacingReplacement = AccessTools.Method(typeof(AssignDestinationsPatch), nameof(LateralSpacing));

    private static readonly MethodInfo DimensionsOriginal = AccessTools.Method(typeof(TakeCoverPlanner.SearchDimensions), nameof(TakeCoverPlanner.SearchDimensions.ForPawnCount));

    private static readonly MethodInfo DimensionsReplacement = AccessTools.Method(typeof(AssignDestinationsPatch), nameof(SearchDimensionsFor));

    public static bool Prefix(List<Pawn> pawns, IntVec3 start, ref IntVec3 end, List<IntVec3> dests, ref TakeCoverSearchArea searchArea)
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
        Formation.Set(start, end, length, pawns.Count, pawns[0].Map);
        end = Formation.Threat;
        return true;
    }

    /// <summary>
    /// Replaces SearchDimensions.ForPawnCount: width from the drag length, depth from the drag length too but never
    /// below TakeCover's pawn-count depth, so wide formations find more cover and the forward cover range fits.
    /// </summary>
    public static TakeCoverPlanner.SearchDimensions SearchDimensionsFor(int pawnCount, int manualLateralRangeOffset)
    {
        TakeCoverPlanner.SearchDimensions dimensions = TakeCoverPlanner.SearchDimensions.ForPawnCount(pawnCount, 0);
        int rear = Mathf.Max(dimensions.RearRange, Formation.RearDepth);
        int forward = Mathf.Max(dimensions.ForwardRange, Formation.FrontDepth);
        return new TakeCoverPlanner.SearchDimensions(Formation.HalfWidth, rear, forward);
    }

    /// <summary>Replaces TakeCoverPlanner.LateralSpacingForOffset so slots spread over the dragged width.</summary>
    public static float LateralSpacing(int manualLateralRangeOffset) => Formation.Spacing;

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        int spacingCount = 0;
        int dimensionsCount = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(SpacingOriginal))
            {
                instruction.operand = SpacingReplacement;
                spacingCount++;
            }
            else if (instruction.Calls(DimensionsOriginal))
            {
                instruction.operand = DimensionsReplacement;
                dimensionsCount++;
            }
            yield return instruction;
        }
        if (dimensionsCount == 0)
        {
            Log.Warning("[TakeCoverTweak] SearchDimensions.ForPawnCount call not found in TakeCoverPlanner.AssignDestinations; search area unchanged.");
        }
        if (spacingCount == 0)
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
        if (!__instance.Active || Tweak.DragLength(__instance.start, __instance.end) >= Tweak.MinDragCells)
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

/// <summary>
/// Keeps pawns from moving past the rally line: a cell in front of it is allowed only when that cell itself has cover
/// (TakeCover's minimum cover chance) and lies at most <see cref="Formation.FrontDepth"/> cells ahead. This is judged
/// per cell, not per tier: when no cell can see the threat, cover cells only reach the planner through the last
/// no-sight tier, which asks for no minimum cover. Behind the line the whole search depth is allowed; the depth
/// penalty keeps pawns close to the line.
/// </summary>
[HarmonyPatch(typeof(TakeCoverPlanner), nameof(TakeCoverPlanner.BuildCandidates))]
public static class RowBandPatch
{
    public static void Postfix(List<TakeCoverPlanner.CoverCandidate> __result)
    {
        // Positive depth is toward the enemy.
        __result.RemoveAll(candidate => candidate.Depth > 0 && (candidate.Depth > Formation.FrontDepth || candidate.CoverChance < TakeCoverPlanner.MinimumCoverChance));
    }
}

/// <summary>
/// After TakeCover hands out destinations: pawns it could not place get a free cell instead of staying put, then the
/// cover of every destination is recorded for the preview colors.
/// </summary>
[HarmonyPatch(typeof(TakeCoverPlanner), nameof(TakeCoverPlanner.AssignSelectedDestinations))]
public static class AssignSelectedDestinationsPatch
{
    public static void Postfix(List<TakeCoverPlanner.PawnProfile> pawnProfiles, List<TakeCoverPlanner.SelectedDestination> selectedDestinations, List<IntVec3> dests)
    {
        PlaceOverflow(pawnProfiles, selectedDestinations, dests);
        CoverPreview.Record(pawnProfiles, selectedDestinations, dests);
    }

    /// <summary>
    /// TakeCover leaves pawns where they are when it runs out of destinations, which happened a lot with large
    /// selections. Those pawns now get a free cell near the rally point, not past the rally line, picked the same way
    /// as the vanilla group move.
    /// </summary>
    private static void PlaceOverflow(List<TakeCoverPlanner.PawnProfile> pawnProfiles, List<TakeCoverPlanner.SelectedDestination> selectedDestinations, List<IntVec3> dests)
    {
        IntVec3 rally = Formation.Rally;
        if (!rally.IsValid || !Formation.Threat.IsValid)
        {
            return;
        }
        HashSet<IntVec3> selectedCells = new HashSet<IntVec3>();
        for (int i = 0; i < selectedDestinations.Count; i++)
        {
            selectedCells.Add(selectedDestinations[i].Cell);
        }
        HashSet<IntVec3> used = new HashSet<IntVec3>();
        List<TakeCoverPlanner.PawnProfile> overflow = new List<TakeCoverPlanner.PawnProfile>();
        foreach (TakeCoverPlanner.PawnProfile profile in pawnProfiles)
        {
            IntVec3 dest = dests[profile.PawnIndex];
            if (dest.IsValid && selectedCells.Contains(dest))
            {
                used.Add(dest);
            }
            else
            {
                overflow.Add(profile);
            }
        }
        if (overflow.Count == 0)
        {
            return;
        }
        Vector3 forward = Formation.Threat.ToVector3() - rally.ToVector3();
        forward.y = 0f;
        forward.Normalize();
        foreach (TakeCoverPlanner.PawnProfile profile in overflow)
        {
            Pawn pawn = profile.Pawn;
            if (!pawn.Spawned)
            {
                continue;
            }
            IntVec3 cell = RCellFinder.BestOrderedGotoDestNear(rally, pawn, c => !used.Contains(c) && Vector3.Dot(c.ToVector3() - rally.ToVector3(), forward) <= 0.5f);
            if (cell.IsValid)
            {
                dests[profile.PawnIndex] = cell;
                used.Add(cell);
            }
        }
    }
}
