using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OficinaMecanica.Billing.Application.Interfaces;
using OficinaMecanica.Billing.Application.Mensagens;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Infrastructure.Data;
using OficinaMecanica.Contracts.Events;

namespace OficinaMecanica.Billing.Integration.Tests.Infra;

[Trait("Category", "Infra")]
[Collection(ColecaoInfra.Nome)]
public class OutboxAtomicoTests
{
    private readonly InfraFixture _infra;

    public OutboxAtomicoTests(InfraFixture infra)
    {
        _infra = infra;
    }

    private static OrcamentoGerado EventoDe(Domain.Entities.Orcamento orcamento) =>
        new(Guid.NewGuid(), orcamento.OrdemDeServicoId, DateTimeOffset.UtcNow, orcamento.Id, orcamento.ValorTotal, orcamento.ValidoAte);

    private static async Task PublicarEPersistirAsync(AmbienteInfra ambiente, Domain.Entities.Orcamento orcamento, bool confirmar)
    {
        using var escopo = ambiente.Servicos.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<BillingDbContext>();
        var publisher = escopo.ServiceProvider.GetRequiredService<IIntegrationEventPublisher>();

        // Padrão canônico de transação manual com a estratégia de retry do EF ligada (caminho HTTP).
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transacao = await db.Database.BeginTransactionAsync();
            db.Orcamentos.Add(orcamento);
            await publisher.PublishAsync(EventoDe(orcamento));
            await db.SaveChangesAsync();
            if (confirmar)
                await transacao.CommitAsync();
            else
                await transacao.RollbackAsync();
        });
    }

    [Fact]
    public async Task Handler_PublishAntesDoSave_EntregaAMensagemQuandoATransacaoConfirma()
    {
        await using var ambiente = await AmbienteInfra.CriarComHostAsync(_infra);
        var ordem = Guid.NewGuid();
        var comando = ComandosDeMensagem.De(MensagensDeTeste.GerarOrcamento(ordem));

        using (var escopo = ambiente.Servicos.CreateScope())
            await escopo.ServiceProvider.GetRequiredService<IMediator>().Send(comando);

        await ambiente.AguardarAsync(() => ambiente.Coletor.De<OrcamentoGerado>(ordem).Count == 1, "o OrcamentoGerado do outbox foi entregue");
        var evento = ambiente.Coletor.De<OrcamentoGerado>(ordem).Single();
        var orcamento = await ambiente.ComBancoAsync(db => db.Orcamentos.SingleAsync(o => o.OrdemDeServicoId == ordem));
        evento.OrcamentoId.Should().Be(orcamento.Id);
        evento.ValorTotal.Should().Be(MensagensDeTeste.ValorDoOrcamento);
        ambiente.Coletor.MessageIdsDe<OrcamentoGerado>().Single().Should().Be(evento.MessageId, "o MessageId do contrato é o do envelope");
    }

    [Fact]
    public async Task TransacaoConfirmada_DeveEntregarOEventoPublicadoAntesDoSave()
    {
        await using var ambiente = await AmbienteInfra.CriarComHostAsync(_infra);
        var orcamento = new OrcamentoBuilder().CriadoEm(DateTimeOffset.UtcNow).Gerado();

        await PublicarEPersistirAsync(ambiente, orcamento, confirmar: true);

        await ambiente.AguardarAsync(() => ambiente.Coletor.De<OrcamentoGerado>(orcamento.OrdemDeServicoId).Count == 1, "o evento foi entregue no commit");
        (await ambiente.ComBancoAsync(db => db.Orcamentos.CountAsync(o => o.Id == orcamento.Id))).Should().Be(1);
    }

    [Fact]
    public async Task TransacaoDesfeita_NaoDeveEntregarOEventoNemDeixarOrcamentoNemMensagemNoOutbox()
    {
        await using var ambiente = await AmbienteInfra.CriarComHostAsync(_infra);
        var orcamento = new OrcamentoBuilder().CriadoEm(DateTimeOffset.UtcNow).Gerado();

        await PublicarEPersistirAsync(ambiente, orcamento, confirmar: false);

        await Task.Delay(TimeSpan.FromSeconds(3));
        ambiente.Coletor.De<OrcamentoGerado>().Should().BeEmpty();
        (await ambiente.ComBancoAsync(db => db.Orcamentos.CountAsync(o => o.Id == orcamento.Id))).Should().Be(0);
        (await ambiente.ComBancoAsync(db => db.Set<MassTransit.EntityFrameworkCoreIntegration.OutboxMessage>().CountAsync()))
            .Should().Be(0, "a mensagem só existe no outbox junto com a transação que a gerou");
    }

    [Fact]
    public async Task SaveChangesQueFalha_NaoDeveEntregarOEventoPublicadoAntes()
    {
        await using var ambiente = await AmbienteInfra.CriarComHostAsync(_infra);
        var primeiro = new OrcamentoBuilder().CriadoEm(DateTimeOffset.UtcNow).Gerado();
        await ambiente.ComBancoAsync(async db =>
        {
            db.Orcamentos.Add(primeiro);
            await db.SaveChangesAsync();
        });
        // Segundo orçamento para a mesma ordem de serviço: viola o índice único e o Save falha.
        var duplicado = new OrcamentoBuilder().DaOrdemDeServico(primeiro.OrdemDeServicoId).CriadoEm(DateTimeOffset.UtcNow).Gerado();

        var act = () => PublicarEPersistirAsync(ambiente, duplicado, confirmar: true);

        var excecao = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        (excecao.InnerException as Microsoft.Data.SqlClient.SqlException)!.Number.Should().BeOneOf(2601, 2627);
        await Task.Delay(TimeSpan.FromSeconds(3));
        ambiente.Coletor.De<OrcamentoGerado>().Should().BeEmpty();
    }

    [Fact]
    public async Task Consumer_FalhaTecnicaDoGateway_NaoDeixaEstadoParcialNemEventoEMandaAMensagemParaAFilaDeErro()
    {
        await using var ambiente = await AmbienteInfra.CriarComHostAsync(_infra);
        ambiente.Comportamento.SempreFalhaNaCriacao = true;
        var ordem = Guid.NewGuid();
        await ambiente.EnviarAoBillingAsync(MensagensDeTeste.GerarOrcamento(ordem), Guid.NewGuid());
        await ambiente.AguardarAsync(() => ambiente.Coletor.De<OrcamentoGerado>(ordem).Count == 1, "o orçamento foi gerado");
        var orcamentoId = ambiente.Coletor.De<OrcamentoGerado>(ordem).Single().OrcamentoId;

        await ambiente.EnviarAoBillingAsync(MensagensDeTeste.SolicitarPagamento(ordem, orcamentoId), Guid.NewGuid());
        await ambiente.AguardarAsync(async () => await ambiente.MensagensComErroAsync() == 1, "a mensagem esgotou o retry e foi para a fila _error");

        ambiente.Comportamento.ChamadasDeCriacao.Should().Be(5, "1 tentativa mais os 4 intervalos de retry configurados");
        ambiente.Coletor.De<PagamentoSolicitado>(ordem).Should().BeEmpty();
        (await ambiente.ComBancoAsync(db => db.Pagamentos.CountAsync(p => p.OrdemDeServicoId == ordem)))
            .Should().Be(0, "o pagamento pré-persistido estava na transação desfeita");
        (await ambiente.ComBancoAsync(db => db.Orcamentos.SingleAsync(o => o.Id == orcamentoId))).Status
            .Should().Be(StatusOrcamento.Gerado, "a aprovação do orçamento também voltou com a transação");
    }

    [Fact]
    public async Task Consumer_FalhaTransitoriaSeguidaDeSucesso_ProcessaNaTentativaSeguinteSemEstadoDuplicado()
    {
        await using var ambiente = await AmbienteInfra.CriarComHostAsync(_infra);
        ambiente.Comportamento.FalhasDeCriacaoAntesDoSucesso = 2;
        var ordem = Guid.NewGuid();
        await ambiente.EnviarAoBillingAsync(MensagensDeTeste.GerarOrcamento(ordem), Guid.NewGuid());
        await ambiente.AguardarAsync(() => ambiente.Coletor.De<OrcamentoGerado>(ordem).Count == 1, "o orçamento foi gerado");
        var orcamentoId = ambiente.Coletor.De<OrcamentoGerado>(ordem).Single().OrcamentoId;

        await ambiente.EnviarAoBillingAsync(MensagensDeTeste.SolicitarPagamento(ordem, orcamentoId), Guid.NewGuid());

        await ambiente.AguardarAsync(() => ambiente.Coletor.De<PagamentoSolicitado>(ordem).Count == 1, "o pagamento foi solicitado na terceira tentativa");
        ambiente.Comportamento.ChamadasDeCriacao.Should().Be(3);
        (await ambiente.ComBancoAsync(db => db.Pagamentos.CountAsync(p => p.OrdemDeServicoId == ordem))).Should().Be(1);
        (await ambiente.MensagensComErroAsync()).Should().Be(0);
        ambiente.Coletor.De<PagamentoSolicitado>(ordem).Single().LinkPagamento.Should().StartWith("https://fake.mercadopago.local/checkout/");
    }

    [Fact]
    public async Task RetryDoEf_DeveEstarLigadoForaDoConsumerEDesligadoDentroDele()
    {
        await using var ambiente = await AmbienteInfra.CriarComHostAsync(_infra);
        var ordem = Guid.NewGuid();
        await ambiente.EnviarAoBillingAsync(MensagensDeTeste.GerarOrcamento(ordem), Guid.NewGuid());
        await ambiente.AguardarAsync(() => ambiente.Coletor.De<OrcamentoGerado>(ordem).Count == 1, "o orçamento foi gerado");
        var orcamentoId = ambiente.Coletor.De<OrcamentoGerado>(ordem).Single().OrcamentoId;
        await ambiente.EnviarAoBillingAsync(MensagensDeTeste.SolicitarPagamento(ordem, orcamentoId), Guid.NewGuid());
        await ambiente.AguardarAsync(() => ambiente.Coletor.De<PagamentoSolicitado>(ordem).Count == 1, "o consumer chamou o gateway e processou");

        using var escopoHttp = ambiente.Servicos.CreateScope();
        var foraDoConsumer = escopoHttp.ServiceProvider.GetRequiredService<BillingDbContext>().Database.CreateExecutionStrategy().RetriesOnFailure;

        ambiente.Comportamento.RetryDoEfObservadoNasChamadas.Should().ContainSingle().Which.Should().BeFalse("dentro do consumer o retry é do MassTransit");
        foraDoConsumer.Should().BeTrue("fora do consumer o EF repete falhas transitórias (ex.: Azure SQL serverless acordando)");
    }
}
