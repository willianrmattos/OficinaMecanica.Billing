using FluentAssertions;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Exceptions;

namespace OficinaMecanica.Billing.Domain.Tests.Entities;

public class OrcamentoTransicoesTests
{
    private static readonly DateTimeOffset Agora = RelogioFixo.InstanteBase;

    [Fact]
    public void Aprovar_OrcamentoGeradoDentroDoPrazo_DeveFicarAprovado()
    {
        var orcamento = new OrcamentoBuilder().Gerado();

        orcamento.Aprovar(Agora);

        orcamento.Status.Should().Be(StatusOrcamento.Aprovado);
    }

    [Fact]
    public void Aprovar_NoInstanteDeValidade_DeveLancarExcecaoPorExpiracao()
    {
        var orcamento = new OrcamentoBuilder().Gerado();

        var act = () => orcamento.Aprovar(orcamento.ValidoAte);

        act.Should().Throw<DomainException>().WithMessage("*expirou*");
        orcamento.Status.Should().Be(StatusOrcamento.Gerado);
    }

    [Fact]
    public void Aprovar_DepoisDaValidade_DeveLancarExcecaoPorExpiracao()
    {
        var orcamento = new OrcamentoBuilder().Gerado();

        var act = () => orcamento.Aprovar(orcamento.ValidoAte.AddMinutes(1));

        act.Should().Throw<DomainException>().WithMessage("*expirou*");
    }

    [Fact]
    public void Aprovar_OrcamentoJaAprovado_DeveLancarExcecao()
    {
        var orcamento = new OrcamentoBuilder().Aprovado();

        var act = () => orcamento.Aprovar(Agora);

        act.Should().Throw<DomainException>().WithMessage("*orçamento gerado*Aprovado*");
    }

    [Fact]
    public void Aprovar_OrcamentoCancelado_DeveLancarExcecao()
    {
        var orcamento = new OrcamentoBuilder().Cancelado();

        var act = () => orcamento.Aprovar(Agora);

        act.Should().Throw<DomainException>().WithMessage("*orçamento gerado*Cancelado*");
    }

    [Fact]
    public void MarcarComoPago_OrcamentoAprovado_DeveFicarPago()
    {
        var orcamento = new OrcamentoBuilder().Aprovado();

        orcamento.MarcarComoPago();

        orcamento.Status.Should().Be(StatusOrcamento.Pago);
    }

    [Fact]
    public void MarcarComoPago_OrcamentoApenasGerado_DeveLancarExcecao()
    {
        var orcamento = new OrcamentoBuilder().Gerado();

        var act = () => orcamento.MarcarComoPago();

        act.Should().Throw<DomainException>().WithMessage("*orçamento aprovado*Gerado*");
    }

    [Fact]
    public void Cancelar_OrcamentoGerado_DeveFicarCanceladoComMotivo()
    {
        var orcamento = new OrcamentoBuilder().Gerado();

        orcamento.Cancelar("Cliente recusou");

        orcamento.Status.Should().Be(StatusOrcamento.Cancelado);
        orcamento.MotivoCancelamento.Should().Be("Cliente recusou");
    }

    [Fact]
    public void Cancelar_OrcamentoAprovado_DeveFicarCancelado()
    {
        var orcamento = new OrcamentoBuilder().Aprovado();

        orcamento.Cancelar("Falta de estoque");

        orcamento.Status.Should().Be(StatusOrcamento.Cancelado);
    }

    [Fact]
    public void Cancelar_OrcamentoPago_DeveLancarExcecaoESeManterPago()
    {
        var orcamento = new OrcamentoBuilder().Pago();

        var act = () => orcamento.Cancelar("Tentativa tardia");

        act.Should().Throw<DomainException>().WithMessage("*já pago*");
        orcamento.Status.Should().Be(StatusOrcamento.Pago);
    }

    [Fact]
    public void Cancelar_OrcamentoJaCancelado_DeveSerIdempotenteESemSobrescreverOMotivo()
    {
        var orcamento = new OrcamentoBuilder().Cancelado("Motivo original");

        orcamento.Cancelar("Outro motivo");

        orcamento.Status.Should().Be(StatusOrcamento.Cancelado);
        orcamento.MotivoCancelamento.Should().Be("Motivo original");
    }

    [Fact]
    public void Cancelar_OrcamentoExpirado_DeveManterExpiradoSemLancar()
    {
        var orcamento = new OrcamentoBuilder().Expirado();

        orcamento.Cancelar("Prazo de aprovação vencido");

        orcamento.Status.Should().Be(StatusOrcamento.Expirado);
        orcamento.MotivoCancelamento.Should().BeNull();
    }

    [Fact]
    public void Expirar_OrcamentoGeradoComPrazoVencido_DeveFicarExpirado()
    {
        var orcamento = new OrcamentoBuilder().Gerado();

        orcamento.Expirar(orcamento.ValidoAte);

        orcamento.Status.Should().Be(StatusOrcamento.Expirado);
    }

    [Fact]
    public void Expirar_OrcamentoAprovadoComPrazoVencido_DeveFicarExpirado()
    {
        var orcamento = new OrcamentoBuilder().Aprovado();

        orcamento.Expirar(orcamento.ValidoAte.AddHours(1));

        orcamento.Status.Should().Be(StatusOrcamento.Expirado);
    }

    [Fact]
    public void Expirar_DentroDoPrazo_DeveLancarExcecao()
    {
        var orcamento = new OrcamentoBuilder().Gerado();

        var act = () => orcamento.Expirar(Agora);

        act.Should().Throw<DomainException>().WithMessage("*dentro do prazo*");
        orcamento.Status.Should().Be(StatusOrcamento.Gerado);
    }

    [Fact]
    public void Expirar_OrcamentoJaExpirado_DeveSerIdempotente()
    {
        var orcamento = new OrcamentoBuilder().Expirado();

        orcamento.Expirar(orcamento.ValidoAte.AddDays(1));

        orcamento.Status.Should().Be(StatusOrcamento.Expirado);
    }

    [Fact]
    public void Expirar_OrcamentoPago_DeveLancarExcecao()
    {
        var orcamento = new OrcamentoBuilder().Pago();

        var act = () => orcamento.Expirar(orcamento.ValidoAte.AddDays(1));

        act.Should().Throw<DomainException>().WithMessage("*gerado ou aprovado*Pago*");
    }

    [Fact]
    public void Expirar_OrcamentoCancelado_DeveLancarExcecao()
    {
        var orcamento = new OrcamentoBuilder().Cancelado();

        var act = () => orcamento.Expirar(orcamento.ValidoAte.AddDays(1));

        act.Should().Throw<DomainException>().WithMessage("*gerado ou aprovado*Cancelado*");
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void EstaExpirado_DeveComparar_OAgoraComAValidade(int segundosEmRelacaoAValidade, bool esperado)
    {
        var orcamento = new OrcamentoBuilder().Gerado();

        var expirado = orcamento.EstaExpirado(orcamento.ValidoAte.AddSeconds(segundosEmRelacaoAValidade));

        expirado.Should().Be(esperado);
    }
}
