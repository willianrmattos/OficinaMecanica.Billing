using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Contracts.Events;

namespace OficinaMecanica.Billing.Integration.Tests.Infra;

// Dois comandos da mesma ordem de serviço chegando ao mesmo tempo (a fila entrega em paralelo): o resultado tem de
// ser consistente e nenhuma mensagem pode parar na fila de erro, porque as corridas pelo índice único e pelo token
// de concorrência se resolvem no retry do MassTransit.
[Trait("Category", "Infra")]
[Collection(ColecaoInfra.Nome)]
public class ConcorrenciaTests
{
    private const int OrdensEmParalelo = 8;

    private readonly InfraFixture _infra;

    public ConcorrenciaTests(InfraFixture infra)
    {
        _infra = infra;
    }

    [Fact]
    public async Task DoisGerarOrcamentoSimultaneosDaMesmaOrdem_DeveFicarComUmOrcamentoESemMensagemEmErro()
    {
        await using var ambiente = await AmbienteInfra.CriarComHostAsync(_infra);
        var ordens = Enumerable.Range(0, OrdensEmParalelo).Select(_ => Guid.NewGuid()).ToList();

        await Task.WhenAll(ordens.SelectMany(ordem => new[] { ordem, ordem })
            .Select(ordem => ambiente.EnviarAoBillingAsync(MensagensDeTeste.GerarOrcamento(ordem), Guid.NewGuid())));

        await ambiente.AguardarAsync(
            () => ordens.All(ordem => ambiente.Coletor.De<OrcamentoGerado>(ordem).Count >= 2),
            "toda ordem recebeu a resposta das duas mensagens", segundos: 60);
        await Task.Delay(TimeSpan.FromSeconds(2));

        foreach (var ordem in ordens)
        {
            (await ambiente.ComBancoAsync(db => db.Orcamentos.CountAsync(o => o.OrdemDeServicoId == ordem))).Should().Be(1);
            ambiente.Coletor.De<OrcamentoGerado>(ordem).Select(e => e.OrcamentoId).Distinct().Should().ContainSingle();
        }
        (await ambiente.MensagensComErroAsync()).Should().Be(0, string.Join(" | ", ambiente.Logs.TakeLast(5)));
    }

    [Fact]
    public async Task GerarECancelarOrcamentoSimultaneos_DeveTerminarConsistenteEmQualquerOrdemDeChegada()
    {
        await using var ambiente = await AmbienteInfra.CriarComHostAsync(_infra);
        var ordens = Enumerable.Range(0, OrdensEmParalelo).Select(_ => Guid.NewGuid()).ToList();

        await Task.WhenAll(ordens.SelectMany(ordem => new Func<Task>[]
        {
            () => ambiente.EnviarAoBillingAsync(MensagensDeTeste.GerarOrcamento(ordem), Guid.NewGuid()),
            () => ambiente.EnviarAoBillingAsync(MensagensDeTeste.CancelarOrcamento(ordem), Guid.NewGuid()),
        }).Select(envio => envio()));

        await ambiente.AguardarAsync(
            () => ordens.All(ordem => ambiente.Coletor.De<OrcamentoGerado>(ordem).Count + ambiente.Coletor.De<OrcamentoNaoGerado>(ordem).Count >= 1),
            "toda ordem recebeu uma resposta do GerarOrcamento", segundos: 60);
        await Task.Delay(TimeSpan.FromSeconds(2));

        foreach (var ordem in ordens)
        {
            var orcamento = await ambiente.ComBancoAsync(db => db.Orcamentos.SingleOrDefaultAsync(o => o.OrdemDeServicoId == ordem));
            var tombstone = await ambiente.ComBancoAsync(db => db.CancelamentosPrevios.AnyAsync(c => c.OrdemDeServicoId == ordem));

            if (orcamento is not null)
                orcamento.Status.Should().Be(StatusOrcamento.Cancelado, "o cancelamento chegou depois e cancelou o orçamento existente");
            else
            {
                tombstone.Should().BeTrue("o cancelamento chegou primeiro e deixou o tombstone");
                ambiente.Coletor.De<OrcamentoNaoGerado>(ordem).Should().NotBeEmpty();
            }
        }
        (await ambiente.MensagensComErroAsync()).Should().Be(0, string.Join(" | ", ambiente.Logs.TakeLast(5)));
    }

    [Fact]
    public async Task SolicitarPagamentoDuplicadoSimultaneo_DeveCriarUmSoPagamentoEUmaSoPreferencia()
    {
        await using var ambiente = await AmbienteInfra.CriarComHostAsync(_infra);
        var ordem = Guid.NewGuid();
        await ambiente.EnviarAoBillingAsync(MensagensDeTeste.GerarOrcamento(ordem), Guid.NewGuid());
        await ambiente.AguardarAsync(() => ambiente.Coletor.De<OrcamentoGerado>(ordem).Count == 1, "o orçamento foi gerado");
        var orcamentoId = ambiente.Coletor.De<OrcamentoGerado>(ordem).Single().OrcamentoId;

        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            ambiente.EnviarAoBillingAsync(MensagensDeTeste.SolicitarPagamento(ordem, orcamentoId), Guid.NewGuid())));

        await ambiente.AguardarAsync(() => ambiente.Coletor.De<PagamentoSolicitado>(ordem).Count >= 3, "as três mensagens foram respondidas", segundos: 60);
        await Task.Delay(TimeSpan.FromSeconds(2));
        (await ambiente.ComBancoAsync(db => db.Pagamentos.CountAsync(p => p.OrdemDeServicoId == ordem))).Should().Be(1);
        ambiente.Coletor.De<PagamentoSolicitado>(ordem).Select(e => e.PagamentoId).Distinct().Should().ContainSingle();
        ambiente.Coletor.De<PagamentoSolicitado>(ordem).Select(e => e.LinkPagamento).Distinct().Should().ContainSingle();
        (await ambiente.MensagensComErroAsync()).Should().Be(0, string.Join(" | ", ambiente.Logs.TakeLast(5)));
    }

    [Fact]
    public async Task SolicitarECancelarPagamentoSimultaneos_DeveTerminarConsistente()
    {
        await using var ambiente = await AmbienteInfra.CriarComHostAsync(_infra);
        var ordens = Enumerable.Range(0, OrdensEmParalelo).Select(_ => Guid.NewGuid()).ToList();
        foreach (var ordem in ordens)
            await ambiente.EnviarAoBillingAsync(MensagensDeTeste.GerarOrcamento(ordem), Guid.NewGuid());
        await ambiente.AguardarAsync(() => ordens.All(o => ambiente.Coletor.De<OrcamentoGerado>(o).Count == 1), "todos os orçamentos foram gerados", segundos: 60);

        await Task.WhenAll(ordens.SelectMany(ordem => new Func<Task>[]
        {
            () => ambiente.EnviarAoBillingAsync(
                MensagensDeTeste.SolicitarPagamento(ordem, ambiente.Coletor.De<OrcamentoGerado>(ordem).Single().OrcamentoId), Guid.NewGuid()),
            () => ambiente.EnviarAoBillingAsync(MensagensDeTeste.CancelarPagamento(ordem), Guid.NewGuid()),
        }).Select(envio => envio()));

        await ambiente.AguardarAsync(
            () => ordens.All(o => ambiente.Coletor.De<PagamentoCancelado>(o).Count == 1
                && ambiente.Coletor.De<PagamentoSolicitado>(o).Count + ambiente.Coletor.De<PagamentoNaoSolicitado>(o).Count >= 1),
            "toda ordem recebeu as respostas", segundos: 60);
        await Task.Delay(TimeSpan.FromSeconds(2));

        foreach (var ordem in ordens)
        {
            var pagamento = await ambiente.ComBancoAsync(db => db.Pagamentos.SingleOrDefaultAsync(p => p.OrdemDeServicoId == ordem));
            if (pagamento is not null)
                pagamento.Status.Should().Be(StatusPagamento.Cancelado, "o cancelamento chegou depois do pagamento existir");
            else
                ambiente.Coletor.De<PagamentoNaoSolicitado>(ordem).Should().NotBeEmpty("o cancelamento chegou antes e bloqueou o pagamento");
        }
        (await ambiente.MensagensComErroAsync()).Should().Be(0, string.Join(" | ", ambiente.Logs.TakeLast(5)));
    }
}
