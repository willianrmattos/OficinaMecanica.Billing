using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OficinaMecanica.Billing.Application.Commands.ProcessarWebhookPagamento;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Infrastructure.BackgroundServices;
using OficinaMecanica.Billing.Infrastructure.Options;
using OficinaMecanica.Billing.Infrastructure.Security;
using OficinaMecanica.Contracts.Events;

namespace OficinaMecanica.Billing.Integration.Tests.Infra;

// O reconciliador sobre o banco real: os pagamentos que ficaram com dinheiro a devolver (estorno parado, falho, de
// aprovação tardia sem comando, ou cobrança em duplicidade) são levados até o fim pelo mesmo consumer de estorno.
[Trait("Category", "Infra")]
[Collection(ColecaoInfra.Nome)]
public class ReconciliadorInfraTests
{
    private readonly InfraFixture _infra;

    public ReconciliadorInfraTests(InfraFixture infra)
    {
        _infra = infra;
    }

    private static ReconciliadorDePagamentosBackgroundService Reconciliador(AmbienteInfra ambiente) =>
        ambiente.Servicos.GetServices<IHostedService>().OfType<ReconciliadorDePagamentosBackgroundService>().Single();

    private static async Task<(Guid Ordem, PagamentoSolicitado Solicitado, string PreferenceId)> PedirPagamentoAsync(AmbienteInfra ambiente)
    {
        var ordem = Guid.NewGuid();
        await ambiente.EnviarAoBillingAsync(MensagensDeTeste.GerarOrcamento(ordem), Guid.NewGuid());
        await ambiente.AguardarAsync(() => ambiente.Coletor.De<OrcamentoGerado>(ordem).Count == 1, "o orçamento foi gerado");
        await ambiente.EnviarAoBillingAsync(
            MensagensDeTeste.SolicitarPagamento(ordem, ambiente.Coletor.De<OrcamentoGerado>(ordem).Single().OrcamentoId), Guid.NewGuid());
        await ambiente.AguardarAsync(() => ambiente.Coletor.De<PagamentoSolicitado>(ordem).Count == 1, "o pagamento foi solicitado");
        var solicitado = ambiente.Coletor.De<PagamentoSolicitado>(ordem).Single();
        return (ordem, solicitado, solicitado.LinkPagamento.Split('/').Last());
    }

    private static async Task ReceberWebhookAsync(AmbienteInfra ambiente, string gatewayPaymentId)
    {
        var requestId = Guid.NewGuid().ToString();
        var assinatura = AssinaturaWebhookMercadoPago.GerarCabecalho(AmbienteInfra.SegredoDoFake, gatewayPaymentId, requestId, DateTimeOffset.UtcNow);
        using var escopo = ambiente.Servicos.CreateScope();
        await escopo.ServiceProvider.GetRequiredService<IMediator>()
            .Send(new ProcessarWebhookPagamentoCommand(gatewayPaymentId, requestId, assinatura));
    }

    private static async Task<Pagamento> CarregarAsync(AmbienteInfra ambiente, Guid pagamentoId) =>
        await ambiente.ComBancoAsync(db => db.Pagamentos.Include(p => p.Tentativas).Include(p => p.Estorno).SingleAsync(p => p.Id == pagamentoId));

    [Fact]
    public async Task EstornoParadoEmEstornando_DeveSerRetomadoEConcluido()
    {
        await using var ambiente = await AmbienteInfra.CriarComHostAsync(_infra);
        var (ordem, solicitado, preferenceId) = await PedirPagamentoAsync(ambiente);
        await ReceberWebhookAsync(ambiente, ambiente.Gateway.AprovarPreferencia(preferenceId).Id);
        await ambiente.AguardarAsync(() => ambiente.Coletor.De<PagamentoAprovado>(ordem).Count == 1, "o pagamento foi aprovado");
        // Simula a queda depois de gravar a intenção de estornar e antes de chamar o gateway.
        await ambiente.ComBancoAsync(async db =>
        {
            var pagamento = await db.Pagamentos.Include(p => p.Tentativas).Include(p => p.Estorno).SingleAsync(p => p.Id == solicitado.PagamentoId);
            pagamento.IniciarEstorno();
            await db.SaveChangesAsync();
        });

        var reconciliados = await Reconciliador(ambiente).ExecutarCicloAsync(CancellationToken.None);

        reconciliados.Should().Be(1);
        await ambiente.AguardarAsync(() => ambiente.Coletor.De<PagamentoEstornado>(ordem).Count == 1, "o estorno foi concluído pelo consumer");
        (await CarregarAsync(ambiente, solicitado.PagamentoId)).Status.Should().Be(StatusPagamento.Estornado);
        (await ambiente.MensagensComErroAsync()).Should().Be(0);
    }

    [Fact]
    public async Task EstornoQueFalhou_DeveSerReenviadoEConcluirQuandoOGatewayVoltar()
    {
        await using var ambiente = await AmbienteInfra.CriarComHostAsync(_infra);
        var (ordem, solicitado, preferenceId) = await PedirPagamentoAsync(ambiente);
        await ReceberWebhookAsync(ambiente, ambiente.Gateway.AprovarPreferencia(preferenceId).Id);
        await ambiente.AguardarAsync(() => ambiente.Coletor.De<PagamentoAprovado>(ordem).Count == 1, "o pagamento foi aprovado");
        ambiente.Gateway.FalhaNoEstorno = new HttpRequestException("gateway fora do ar");
        await ambiente.EnviarAoBillingAsync(MensagensDeTeste.EstornarPagamento(ordem, solicitado.PagamentoId), Guid.NewGuid());
        await ambiente.AguardarAsync(() => ambiente.Coletor.De<EstornoFalhou>(ordem).Count == 1, "a falha do estorno foi comunicada");
        (await CarregarAsync(ambiente, solicitado.PagamentoId)).Status.Should().Be(StatusPagamento.EstornoFalhou);
        ambiente.Gateway.FalhaNoEstorno = null;

        await Reconciliador(ambiente).ExecutarCicloAsync(CancellationToken.None);

        await ambiente.AguardarAsync(() => ambiente.Coletor.De<PagamentoEstornado>(ordem).Count == 1, "o estorno foi refeito");
        var pagamento = await CarregarAsync(ambiente, solicitado.PagamentoId);
        pagamento.Status.Should().Be(StatusPagamento.Estornado);
        pagamento.Estorno!.Tentativas.Should().Be(1, "só a primeira tentativa falhou");
    }

    [Fact]
    public async Task AprovacaoTardiaSemOComandoDeEstorno_DeveSerEstornadaPelaReconciliacao()
    {
        await using var ambiente = await AmbienteInfra.CriarComHostAsync(_infra);
        var (ordem, solicitado, preferenceId) = await PedirPagamentoAsync(ambiente);
        await ambiente.EnviarAoBillingAsync(MensagensDeTeste.CancelarPagamento(ordem, solicitado.PagamentoId), Guid.NewGuid());
        await ambiente.AguardarAsync(() => ambiente.Coletor.De<PagamentoCancelado>(ordem).Count == 1, "o pagamento foi cancelado");
        // O gateway aprovou depois do cancelamento, mas o comando de estorno automático nunca chegou: grava só o estado.
        var pagamentoDoGateway = ambiente.Gateway.AprovarPreferencia(preferenceId);
        await ambiente.ComBancoAsync(async db =>
        {
            var pagamento = await db.Pagamentos.Include(p => p.Tentativas).Include(p => p.Estorno).SingleAsync(p => p.Id == solicitado.PagamentoId);
            pagamento.RegistrarTentativa(pagamentoDoGateway.Id, "approved", "accredited", pagamento.Valor, DateTimeOffset.UtcNow);
            pagamento.ConfirmarAprovacao(pagamentoDoGateway.Id, pagamento.Valor, "BRL", pagamento.Id.ToString(), DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        });
        (await CarregarAsync(ambiente, solicitado.PagamentoId)).EstornoPendente.Should().BeTrue();

        await Reconciliador(ambiente).ExecutarCicloAsync(CancellationToken.None);

        await ambiente.AguardarAsync(() => ambiente.Coletor.De<PagamentoEstornado>(ordem).Count == 1, "o estorno automático foi feito");
        (await CarregarAsync(ambiente, solicitado.PagamentoId)).Status.Should().Be(StatusPagamento.Estornado);
    }

    [Fact]
    public async Task CobrancaEmDuplicidade_DeveSerEstornadaUmaVezSoSemTocarNoPagamentoAdotado()
    {
        await using var ambiente = await AmbienteInfra.CriarComHostAsync(_infra);
        var (ordem, solicitado, preferenceId) = await PedirPagamentoAsync(ambiente);
        var primeiro = ambiente.Gateway.AprovarPreferencia(preferenceId);
        await ReceberWebhookAsync(ambiente, primeiro.Id);
        await ambiente.AguardarAsync(() => ambiente.Coletor.De<PagamentoAprovado>(ordem).Count == 1, "o pagamento foi aprovado");
        var duplicado = ambiente.Gateway.AprovarPreferencia(preferenceId);
        await ReceberWebhookAsync(ambiente, duplicado.Id);
        (await CarregarAsync(ambiente, solicitado.PagamentoId)).AprovacoesDuplicadasSemEstorno().Should().ContainSingle();

        var servico = Reconciliador(ambiente);
        await servico.ExecutarCicloAsync(CancellationToken.None);
        await servico.ExecutarCicloAsync(CancellationToken.None);

        var pagamento = await CarregarAsync(ambiente, solicitado.PagamentoId);
        pagamento.Status.Should().Be(StatusPagamento.Aprovado);
        pagamento.GatewayPaymentIdAprovado.Should().Be(primeiro.Id);
        pagamento.Tentativas.Single(t => t.GatewayPaymentId == duplicado.Id).GatewayRefundId.Should().StartWith("fake-refund-");
        pagamento.Tentativas.Single(t => t.GatewayPaymentId == primeiro.Id).GatewayRefundId.Should().BeNull();
        (await ambiente.Gateway.ObterPagamentoAsync(duplicado.Id))!.Status.Should().Be("refunded");
        (await ambiente.Gateway.ObterPagamentoAsync(primeiro.Id))!.Status.Should().Be("approved");
        ambiente.Coletor.De<PagamentoEstornado>(ordem).Should().BeEmpty("a duplicidade não passa pela saga");
    }

    [Fact]
    public async Task CicloSemNadaPendente_NaoDeveReconciliarNada()
    {
        await using var ambiente = await AmbienteInfra.CriarComHostAsync(_infra);

        (await Reconciliador(ambiente).ExecutarCicloAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task EstornoComTentativasEsgotadas_NaoDeveSerReenviado()
    {
        await using var ambiente = await AmbienteInfra.CriarComHostAsync(
            _infra, servicos => servicos.Configure<OpcoesDoBilling>(o => o.ReconciliadorMaximoDeTentativasDeEstorno = 1));
        var (ordem, solicitado, preferenceId) = await PedirPagamentoAsync(ambiente);
        await ReceberWebhookAsync(ambiente, ambiente.Gateway.AprovarPreferencia(preferenceId).Id);
        await ambiente.AguardarAsync(() => ambiente.Coletor.De<PagamentoAprovado>(ordem).Count == 1, "o pagamento foi aprovado");
        ambiente.Gateway.FalhaNoEstorno = new HttpRequestException("gateway fora do ar");
        await ambiente.EnviarAoBillingAsync(MensagensDeTeste.EstornarPagamento(ordem, solicitado.PagamentoId), Guid.NewGuid());
        await ambiente.AguardarAsync(() => ambiente.Coletor.De<EstornoFalhou>(ordem).Count == 1, "a falha do estorno foi comunicada");
        ambiente.Gateway.FalhaNoEstorno = null;

        await Reconciliador(ambiente).ExecutarCicloAsync(CancellationToken.None);

        await Task.Delay(TimeSpan.FromSeconds(3));
        ambiente.Coletor.De<PagamentoEstornado>(ordem).Should().BeEmpty("o teto de tentativas foi atingido e o caso fica com o staff");
        (await CarregarAsync(ambiente, solicitado.PagamentoId)).Status.Should().Be(StatusPagamento.EstornoFalhou);
    }
}
