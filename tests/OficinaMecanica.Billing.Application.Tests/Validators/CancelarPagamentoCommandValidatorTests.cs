using FluentAssertions;
using OficinaMecanica.Billing.Application.Commands.CancelarPagamento;

namespace OficinaMecanica.Billing.Application.Tests.Validators;

public class CancelarPagamentoCommandValidatorTests
{
    private readonly CancelarPagamentoCommandValidator _validator = new();

    [Fact]
    public async Task Validate_ComPagamentoEMotivo_DeveSerValido()
    {
        var command = new CancelarPagamentoCommand(Guid.NewGuid(), Guid.NewGuid(), "Prazo expirado");

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_SemPagamentoConhecido_DeveSerValidoPoisAOrdemDeServicoLocalizaOPagamento()
    {
        var command = new CancelarPagamentoCommand(Guid.NewGuid(), null, "Cancelamento antes do pagamento");

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_SemOrdemDeServico_DeveSerInvalido()
    {
        var command = new CancelarPagamentoCommand(Guid.Empty, null, "Prazo expirado");

        var result = await _validator.ValidateAsync(command);

        result.Errors.Should().Contain(e => e.PropertyName == "OrdemDeServicoId" && e.ErrorMessage == "A ordem de serviço é obrigatória.");
    }

    [Fact]
    public async Task Validate_PagamentoInformadoMasVazio_DeveSerValidoPoisOHandlerTrataComoAusente()
    {
        var command = new CancelarPagamentoCommand(Guid.NewGuid(), Guid.Empty, "Prazo expirado");

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Validate_SemMotivo_DeveSerValidoPoisOHandlerNormalizaParaResponderASaga(string? motivo)
    {
        var command = new CancelarPagamentoCommand(Guid.NewGuid(), null, motivo!);

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_MotivoMuitoLongo_DeveSerValidoPoisOHandlerTrunca()
    {
        var command = new CancelarPagamentoCommand(Guid.NewGuid(), null, new string('a', 5000));

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }
}
