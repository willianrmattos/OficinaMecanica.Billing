using FluentAssertions;
using OficinaMecanica.Billing.Application.Commands.ProcessarWebhookPagamento;

namespace OficinaMecanica.Billing.Application.Tests.Validators;

public class ProcessarWebhookPagamentoCommandValidatorTests
{
    private readonly ProcessarWebhookPagamentoCommandValidator _validator = new();

    [Fact]
    public async Task Validate_ComDadosValidos_DeveSerValido()
    {
        var command = new ProcessarWebhookPagamentoCommand("123456", "req-1", "ts=1,v1=abc");

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_SemAssinatura_DeveSerValidoPoisAssinaturaAusenteEAssinaturaInvalida()
    {
        var command = new ProcessarWebhookPagamentoCommand("123456", "req-1", null);

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Validate_SemDataId_DeveSerInvalido(string dataId)
    {
        var command = new ProcessarWebhookPagamentoCommand(dataId, "req-1", "ts=1,v1=abc");

        var result = await _validator.ValidateAsync(command);

        result.Errors.Should().Contain(e => e.PropertyName == "DataId" && e.ErrorMessage == "O identificador do pagamento (data.id) é obrigatório.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Validate_SemRequestId_DeveSerInvalido(string requestId)
    {
        var command = new ProcessarWebhookPagamentoCommand("123456", requestId, "ts=1,v1=abc");

        var result = await _validator.ValidateAsync(command);

        result.Errors.Should().Contain(e => e.PropertyName == "XRequestId" && e.ErrorMessage == "O cabeçalho x-request-id é obrigatório.");
    }
}
