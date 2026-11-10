using FluentAssertions;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Exceptions;

namespace OficinaMecanica.Billing.Domain.Tests.Entities;

public class PagamentoTentativasTests
{
    private static readonly DateTimeOffset Agora = RelogioFixo.InstanteBase;

    [Fact]
    public void RegistrarTentativa_PrimeiraNotificacao_DeveAdicionarTentativa()
    {
        var pagamento = new PagamentoBuilder().ComPreferencia();

        pagamento.RegistrarTentativa("111", "pending", "pending_waiting_payment", 150m, Agora);

        var tentativa = pagamento.Tentativas.Should().ContainSingle().Subject;
        tentativa.GatewayPaymentId.Should().Be("111");
        tentativa.StatusGateway.Should().Be("pending");
        tentativa.StatusDetalhe.Should().Be("pending_waiting_payment");
        tentativa.ValorGateway.Should().Be(150m);
        tentativa.AtualizadoEm.Should().Be(Agora);
        tentativa.Aprovada.Should().BeFalse();
    }

    [Fact]
    public void RegistrarTentativa_MesmoGatewayPaymentIdDeNovo_DeveAtualizarSemDuplicar()
    {
        var pagamento = new PagamentoBuilder().ComPreferencia();
        pagamento.RegistrarTentativa("111", "pending", null, 150m, Agora);

        pagamento.RegistrarTentativa("111", "in_process", "pending_review_manual", 150m, Agora.AddMinutes(5));

        var tentativa = pagamento.Tentativas.Should().ContainSingle().Subject;
        tentativa.StatusGateway.Should().Be("in_process");
        tentativa.StatusDetalhe.Should().Be("pending_review_manual");
        tentativa.AtualizadoEm.Should().Be(Agora.AddMinutes(5));
    }

    [Fact]
    public void RegistrarTentativa_GatewayPaymentIdsDiferentes_DeveManterUmaTentativaParaCada()
    {
        var pagamento = new PagamentoBuilder().ComPreferencia();

        pagamento.RegistrarTentativa("111", "rejected", "cc_rejected_insufficient_amount", 150m, Agora);
        pagamento.RegistrarTentativa("222", "approved", "accredited", 150m, Agora.AddMinutes(2));

        pagamento.Tentativas.Select(t => t.GatewayPaymentId).Should().BeEquivalentTo(new[] { "111", "222" });
    }

    [Fact]
    public void RegistrarTentativa_RejeicaoDoGateway_NaoDeveMudarOStatusDoPagamento()
    {
        var pagamento = new PagamentoBuilder().ComPreferencia();

        pagamento.RegistrarTentativa("111", "rejected", "cc_rejected_other_reason", 150m, Agora);

        pagamento.Status.Should().Be(StatusPagamento.Pendente);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("in_process")]
    [InlineData("rejected")]
    [InlineData("cancelled")]
    [InlineData("refunded")]
    public void RegistrarTentativa_TentativaJaAprovada_NuncaDeveSerRebaixada(string statusAtrasado)
    {
        var pagamento = new PagamentoBuilder().ComPreferencia();
        pagamento.RegistrarTentativa("111", "approved", "accredited", 150m, Agora);

        pagamento.RegistrarTentativa("111", statusAtrasado, "detalhe_atrasado", 99m, Agora.AddMinutes(10));

        var tentativa = pagamento.Tentativas.Should().ContainSingle().Subject;
        tentativa.StatusGateway.Should().Be("approved");
        tentativa.StatusDetalhe.Should().Be("accredited");
        tentativa.ValorGateway.Should().Be(150m);
        tentativa.AtualizadoEm.Should().Be(Agora);
        tentativa.Aprovada.Should().BeTrue();
    }

    [Fact]
    public void RegistrarTentativa_TentativaAprovadaReentregue_DeveContinuarAprovada()
    {
        var pagamento = new PagamentoBuilder().ComPreferencia();
        pagamento.RegistrarTentativa("111", "approved", "accredited", 150m, Agora);

        pagamento.RegistrarTentativa("111", "APPROVED", "accredited", 150m, Agora.AddMinutes(1));

        pagamento.Tentativas.Should().ContainSingle().Which.Aprovada.Should().BeTrue();
    }

    [Fact]
    public void RegistrarTentativa_TentativaNaoAprovadaQueViraAprovada_DeveAtualizar()
    {
        var pagamento = new PagamentoBuilder().ComPreferencia();
        pagamento.RegistrarTentativa("111", "in_process", null, 150m, Agora);

        pagamento.RegistrarTentativa("111", "approved", "accredited", 150m, Agora.AddMinutes(3));

        pagamento.Tentativas.Should().ContainSingle().Which.Aprovada.Should().BeTrue();
    }

    [Theory]
    [InlineData("", "approved")]
    [InlineData("  ", "approved")]
    [InlineData("111", "")]
    [InlineData("111", "  ")]
    public void RegistrarTentativa_ComIdentificadorOuStatusVazio_DeveLancarExcecao(string gatewayPaymentId, string status)
    {
        var pagamento = new PagamentoBuilder().ComPreferencia();

        var act = () => pagamento.RegistrarTentativa(gatewayPaymentId, status, null, 150m, Agora);

        act.Should().Throw<DomainException>().WithMessage("*gateway*obrigatório*");
    }

    [Theory]
    [InlineData("approved", true)]
    [InlineData("APPROVED", true)]
    [InlineData("pending", false)]
    [InlineData("", false)]
    public void EhStatusAprovado_DeveReconhecerSomenteApproved(string status, bool esperado)
    {
        TentativaPagamento.EhStatusAprovado(status).Should().Be(esperado);
    }
}
