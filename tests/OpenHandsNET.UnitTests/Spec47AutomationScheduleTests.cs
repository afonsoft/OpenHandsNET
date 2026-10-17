using OpenHandsNET.Application.Automations;
using Shouldly;
using Xunit;

namespace OpenHandsNET.UnitTests;

/// <summary>
/// SPEC-47 RF-002: <see cref="AutomationSchedule.ComputeNextRun"/> —
/// tabela verdade dos 4 kinds (interval/daily/weekly/once), tudo UTC.
/// </summary>
public sealed class Spec47AutomationScheduleTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 17, 10, 30, 0, TimeSpan.Zero); // sábado

    [Fact]
    public void Interval_Soma_Minutos_Clap_1()
    {
        AutomationSchedule.ComputeNextRun(
            "interval", null, 15, null, null, Now)
            .ShouldBe(Now.AddMinutes(15));
        // abaixo do mínimo → clamp em 1 (validação de entrada impõe ≥5;
        // o schedule defensivamente garante ≥1).
        AutomationSchedule.ComputeNextRun(
            "interval", null, 0, null, null, Now)
            .ShouldBe(Now.AddMinutes(1));
    }

    [Fact]
    public void Daily_Hoje_Se_Futuro_Senao_Amanha()
    {
        // 11:00 ainda não passou → hoje
        AutomationSchedule.ComputeNextRun(
            "daily", "11:00", null, null, null, Now)
            .ShouldBe(new DateTimeOffset(2026, 10, 17, 11, 0, 0, TimeSpan.Zero));
        // 09:00 já passou → amanhã
        AutomationSchedule.ComputeNextRun(
            "daily", "09:00", null, null, null, Now)
            .ShouldBe(new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero));
        // horário inválido → null (agenda quebrada não dispara)
        AutomationSchedule.ComputeNextRun(
            "daily", "banana", null, null, null, Now)
            .ShouldBeNull();
        AutomationSchedule.ComputeNextRun(
            "daily", null, null, null, null, Now)
            .ShouldBeNull();
    }

    [Fact]
    public void Weekly_Proximo_Weekday_HHmm()
    {
        // sábado(6) 10:30 → domingo(0) 09:00 amanhã
        AutomationSchedule.ComputeNextRun(
            "weekly", "09:00", null, 0, null, Now)
            .ShouldBe(new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero));
        // mesmo weekday mas horário passado → +7d
        AutomationSchedule.ComputeNextRun(
            "weekly", "09:00", null, 6, null, Now)
            .ShouldBe(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));
        // mesmo weekday, horário futuro → hoje
        AutomationSchedule.ComputeNextRun(
            "weekly", "12:00", null, 6, null, Now)
            .ShouldBe(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
        // weekday fora da faixa → clamp 0..6 (7 → sábado; horário já
        // passou hoje → próximo sábado)
        AutomationSchedule.ComputeNextRun(
            "weekly", "09:00", null, 7, null, Now)
            .ShouldBe(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Once_Mantem_Futuro_E_Nao_Reagenda()
    {
        var future = Now.AddHours(2);
        AutomationSchedule.ComputeNextRun(
            "once", null, null, null, future, Now)
            .ShouldBe(future);
        // já disparou (next armazenado no passado) → null, nunca reagenda
        AutomationSchedule.ComputeNextRun(
            "once", null, null, null, Now.AddHours(-2), Now)
            .ShouldBeNull();
        AutomationSchedule.ComputeNextRun(
            "once", null, null, null, null, Now)
            .ShouldBeNull();
    }

    [Fact]
    public void Kind_Desconhecido_Retorna_Null() =>
        AutomationSchedule.ComputeNextRun(
            "cron", "10:00", 10, 0, null, Now)
            .ShouldBeNull();

    [Fact]
    public void Catchup_Dispara_Uma_Vez_Nao_Acumula()
    {
        // SPEC-47 RF-002: serviço caiu → NextRunAt ficou no passado.
        // O tick dispara uma vez e ComputeNextRun projeta a partir de
        // AGORA (não percorre cada ocorrência perdida).
        var next = AutomationSchedule.ComputeNextRun(
            "interval", null, 60, null, null, Now);
        next.ShouldBe(Now.AddMinutes(60));
    }
}
