using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using TakeCover;
using Verse;

namespace TakeCoverTweak;

/// <summary>
/// Faster drop-in for TakeCoverPlanner.AddSelectedDestinations (greedy pick + optimization passes).
/// The original recomputes every candidate's friendly-fire penalty against every selected cell for each slot and
/// each optimization step (about candidates x pawns^2 pair checks, 400+ ms with 40 pawns). Here each candidate keeps
/// a running penalty total that is updated only for the pair that changed, and the per-slot sort becomes a min scan
/// with the same comparer. Slot ideal positions come from Tweak.IdealLateral instead of the fixed center-out spacing. Debug-logged runs still use the original so the trace stays complete.
/// </summary>
[HarmonyPatch(typeof(TakeCoverPlanner), nameof(TakeCoverPlanner.AddSelectedDestinations))]
public static class FastSelectPatch
{
    public static bool Prefix(List<TakeCoverPlanner.SelectedDestination> selected, List<TakeCoverPlanner.CoverCandidate> sourceCandidates, int desiredCount, float lateralSpacing, IntVec3 targetCell, string sourceName, StringBuilder debugLog)
    {
        if (debugLog != null)
        {
            return true;
        }
        if (selected.Count >= desiredCount)
        {
            return false;
        }
        Run(selected, sourceCandidates, desiredCount, targetCell, sourceName);
        return false;
    }

    private static void Run(List<TakeCoverPlanner.SelectedDestination> selected, List<TakeCoverPlanner.CoverCandidate> sourceCandidates, int desiredCount, IntVec3 targetCell, string sourceName)
    {
        TakeCoverPlanner.ICombatModel model = TakeCoverPlanner.CombatModel;
        int candidateCount = sourceCandidates.Count;
        int firstMutableIndex = selected.Count;

        Dictionary<IntVec3, int> indexOfCell = new Dictionary<IntVec3, int>(candidateCount);
        IntVec3[] cells = new IntVec3[candidateCount];
        for (int c = 0; c < candidateCount; c++)
        {
            cells[c] = sourceCandidates[c].Cell;
            indexOfCell[cells[c]] = c;
        }

        // penalty[c] = friendly-fire penalty of candidate c against every currently selected cell.
        float[] penalty = new float[candidateCount];
        bool[] taken = new bool[candidateCount];
        for (int i = 0; i < selected.Count; i++)
        {
            IntVec3 cell = selected[i].Cell;
            if (indexOfCell.TryGetValue(cell, out int index))
            {
                taken[index] = true;
            }
            AddPairs(model, cells, penalty, cell, targetCell, 1f);
        }

        while (selected.Count < desiredCount)
        {
            int slotIndex = selected.Count;
            float idealLateral = Tweak.IdealLateral(slotIndex, desiredCount);
            int best = -1;
            TakeCoverPlanner.SelectedDestination bestDestination = default;
            for (int c = 0; c < candidateCount; c++)
            {
                if (taken[c])
                {
                    continue;
                }
                TakeCoverPlanner.SelectedDestination destination = Make(sourceCandidates[c], penalty[c], sourceName, slotIndex, idealLateral);
                if (best < 0 || TakeCoverPlanner.CompareFormationCandidates(destination, bestDestination) < 0)
                {
                    best = c;
                    bestDestination = destination;
                }
            }
            if (best < 0)
            {
                // Same as the original: running out of candidates returns before the optimization passes.
                return;
            }
            selected.Add(bestDestination);
            taken[best] = true;
            AddPairs(model, cells, penalty, bestDestination.Cell, targetCell, 1f);
        }

        Optimize(model, selected, sourceCandidates, cells, indexOfCell, penalty, taken, firstMutableIndex, targetCell, sourceName);
    }

    private static void Optimize(TakeCoverPlanner.ICombatModel model, List<TakeCoverPlanner.SelectedDestination> selected, List<TakeCoverPlanner.CoverCandidate> sourceCandidates, IntVec3[] cells, Dictionary<IntVec3, int> indexOfCell, float[] penalty, bool[] taken, int firstMutableIndex, IntVec3 targetCell, string sourceName)
    {
        int candidateCount = cells.Length;
        for (int pass = 0; pass < TakeCoverPlanner.FormationOptimizationPasses; pass++)
        {
            bool changed = false;
            for (int j = firstMutableIndex; j < selected.Count; j++)
            {
                TakeCoverPlanner.SelectedDestination current = Refresh(model, selected, j, penalty, indexOfCell, targetCell);
                IntVec3 currentCell = current.Cell;
                TakeCoverPlanner.SelectedDestination best = current;
                int bestIndex = -1;
                for (int c = 0; c < candidateCount; c++)
                {
                    if (taken[c] && cells[c] != currentCell)
                    {
                        continue;
                    }
                    float friendlyFirePenalty = penalty[c] - PairPenalty(model, cells[c], currentCell, targetCell);
                    TakeCoverPlanner.SelectedDestination destination = Make(sourceCandidates[c], friendlyFirePenalty, sourceName, current.SlotIndex, current.IdealLateral);
                    if (TakeCoverPlanner.CompareFormationCandidates(destination, best) < 0)
                    {
                        best = destination;
                        bestIndex = c;
                    }
                }
                selected[j] = best;
                if (best.Cell != currentCell)
                {
                    changed = true;
                    if (indexOfCell.TryGetValue(currentCell, out int currentIndex))
                    {
                        taken[currentIndex] = false;
                    }
                    taken[bestIndex] = true;
                    AddPairs(model, cells, penalty, currentCell, targetCell, -1f);
                    AddPairs(model, cells, penalty, best.Cell, targetCell, 1f);
                }
            }
            if (!changed)
            {
                break;
            }
        }
        for (int l = firstMutableIndex; l < selected.Count; l++)
        {
            selected[l] = Refresh(model, selected, l, penalty, indexOfCell, targetCell);
        }
    }

    /// <summary>Re-scores selected[index] against all other selected cells, like RefreshSelectedDestinationScore.</summary>
    private static TakeCoverPlanner.SelectedDestination Refresh(TakeCoverPlanner.ICombatModel model, List<TakeCoverPlanner.SelectedDestination> selected, int index, float[] penalty, Dictionary<IntVec3, int> indexOfCell, IntVec3 targetCell)
    {
        TakeCoverPlanner.SelectedDestination destination = selected[index];
        float friendlyFirePenalty;
        if (indexOfCell.TryGetValue(destination.Cell, out int c))
        {
            // The pair of a cell with itself is always zero, so the running total already excludes it.
            friendlyFirePenalty = penalty[c];
        }
        else
        {
            friendlyFirePenalty = TakeCoverPlanner.FriendlyFirePenaltyFor(destination.Cell, selected, index, targetCell);
        }
        float score = TakeCoverPlanner.ScoreFormationCell(destination.CoverChance, destination.SurroundingCover, destination.Lateral, destination.Depth, destination.IdealLateral, friendlyFirePenalty);
        return destination.WithScore(score, friendlyFirePenalty);
    }

    private static TakeCoverPlanner.SelectedDestination Make(TakeCoverPlanner.CoverCandidate candidate, float friendlyFirePenalty, string sourceName, int slotIndex, float idealLateral)
    {
        float score = TakeCoverPlanner.ScoreFormationCell(candidate.CoverChance, candidate.SurroundingCover, candidate.Lateral, candidate.Depth, idealLateral, friendlyFirePenalty);
        return new TakeCoverPlanner.SelectedDestination(candidate, score, sourceName, slotIndex, idealLateral, friendlyFirePenalty);
    }

    private static float PairPenalty(TakeCoverPlanner.ICombatModel model, IntVec3 a, IntVec3 b, IntVec3 targetCell)
    {
        return model.FriendlyFirePairPenalty(a, b, targetCell) + model.FriendlyFirePairPenalty(b, a, targetCell);
    }

    private static void AddPairs(TakeCoverPlanner.ICombatModel model, IntVec3[] cells, float[] penalty, IntVec3 selectedCell, IntVec3 targetCell, float sign)
    {
        for (int c = 0; c < cells.Length; c++)
        {
            penalty[c] += sign * PairPenalty(model, cells[c], selectedCell, targetCell);
        }
    }
}
