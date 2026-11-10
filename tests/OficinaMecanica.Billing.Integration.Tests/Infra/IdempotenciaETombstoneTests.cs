using FluentAssertions;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Contracts.Events;

namespace OficinaMecanica.Billing.Integration.Tests.Infra;

[Trait("Category", "Infra")]
[Collection(ColecaoInfra.Nome)]
public class IdempotenciaETombstoneTests
{
    private readonly InfraFixture _infra;

    public IdempotenciaETombstoneTests(InfraFixture infra)
    {
        _infra = infra;
    }

    // ---- Mensagem duplicada ----

    [Fact]
    public async Task MesmoMessageIdEnviadoSimultaneamente_DeveSerProcessadoUmaVezSo()
    {
        await using var ambiente = await AmbienteInfra.CriarComHostAsync(_infra);
        var ordem = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var comando = MensagensDeTeste.GerarOrcamento(ordem, messageId);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => ambiente.EnviarAoBillingAsync(comando, messageId)));

        await ambiente.AguardarAsync(() => ambiente.Coletor.De<OrcamentoGerado>(ordem).Count >= 1, "o orçamento foi gerado");
        await Task.Delay(TimeSpan.FromSeconds(4));
        ambiente.Coletor.De<OrcamentoGerado>(ordem).Should().ContainSingle("o inbox descarta as cópias do mesmo MessageId");
        (await ambiente.ComBancoAsync(db => db.Orcamentos.CountAsync(o => o.OrdemDeServicoId == ordem))).Should().Be(1);
        var inbox = await ambiente.ComBancoAsync(db => db.Set<InboxState>().Where(i => i.MessageId == messageId).ToListAsync());
        inbox.Should().ContainSingle().Which.Consumed.Should().NotBeNull();
        (await ambiente.MensagensComErroAsync()).Should().Be(0);
    }

    [Fact]
    public async Task MesmoMessageIdReentregueDepoisDeProcessado_DeveSerIgnorado()
    {
        await using var ambiente = await AmbienteInfra.CriarComHostAsync(_infra);
        var ordem = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var comando = MensagensDeTeste.GerarOrcamento(ordem, messageId);
        await ambiente.EnviarAoBillingAsync(comando, messageId);
        await ambiente.AguardarAsync(() => ambiente.Coletor.De<OrcamentoGerado>(ordem).Count == 1, "o orçamento foi gerado");

        await ambiente.EnviarAoBillingAsync(comando, messageId);

        await Task.Delay(TimeSpan.FromSeconds(3));
        ambiente.Coletor.De<OrcamentoGerado>(ordem).Should().ContainSingle();
        (await ambiente.MensagensComErroAsync()).Should().Be(0);
    }

    [Fact]
    public async Task MensagensDiferentesParaOMesmoPedido_DeveManterUmSoOrcamentoERepublicarOMesmoEvento()
    {
        await using var ambiente = await AmbienteInfra.CriarComHostAsync(_infra);
        var ordem = Guid.NewGuid();

        for (var i = 0; i < 3; i++)
        {
            await ambiente.EnviarAoBillingAsync(MensagensDeTeste.GerarOrcamento(ordem), Guid.NewGuid());
            await ambiente.AguardarAsync(() => ambiente.Coletor.De<OrcamentoGerado>(ordem).Count == i + 1, $"a resposta {i + 1} chegou");
        }

        (await ambiente.ComBancoAsync(db => db.Orcamentos.CountAsync(o => o.OrdemDeServicoId == ordem))).Should().Be(1);
        ambiente.Coletor.De<OrcamentoGerado>(ordem).Select(e => e.OrcamentoId).Distinct().Should().ContainSingle();
        ambiente.Coletor.De<OrcamentoGerado>(ordem).Select(e => e.MessageId).Distinct().Should().ContainSingle(
            "a republicação usa o MessageId determinístico, para o inbox do OS Service deduplicar");
    }

    // ---- Cancelamento previo (tombstone) ----

    [Fact]
    public async Task CancelarOrcamentoQueChegaAntesDeGerar_DeveRegistrarOTombstoneEOGerarAtrasadoVirarOrcamentoNaoGerado()
    {
        await using var ambiente = await AmbienteInfra.CriarComHostAsync(_infra);
        var ordem = Guid.NewGuid();

        await ambiente.EnviarAoBillingAsync(MensagensDeTeste.CancelarOrcamento(ordem), Guid.NewGuid());
        await ambiente.AguardarAsync(
            async () => await ambiente.ComBancoAsync(db => db.CancelamentosPrevios.AnyAsync(c => c.OrdemDeServicoId == ordem && c.Tipo == TipoCancelamentoPrevio.Orcamento)),
            "o cancelamento prévio foi registrado");

        await ambiente.EnviarAoBillingAsync(MensagensDeTeste.GerarOrcamento(ordem), Guid.NewGuid());

        await ambiente.AguardarAsync(() => ambiente.Coletor.De<OrcamentoNaoGerado>(ordem).Count == 1, "o GerarOrcamento atrasado virou OrcamentoNaoGerado");
        ambiente.Coletor.De<OrcamentoGerado>(ordem).Should().BeEmpty();
        (await ambiente.ComBancoAsync(db => db.Orcamentos.CountAsync(o => o.OrdemDeServicoId == ordem))).Should().Be(0);
        (await ambiente.MensagensComErroAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CancelarOrcamentoDuplicadoAntesDeGerar_DeveManterUmSoTombstone()
    {
        await using var ambiente = await AmbienteInfra.CriarComHostAsync(_infra);
        var ordem = Guid.NewGuid();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            ambiente.EnviarAoBillingAsync(MensagensDeTeste.CancelarOrcamento(ordem), Guid.NewGuid())));

        await ambiente.AguardarAsync(
            async () => await ambiente.ComBancoAsync(db => db.CancelamentosPrevios.CountAsync(c => c.OrdemDeServicoId == ordem)) == 1,
            "um único cancelamento prévio foi registrado");
        await Task.Delay(TimeSpan.FromSeconds(3));
        (await ambiente.ComBancoAsync(db => db.CancelamentosPrevios.CountAsync(c => c.OrdemDeServicoId == ordem))).Should().Be(1);
        (await ambiente.MensagensComErroAsync()).Should().Be(0, "a corrida pelo índice único se resolve no retry da mensagem");
    }

    [Fact]
    public async Task CancelarPagamentoQueChegaAntesDeSolicitar_DeveResponderPagamentoCanceladoSemIdEBloquearOPagamentoAtrasado()
    {
        await using var ambiente = await AmbienteInfra.CriarComHostAsync(_infra);
        var ordem = Guid.NewGuid();
        await ambiente.EnviarAoBillingAsync(MensagensDeTeste.GerarOrcamento(ordem), Guid.NewGuid());
        await ambiente.AguardarAsync(() => ambiente.Coletor.De<OrcamentoGerado>(ordem).Count == 1, "o orçamento foi gerado");
        var orcamentoId = ambiente.Coletor.De<OrcamentoGerado>(ordem).Single().OrcamentoId;

        await ambiente.EnviarAoBillingAsync(MensagensDeTeste.CancelarPagamento(ordem), Guid.NewGuid());
        await ambiente.AguardarAsync(() => ambiente.Coletor.De<PagamentoCancelado>(ordem).Count == 1, "o cancelamento respondeu à saga");
        ambiente.Coletor.De<PagamentoCancelado>(ordem).Single().PagamentoId.Should().BeNull();

        await ambiente.EnviarAoBillingAsync(MensagensDeTeste.SolicitarPagamento(ordem, orcamentoId), Guid.NewGuid());

        await ambiente.AguardarAsync(() => ambiente.Coletor.De<PagamentoNaoSolicitado>(ordem).Count == 1, "o SolicitarPagamento atrasado virou PagamentoNaoSolicitado");
        (await ambiente.ComBancoAsync(db => db.Pagamentos.CountAsync(p => p.OrdemDeServicoId == ordem))).Should().Be(0);
        ambiente.Coletor.De<PagamentoSolicitado>(ordem).Should().BeEmpty();
    }

    [Fact]
    public async Task CancelarOrcamentoDepoisDeGerado_DeveCancelarOOrcamentoSemTombstone()
    {
        await using var ambiente = await AmbienteInfra.CriarComHostAsync(_infra);
        var ordem = Guid.NewGuid();
        await ambiente.EnviarAoBillingAsync(MensagensDeTeste.GerarOrcamento(ordem), Guid.NewGuid());
        await ambiente.AguardarAsync(() => ambiente.Coletor.De<OrcamentoGerado>(ordem).Count == 1, "o orçamento foi gerado");

        await ambiente.EnviarAoBillingAsync(MensagensDeTeste.CancelarOrcamento(ordem), Guid.NewGuid());

        await ambiente.AguardarAsync(
            async () => await ambiente.ComBancoAsync(db => db.Orcamentos.AnyAsync(o => o.OrdemDeServicoId == ordem && o.Status == StatusOrcamento.Cancelado)),
            "o orçamento foi cancelado");
        (await ambiente.ComBancoAsync(db => db.CancelamentosPrevios.CountAsync(c => c.OrdemDeServicoId == ordem))).Should().Be(0);
    }

    // ---- Mensagem invalida (nao retentada) ----

    [Fact]
    public async Task ComandoInvalido_NaoDeveSerRetentadoEDeveIrParaAFilaDeErro()
    {
        await using var ambiente = await AmbienteInfra.CriarComHostAsync(_infra);

        await ambiente.EnviarAoBillingAsync(MensagensDeTeste.GerarOrcamento(Guid.Empty), Guid.NewGuid());

        await ambiente.AguardarAsync(async () => await ambiente.MensagensComErroAsync() == 1, "o comando inválido foi para a fila _error");
        ambiente.Coletor.Todas.Should().BeEmpty();
    }
}
