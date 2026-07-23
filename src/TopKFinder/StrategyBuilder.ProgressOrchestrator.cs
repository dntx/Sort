using System;
using System.Collections.Generic;

namespace TopKFinder;

partial class StrategyBuilder
{
    private sealed class ProgressOrchestrator
    {
        private readonly StrategyBuilder _owner;

        public ProgressOrchestrator(StrategyBuilder owner)
        {
            _owner = owner;
        }

        public void ReportProgress(bool force = false)
        {
            if (_owner._progressCallback is null)
                return;

            _owner._searchedStates = _owner._visitedSearchStates.Count;
            long elapsedMs = _owner._progressStopwatch.ElapsedMilliseconds;
            if (!force && elapsedMs - _owner._lastProgressReportMs < ProgressReportIntervalMs)
                return;

            _owner._lastProgressReportMs = elapsedMs;
            double localProgress = EstimateProgress(elapsedMs);
            double estimatedProgress01 = MapToReportedProgress(localProgress);
            _owner._progressCallback(BuildProgressSnapshot(elapsedMs, estimatedProgress01));
        }

        public void ThrottledReportProgressDuringFeasibleBuild()
        {
            if (_owner._progressCallback is null || _owner._progressScope != ProgressScope.FeasibleInCombinedRun)
                return;

            long elapsedMs = _owner._progressStopwatch.ElapsedMilliseconds;
            if (elapsedMs - _owner._lastProgressReportMs < ProgressReportIntervalMs)
                return;

            // Only report if the visited-state count changed to avoid noisy duplicate updates.
            int currentVisitedCount = _owner._visitedSearchStates.Count;
            if (currentVisitedCount <= _owner._lastReportedVisitedStatesCount)
                return;

            _owner._lastReportedVisitedStatesCount = currentVisitedCount;
            _owner._searchedStates = currentVisitedCount;
            _owner._lastProgressReportMs = elapsedMs;

            double localProgress = EstimateProgress(elapsedMs);
            double estimatedProgress01 = MapToReportedProgress(localProgress);
            _owner._progressCallback(BuildProgressSnapshot(elapsedMs, estimatedProgress01));
        }

        public double MapToReportedProgress(double localProgress01)
        {
            return ProgressEstimationService.MapToReportedProgress(
                _owner._reportCombinedRunProgress,
                _owner._progressScope,
                localProgress01,
                ProgressTuning.CombinedRun.FeasibleSpanPercent,
                ProgressTuning.CombinedRun.DefaultSpanPercent,
                ProgressTuning.CombinedRun.CompactPrimaryBasePercent,
                ProgressTuning.CombinedRun.CompactPrimarySpanPercent,
                ProgressTuning.CombinedRun.CompactFeasibleBasePercent,
                ProgressTuning.CombinedRun.CompactFeasibleSpanPercent);
        }

        public double EstimateProgress(long elapsedMs)
        {
            if (_owner._progressScope == ProgressScope.FeasibleInCombinedRun)
            {
                if (_owner._feasiblePhaseSolved)
                    return 1.0;

                if (_owner._feasiblePhase2StartMs < 0)
                    return 0.0;

                long elapsedInPhase2 = elapsedMs - _owner._feasiblePhase2StartMs;
                return ProgressEstimationService.EstimateAsymptoticProgress(
                    elapsedInPhase2,
                    ProgressTuning.Asymptote.MinimumRemainingMs,
                    ProgressTuning.Asymptote.InitialRemainingMs,
                    ProgressTuning.Asymptote.ElapsedDivisor,
                    ProgressTuning.Asymptote.FeasibleSoftCap);
            }

            if (_owner._progressScope == ProgressScope.CompactFeasibleInCombinedRun)
            {
                if (_owner._phase1bSolved)
                    return 1.0;

                double stageFraction = 0.0;
                if (_owner._feasibleCompactStateEstimate > 0)
                {
                    stageFraction = ProgressEstimationService.EstimateSolvedVsScaleProgress(
                        _owner._compactStatesSolved,
                        _owner._feasibleCompactStateEstimate,
                        ProgressTuning.Asymptote.CompactFeasibleSoftCap);
                }

                if (_owner._proofTightenInitialBudget >= 0 && _owner._proofTightenCurrentBudget >= 0)
                {
                    int totalRange = _owner._proofTightenInitialBudget - _owner._proofTightenLowerBound + 1;
                    if (totalRange > 0)
                    {
                        int completedStages = _owner._proofTightenInitialBudget - _owner._proofTightenCurrentBudget;
                        double rawCombined = (completedStages + stageFraction) / totalRange;
                        rawCombined = Math.Clamp(rawCombined, 0.0, ProgressTuning.Asymptote.CompactFeasibleSoftCap);

                        if (!_owner._proofTightenProgressEmaInitialized)
                        {
                            _owner._proofTightenProgressEmaInitialized = true;
                            _owner._proofTightenProgressEma01 = rawCombined;
                        }
                        else
                        {
                            _owner._proofTightenProgressEma01 +=
                                ProgressTuning.Ema.ProofTightenCombinedProgressAlpha *
                                (rawCombined - _owner._proofTightenProgressEma01);
                        }

                        return _owner._proofTightenProgressEma01;
                    }
                }

                return stageFraction;
            }

            if (_owner._searchedStates <= 0)
                return 0.0;

            if (_owner._lastProgressSampleElapsedMs >= 0)
            {
                int deltaSearched = Math.Max(0, _owner._searchedStates - _owner._lastProgressSampleSearched);
                long deltaElapsedMs = Math.Max(0, elapsedMs - _owner._lastProgressSampleElapsedMs);
                if (deltaElapsedMs > 0 && deltaSearched > 0)
                {
                    double observedSearchRate = deltaSearched / (double)deltaElapsedMs;
                    if (!_owner._searchRateEstimateInitialized)
                    {
                        _owner._searchRateEstimateInitialized = true;
                        _owner._searchRateStatesPerMs = observedSearchRate;
                    }
                    else
                    {
                        _owner._searchRateStatesPerMs +=
                            ProgressTuning.Ema.SearchRateAlpha * (observedSearchRate - _owner._searchRateStatesPerMs);
                    }
                }

                if (_owner._pendingAtCostSample < 0)
                    _owner._pendingAtCostSample = _owner._pendingStates;

                _owner._searchedSinceCostSample += deltaSearched;

                int consumedPending = _owner._pendingAtCostSample - _owner._pendingStates;
                if (consumedPending > 0 && _owner._searchedSinceCostSample > 0)
                {
                    double observedCostPerPending = _owner._searchedSinceCostSample / (double)consumedPending;
                    if (!_owner._pendingCostEstimateInitialized)
                    {
                        _owner._pendingCostEstimateInitialized = true;
                        _owner._pendingCostStatesPerPending = observedCostPerPending;
                        _owner._pendingCostConservativeStatesPerPending = observedCostPerPending;
                    }
                    else
                    {
                        _owner._pendingCostStatesPerPending +=
                            ProgressTuning.Ema.PendingCostAlpha * (observedCostPerPending - _owner._pendingCostStatesPerPending);

                        double conservativeAlpha =
                            observedCostPerPending >= _owner._pendingCostConservativeStatesPerPending
                                ? ProgressTuning.Ema.PendingCostConservativeRiseAlpha
                                : ProgressTuning.Ema.PendingCostConservativeFallAlpha;
                        _owner._pendingCostConservativeStatesPerPending +=
                            conservativeAlpha * (observedCostPerPending - _owner._pendingCostConservativeStatesPerPending);
                    }

                    _owner._searchedSinceCostSample = 0;
                    _owner._pendingAtCostSample = _owner._pendingStates;
                }
                else if (_owner._pendingStates > _owner._pendingAtCostSample)
                {
                    _owner._pendingAtCostSample = _owner._pendingStates;
                }

                if (_owner._pendingStates > 0 && _owner._searchedSinceCostSample > 0 && _owner._pendingCostEstimateInitialized)
                {
                    double noDrainFloor = _owner._searchedSinceCostSample / (double)_owner._pendingStates;
                    _owner._pendingCostStatesPerPending = Math.Max(_owner._pendingCostStatesPerPending, noDrainFloor);
                    _owner._pendingCostConservativeStatesPerPending = Math.Max(_owner._pendingCostConservativeStatesPerPending, noDrainFloor);
                }
            }

            _owner._lastProgressSampleElapsedMs = elapsedMs;
            _owner._lastProgressSampleSearched = _owner._searchedStates;

            if (!_owner._pendingCostEstimateInitialized)
            {
                _owner._pendingCostEstimateInitialized = true;
                _owner._pendingCostStatesPerPending = _owner._pendingStates > 0
                    ? Math.Max(ProgressTuning.Pending.CostBootstrapFloor, _owner._searchedStates / (double)_owner._pendingStates)
                    : ProgressTuning.Pending.CostBootstrapFloor;
                _owner._pendingCostConservativeStatesPerPending = _owner._pendingCostStatesPerPending;
            }

            bool isDefaultScope =
                _owner._progressScope is ProgressScope.DefaultStandalone or ProgressScope.DefaultInCombinedRun;
            double costPerPending = Math.Max(1.0, _owner._pendingCostStatesPerPending);
            if (isDefaultScope)
                costPerPending = Math.Max(costPerPending, _owner._pendingCostConservativeStatesPerPending);

            int effectivePending = _owner._pendingStates;
            if (_owner._pendingStates == 0)
            {
                if (!_owner._pendingZeroSettling)
                {
                    _owner._pendingZeroSettling = true;
                    _owner._pendingZeroSinceMs = elapsedMs;
                    _owner._pendingZeroSearchedAtStart = _owner._searchedStates;
                }

                bool zeroSettled =
                    elapsedMs - _owner._pendingZeroSinceMs >= ProgressTuning.Pending.ZeroSettlingWindowMs &&
                    _owner._searchedStates == _owner._pendingZeroSearchedAtStart;
                if (zeroSettled)
                {
                    _owner._progressEstimateInitialized = true;
                    _owner._progressEstimateEma01 = 1.0;
                    return 1.0;
                }

                effectivePending = 1;
            }
            else
            {
                _owner._pendingZeroSettling = false;
            }

            if (isDefaultScope && effectivePending <= ProgressTuning.Pending.TailThreshold)
            {
                double inflation = effectivePending switch
                {
                    1 => ProgressTuning.Pending.TailInflationOnePending,
                    2 => ProgressTuning.Pending.TailInflationTwoPending,
                    _ => ProgressTuning.Pending.TailInflationThreePending,
                };
                costPerPending *= inflation;
            }

            double estimatedRemainingSearchStates = effectivePending * costPerPending;
            double estimatedTotal = _owner._searchedStates + estimatedRemainingSearchStates;
            if (estimatedTotal <= 0)
                return 0.0;

            double rawProgress = Math.Clamp(_owner._searchedStates / estimatedTotal, 0.0, 1.0);
            if (effectivePending > 0)
                rawProgress = Math.Min(rawProgress, ProgressTuning.Asymptote.RawProgressSoftCapWithPending);

            if (!_owner._progressEstimateInitialized)
            {
                _owner._progressEstimateInitialized = true;
                _owner._progressEstimateEma01 = rawProgress;
            }
            else
            {
                double alpha = rawProgress >= _owner._progressEstimateEma01
                    ? ProgressTuning.Ema.ProgressRiseAlpha
                    : ProgressTuning.Ema.ProgressFallAlpha;
                _owner._progressEstimateEma01 += alpha * (rawProgress - _owner._progressEstimateEma01);
            }

            return Math.Clamp(_owner._progressEstimateEma01, 0.0, 1.0);
        }

        public void RecordRootIncumbent(int bestWorstCaseSteps, IReadOnlyList<int> group)
        {
            _owner._searchedStates = _owner._visitedSearchStates.Count;
            _owner._rootIncumbents.Add(new SearchMilestone(
                bestWorstCaseSteps,
                $"sort({StrategyBuilder.FormatItemSet(group)})",
                _owner._progressStopwatch.ElapsedMilliseconds,
                _owner._searchedStates,
                _owner._pendingStates,
                _owner._peakPendingStates,
                _owner._stateIds.Count,
                _owner._lowerBoundPrunes));
            ReportProgress(force: true);
        }

        public void RecordRootProvenLowerBound(int provenLowerBound)
        {
            if (provenLowerBound <= _owner._rootProvenLowerBound)
                return;

            _owner._rootProvenLowerBound = provenLowerBound;
            ReportProgress(force: true);
        }

        private SearchProgressSnapshot BuildProgressSnapshot(long elapsedMs, double estimatedProgress01)
        {
            return new SearchProgressSnapshot(
                elapsedMs,
                _owner._searchedStates,
                _owner._pendingStates,
                _owner._peakPendingStates,
                _owner._stateIds.Count,
                _owner._rootIncumbents.Count == 0 ? null : _owner._rootIncumbents[^1],
                _owner._rootIncumbents.Count,
                _owner._lowerBoundPrunes,
                _owner._duplicateOutcomeSkips,
                _owner._mergedOutcomeCollisions,
                _owner._exactCacheHits,
                _owner._lowerBoundCacheHits,
                _owner._feasibleTopSetCacheHits,
                _owner._bestGroupPatternCacheHits,
                _owner._outcomesConstructed,
                _owner._candidateGroupsEnumerated,
                _owner._lowerBoundStepsCache.Count,
                _owner._feasibleTopSetCache.Count,
                _owner._compactStatesSolved,
                _owner._compactGroupsEnumerated,
                _owner._compactStepOptimalGroups,
                _owner._feasibleCompactStateEstimate,
                estimatedProgress01,
                _owner._rootProvenLowerBound);
        }
    }
}
