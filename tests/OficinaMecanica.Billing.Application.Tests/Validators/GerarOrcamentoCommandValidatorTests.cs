using FluentAssertions;
using OficinaMecanica.Billing.Application.Commands.GerarOrcamento;

namespace OficinaMecanica.Billing.Application.Tests.Validators;

public class GerarOrcamentoCommandValidatorTests
{
    private readonly GerarOrcamentoCommandValidator _validator = new();

    [Fact]
    public async Task Validate_ComDadosValidos_DeveSerValido()
    {
        var command = new GerarOrcamentoCommandBuilder().Build();

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_SemOrdemDeServico_DeveSerInvalido()
    {
        var command = new GerarOrcamentoCommandBuilder().DaOrdemDeServico(Guid.Empty).Build();

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "OrdemDeServicoId" && e.ErrorMessage == "A ordem de serviço é obrigatória.");
    }

    [Fact]
    public async Task Validate_ListasAusentes_DeveSerInvalido()
    {
        var command = new GerarOrcamentoCommandBuilder().Build() with { Servicos = null!, Pecas = null! };

        var result = await _validator.ValidateAsync(command);

        result.Errors.Should().Contain(e => e.PropertyName == "Servicos" && e.ErrorMessage == "A lista de serviços é obrigatória.");
        result.Errors.Should().Contain(e => e.PropertyName == "Pecas" && e.ErrorMessage == "A lista de peças é obrigatória.");
    }

    [Fact]
    public async Task Validate_RegraDeNegocioDoOrcamento_NaoDeveSerRejeitadaAquiParaResponderASaga()
    {
        var command = new GerarOrcamentoCommandBuilder().ComValorTotal(-5m).SemItens().Build();

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }
}
