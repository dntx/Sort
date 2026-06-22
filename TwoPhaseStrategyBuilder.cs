using System;
using System.Threading;

sealed class TwoPhaseStrategyBuilder
{
    private readonly int _n;
    private readonly int _m;
    private readonly int _k;
    private readonly CancellationToken _cancellationToken;
    private readonly Action<SearchProgressSnapshot>? _progressCallback;

    public TwoPhaseStrategyBuilder(
        int n,
        int m,
        int k,
        CancellationToken cancellationToken = default,
        Action<SearchProgressSnapshot>? progressCallback = null)
    {
        _n = n;
        _m = m;
        _k = k;
        _cancellationToken = cancellationToken;
        _progressCallback = progressCallback;
    }

    public StrategyPlan Build()
    {
        return new StrategyBuilder(_n, _m, _k, _cancellationToken, _progressCallback).BuildTwoPhase();
    }
}
