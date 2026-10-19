using System.Reflection;

using OpenHandsNET.Cli.Commands;

using Shouldly;

using Spectre.Console.Cli;

namespace OpenHandsNET.UnitTests.Cli;

/// <summary>
/// Spectre.Console.Cli 0.57 contract: Command&lt;T&gt;.Execute and
/// AsyncCommand&lt;T&gt;.ExecuteAsync require a CancellationToken
/// parameter. Guards every concrete CLI command against the signature
/// regression that broke CI after the dependabot bump.
/// </summary>
public sealed class CommandSignatureTests
{
    private static IEnumerable<(Type Type, string Method)> ConcreteCommands()
    {
        var asm = typeof(RunCommand).Assembly;
        foreach (var type in asm.GetTypes().Where(t => t is { IsAbstract: false, IsClass: true }))
        {
            for (var b = type.BaseType; b is not null; b = b.BaseType)
            {
                if (!b.IsGenericType)
                    continue;
                var def = b.GetGenericTypeDefinition();
                if (def == typeof(AsyncCommand<>))
                {
                    yield return (type, nameof(AsyncCommand<CommandSettings>.ExecuteAsync));
                    break;
                }
                if (def == typeof(Command<>))
                {
                    yield return (type, nameof(Command<CommandSettings>.Execute));
                    break;
                }
            }
        }
    }

    [Fact]
    public void Every_Command_Implements_Execute_With_CancellationToken()
    {
        var commands = ConcreteCommands().ToList();
        commands.ShouldNotBeEmpty();

        foreach (var (type, methodName) in commands)
        {
            var method = type.GetMethod(methodName,
                BindingFlags.Public | BindingFlags.Instance);
            method.ShouldNotBeNull($"{type.Name}.{methodName} not found");
            method!.GetParameters()
                .ShouldContain(p => p.ParameterType == typeof(CancellationToken),
                    $"{type.Name}.{methodName} must accept CancellationToken (Spectre 0.57)");
        }
    }
}
