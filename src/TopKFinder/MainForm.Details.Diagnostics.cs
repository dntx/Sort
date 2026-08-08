using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace TopKFinder;

partial class MainForm
{
    private void UpdateElapsedLabel()
    {
        long totalMs = (long)((_runStopwatch?.Elapsed.TotalMilliseconds) ?? 0);

        // Four-line panel, all per the current stage except the first line (cumulative total):
        //   <total> s
        //   <stage name>: <stage-own elapsed> s
        //   progress: <current stage %>
        //   eta: <current stage remaining>
        // The stage clock counts from _stageStartMs (reset at every stage boundary), so it always
        // reports the running stage's own time rather than a cumulative figure.
        double totalSeconds = totalMs / 1000.0;
        double stageSeconds = Math.Max(0, totalMs - _stageStartMs) / 1000.0;

        double etaSeconds = EstimateLiveEtaSeconds(totalMs);
        string etaLineValue = etaSeconds >= 0 ? $"{etaSeconds:F3} s" : "-";

        string text =
            $"{totalSeconds:F3} s\n" +
            $"{_currentStageName}: {stageSeconds:F3} s\n" +
            $"progress: {_latestProgress.EstimatedProgress01 * 100.0:F1}%\n" +
            $"eta: {etaLineValue}";
        SetStatText(_progressTextBox, text);
    }

    private void UpdateSearchProgress(SearchProgressSnapshot snapshot)
    {
        _latestProgress = snapshot;
        UpdateStatsPanels();
        string incumbent = snapshot.LatestRootIncumbent is null
            ? "incumbent: -"
            : $"incumbent: <= {snapshot.LatestRootIncumbent.BestWorstCaseSteps}";
        double statusEtaSeconds = EstimateLiveEtaSeconds((long)(GetRunElapsedSeconds() * 1000));
        string etaText = statusEtaSeconds >= 0 ? $"{statusEtaSeconds:F1} s" : "-";
        _statusLabel.Text = $"Running (phase {GetPhaseLabel()})... elapsed: {GetRunElapsedSeconds():F1} s, searched: {snapshot.SearchedStates}, {FormatSqueeze(snapshot)}, {incumbent}, " +
            $"progress: {snapshot.EstimatedProgress01 * 100.0:F1}%, eta: {etaText}.";
        _detailsTextBox.Text = BuildLiveDiagnosticsText(snapshot);
    }

    private void ResetRunTimeline()
    {
        lock (_timelineLock)
            _runTimeline.Clear();
    }

    private void RecordRunTimeline(string label, string? detail = null)
    {
        long elapsedMilliseconds = (long)((_runStopwatch?.Elapsed.TotalMilliseconds) ?? 0);
        lock (_timelineLock)
            _runTimeline.Add(new RunTimelineEvent(elapsedMilliseconds, label, detail));

        string suffix = string.IsNullOrWhiteSpace(detail) ? label : $"{label}: {detail}";
        System.Diagnostics.Debug.WriteLine($"[run {elapsedMilliseconds / 1000.0:F3}s] {suffix}");
    }

    private List<RunTimelineEvent> SnapshotRunTimeline()
    {
        lock (_timelineLock)
            return _runTimeline.ToList();
    }

    private static string FormatTimelineText(IReadOnlyList<RunTimelineEvent> events)
    {
        if (events.Count == 0)
            return "Timeline: (no events captured yet)";

        var lines = new List<string> { "Timeline:" };
        int start = Math.Max(0, events.Count - 12);
        long? previousElapsed = null;
        for (int i = start; i < events.Count; i++)
        {
            RunTimelineEvent entry = events[i];
            long deltaMs = previousElapsed is null ? entry.ElapsedMilliseconds : entry.ElapsedMilliseconds - previousElapsed.Value;
            string detailText = string.IsNullOrWhiteSpace(entry.Detail) ? string.Empty : $" ({entry.Detail})";
            lines.Add($"  +{deltaMs / 1000.0:F3}s @ {entry.ElapsedMilliseconds / 1000.0:F3}s: {entry.Label}{detailText}");
            previousElapsed = entry.ElapsedMilliseconds;
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatStageRecord(StageResult? stage)
    {
        if (stage is null)
            return "-";

        StageResult value = stage.Value;
        string outcome = value.Skipped
            ? "skipped"
            : value.IsTightened
                ? "tightened"
                : value.ProvesOptimal
                    ? "proven-infeasible"
                    : value.Incomplete
                        ? "incomplete"
                        : value.IsCompleted
                            ? "completed"
                            : value.Outcome.ToString().ToLowerInvariant();
        string solution = value.Solution is null
            ? "solution: -"
            : $"solution max={value.Solution.Score.WorstCaseSteps}";
        string plan = value.HasPlan
            ? $"materialized max={value.MaterializedPlan!.MaxStep}"
            : "materialized: -";
        return $"{value.Name} [{outcome}], {solution}, {plan}";
    }

    // Updates the three live stat panels (States / Work / Progress) from the latest snapshot.
    // Each metric lives in exactly one panel so the panels do not duplicate one another.
    private void UpdateStatsPanels()
    {
        SearchProgressSnapshot snapshot = _latestProgress;

        bool edgePhase = Volatile.Read(ref _activePhase) == 2;
        SetStatText(_statesTextBox, BuildStatesPanelText(snapshot, edgePhase));
        SetStatText(_workTextBox, BuildWorkPanelText(snapshot, _currentStageName));
    }

    private static string BuildStatesPanelText(SearchProgressSnapshot snapshot, bool edgePhase)
    {
        // During the edge phase the step counters (searched/pending/output/...) are frozen at 0, so
        // repurpose the States panel to surface the compact solve's live progress instead of a dead
        // all-zero block. The "solved / ~estimate (pct%)" denominator comes from the step phase's
        // distinct-state count (CompactStateEstimate); when unknown (-1) we just show the raw count.
        if (edgePhase)
            return BuildEdgePhaseStatesPanelText(snapshot);

        return
            $"searched: {snapshot.SearchedStates}\n" +
            $"pending: {snapshot.PendingStates} (peak {snapshot.PeakPendingStates})\n" +
            $"output: {snapshot.OutputStates}\n" +
            $"lower-bound: {snapshot.LowerBoundStates}\n" +
            $"top-set: {snapshot.FeasibleTopSetStates}";
    }

    private static string BuildEdgePhaseStatesPanelText(SearchProgressSnapshot snapshot)
    {
        string solvedLine = snapshot.CompactStateEstimate > 0
            ? $"compact solved: {snapshot.CompactStatesSolved} ({ComputeEdgeLocalProgressFraction(snapshot) * 100.0:F1}%)"
            : $"compact solved: {snapshot.CompactStatesSolved}";

        return solvedLine + "\n" +
            $"compact groups: {snapshot.CompactGroupsEnumerated} ({snapshot.CompactStepOptimalGroups} opt)\n" +
            $"(step) output: {snapshot.OutputStates}\n" +
            $"(step) lower-bound: {snapshot.LowerBoundStates}\n" +
            $"(step) top-set: {snapshot.FeasibleTopSetStates}";
    }

    private static string BuildWorkPanelText(SearchProgressSnapshot snapshot, string currentStageName)
    {
        string stageLabel = FormatWorkStageLabel(currentStageName);
        string edgeText = snapshot.CompactStatesSolved > 0
            ? $"[{stageLabel}] {snapshot.CompactStatesSolved} solved, {snapshot.CompactGroupsEnumerated} groups ({snapshot.CompactStepOptimalGroups} opt)"
            : $"[{stageLabel}] -";

        return
            $"outcomes: {snapshot.OutcomesConstructed} (cand groups {snapshot.CandidateGroupsEnumerated})\n" +
            $"duplicate skips: {snapshot.DuplicateOutcomeSkips}\n" +
            $"merged collisions: {snapshot.MergedOutcomeCollisions}\n" +
            $"prunes: {snapshot.LowerBoundPrunes}\n" +
            $"cache: {snapshot.ExactCacheHits}/{snapshot.LowerBoundCacheHits}/{snapshot.FeasibleTopSetCacheHits}/{snapshot.BestGroupPatternCacheHits}\n" +
            edgeText;
    }

    private static string FormatWorkStageLabel(string currentStageName)
    {
        if (string.IsNullOrWhiteSpace(currentStageName))
            return "unknown";

        int length = 0;
        while (length < currentStageName.Length)
        {
            char c = currentStageName[length];
            if ((c >= 'a' && c <= 'z') || c == '-')
            {
                length++;
                continue;
            }

            break;
        }

        return length > 0 ? currentStageName[..length] : "unknown";
    }

    // Local 0..1 fraction of the edge phase's compact solve, mirroring the engine's own self-correcting
    // asymptote (TopKFinder.EstimateProgress): solved / (solved + scale), where the scale is the step
    // phase's state-count estimate. Strictly increasing, always below 1, and never stuck even when the
    // scale badly under/over-shoots the true edge work.
    private static double ComputeEdgeLocalProgressFraction(SearchProgressSnapshot snapshot)
    {
        if (snapshot.CompactStateEstimate <= 0)
            return 0.0;
        double scale = snapshot.CompactStateEstimate;
        double fraction = snapshot.CompactStatesSolved / (snapshot.CompactStatesSolved + scale);
        return Math.Min(fraction, 0.999);
    }

    private static string FormatSearchStatsSummary(SearchProgressSnapshot snapshot, bool includeOutputStates)
    {
        string outputText = includeOutputStates ? $", output={snapshot.OutputStates}" : string.Empty;
        return $"searched={snapshot.SearchedStates}, pending={snapshot.PendingStates}, peak pending={snapshot.PeakPendingStates}{outputText}";
    }

    private static SearchProgressSnapshot CreateInitialProgressSnapshot()
    {
        return new SearchProgressSnapshot(0, 0, 0, 0, 0, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -1, 0.0, 0);
    }

    private static SearchProgressSnapshot CreateSnapshotFromPlan(StrategyPlan plan)
    {
        return new SearchProgressSnapshot(
            (long)plan.Elapsed.TotalMilliseconds,
            plan.SearchStatistics.SearchedStates,
            plan.SearchStatistics.PendingStates,
            plan.SearchStatistics.PeakPendingStates,
            plan.SearchStatistics.OutputStates,
            plan.SearchStatistics.Diagnostics.RootIncumbents.LastOrDefault(),
            plan.SearchStatistics.Diagnostics.RootIncumbents.Count,
            plan.SearchStatistics.Diagnostics.LowerBoundPrunes,
            plan.SearchStatistics.Diagnostics.DuplicateOutcomeSkips,
            plan.SearchStatistics.Diagnostics.MergedOutcomeCollisions,
            plan.SearchStatistics.Diagnostics.ExactCacheHits,
            plan.SearchStatistics.Diagnostics.LowerBoundCacheHits,
            plan.SearchStatistics.Diagnostics.FeasibleTopSetCacheHits,
            plan.SearchStatistics.Diagnostics.BestGroupPatternCacheHits,
            plan.SearchStatistics.OutcomesConstructed,
            plan.SearchStatistics.CandidateGroupsEnumerated,
            plan.SearchStatistics.LowerBoundStates,
            plan.SearchStatistics.FeasibleTopSetStates,
            plan.SearchStatistics.CompactStatesSolved,
            plan.SearchStatistics.CompactGroupsEnumerated,
            plan.SearchStatistics.CompactStepOptimalGroups,
            plan.SearchStatistics.CompactStatesSolved,
            1.0,
            plan.SearchStatistics.RootProvenLowerBound);
    }

    // The label shown in the status bar's "Running (phase ...)" text: the current stage name, with a
    // 1/2 or 2/2 prefix so the user sees overall progress through the two-phase run.
    private string GetPhaseLabel()
    {
        int phase = Volatile.Read(ref _activePhase);
        string prefix = phase >= 2 ? "2/2" : "1/2";
        return $"{prefix} {_currentStageName}";
    }

    private static string BuildDiagnosticsDetails(SearchDiagnostics diagnostics)
    {
        var lines = new List<string>
        {
            "Search diagnostics:",
            $"  root incumbents = {diagnostics.RootIncumbents.Count}",
            $"  lower-bound prunes = {diagnostics.LowerBoundPrunes}",
            $"  duplicate outcome skips = {diagnostics.DuplicateOutcomeSkips}",
            $"  merged outcome collisions = {diagnostics.MergedOutcomeCollisions}",
            $"  cache hits = exact {diagnostics.ExactCacheHits}, lower-bound {diagnostics.LowerBoundCacheHits}, top-set {diagnostics.FeasibleTopSetCacheHits}, best-group-pattern {diagnostics.BestGroupPatternCacheHits}",
        };

        if (diagnostics.RootIncumbents.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Root incumbent timeline:");
            foreach (SearchMilestone milestone in diagnostics.RootIncumbents)
            {
                lines.Add(
                    $"  {milestone.ElapsedMilliseconds / 1000.0:F1}s: max steps <= {milestone.BestWorstCaseSteps} via {milestone.ComparisonGroupText} " +
                    $"(searched {milestone.SearchedStates}, pending {milestone.PendingStates}, peak {milestone.PeakPendingStates}, output {milestone.OutputStates}, prunes {milestone.LowerBoundPrunes})");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private string BuildLiveDiagnosticsText(SearchProgressSnapshot snapshot)
    {
        var lines = new List<string>
        {
            "Live search diagnostics",
            $"elapsed: {snapshot.ElapsedMilliseconds / 1000.0:F1} s",
            $"{FormatSqueeze(snapshot)}",
            "(see the States / Work / Progress panels for live counters)",
            string.Empty,
        };

        if (snapshot.LatestRootIncumbent is null)
        {
            lines.Add("latest incumbent: not found yet");
        }
        else
        {
            SearchMilestone latest = snapshot.LatestRootIncumbent;
            lines.Add($"latest incumbent: max steps <= {latest.BestWorstCaseSteps} via {latest.ComparisonGroupText}");
            lines.Add(
                $"  found at t={latest.ElapsedMilliseconds / 1000.0:F1}s, searched={latest.SearchedStates}, output={latest.OutputStates}, prunes={latest.LowerBoundPrunes}");
        }

        lines.Add(string.Empty);
        lines.Add("Stage records (incumbent = best objective stage; display-* = materialized tree slots):");
        lines.Add($"  greedy-feasible: {FormatStageRecord(_initialGreedyStage)}");
        lines.Add($"  greedy-tighten: {FormatStageRecord(_greedyTightenStage)}");
        lines.Add($"  incumbent: {FormatStageRecord(_incumbentStage)}");
        lines.Add($"  display-step: {FormatStageRecord(_materializedStepStage)}");
        lines.Add($"  display-compact: {FormatStageRecord(_materializedCompactStage)}");

        lines.Add(string.Empty);
        lines.Add(FormatTimelineText(SnapshotRunTimeline()));

        return string.Join(Environment.NewLine, lines);
    }

    private string FormatLiveDiagnosticsSummary(SearchProgressSnapshot snapshot)
    {
        string incumbentText = snapshot.LatestRootIncumbent is null
            ? "incumbent: -"
            : $"incumbent: <= {snapshot.LatestRootIncumbent.BestWorstCaseSteps}";
        return $"{FormatSqueeze(snapshot)}, {incumbentText}, milestones: {snapshot.RootIncumbentCount}, prunes: {snapshot.LowerBoundPrunes}, cache hits: {snapshot.ExactCacheHits}/{snapshot.LowerBoundCacheHits}/{snapshot.FeasibleTopSetCacheHits}/{snapshot.BestGroupPatternCacheHits}";
    }

    // Formats the squeeze on the optimal max-step count: L is the proven lower bound
    // (RootProvenLowerBound), U is the best incumbent worst-case steps. Either side may be unknown
    // ("?") early on; when both are known and equal the optimum is proven exactly. The value is a
    // global result of the phase-1 solve and stays fixed through the compact phase, so it is shown
    // as "step-opt" rather than tagged to a single phase.
    private string FormatSqueeze(SearchProgressSnapshot snapshot)
    {
        int lower = snapshot.RootProvenLowerBound;
        int? incumbent = snapshot.LatestRootIncumbent?.BestWorstCaseSteps;
        // The greedy feasible plan (phase 0) already gives a valid achievable upper bound, so even
        // before the exact search produces an incumbent the U side is known: take the tighter of the
        // two whenever both are present.
        int? feasibleUpper = _feasiblePlan?.MaxStep;
        int? upper = (incumbent, feasibleUpper) switch
        {
            (int a, int b) => Math.Min(a, b),
            (int a, null) => a,
            (null, int b) => b,
            _ => (int?)null,
        };
        if (lower > 0 && upper is int u && lower == u)
            return $"max steps = {lower} (proven)";

        string lowerText = lower > 0 ? lower.ToString() : "?";
        string upperText = upper?.ToString() ?? "?";
        return $"{lowerText} <= max steps <= {upperText}";
    }

    private double GetRunElapsedSeconds()
    {
        return _runStopwatch?.Elapsed.TotalSeconds ?? 0;
    }

    // ETA derived directly from the live elapsed clock and the displayed progress, in seconds, or -1
    // until progress rises above zero. This keeps the displayed elapsed/progress/eta trio
    // mathematically self-consistent and lets ETA count down on every elapsed tick rather than only
    // when a new progress snapshot arrives. Assuming roughly linear progress, total =
    // elapsed / progress, so remaining = elapsed * (1 - progress) / progress.
    private double EstimateLiveEtaSeconds(long liveElapsedMs)
    {
        double progress = _latestProgress.EstimatedProgress01;
        if (progress <= 0.0)
            return -1;
        if (progress >= 1.0)
            return 0.0;
        return liveElapsedMs * (1.0 - progress) / progress / 1000.0;
    }
}
