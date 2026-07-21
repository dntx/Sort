using System;
using System.Collections.Generic;
using System.Linq;

namespace TopKFinder;

// Measurement-only probe (opt-in via EnableProjectionPairingProbe). It NEVER changes rendering or
// the plan; it only accumulates per-bucket statistics during display materialization.
//
// For each merged display bucket it compares two honest pairings of the bucket's orderings:
//   * parentOrbitLines  -- the parent-automorphism orbits, i.e. the pairing the current display
//                          uses (each orbit is one symbolic line); and
//   * projectionLines   -- those orbits further merged whenever a projection automorphism (an
//                          automorphism of the parent poset with the two orderings' commonly-doomed
//                          items removed) relates two orbit representatives. This is the proposed
//                          "projection pairing", rendered in the parent-automorphism quotient
//                          (e.g. "A1 > {A2, #7}").
//
// The gap parentOrbitLines - projectionLines is how many symbolic lines the projection pairing would
// save. A component that folds together a NON-singleton orbit (a count>=2 family) is the genuinely
// new case the current opt-in (singleton-only) merge does not reach; those are counted separately and
// sampled, because they are honest only in the quotient form and must be reviewed before adoption.
partial class StrategyBuilder
{
    private const int ProjectionPairingProbeSampleLimit = 40;

    internal bool EnableProjectionPairingProbe { get; set; }

    private int _probeBuckets;
    private int _probeParentOrbitLines;
    private int _probeProjectionLines;
    private int _probeMultiOrbitComponents;   // merged components spanning >=2 parent orbits
    private int _probeMultiFamilyComponents;  // such components that fold a count>=2 family
    private int _probeMaxComponentOrbits;
    private int _probeComponentsGe3;          // merged components spanning >=3 parent orbits
    private int _probeComponentsLeak;         // components that are NOT one global-drop orbit
    private int _parentOrbitAutomorphismChecks;
    private int _parentOrbitColorPrefilterSkips;
    private int _projectionOrbitAutomorphismChecks;
    private int _projectionOrbitColorPrefilterSkips;
    private int _projectionOrbitProjectedStateBuilds;
    private int _projectionOrbitProjectedStateCacheHits;
    private readonly List<string> _probeWinSamples = new();
    private readonly List<string> _probeLeakSamples = new();
    private readonly List<string> _probeGe3Samples = new();

    internal int ProbeBuckets => _probeBuckets;
    internal int ProbeParentOrbitLines => _probeParentOrbitLines;
    internal int ProbeProjectionLines => _probeProjectionLines;
    internal int ProbeLineSavings => _probeParentOrbitLines - _probeProjectionLines;
    internal int ProbeMultiOrbitComponents => _probeMultiOrbitComponents;
    internal int ProbeMultiFamilyComponents => _probeMultiFamilyComponents;
    internal int ProbeMaxComponentOrbits => _probeMaxComponentOrbits;
    internal int ProbeComponentsGe3 => _probeComponentsGe3;
    internal int ProbeComponentsLeak => _probeComponentsLeak;
    internal int ParentOrbitAutomorphismChecks => _parentOrbitAutomorphismChecks;
    internal int ParentOrbitColorPrefilterSkips => _parentOrbitColorPrefilterSkips;
    internal int ProjectionOrbitAutomorphismChecks => _projectionOrbitAutomorphismChecks;
    internal int ProjectionOrbitColorPrefilterSkips => _projectionOrbitColorPrefilterSkips;
    internal int ProjectionOrbitProjectedStateBuilds => _projectionOrbitProjectedStateBuilds;
    internal int ProjectionOrbitProjectedStateCacheHits => _projectionOrbitProjectedStateCacheHits;
    internal IReadOnlyList<string> ProbeWinSamples => _probeWinSamples;
    internal IReadOnlyList<string> ProbeLeakSamples => _probeLeakSamples;
    internal IReadOnlyList<string> ProbeGe3Samples => _probeGe3Samples;

    private void RecordProjectionPairingBucket(ComparisonState state, List<MergedFamilyOutcome> bucket)
    {
        _probeBuckets++;

        if (bucket.Count <= 1)
        {
            _probeParentOrbitLines += 1;
            _probeProjectionLines += 1;
            return;
        }

        List<List<MergedFamilyOutcome>> orbits = PartitionFamiliesIntoOrbits(state, bucket);
        _probeParentOrbitLines += orbits.Count;

        var projectionCache = new Dictionary<ulong, (ComparisonState State, int[] Colors)>();

        List<List<int>> components = ProjectionKernel.BuildProjectionComponents(
            orbits,
            areProjectionEquivalent: (left, right) =>
                TryProjectionAutomorphism(state, left, right, projectionCache));

        _probeProjectionLines += components.Count;

        foreach (List<int> component in components)
        {
            int orbitCount = component.Count;
            if (orbitCount <= 1)
                continue;

            _probeMultiOrbitComponents++;
            _probeMaxComponentOrbits = Math.Max(_probeMaxComponentOrbits, orbitCount);

            // Flatten the component's orbits into the family list the renderer would receive.
            var flattened = new List<MergedFamilyOutcome>();
            foreach (int orbitIndex in component)
                flattened.AddRange(orbits[orbitIndex]);

            int maxFamily = flattened.Max(outcome => outcome.Family.Count);
            if (maxFamily > 1)
                _probeMultiFamilyComponents++;

            bool honest = ComponentIsSingleGlobalDropOrbit(state, flattened, out ulong globalDrop);
            if (!honest)
            {
                _probeComponentsLeak++;
                if (_probeLeakSamples.Count < ProjectionPairingProbeSampleLimit)
                    _probeLeakSamples.Add(DescribeComponent(orbits, component, globalDrop, maxFamily));
            }

            if (orbitCount > 2)
            {
                _probeComponentsGe3++;
                if (_probeGe3Samples.Count < ProjectionPairingProbeSampleLimit)
                    _probeGe3Samples.Add(
                        (honest ? "OK   " : "LEAK ") + DescribeComponent(orbits, component, globalDrop, maxFamily));
            }
        }

        if (components.Count < orbits.Count && _probeWinSamples.Count < ProjectionPairingProbeSampleLimit)
        {
            string reps = string.Join(" | ", orbits.Select(orbit => orbit[0].Family.RepresentativeOrder));
            _probeWinSamples.Add($"({_n},{_m},{_requestedK}) orbits {orbits.Count}->{components.Count}  [{reps}]");
        }
    }

    // Mirrors BuildRelabelingOrbitSummary's honesty contract: a merged line is honest iff, after
    // dropping the GLOBAL common doomed set, every family maps onto the representative via a
    // full-poset automorphism or (failing that) a projected automorphism. If any family cannot be
    // mapped, the rendered line would silently omit its relabeling -- a transitivity leak.
    private bool ComponentIsSingleGlobalDropOrbit(
        ComparisonState state, List<MergedFamilyOutcome> line, out ulong globalDrop)
    {
        ulong common = ~0UL;
        foreach (MergedFamilyOutcome member in line)
            common &= EliminatedMask(state, member);
        globalDrop = common;

        MergedFamilyOutcome representative = line[0];
        IReadOnlyList<int> repOrder = representative.Family.RepresentativeOrderItems;
        List<int> repProjected = ProjectionKernel.RestrictOrderByDropMask(repOrder, common);

        ComparisonState? projected = null;
        for (int i = 1; i < line.Count; i++)
        {
            IReadOnlyList<int> memberOrder = line[i].Family.RepresentativeOrderItems;
            if (state.TryMapOrderByAutomorphism(0, repOrder, memberOrder))
                continue;

            if (common == 0)
                return false;

            projected ??= ProjectionKernel.CloneDeactivated(state, common);
            List<int> memberProjected = ProjectionKernel.RestrictOrderByDropMask(memberOrder, common);
            if (repProjected.Count != memberProjected.Count
                || !projected.TryMapOrderByAutomorphism(0, repProjected, memberProjected))
                return false;
        }

        return true;
    }

    private string DescribeComponent(
        List<List<MergedFamilyOutcome>> orbits, List<int> component, ulong globalDrop, int maxFamily)
    {
        string reps = string.Join(" | ", component.Select(idx => orbits[idx][0].Family.RepresentativeOrder));
        string drop = globalDrop == 0
            ? "{}"
            : "{" + string.Join(", ", ComparisonState.MaskToOrderedList(globalDrop).Select(item => $"#{item + 1}")) + "}";
        return $"({_n},{_m},{_requestedK}) size={component.Count} maxFamily={maxFamily} drop={drop}  [{reps}]";
    }
}
