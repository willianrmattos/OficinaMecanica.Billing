using FluentAssertions;
using OficinaMecanica.Billing.Domain.Exceptions;

namespace OficinaMecanica.Billing.Domain.Tests.Entities;

public class WebhookRecebidoTests
{
    [Fact]
    public void Construtor_ComDadosValidos_DeveNascerNaoProcessado()
    {
        var webhook = new WebhookRecebidoBuilder()
            .ComRequestId("req-123")
            .ComDataId("987654321")
            .RecebidoEm(RelogioFixo.InstanteBase)
            .Recebido();

        webhook.RequestId.Should().Be("req-123");
        webhook.DataId.Should().Be("987654321");
        webhook.RecebidoEm.Should().Be(RelogioFixo.InstanteBase);
        webhook.Processado.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Construtor_SemRequestId_DeveLancarExcecao(string requestId)
    {
        var act = () => new WebhookRecebidoBuilder().ComRequestId(requestId).Recebido();

        act.Should().Throw<DomainException>().WithMessage("*requisição*obrigatório*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Construtor_SemDataId_DeveLancarExcecao(string dataId)
    {
        var act = () => new WebhookRecebidoBuilder().ComDataId(dataId).Recebido();

        act.Should().Throw<DomainException>().WithMessage("*dado*obrigatório*");
    }

    [Fact]
    public void MarcarComoProcessado_DeveMarcarOWebhook()
    {
        var webhook = new WebhookRecebidoBuilder().Recebido();

        webhook.MarcarComoProcessado();

        webhook.Processado.Should().BeTrue();
    }

    [Fact]
    public void MarcarComoProcessado_ChamadoDuasVezes_DeveContinuarProcessado()
    {
        var webhook = new WebhookRecebidoBuilder().Processado();

        webhook.MarcarComoProcessado();

        webhook.Processado.Should().BeTrue();
    }
}
