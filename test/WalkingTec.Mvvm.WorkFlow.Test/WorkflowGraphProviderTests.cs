#nullable enable
// #666: IWorkflowGraphProvider / WorkflowGraphProvider unit tests.
//
// Proves:
//   1. Same DefinitionVersionId served twice returns the SAME cached WorkflowGraph instance
//      (i.e. WorkflowGraphSerializer.Deserialize ran exactly once for that ID) — verified via
//      ReferenceEquals, which can only pass if the second call skipped deserialization and
//      handed back the exact object the first call produced.
//   2. Distinct DefinitionVersionIds do not cross-contaminate: each gets its own independent
//      graph instance/content, and mutating one cached graph's collections does not leak into
//      another version's cached graph.
//   3. No caching (WorkflowEngine/WorkflowTimerExecutor default-instance fallback path) still
//      produces a functionally correct graph — sanity check on the provider used standalone,
//      without any DbContext (GetGraph only reads ID + GraphJson off the version row).

using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Engine;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Test;

[TestClass]
public class WorkflowGraphProviderTests
{
    /// <summary>Build a minimal valid graph JSON string with a distinguishing Key.</summary>
    private static string BuildGraphJson(string key) =>
        WorkflowGraphSerializer.Serialize(new WorkflowGraph
        {
            Key = key,
            Name = key,
            Nodes =
            {
                new NodeDef { NodeKey = "start", Kind = NodeKind.Start },
                new NodeDef { NodeKey = "end", Kind = NodeKind.End },
            },
            Transitions =
            {
                new TransitionDef { From = "start", To = "end" },
            },
        });

    /// <summary>Build a bare in-memory ProcessDefinitionVersion row — no DbContext needed;
    /// GetGraph only reads <c>ID</c> and <c>GraphJson</c>.</summary>
    private static ProcessDefinitionVersion MakeVersion(Guid id, string graphJson) => new()
    {
        ID = id,
        DefinitionId = Guid.NewGuid(),
        VersionNo = 1,
        SchemaVersion = 1,
        GraphJson = graphJson,
        ContentHash = "test-hash-" + Guid.NewGuid().ToString("N"),
        PublishedAt = DateTime.UtcNow,
        PublishedBy = "test",
        IsValid = true,
    };

    [TestMethod]
    public void GetGraph_SameDefinitionVersionId_ServedTwice_DeserializesOnce()
    {
        var provider = new WorkflowGraphProvider();
        var id = Guid.NewGuid();
        var json = BuildGraphJson("GraphA");

        var first = provider.GetGraph(MakeVersion(id, json));
        var second = provider.GetGraph(MakeVersion(id, json));

        // ReferenceEquals only passes if the second call returned the FIRST call's cached
        // object instead of deserializing a fresh WorkflowGraph — direct proof of "once".
        Assert.AreSame(first, second,
            "Second GetGraph() call for the same DefinitionVersionId must return the cached " +
            "instance from the first call, not a freshly deserialized object.");
    }

    [TestMethod]
    public void GetGraph_SameDefinitionVersionId_ThreeCallsAllReturnSameInstance()
    {
        var provider = new WorkflowGraphProvider();
        var id = Guid.NewGuid();
        var json = BuildGraphJson("GraphRepeat");

        var g1 = provider.GetGraph(MakeVersion(id, json));
        var g2 = provider.GetGraph(MakeVersion(id, json));
        var g3 = provider.GetGraph(MakeVersion(id, json));

        Assert.AreSame(g1, g2);
        Assert.AreSame(g2, g3);
    }

    [TestMethod]
    public void GetGraph_DistinctDefinitionVersionIds_DoNotCrossContaminate()
    {
        var provider = new WorkflowGraphProvider();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var json1 = BuildGraphJson("GraphA");
        var json2 = BuildGraphJson("GraphB");

        var g1 = provider.GetGraph(MakeVersion(id1, json1));
        var g2 = provider.GetGraph(MakeVersion(id2, json2));

        // Distinct object instances.
        Assert.AreNotSame(g1, g2);

        // Distinct, correct content — id1 never sees id2's graph content and vice versa.
        Assert.AreEqual("GraphA", g1.Key);
        Assert.AreEqual("GraphB", g2.Key);

        // Re-fetching id1 after id2 was cached must still return id1's own graph, unaffected.
        var g1Again = provider.GetGraph(MakeVersion(id1, json1));
        Assert.AreSame(g1, g1Again);
        Assert.AreEqual("GraphA", g1Again.Key);
    }

    [TestMethod]
    public void GetGraph_MutatingOneCachedGraph_DoesNotLeakIntoAnotherVersionsCache()
    {
        var provider = new WorkflowGraphProvider();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var json1 = BuildGraphJson("GraphA");
        var json2 = BuildGraphJson("GraphB");

        var g1 = provider.GetGraph(MakeVersion(id1, json1));
        var g2 = provider.GetGraph(MakeVersion(id2, json2));

        // Simulate a hypothetical misbehaving caller mutating the graph it was handed
        // (current engine/executor call sites never do this — see IWorkflowGraphProvider's
        // immutability contract doc) — confirms the two cache entries are independent object
        // graphs, not sharing any nested collection.
        g1.Nodes.Add(new NodeDef { NodeKey = "injected", Kind = NodeKind.End });

        var g2Again = provider.GetGraph(MakeVersion(id2, json2));
        Assert.IsFalse(g2Again.Nodes.Any(n => n.NodeKey == "injected"),
            "Mutating the cached graph for id1 must not affect the independently-cached graph for id2.");

        // And id1's own cache entry reflects the mutation on subsequent reads (same instance) —
        // documents the shared-instance contract rather than silently cloning.
        var g1Again = provider.GetGraph(MakeVersion(id1, json1));
        Assert.IsTrue(g1Again.Nodes.Any(n => n.NodeKey == "injected"));
    }

    [TestMethod]
    public void GetGraph_ReturnsCorrectlyDeserializedGraph()
    {
        var provider = new WorkflowGraphProvider();
        var id = Guid.NewGuid();
        var json = BuildGraphJson("SanityGraph");

        var graph = provider.GetGraph(MakeVersion(id, json));

        Assert.AreEqual("SanityGraph", graph.Key);
        Assert.AreEqual(2, graph.Nodes.Count);
        Assert.IsTrue(graph.Nodes.Any(n => n.NodeKey == "start" && n.Kind == NodeKind.Start));
        Assert.IsTrue(graph.Nodes.Any(n => n.NodeKey == "end" && n.Kind == NodeKind.End));
        Assert.AreEqual(1, graph.Transitions.Count);
    }
}
