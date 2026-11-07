using FluentAssertions;
using OficinaMecanica.Billing.Application.Commands.CancelarOrcamento;

namespace OficinaMecanica.Billing.Application.Tests.Validators;

public class CancelarOrcamentoCommandValidatorTests
{
    private readonly CancelarOrcamentoCommandValidator _validator = new();

    [Fact]
    public async Task Validate_ComOrcamentoEMotivo_DeveSerValido()
    {
        var command = new CancelarOrcamentoCommand(Guid.NewGuid(), Guid.NewGuid(), "Cliente recusou");

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_SemOrcamentoConhecido_DeveSerValidoPoisAOrdemDeServicoLocalizaOOrcamento()
    {
        var command = new CancelarOrcamentoCommand(Guid.NewGuid(), null, "Cancelamento antes do orçamento");

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_SemOrdemDeServico_DeveSerInvalido()
    {
        var command = new CancelarOrcamentoCommand(Guid.Empty, null, "Cliente recusou");

        var result = await _validator.ValidateAsync(command);

        result.Errors.Should().Contain(e => e.PropertyName == "OrdemDeServicoId" && e.ErrorMessage == "A ordem de serviço é obrigatória.");
    }

    [Fact]
    public async Task Validate_OrcamentoInformadoMasVazio_DeveSerValidoPoisOHandlerTrataComoAusente()
    {
        var command = new CancelarOrcamentoCommand(Guid.NewGuid(), Guid.Empty, "Cliente recusou");

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Validate_SemMotivo_DeveSerValidoPoisOHandlerNormalizaParaResponderASaga(string? motivo)
    {
        var command = new CancelarOrcamentoCommand(Guid.NewGuid(), null, motivo!);

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_MotivoMuitoLongo_DeveSerValidoPoisOHandlerTrunca()
    {
        var command = new CancelarOrcamentoCommand(Guid.NewGuid(), null, new string('a', 5000));

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }
}
