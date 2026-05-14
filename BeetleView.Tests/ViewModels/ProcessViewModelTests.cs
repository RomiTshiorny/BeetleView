using System.Collections.Generic;
using BeetleView.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeetleView.Tests.ViewModels;

/// <summary>
/// Tests for <see cref="ProcessViewModel"/>'s pure logic — search filter
/// matching, recursive include cascade, and notification behavior. The
/// <c>Process</c> property is left null (sentinel-style) because
/// <c>GuiLabs.Dotnet.Recorder.Process</c> has no public constructors and can
/// only be obtained by parsing a real .beetle file.
/// </summary>
[TestClass]
public class ProcessViewModelTests
{
    private static ProcessViewModel MakeNode(
        string displayName,
        int pid = 0,
        IReadOnlyList<ProcessViewModel>? children = null)
    {
        return new ProcessViewModel
        {
            DisplayName = displayName,
            Pid = pid,
            AllChildren = children ?? System.Array.Empty<ProcessViewModel>(),
        };
    }

    [TestMethod]
    public void MatchesFilter_EmptyFilter_ReturnsTrue()
    {
        var node = MakeNode("dotnet.exe");
        Assert.IsTrue(node.MatchesFilter(""));
    }

    [TestMethod]
    public void MatchesFilter_MatchingNameIgnoresCase_ReturnsTrue()
    {
        var node = MakeNode("MyApp.exe");
        Assert.IsTrue(node.MatchesFilter("myapp"));
    }

    [TestMethod]
    public void MatchesFilter_NonMatching_ReturnsFalse()
    {
        var node = MakeNode("MyApp.exe");
        Assert.IsFalse(node.MatchesFilter("other"));
    }

    [TestMethod]
    public void MatchesFilter_SentinelWithPidMatch_IgnoresPid()
    {
        // Sentinel nodes have null Process — Pid match should not apply.
        var node = MakeNode("(All processes)", pid: 1234);
        Assert.IsFalse(node.MatchesFilter("1234"));
    }

    [TestMethod]
    public void SetIncludedRecursive_False_CascadesToAllDescendants()
    {
        var leaf = MakeNode("leaf");
        var mid = MakeNode("mid", children: new[] { leaf });
        var root = MakeNode("root", children: new[] { mid });

        root.SetIncludedRecursive(false);

        Assert.IsFalse(root.IsIncluded);
        Assert.IsFalse(mid.IsIncluded);
        Assert.IsFalse(leaf.IsIncluded);
    }

    [TestMethod]
    public void SetIncludedRecursive_True_CascadesToAllDescendants()
    {
        var leaf = MakeNode("leaf");
        var mid = MakeNode("mid", children: new[] { leaf });
        var root = MakeNode("root", children: new[] { mid });

        root.SetIncludedRecursive(false);
        root.SetIncludedRecursive(true);

        Assert.IsTrue(root.IsIncluded);
        Assert.IsTrue(mid.IsIncluded);
        Assert.IsTrue(leaf.IsIncluded);
    }

    [TestMethod]
    public void SetIncludedRecursive_WalksUnfilteredAllChildren_NotFilteredView()
    {
        // The filtered Children collection is mutated by the search filter
        // and may be empty even when AllChildren has entries. The recursive
        // include must walk AllChildren so it doesn't silently miss hidden
        // descendants.
        var hidden = MakeNode("hidden");
        var root = MakeNode("root", children: new[] { hidden });
        Assert.AreEqual(0, root.Children.Count);

        root.SetIncludedRecursive(false);

        Assert.IsFalse(hidden.IsIncluded);
    }

    [TestMethod]
    public void IsIncluded_SetToSameValue_DoesNotRaisePropertyChanged()
    {
        var node = MakeNode("n");
        int events = 0;
        node.PropertyChanged += (_, _) => events++;

        node.IsIncluded = true;

        Assert.AreEqual(0, events);
    }

    [TestMethod]
    public void IsIncluded_SetToNewValue_RaisesPropertyChanged()
    {
        var node = MakeNode("n");
        int events = 0;
        node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProcessViewModel.IsIncluded)) events++;
        };

        node.IsIncluded = false;

        Assert.AreEqual(1, events);
    }
}
