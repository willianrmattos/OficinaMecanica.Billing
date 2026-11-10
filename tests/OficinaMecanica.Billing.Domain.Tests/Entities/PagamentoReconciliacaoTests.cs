using FluentAssertions;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Exceptions;

namespace OficinaMecanica.Billing.Domain.Tests.Entities;

public class PagamentoReconciliacaoTests
{
    [Fact]
    public void EstornoPendente_PagamentoEstornando_DeveSerVerdadeiro()
    {
        new PagamentoBuilder().Estornando().EstornoPendente.Should().BeTrue();
    }

    [Fact]
    public void EstornoPendente_PagamentoComEstornoFalho_DeveSerVerdadeiro()
    {
        new PagamentoBuilder().ComEstornoFalho().EstornoPendente.Should().BeTrue();
    }

    [Fact]
    public void EstornoPendente_AprovacaoTardiaAposCancelamento_DeveSerVerdadeiro()
    {
        new PagamentoBuilder().AprovadoAposCancelamento().EstornoPendente.Should().BeTrue();
    }

    [Fact]
    public void EstornoPendente_AprovacaoTardiaAposExpiracao_DeveSerVerdadeiro()
    {
        new PagamentoBuilder().AprovadoAposExpiracao().EstornoPendente.Should().BeTrue();
    }

    [Fact]
    public void EstornoPendente_PagamentoPendente_DeveSerFalso()
    {
        new PagamentoBuilder().ComPreferencia().EstornoPendente.Should().BeFalse();
    }

    [Fact]
    public void EstornoPendente_PagamentoAprovadoNormal_DeveSerFalso()
    {
        new PagamentoBuilder().Aprovado().EstornoPendente.Should().BeFalse();
    }

    [Fact]
    public void EstornoPendente_PagamentoCanceladoSemAprovacaoTardia_DeveSerFalso()
    {
        new PagamentoBuilder().Cancelado().EstornoPendente.Should().BeFalse();
    }

    [Fact]
    public void EstornoPendente_PagamentoJaEstornado_DeveSerFalso()
    {
        new PagamentoBuilder().Estornado().EstornoPendente.Should().BeFalse();
    }

    [Fact]
    public void EstornoPendente_AprovacaoTardiaJaEstornada_DeveSerFalso()
    {
        var pagamento = new PagamentoBuilder().AprovadoAposCancelamento();
        pagamento.IniciarEstorno();
        pagamento.ConfirmarEstorno("refund-1");

        pagamento.EstornoPendente.Should().BeFalse();
    }

    [Fact]
    public void AprovacoesDuplicadasSemEstorno_PagamentoSemDuplicidade_DeveSerVazio()
    {
        new PagamentoBuilder().Aprovado().AprovacoesDuplicadasSemEstorno().Should().BeEmpty();
    }

    [Fact]
    public void AprovacoesDuplicadasSemEstorno_PagamentoSemAprovacaoAdotada_DeveSerVazio()
    {
        var pagamento = new PagamentoBuilder().ComPreferencia();
        pagamento.RegistrarTentativa("123", "approved", "accredited", pagamento.Valor, RelogioFixo.InstanteBase);

        pagamento.AprovacoesDuplicadasSemEstorno().Should().BeEmpty();
    }

    [Fact]
    public void AprovacoesDuplicadasSemEstorno_SegundaAprovacao_DeveListarSoAPagaEmDuplicidade()
    {
        var pagamento = new PagamentoBuilder().ComGatewayPaymentId("1000").AprovadoComAprovacaoDuplicada("2000");

        var duplicadas = pagamento.AprovacoesDuplicadasSemEstorno();

        duplicadas.Should().ContainSingle().Which.GatewayPaymentId.Should().Be("2000");
        pagamento.GatewayPaymentIdAprovado.Should().Be("1000");
    }

    [Fact]
    public void AprovacoesDuplicadasSemEstorno_TentativaDuplicadaNaoAprovada_NaoDeveListar()
    {
        var pagamento = new PagamentoBuilder().Aprovado();
        pagamento.RegistrarTentativa("3000", "rejected", "cc_rejected_other_reason", pagamento.Valor, RelogioFixo.InstanteBase);

        pagamento.AprovacoesDuplicadasSemEstorno().Should().BeEmpty();
    }

    [Fact]
    public void RegistrarEstornoDeAprovacaoDuplicada_AprovacaoDuplicada_DeveGuardarOEstornoEDeixarDeListar()
    {
        var pagamento = new PagamentoBuilder().AprovadoComAprovacaoDuplicada("2000");

        pagamento.RegistrarEstornoDeAprovacaoDuplicada("2000", "refund-dup-1");

        pagamento.Tentativas.Single(t => t.GatewayPaymentId == "2000").GatewayRefundId.Should().Be("refund-dup-1");
        pagamento.AprovacoesDuplicadasSemEstorno().Should().BeEmpty();
        pagamento.Status.Should().Be(StatusPagamento.Aprovado);
    }

    [Fact]
    public void RegistrarEstornoDeAprovacaoDuplicada_ChamadaRepetida_DeveManterOPrimeiroEstorno()
    {
        var pagamento = new PagamentoBuilder().AprovadoComAprovacaoDuplicada("2000");
        pagamento.RegistrarEstornoDeAprovacaoDuplicada("2000", "refund-dup-1");

        pagamento.RegistrarEstornoDeAprovacaoDuplicada("2000", "refund-dup-2");

        pagamento.Tentativas.Single(t => t.GatewayPaymentId == "2000").GatewayRefundId.Should().Be("refund-dup-1");
    }

    [Fact]
    public void RegistrarEstornoDeAprovacaoDuplicada_EstornoVazio_DeveLancarDomainException()
    {
        var pagamento = new PagamentoBuilder().AprovadoComAprovacaoDuplicada("2000");

        var act = () => pagamento.RegistrarEstornoDeAprovacaoDuplicada("2000", " ");

        act.Should().Throw<DomainException>().WithMessage("*estorno*");
    }

    [Fact]
    public void RegistrarEstornoDeAprovacaoDuplicada_TentativaInexistente_DeveLancarDomainException()
    {
        var pagamento = new PagamentoBuilder().AprovadoComAprovacaoDuplicada("2000");

        var act = () => pagamento.RegistrarEstornoDeAprovacaoDuplicada("9999", "refund-x");

        act.Should().Throw<DomainException>().WithMessage("*não existe*");
    }

    [Fact]
    public void RegistrarEstornoDeAprovacaoDuplicada_PagamentoAdotado_DeveLancarDomainException()
    {
        var pagamento = new PagamentoBuilder().ComGatewayPaymentId("1000").AprovadoComAprovacaoDuplicada("2000");

        var act = () => pagamento.RegistrarEstornoDeAprovacaoDuplicada("1000", "refund-x");

        act.Should().Throw<DomainException>().WithMessage("*duplicada*");
    }

    [Fact]
    public void RegistrarEstornoDeAprovacaoDuplicada_TentativaNaoAprovada_DeveLancarDomainException()
    {
        var pagamento = new PagamentoBuilder().Aprovado();
        pagamento.RegistrarTentativa("3000", "rejected", null, pagamento.Valor, RelogioFixo.InstanteBase);

        var act = () => pagamento.RegistrarEstornoDeAprovacaoDuplicada("3000", "refund-x");

        act.Should().Throw<DomainException>().WithMessage("*duplicada*");
    }

    [Fact]
    public void AbandonarEstorno_PrimeiraVez_DeveMarcarERetornarVerdadeiro()
    {
        var pagamento = new PagamentoBuilder().ComEstornoFalho();
        var agora = RelogioFixo.InstanteBase;

        var transicao = pagamento.AbandonarEstorno(agora);

        transicao.Should().BeTrue();
        pagamento.EstornoAbandonado.Should().BeTrue();
        pagamento.Estorno!.AbandonadoEm.Should().Be(agora);
    }

    [Fact]
    public void AbandonarEstorno_SegundaVez_DeveRetornarFalsoEManterAPrimeiraData()
    {
        var pagamento = new PagamentoBuilder().ComEstornoFalho();
        pagamento.AbandonarEstorno(RelogioFixo.InstanteBase);

        var transicao = pagamento.AbandonarEstorno(RelogioFixo.InstanteBase.AddHours(1));

        transicao.Should().BeFalse();
        pagamento.Estorno!.AbandonadoEm.Should().Be(RelogioFixo.InstanteBase);
    }

    [Fact]
    public void AbandonarEstorno_SemEstornoIniciado_DeveLancarDomainException()
    {
        var pagamento = new PagamentoBuilder().AprovadoAposCancelamento();

        var act = () => pagamento.AbandonarEstorno(RelogioFixo.InstanteBase);

        act.Should().Throw<DomainException>().WithMessage("*não foi iniciado*");
    }

    [Fact]
    public void IniciarEstorno_DepoisDeAbandonado_DeveReabrirELimparAMarca()
    {
        var pagamento = new PagamentoBuilder().ComEstornoFalho();
        pagamento.AbandonarEstorno(RelogioFixo.InstanteBase);

        pagamento.IniciarEstorno();

        pagamento.EstornoAbandonado.Should().BeFalse();
        pagamento.Estorno!.AbandonadoEm.Should().BeNull();
        pagamento.Status.Should().Be(StatusPagamento.Estornando);
    }

    [Fact]
    public void EstornoAbandonado_SemEstorno_DeveSerFalso()
    {
        new PagamentoBuilder().Aprovado().EstornoAbandonado.Should().BeFalse();
    }
}
