using Sandcastle.Application.Eval;

using Shouldly;

namespace Sandcastle.UnitTests.Eval;

/// <summary>
/// SPEC-152 RF-001 — <c>eval_task_v1</c> contract: parse válido e
/// inválido (jsonl linha a linha), validação semântica
/// (task_id/repo⊕image/prompt/checker⊕/timeout_s) e vocabulário de
/// verdicts/status.
/// </summary>
public sealed class Spec152EvalContractTests
{
    private const string ValidTask = """
        {"task_id":"t1","image":"img:1","setup_cmds":["echo hi"],
         "prompt":"do it","checker":{"cmd":"bash check.sh"},
         "timeout_s":60,"metadata":{"difficulty":"easy"}}
        """;

    private static string Line(string task) => task.Replace("\n", " ");

    [Fact]
    public void Parse_Valid_Task_Roundtrips()
    {
        var errors = new List<string>();
        var task = EvalTaskParser.ParseLine(Line(ValidTask), 1, errors);

        errors.ShouldBeEmpty();
        task.ShouldNotBeNull();
        task.TaskId.ShouldBe("t1");
        task.Image.ShouldBe("img:1");
        task.SetupCmds.ShouldBe(["echo hi"]);
        task.Prompt.ShouldBe("do it");
        task.Checker.Cmd.ShouldBe("bash check.sh");
        task.TimeoutS.ShouldBe(60);
        task.Metadata!["difficulty"].GetString().ShouldBe("easy");
        // Round-trip preserva a forma canônica (snake_case).
        EvalTaskParser.ToJson(task).ShouldContain("\"task_id\":\"t1\"");
    }

    [Fact]
    public void Parse_Repo_Task_Is_Valid()
    {
        var errors = new List<string>();
        var task = EvalTaskParser.ParseLine(
            "{\"task_id\":\"r1\",\"repo\":\"org/repo\",\"prompt\":\"fix\","
            + "\"checker\":{\"path\":\"/check.sh\"}}", 1, errors);

        errors.ShouldBeEmpty();
        task!.Repo.ShouldBe("org/repo");
        task.Checker.Path.ShouldBe("/check.sh");
    }

    [Theory]
    [InlineData("{\"image\":\"i\",\"prompt\":\"p\",\"checker\":{\"cmd\":\"c\"}}",
        "task_id")]
    [InlineData("{\"task_id\":\"\",\"image\":\"i\",\"prompt\":\"p\",\"checker\":{\"cmd\":\"c\"}}",
        "task_id")]
    [InlineData("{\"task_id\":\"t\",\"prompt\":\"p\",\"checker\":{\"cmd\":\"c\"}}",
        "repo|image")]
    [InlineData("{\"task_id\":\"t\",\"repo\":\"a/b\",\"image\":\"i\",\"prompt\":\"p\",\"checker\":{\"cmd\":\"c\"}}",
        "repo|image")]
    [InlineData("{\"task_id\":\"t\",\"image\":\"i\",\"checker\":{\"cmd\":\"c\"}}",
        "prompt")]
    [InlineData("{\"task_id\":\"t\",\"image\":\"i\",\"prompt\":\"p\"}",
        "checker")]
    [InlineData("{\"task_id\":\"t\",\"image\":\"i\",\"prompt\":\"p\",\"checker\":{\"cmd\":\"c\",\"path\":\"/p\"}}",
        "checker")]
    [InlineData("{\"task_id\":\"t\",\"image\":\"i\",\"prompt\":\"p\",\"checker\":{\"cmd\":\"c\"},\"timeout_s\":0}",
        "timeout_s")]
    public void Parse_Invalid_Task_Collected_With_Reason(
        string line, string expectedReason)
    {
        var errors = new List<string>();
        EvalTaskParser.ParseLine(line, 7, errors).ShouldBeNull();

        errors.ShouldHaveSingleItem();
        errors[0].ShouldStartWith("line 7:");
        errors[0].ShouldContain(expectedReason);
    }

    [Fact]
    public void Parse_Malformed_Json_Collected_Not_Thrown()
    {
        var errors = new List<string>();
        EvalTaskParser.ParseLine("{not json", 3, errors).ShouldBeNull();
        errors.ShouldHaveSingleItem().ShouldContain("line 3:");
    }

    [Fact]
    public void ParseJsonl_Dedup_And_Blank_Lines()
    {
        var content = Line(ValidTask) + "\n\n"
            + Line(ValidTask) + "\n"; // dup task_id
        var errors = new List<string>();
        var tasks = EvalTaskParser.ParseJsonl(content, errors);

        tasks.ShouldHaveSingleItem();
        errors.ShouldHaveSingleItem().ShouldContain("duplicate task_id");
    }

    [Fact]
    public void Verdicts_And_Status_Vocabulary()
    {
        EvalVerdicts.Terminal.ShouldBe([
            "resolved", "failed", "error", "timeout"], ignoreOrder: true);
        EvalRunStatuses.Terminal.ShouldBe([
            "completed", "failed", "cancelled"], ignoreOrder: true);
        EvalSuites.IsValid("swe-lite").ShouldBeTrue();
        EvalSuites.IsValid("terminal-bench-2").ShouldBeTrue();
        EvalSuites.IsValid("custom").ShouldBeTrue();
        EvalSuites.IsValid("bogus").ShouldBeFalse();
    }

    [Fact]
    public void EvalMode_Is_AgentType_Eval()
    {
        // eval_mode flag = agent_type "eval" persistido (SPEC-152 §5).
        EvalAgentTypes.IsEval("eval").ShouldBeTrue();
        EvalAgentTypes.IsEval(null).ShouldBeFalse();
        EvalAgentTypes.IsEval("code").ShouldBeFalse();
        EvalAgentTypes.IsEval("Eval").ShouldBeFalse(); // case-sensitive
    }
}