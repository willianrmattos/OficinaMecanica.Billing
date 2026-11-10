using FluentAssertions;
using OficinaMecanica.Billing.Application.Commands.SolicitarPagamento;

namespace OficinaMecanica.Billing.Application.Tests.Validators;

public class SolicitarPagamentoCommandValidatorTests
{
    private readonly SolicitarPagamentoCommandValidator _validator = new();

    [Fact]
    public async Task Validate_ComDadosValidos_DeveSerValido()
    {
        var command = new SolicitarPagamentoCommand(Guid.NewGuid(), Guid.NewGuid(), RelogioFixo.InstanteBase.AddDays(2));

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_SemOrdemDeServico_DeveSerInvalido()
    {
        var command = new SolicitarPagamentoCommand(Guid.Empty, Guid.NewGuid(), RelogioFixo.InstanteBase.AddDays(2));

        var result = await _validator.ValidateAsync(command);

        result.Errors.Should().Contain(e => e.PropertyName == "OrdemDeServicoId" && e.ErrorMessage == "A ordem de serviço é obrigatória.");
    }

    [Fact]
    public async Task Validate_SemOrcamento_DeveSerInvalido()
    {
        var command = new SolicitarPagamentoCommand(Guid.NewGuid(), Guid.Empty, RelogioFixo.InstanteBase.AddDays(2));

        var result = await _validator.ValidateAsync(command);

        result.Errors.Should().Contain(e => e.PropertyName == "OrcamentoId" && e.ErrorMessage == "O orçamento é obrigatório.");
    }
}
