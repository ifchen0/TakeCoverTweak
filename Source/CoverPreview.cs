using System.Collections.Generic;
using HarmonyLib;
using TakeCover;
using UnityEngine;
using Verse;

namespace TakeCoverTweak;

/// <summary>
/// Colors each destination circle by the cover it gives against the threat:
/// green at 50% or more, yellow at 20% or more, red below that.
/// </summary>
[StaticConstructorOnStartup]
public static class CoverPreview
{
    public const float GoodCover = 0.5f;

    public const float PartialCover = 0.2f;

    private static readonly Color CircleAlpha = new Color(1f, 1f, 1f, 0.5f);

    private static readonly Material GoodMaterial = CircleMaterial(new Color(0.44f, 0.58f, 0.44f));

    private static readonly Material PartialMaterial = CircleMaterial(new Color(0.62f, 0.58f, 0.42f));

    private static readonly Material NoneMaterial = CircleMaterial(new Color(0.62f, 0.42f, 0.4f));

    /// <summary>Threat cell of the planner call in progress, used to rate cells that are not planner candidates.</summary>
    public static IntVec3 ThreatCell = IntVec3.Invalid;

    /// <summary>Cover chance of each destination, indexed like TakeCoverController.dests.</summary>
    public static readonly List<float> DestCover = new List<float>();

    private static Material CircleMaterial(Color color)
    {
        return MaterialPool.MatFrom("UI/Overlays/Circle75Solid", ShaderDatabase.Transparent, color * CircleAlpha);
    }

    public static Material MaterialFor(float coverChance)
    {
        if (coverChance >= GoodCover)
        {
            return GoodMaterial;
        }
        return coverChance >= PartialCover ? PartialMaterial : NoneMaterial;
    }
}

/// <summary>Records the cover chance of every assigned destination, including pawns that keep their position.</summary>
[HarmonyPatch(typeof(TakeCoverPlanner), nameof(TakeCoverPlanner.AssignSelectedDestinations))]
public static class RecordCoverPatch
{
    public static void Postfix(List<TakeCoverPlanner.PawnProfile> pawnProfiles, List<TakeCoverPlanner.SelectedDestination> selectedDestinations, List<IntVec3> dests)
    {
        List<float> covers = CoverPreview.DestCover;
        covers.Clear();
        Dictionary<IntVec3, float> coverOfCell = new Dictionary<IntVec3, float>(selectedDestinations.Count);
        for (int i = 0; i < selectedDestinations.Count; i++)
        {
            coverOfCell[selectedDestinations[i].Cell] = selectedDestinations[i].CoverChance;
        }
        Map map = pawnProfiles.Count > 0 ? pawnProfiles[0].Pawn.Map : null;
        for (int i = 0; i < dests.Count; i++)
        {
            IntVec3 cell = dests[i];
            if (!cell.IsValid || map == null)
            {
                covers.Add(0f);
            }
            else if (coverOfCell.TryGetValue(cell, out float cover))
            {
                covers.Add(cover);
            }
            else
            {
                // Fallback: the pawn stays where it is, which was never rated as a candidate.
                covers.Add(TakeCoverPlanner.CombatModel.EvaluateCover(cell, CoverPreview.ThreatCell, map).CoverChance);
            }
        }
    }
}

/// <summary>
/// Replaces TakeCoverController.Draw: same ghosts and circles, circles colored by cover, no start-to-end drag line.
/// </summary>
[HarmonyPatch(typeof(TakeCoverController), nameof(TakeCoverController.Draw))]
public static class DrawPatch
{
    public static bool Prefix(TakeCoverController __instance)
    {
        if (!__instance.active)
        {
            return false;
        }
        List<Pawn> pawns = __instance.pawns;
        List<IntVec3> dests = __instance.dests;
        List<float> covers = CoverPreview.DestCover;
        float altitude = AltitudeLayer.MetaOverlays.AltitudeFor();
        Vector3 scale = new Vector3(1.7f, 1f, 1.7f);
        float circleAltitude = altitude + 0.03658537f;
        for (int i = 0; i < pawns.Count; i++)
        {
            Pawn pawn = pawns[i];
            IntVec3 cell = dests[i];
            if (cell.IsValid && pawn.Spawned && !cell.Fogged(pawn.Map))
            {
                pawn.Drawer.renderer.RenderPawnAt(cell.ToVector3ShiftedWithAltitude(altitude), Rot4.South);
                Material material = i < covers.Count ? CoverPreview.MaterialFor(covers[i]) : TakeCoverController.GotoCircleMaterial;
                Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(cell.ToVector3ShiftedWithAltitude(circleAltitude), Quaternion.identity, scale), material, 0);
            }
        }
        return false;
    }
}

