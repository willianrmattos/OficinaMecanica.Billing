using FluentAssertions;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Exceptions;

namespace OficinaMecanica.Billing.Domain.Tests.Entities;

public class PagamentoCancelamentoTests
{
    private static readonly DateTimeOffset Agora = RelogioFixo.InstanteBase;

    [Fact]
    public void Cancelar_PagamentoPendente_DeveFicarCanceladoComMotivo()
    {
        var pagamento = new PagamentoBuilder().ComPreferencia();

        var resultado = pagamento.Cancelar("Prazo de pagamento expirado");

        resultado.Should().Be(ResultadoCancelamentoPagamento.Cancelado);
        pagamento.Status.Should().Be(StatusPagamento.Cancelado);
        pagamento.MotivoCancelamento.Should().Be("Prazo de pagamento expirado");
    }

    [Fact]
    public void Cancelar_PagamentoPendenteSemPreferencia_DeveFicarCancelado()
    {
        var pagamento = new PagamentoBuilder().Pendente();

        var resultado = pagamento.Cancelar("Falha no gateway");

        resultado.Should().Be(ResultadoCancelamentoPagamento.Cancelado);
    }

    [Fact]
    public void Cancelar_PagamentoJaCancelado_DeveSerIdempotenteSemSobrescreverOMotivo()
    {
        var pagamento = new PagamentoBuilder().Cancelado("Motivo original");

        var resultado = pagamento.Cancelar("Outro motivo");

        resultado.Should().Be(ResultadoCancelamentoPagamento.JaCancelado);
        pagamento.MotivoCancelamento.Should().Be("Motivo original");
    }

    [Fact]
    public void Cancelar_PagamentoExpirado_DeveResponderJaCanceladoSemMudarOStatus()
    {
        var pagamento = new PagamentoBuilder().Expirado();

        var resultado = pagamento.Cancelar("Cancelamento da saga");

        resultado.Should().Be(ResultadoCancelamentoPagamento.JaCancelado);
        pagamento.Status.Should().Be(StatusPagamento.Expirado);
    }

    [Fact]
    public void Cancelar_PagamentoJaAprovado_DeveIndicarJaAprovadoSemCancelar()
    {
        var pagamento = new PagamentoBuilder().Aprovado();

        var resultado = pagamento.Cancelar("Saga tentou cancelar");

        resultado.Should().Be(ResultadoCancelamentoPagamento.JaAprovado);
        pagamento.Status.Should().Be(StatusPagamento.Aprovado);
        pagamento.MotivoCancelamento.Should().BeNull();
    }

    [Fact]
    public void Cancelar_PagamentoAprovadoEJaEstornado_DeveIndicarJaAprovado()
    {
        var pagamento = new PagamentoBuilder().Estornado();

        var resultado = pagamento.Cancelar("Saga tentou cancelar");

        resultado.Should().Be(ResultadoCancelamentoPagamento.JaAprovado);
        pagamento.Status.Should().Be(StatusPagamento.Estornado);
    }

    [Fact]
    public void Cancelar_PagamentoComAprovacaoTardiaJaEmEstorno_DeveIndicarJaCancelado()
    {
        var pagamento = new PagamentoBuilder().AprovadoAposCancelamento();
        pagamento.IniciarEstorno();

        var resultado = pagamento.Cancelar("Reentrega do cancelamento");

        resultado.Should().Be(ResultadoCancelamentoPagamento.JaCancelado);
        pagamento.Status.Should().Be(StatusPagamento.Estornando);
    }

    [Fact]
    public void Expirar_PagamentoPendenteComPrazoVencido_DeveFicarExpirado()
    {
        var pagamento = new PagamentoBuilder().ComPreferencia();

        pagamento.Expirar(pagamento.ExpiraEm);

        pagamento.Status.Should().Be(StatusPagamento.Expirado);
    }

    [Fact]
    public void Expirar_DentroDoPrazo_DeveLancarExcecao()
    {
        var pagamento = new PagamentoBuilder().ComPreferencia();

        var act = () => pagamento.Expirar(Agora);

        act.Should().Throw<DomainException>().WithMessage("*dentro do prazo*");
        pagamento.Status.Should().Be(StatusPagamento.Pendente);
    }

    [Fact]
    public void Expirar_PagamentoJaExpirado_DeveSerIdempotente()
    {
        var pagamento = new PagamentoBuilder().Expirado();

        pagamento.Expirar(pagamento.ExpiraEm.AddDays(1));

        pagamento.Status.Should().Be(StatusPagamento.Expirado);
    }

    [Fact]
    public void Expirar_PagamentoAprovado_DeveLancarExcecao()
    {
        var pagamento = new PagamentoBuilder().Aprovado();

        var act = () => pagamento.Expirar(pagamento.ExpiraEm.AddDays(1));

        act.Should().Throw<DomainException>().WithMessage("*pagamento pendente*Aprovado*");
        pagamento.Status.Should().Be(StatusPagamento.Aprovado);
    }

    [Fact]
    public void Expirar_PagamentoCancelado_DeveLancarExcecao()
    {
        var pagamento = new PagamentoBuilder().Cancelado();

        var act = () => pagamento.Expirar(pagamento.ExpiraEm.AddDays(1));

        act.Should().Throw<DomainException>().WithMessage("*pagamento pendente*Cancelado*");
    }
}
