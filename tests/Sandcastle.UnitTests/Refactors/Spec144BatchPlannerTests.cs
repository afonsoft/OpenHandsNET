using Sandcastle.Application.Refactors;
using Sandcastle.Infrastructure.Refactors;

using Shouldly;

using Xunit;

namespace Sandcastle.UnitTests.Refactors;

/// <summary>
/// SPEC-144 RF-001: deterministic batch planner — Kahn topological
/// ordering (C# using/proj-ref edges first, directory fallback), the
/// file-overlap guard (independent mode → forced stacked), the stacked
/// predecessor chain, the batch state-machine transition table and the
/// concurrency/verify caps.
/// </summary>
public sealed class Spec144BatchPlannerTests
{
    private static RefactorPlanOutput Plan(
        IReadOnlyList<string> files,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? edges = null,
        string mode = RefactorModes.Independent,
        int batchSize = 1,
        int startN = 1) =>
        RefactorBatchPlanner.Plan(
            "org/repo", files,
            edges ?? new Dictionary<string, IReadOnlyCollection<string>>(),
            mode, batchSize,
            maxBatchFiles: 200, maxBatches: 64,
            verifyCommand: "dotnet test", startN);

    [Fact]
    public void Topological_Order_Respects_File_Dependencies()
    {
        // b depends on a (import edge) — a must land in an earlier batch.
        var plan = Plan(
            ["src/A.cs", "src/B.cs"],
            new Dictionary<string, IReadOnlyCollection<string>>
            { ["src/B.cs"] = ["src/A.cs"] },
            batchSize: 1);

        plan.Batches.Count.ShouldBe(2);
        plan.Batches[0].Files.ShouldBe(["src/A.cs"]);
        plan.Batches[0].DependsOn.ShouldBeEmpty();
        plan.Batches[1].Files.ShouldBe(["src/B.cs"]);
        // depends_on carries 1-based batch n values (not local indexes).
        plan.Batches[1].DependsOn.ShouldBe([1]);
    }

    [Fact]
    public void Transitive_Dependencies_Order_Three_Levels()
    {
        // c → b → a chain ⇒ three ordered batches.
        var plan = Plan(
            ["src/A.cs", "src/B.cs", "src/C.cs"],
            new Dictionary<string, IReadOnlyCollection<string>>
            {
                ["src/B.cs"] = ["src/A.cs"],
                ["src/C.cs"] = ["src/B.cs"],
            },
            batchSize: 1);

        plan.Batches.Select(b => b.Files[0]).ShouldBe(
            ["src/A.cs", "src/B.cs", "src/C.cs"]);
        plan.Batches[1].DependsOn.ShouldBe([1]);
        plan.Batches[2].DependsOn.ShouldBe([2]);
    }

    [Fact]
    public void Same_Level_Files_Split_By_Batch_Size()
    {
        // No edges — all level 0 — packed by batchSize (same-level
        // files are independent → parallelizable batches).
        var plan = Plan(
            ["src/A.cs", "src/B.cs", "src/C.cs", "src/D.cs"],
            batchSize: 2);

        plan.Batches.Count.ShouldBe(2);
        plan.Batches[0].Files.ShouldBe(["src/A.cs", "src/B.cs"]);
        plan.Batches[1].Files.ShouldBe(["src/C.cs", "src/D.cs"]);
        plan.Batches[1].DependsOn.ShouldBeEmpty();
    }

    [Fact]
    public void Cycles_Break_Deterministically()
    {
        // a ↔ b cycle — planner must still terminate and partition.
        var plan = Plan(
            ["src/A.cs", "src/B.cs"],
            new Dictionary<string, IReadOnlyCollection<string>>
            {
                ["src/A.cs"] = ["src/B.cs"],
                ["src/B.cs"] = ["src/A.cs"],
            },
            batchSize: 1);

        plan.Batches.Count.ShouldBe(2);
        plan.Batches.SelectMany(b => b.Files)
            .OrderBy(f => f).ShouldBe(["src/A.cs", "src/B.cs"]);
    }

    [Fact]
    public void Every_File_Appears_In_Exactly_One_Batch()
    {
        var files = Enumerable.Range(1, 25)
            .Select(i => $"src/F{i}.cs").ToList();
        var plan = Plan(files, batchSize: 7);

        var seen = plan.Batches.SelectMany(b => b.Files).ToList();
        seen.OrderBy(f => f, StringComparer.Ordinal)
            .ShouldBe(files.OrderBy(f => f, StringComparer.Ordinal));
        RefactorBatchPlanner.FileOverlap(plan.Batches).ShouldBeEmpty();
    }

    [Fact]
    public void FileOverlap_Detects_Duplicate_Files()
    {
        var overlap = RefactorBatchPlanner.FileOverlap(
            [["src/A.cs", "src/B.cs"], ["src/B.cs", "src/C.cs"]]);
        overlap.ShouldBe(["src/B.cs"]);
    }

    [Fact]
    public void Stacked_Mode_Chains_Each_Batch_On_Predecessor()
    {
        var plan = Plan(
            ["src/A.cs", "src/B.cs", "src/C.cs"],
            mode: RefactorModes.Stacked, batchSize: 1);

        plan.Mode.ShouldBe(RefactorModes.Stacked);
        plan.Batches[0].DependsOn.ShouldBeEmpty();
        plan.Batches[1].DependsOn.ShouldContain(1);
        plan.Batches[2].DependsOn.ShouldContain(2);
    }

    [Fact]
    public void StartN_Offsets_DependsOn_For_MultiRepo()
    {
        // Second repo in a plan: batches numbered from startN — dep
        // pointers must shift by the same offset.
        var plan = Plan(
            ["src/A.cs", "src/B.cs"],
            new Dictionary<string, IReadOnlyCollection<string>>
            { ["src/B.cs"] = ["src/A.cs"] },
            batchSize: 1, startN: 4);

        plan.Batches[1].DependsOn.ShouldBe([4]);
    }

    [Fact]
    public void Edges_To_Unknown_Files_Are_Ignored()
    {
        var plan = Plan(
            ["src/A.cs", "src/B.cs"],
            new Dictionary<string, IReadOnlyCollection<string>>
            { ["src/B.cs"] = ["external/NotInRepo.cs"] },
            batchSize: 1);

        plan.Batches[1].DependsOn.ShouldBeEmpty();
    }

    [Fact]
    public void Empty_Input_Produces_Empty_Plan()
    {
        var plan = Plan([], batchSize: 5);
        plan.Batches.ShouldBeEmpty();
    }

    [Fact]
    public void State_Machine_Allows_Documented_Transitions()
    {
        RefactorBatchPlanner.CanTransition(
            RefactorBatchStates.Pending, RefactorBatchStates.Ready)
            .ShouldBeTrue();
        RefactorBatchPlanner.CanTransition(
            RefactorBatchStates.Pending, RefactorBatchStates.Blocked)
            .ShouldBeTrue();
        RefactorBatchPlanner.CanTransition(
            RefactorBatchStates.Ready, RefactorBatchStates.Running)
            .ShouldBeTrue();
        RefactorBatchPlanner.CanTransition(
            RefactorBatchStates.Running, RefactorBatchStates.Verifying)
            .ShouldBeTrue();
        RefactorBatchPlanner.CanTransition(
            RefactorBatchStates.Running, RefactorBatchStates.Failed)
            .ShouldBeTrue();
        RefactorBatchPlanner.CanTransition(
            RefactorBatchStates.Verifying, RefactorBatchStates.PrOpen)
            .ShouldBeTrue();
        RefactorBatchPlanner.CanTransition(
            RefactorBatchStates.Verifying, RefactorBatchStates.Failed)
            .ShouldBeTrue();
        RefactorBatchPlanner.CanTransition(
            RefactorBatchStates.PrOpen, RefactorBatchStates.Merged)
            .ShouldBeTrue();
        RefactorBatchPlanner.CanTransition(
            RefactorBatchStates.PrOpen, RefactorBatchStates.Failed)
            .ShouldBeTrue();
        RefactorBatchPlanner.CanTransition(
            RefactorBatchStates.Blocked, RefactorBatchStates.Pending)
            .ShouldBeTrue();
        RefactorBatchPlanner.CanTransition(
            RefactorBatchStates.Failed, RefactorBatchStates.Ready)
            .ShouldBeTrue();
    }

    [Fact]
    public void State_Machine_Rejects_Invalid_Transitions()
    {
        // No auto-merge, no skip-ahead, merged is terminal.
        RefactorBatchPlanner.CanTransition(
            RefactorBatchStates.Pending, RefactorBatchStates.Running)
            .ShouldBeFalse();
        RefactorBatchPlanner.CanTransition(
            RefactorBatchStates.Running, RefactorBatchStates.PrOpen)
            .ShouldBeFalse();
        RefactorBatchPlanner.CanTransition(
            RefactorBatchStates.Verifying, RefactorBatchStates.Merged)
            .ShouldBeFalse();
        RefactorBatchPlanner.CanTransition(
            RefactorBatchStates.Merged, RefactorBatchStates.Ready)
            .ShouldBeFalse();
        RefactorBatchPlanner.CanTransition(
            RefactorBatchStates.Merged, RefactorBatchStates.Pending)
            .ShouldBeFalse();
        RefactorBatchPlanner.CanTransition(
            RefactorBatchStates.Failed, RefactorBatchStates.Running)
            .ShouldBeFalse();
        RefactorBatchPlanner.CanTransition(
            RefactorBatchStates.Blocked, RefactorBatchStates.Running)
            .ShouldBeFalse();
    }

    [Fact]
    public void Options_Clamp_Concurrency_To_Hard_Cap()
    {
        var o = new RefactorOptions();
        o.DefaultMaxConcurrency.ShouldBe(5);     // spec default
        o.MaxConcurrencyCap.ShouldBe(8);         // delegation sibling cap
        o.MaxVerifyFixLoops.ShouldBe(5);         // spec verify cap

        o.EffectiveConcurrency(null).ShouldBe(5);
        o.EffectiveConcurrency(3).ShouldBe(3);
        o.EffectiveConcurrency(99).ShouldBe(8);  // hard cap
        o.EffectiveConcurrency(0).ShouldBe(1);
    }

    [Fact]
    public void Worker_Prompt_Carries_Verify_Loop_Cap_And_Branch()
    {
        var planId = Guid.NewGuid();
        var plan = new RefactorPlanRecord
        {
            Id = planId,
            RepositoriesJson = "[\"org/repo\"]",
            Goal = "migrate to minimal APIs",
            Strategy = RefactorStrategies.ImportGraph,
            Mode = RefactorModes.Independent,
            RequestedMode = RefactorModes.Independent,
            Status = RefactorPlanStatuses.Running,
        };
        var batch = new RefactorBatchRecord
        {
            Id = Guid.NewGuid(),
            PlanId = planId,
            N = 2,
            Repository = "org/repo",
            FilesJson = "[\"src/A.cs\"]",
            DependsOnJson = "[1]",
            Risk = RefactorRisks.Low,
            VerifyCommand = "dotnet test",
            State = RefactorBatchStates.Ready,
        };

        var prompt = BatchExecutor.BuildWorkerPrompt(
            plan, batch, [batch], new RefactorOptions());

        prompt.ShouldContain("refactor/" + planId.ToString("N") + "/2");
        prompt.ShouldContain("dotnet test");
        prompt.ShouldContain("migrate to minimal APIs");
        // verify→fix loop cap = Refactor:MaxVerifyFixLoops (5 default).
        prompt.ShouldContain("5");
        prompt.ShouldContain("src/A.cs");
    }

    [Fact]
    public void Worker_Prompt_Stacked_Bases_On_Predecessor_Head()
    {
        var planId = Guid.NewGuid();
        var plan = new RefactorPlanRecord
        {
            Id = planId,
            RepositoriesJson = "[\"org/repo\"]",
            Goal = "upgrade deps",
            Strategy = RefactorStrategies.Directory,
            Mode = RefactorModes.Stacked,
            RequestedMode = RefactorModes.Stacked,
            Status = RefactorPlanStatuses.Running,
        };
        var batch = new RefactorBatchRecord
        {
            Id = Guid.NewGuid(),
            PlanId = planId,
            N = 3,
            Repository = "org/repo",
            FilesJson = "[\"src/A.cs\"]",
            DependsOnJson = "[1,2]",
            Risk = RefactorRisks.Medium,
            State = RefactorBatchStates.Ready,
        };

        var prompt = BatchExecutor.BuildWorkerPrompt(
            plan, batch, [batch], new RefactorOptions());
        prompt.ShouldContain(
            "refactor/" + planId.ToString("N") + "/2");
    }
}