using FluentAssertions;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Exceptions;

namespace OficinaMecanica.Billing.Domain.Tests.Entities;

public class PagamentoEstornoTests
{
    [Fact]
    public void IniciarEstorno_PagamentoAprovado_DeveFicarEstornandoEDevolverOPagamentoDoGateway()
    {
        var pagamento = new PagamentoBuilder().ComGatewayPaymentId("999").Aprovado();

        var gatewayPaymentId = pagamento.IniciarEstorno();

        gatewayPaymentId.Should().Be("999");
        pagamento.Status.Should().Be(StatusPagamento.Estornando);
        pagamento.Estorno.Should().NotBeNull();
        pagamento.Estorno!.Status.Should().Be(StatusEstorno.Pendente);
        pagamento.Estorno.Tentativas.Should().Be(0);
        pagamento.Estorno.GatewayRefundId.Should().BeNull();
    }

    [Fact]
    public void IniciarEstorno_AprovacaoTardiaAposCancelamento_DeveFicarEstornandoMantendoAMarcaDeAprovacaoTardia()
    {
        var pagamento = new PagamentoBuilder().ComGatewayPaymentId("777").AprovadoAposCancelamento();

        var gatewayPaymentId = pagamento.IniciarEstorno();

        gatewayPaymentId.Should().Be("777");
        pagamento.Status.Should().Be(StatusPagamento.Estornando);
        pagamento.AprovacaoTardia.Should().BeTrue();
    }

    [Fact]
    public void IniciarEstorno_AprovacaoTardiaAposExpiracao_DeveFicarEstornando()
    {
        var pagamento = new PagamentoBuilder().AprovadoAposExpiracao();

        pagamento.IniciarEstorno();

        pagamento.Status.Should().Be(StatusPagamento.Estornando);
    }

    [Fact]
    public void IniciarEstorno_PagamentoJaEstornando_DeveRetomarSemCriarOutroEstorno()
    {
        var pagamento = new PagamentoBuilder().ComGatewayPaymentId("999").Estornando();
        var estornoOriginal = pagamento.Estorno;

        var gatewayPaymentId = pagamento.IniciarEstorno();

        gatewayPaymentId.Should().Be("999");
        pagamento.Status.Should().Be(StatusPagamento.Estornando);
        pagamento.Estorno.Should().BeSameAs(estornoOriginal);
    }

    [Fact]
    public void IniciarEstorno_AposFalhaAnterior_DeveReabrirOMesmoEstornoPreservandoAsTentativas()
    {
        var pagamento = new PagamentoBuilder().ComEstornoFalho();
        var estornoOriginal = pagamento.Estorno;

        pagamento.IniciarEstorno();

        pagamento.Status.Should().Be(StatusPagamento.Estornando);
        pagamento.Estorno.Should().BeSameAs(estornoOriginal);
        pagamento.Estorno!.Status.Should().Be(StatusEstorno.Pendente);
        pagamento.Estorno.Tentativas.Should().Be(1);
    }

    [Fact]
    public void IniciarEstorno_PagamentoPendente_DeveLancarExcecao()
    {
        var pagamento = new PagamentoBuilder().ComPreferencia();

        var act = () => pagamento.IniciarEstorno();

        act.Should().Throw<DomainException>().WithMessage("*aprovado no gateway*");
        pagamento.Status.Should().Be(StatusPagamento.Pendente);
    }

    [Fact]
    public void IniciarEstorno_PagamentoCanceladoSemAprovacaoTardia_DeveLancarExcecao()
    {
        var pagamento = new PagamentoBuilder().Cancelado();

        var act = () => pagamento.IniciarEstorno();

        act.Should().Throw<DomainException>().WithMessage("*aprovado no gateway*");
    }

    [Fact]
    public void IniciarEstorno_PagamentoJaEstornado_DeveLancarExcecao()
    {
        var pagamento = new PagamentoBuilder().Estornado();

        var act = () => pagamento.IniciarEstorno();

        act.Should().Throw<DomainException>().WithMessage("*já foi estornado*");
    }

    [Fact]
    public void ConfirmarEstorno_PagamentoEstornando_DeveFicarEstornadoComOIdDoReembolso()
    {
        var pagamento = new PagamentoBuilder().Estornando();

        pagamento.ConfirmarEstorno("refund-987");

        pagamento.Status.Should().Be(StatusPagamento.Estornado);
        pagamento.Estorno!.GatewayRefundId.Should().Be("refund-987");
        pagamento.Estorno.Status.Should().Be(StatusEstorno.Concluido);
        pagamento.Estorno.Erro.Should().BeNull();
    }

    [Fact]
    public void ConfirmarEstorno_AposFalhaEReabertura_DeveLimparOErroDaTentativaAnterior()
    {
        var pagamento = new PagamentoBuilder().ComEstornoFalho("Gateway fora do ar");
        pagamento.IniciarEstorno();

        pagamento.ConfirmarEstorno("refund-987");

        pagamento.Estorno!.Erro.Should().BeNull();
        pagamento.Estorno.Tentativas.Should().Be(1);
        pagamento.Status.Should().Be(StatusPagamento.Estornado);
    }

    [Fact]
    public void ConfirmarEstorno_PagamentoJaEstornado_DeveSerIdempotenteSemTrocarOIdDoReembolso()
    {
        var pagamento = new PagamentoBuilder().Estornado("refund-original");

        pagamento.ConfirmarEstorno("refund-outro");

        pagamento.Status.Should().Be(StatusPagamento.Estornado);
        pagamento.Estorno!.GatewayRefundId.Should().Be("refund-original");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ConfirmarEstorno_SemIdDoReembolso_DeveLancarExcecao(string gatewayRefundId)
    {
        var pagamento = new PagamentoBuilder().Estornando();

        var act = () => pagamento.ConfirmarEstorno(gatewayRefundId);

        act.Should().Throw<DomainException>().WithMessage("*estorno no gateway*obrigatório*");
        pagamento.Status.Should().Be(StatusPagamento.Estornando);
    }

    [Fact]
    public void ConfirmarEstorno_PagamentoAprovadoSemEstornoIniciado_DeveLancarExcecao()
    {
        var pagamento = new PagamentoBuilder().Aprovado();

        var act = () => pagamento.ConfirmarEstorno("refund-1");

        act.Should().Throw<DomainException>().WithMessage("*em estorno*Aprovado*");
    }

    [Fact]
    public void FalharEstorno_PagamentoEstornando_DeveFicarComEstornoFalhouEContarATentativa()
    {
        var pagamento = new PagamentoBuilder().Estornando();

        pagamento.FalharEstorno("Gateway indisponível");

        pagamento.Status.Should().Be(StatusPagamento.EstornoFalhou);
        pagamento.Estorno!.Status.Should().Be(StatusEstorno.Falhou);
        pagamento.Estorno.Erro.Should().Be("Gateway indisponível");
        pagamento.Estorno.Tentativas.Should().Be(1);
    }

    [Fact]
    public void FalharEstorno_FalhasEmTentativasSeguidas_DeveIncrementarAsTentativas()
    {
        var pagamento = new PagamentoBuilder().ComEstornoFalho("Primeira falha");
        pagamento.IniciarEstorno();

        pagamento.FalharEstorno("Segunda falha");

        pagamento.Estorno!.Tentativas.Should().Be(2);
        pagamento.Estorno.Erro.Should().Be("Segunda falha");
        pagamento.Status.Should().Be(StatusPagamento.EstornoFalhou);
    }

    [Fact]
    public void FalharEstorno_PagamentoAprovadoSemEstornoIniciado_DeveLancarExcecao()
    {
        var pagamento = new PagamentoBuilder().Aprovado();

        var act = () => pagamento.FalharEstorno("erro");

        act.Should().Throw<DomainException>().WithMessage("*em estorno*Aprovado*");
    }

    [Fact]
    public void FalharEstorno_PagamentoJaEstornado_DeveLancarExcecaoSemDesfazerOEstorno()
    {
        var pagamento = new PagamentoBuilder().Estornado();

        var act = () => pagamento.FalharEstorno("erro tardio");

        act.Should().Throw<DomainException>();
        pagamento.Status.Should().Be(StatusPagamento.Estornado);
    }
}
