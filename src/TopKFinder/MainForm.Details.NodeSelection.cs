using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TopKFinder;

partial class MainForm
{
    private static string BuildBranchDetails(StrategyBranch branch)
    {
        string details = branch.OrderText;
        string effectDetails = DisplayEngine.FormatEffectDetails(branch.Effect);
        if (!string.IsNullOrEmpty(effectDetails))
            details += "\n" + effectDetails;

        if (branch.EquivalentOrders is not null)
        {
            details += "\n" + DisplayEngine.FormatEquivalentDetails(branch.EquivalentOrders);
        }

        return details;
    }

    private static string BuildStateDetails(StrategyNode node)
    {
        string stepAndGroup =
            $"Step: {node.Step}\n" +
            $"Comparison group: ({DisplayEngine.FormatSet(node.Group)})";
        string details = node.FinalChoice is not null
            ? stepAndGroup
            : $"State S{node.StateId}\n" + stepAndGroup;

        if (node.FinalChoice is not null)
        {
            int k = node.FinalChoice.FixedTopSet.Count + node.FinalChoice.RemainingSlots;
            details += "\n" +
                "Compressed final choice: yes\n" +
                BuildCompressedFinalChoiceDetails(node.FinalChoice, k);
        }

        return details;
    }

    private static string BuildCompressedFinalChoiceText(FinalChoiceSummary summary, int k)
    {
        return $"fixed ({DisplayEngine.FormatSet(summary.FixedTopSet)}); choose {summary.RemainingSlots} of ({DisplayEngine.FormatSet(summary.CandidatePool)}) into top {k}";
    }

    private static string BuildCompressedFinalChoiceDetails(FinalChoiceSummary summary, int k)
    {
        return
            $"Fixed top-{k} members: ({DisplayEngine.FormatSet(summary.FixedTopSet)})\n" +
            $"Choose {summary.RemainingSlots} of ({DisplayEngine.FormatSet(summary.CandidatePool)}) to complete top {k}";
    }

    private void ShowNodeDetails(TreeNode? node)
    {
        _detailsTextBox.Clear();
        if (node is null)
            return;

        int requestVersion = Interlocked.Increment(ref _detailsRequestVersion);
        switch (node.Tag)
        {
            case string text:
                _detailsTextBox.Text = text;
                return;
            case LazyNodeDetails lazy when lazy.TryGetCached(out string cached):
                _detailsTextBox.Text = cached;
                return;
            case LazyNodeDetails lazy:
                _detailsTextBox.Text = "Loading details...";
                _ = Task.Run(lazy.GetOrCreate).ContinueWith(t =>
                {
                    if (!IsHandleCreated || IsDisposed)
                        return;

                    if (t.IsFaulted)
                    {
                        string error = t.Exception?.GetBaseException().Message ?? "unknown error";
                        Debug.WriteLine($"Details load failed: {error}");
                        BeginInvoke(new Action(() =>
                        {
                            if (requestVersion != Volatile.Read(ref _detailsRequestVersion))
                                return;
                            if (!ReferenceEquals(_treeView.SelectedNode, node))
                                return;

                            _detailsTextBox.Text = $"Failed to load details: {error}";
                        }));
                        return;
                    }

                    BeginInvoke(new Action(() =>
                    {
                        if (requestVersion != Volatile.Read(ref _detailsRequestVersion))
                            return;
                        if (!ReferenceEquals(_treeView.SelectedNode, node))
                            return;

                        _detailsTextBox.Text = t.Result;
                    }));
                }, TaskScheduler.Default);
                return;
            default:
                return;
        }
    }
}
