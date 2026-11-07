using FluentAssertions;
using OficinaMecanica.Billing.Application.Commands.EstornarPagamento;

namespace OficinaMecanica.Billing.Application.Tests.Validators;

public class EstornarPagamentoCommandValidatorTests
{
    private readonly EstornarPagamentoCommandValidator _validator = new();

    [Fact]
    public async Task Validate_ComDadosValidos_DeveSerValido()
    {
        var command = new EstornarPagamentoCommand(Guid.NewGuid(), Guid.NewGuid(), "Execução inviável");

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_PagamentoVazio_DeveSerValidoPoisOHandlerRespondeEstornoFalhou()
    {
        var command = new EstornarPagamentoCommand(Guid.NewGuid(), Guid.Empty, "Execução inviável");

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_SemOrdemDeServico_DeveSerInvalido()
    {
        var command = new EstornarPagamentoCommand(Guid.Empty, Guid.NewGuid(), "Execução inviável");

        var result = await _validator.ValidateAsync(command);

        result.Errors.Should().Contain(e => e.PropertyName == "OrdemDeServicoId" && e.ErrorMessage == "A ordem de serviço é obrigatória.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Validate_SemMotivo_DeveSerValidoPoisOHandlerNormalizaParaResponderASaga(string motivo)
    {
        var command = new EstornarPagamentoCommand(Guid.NewGuid(), Guid.NewGuid(), motivo);

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_MotivoMuitoLongo_DeveSerValidoPoisOHandlerTrunca()
    {
        var command = new EstornarPagamentoCommand(Guid.NewGuid(), Guid.NewGuid(), new string('a', 5000));

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }
}
