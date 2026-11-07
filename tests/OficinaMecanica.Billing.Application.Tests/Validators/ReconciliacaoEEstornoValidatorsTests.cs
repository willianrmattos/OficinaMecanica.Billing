using FluentAssertions;
using OficinaMecanica.Billing.Application.Commands.ReconciliarPagamento;
using OficinaMecanica.Billing.Application.Commands.RefazerEstorno;
using OficinaMecanica.Billing.Application.Queries.ListarPagamentosParaReconciliacao;

namespace OficinaMecanica.Billing.Application.Tests.Validators;

public class ReconciliacaoEEstornoValidatorsTests
{
    [Fact]
    public async Task ReconciliarPagamento_ComDadosValidos_DeveSerValido()
    {
        var resultado = await new ReconciliarPagamentoCommandValidator().ValidateAsync(new ReconciliarPagamentoCommand(Guid.NewGuid(), 10));

        resultado.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task ReconciliarPagamento_PagamentoVazio_DeveSerInvalido()
    {
        var resultado = await new ReconciliarPagamentoCommandValidator().ValidateAsync(new ReconciliarPagamentoCommand(Guid.Empty, 10));

        resultado.Errors.Should().Contain(e => e.PropertyName == "PagamentoId" && e.ErrorMessage == "O pagamento é obrigatório.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ReconciliarPagamento_MaximoDeTentativasNaoPositivo_DeveSerInvalido(int maximo)
    {
        var resultado = await new ReconciliarPagamentoCommandValidator().ValidateAsync(new ReconciliarPagamentoCommand(Guid.NewGuid(), maximo));

        resultado.Errors.Should().Contain(e => e.PropertyName == "MaximoDeTentativasDeEstorno");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public async Task ListarParaReconciliacao_LimiteNaFaixa_DeveSerValido(int limite)
    {
        var resultado = await new ListarPagamentosParaReconciliacaoQueryValidator().ValidateAsync(new ListarPagamentosParaReconciliacaoQuery(limite));

        resultado.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task ListarParaReconciliacao_LimiteForaDaFaixa_DeveSerInvalido(int limite)
    {
        var resultado = await new ListarPagamentosParaReconciliacaoQueryValidator().ValidateAsync(new ListarPagamentosParaReconciliacaoQuery(limite));

        resultado.Errors.Should().Contain(e => e.PropertyName == "Limite");
    }

    [Fact]
    public async Task RefazerEstorno_ComDadosValidos_DeveSerValido()
    {
        var resultado = await new RefazerEstornoCommandValidator().ValidateAsync(new RefazerEstornoCommand(Guid.NewGuid(), "Motivo"));

        resultado.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task RefazerEstorno_PagamentoVazio_DeveSerInvalido()
    {
        var resultado = await new RefazerEstornoCommandValidator().ValidateAsync(new RefazerEstornoCommand(Guid.Empty, null));

        resultado.Errors.Should().Contain(e => e.PropertyName == "PagamentoId");
    }

    [Fact]
    public async Task RefazerEstorno_MotivoMuitoLongo_DeveSerInvalido()
    {
        var resultado = await new RefazerEstornoCommandValidator().ValidateAsync(new RefazerEstornoCommand(Guid.NewGuid(), new string('x', 501)));

        resultado.Errors.Should().Contain(e => e.PropertyName == "Motivo");
    }
}
