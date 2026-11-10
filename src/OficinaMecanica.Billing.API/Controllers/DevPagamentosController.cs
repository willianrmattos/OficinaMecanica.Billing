using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OficinaMecanica.Billing.Application.Commands.ProcessarWebhookPagamento;
using OficinaMecanica.Billing.Infrastructure.Gateways;
using OficinaMecanica.Billing.Infrastructure.Options;
using OficinaMecanica.Billing.Infrastructure.Security;

namespace OficinaMecanica.Billing.API.Controllers;

/// <summary>Simulação do checkout do Mercado Pago, só em Development com o gateway Fake.</summary>
/// <remarks>
/// Este controller só existe no processo quando <c>Pagamento:Provider=Fake</c> e o ambiente é Development
/// (ver <c>ControllersDeDevFeatureProvider</c>): em qualquer outra configuração a rota nem é registrada.
/// </remarks>
[ApiController]
[Route("dev/pagamentos")]
[AllowAnonymous]
[Produces("application/json")]
public class DevPagamentosController : ControllerBase
{
    private readonly FakePagamentoGateway _gateway;
    private readonly IMediator _mediator;
    private readonly OpcoesDoGatewayFake _opcoes;
    private readonly TimeProvider _timeProvider;

    public DevPagamentosController(
        FakePagamentoGateway gateway,
        IMediator mediator,
        IOptions<OpcoesDoGatewayFake> opcoes,
        TimeProvider timeProvider)
    {
        _gateway = gateway;
        _mediator = mediator;
        _opcoes = opcoes.Value;
        _timeProvider = timeProvider;
    }

    /// <summary>Simula o cliente pagando e o Mercado Pago aprovando o pagamento.</summary>
    /// <remarks>
    /// Cria um pagamento aprovado no gateway fake e dispara o webhook pelo mesmo command da rota real, com uma
    /// assinatura válida calculada com <c>Fake:WebhookSecret</c>, então o validador de assinatura de verdade é exercitado.
    /// </remarks>
    /// <param name="preferenceId">Identificador da preferência, devolvido na criação do pagamento (<c>fake-pref-...</c>).</param>
    /// <response code="200">Pagamento simulado e webhook processado.</response>
    /// <response code="404">Preferência desconhecida pelo gateway fake.</response>
    [HttpPost("{preferenceId}/aprovar")]
    [ProducesResponseType(typeof(SimulacaoDePagamentoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Aprovar(string preferenceId, CancellationToken cancellationToken) =>
        SimularAsync(() => _gateway.AprovarPreferencia(preferenceId), cancellationToken);

    /// <summary>Simula o Mercado Pago recusando a tentativa de pagamento (o cartão foi rejeitado).</summary>
    /// <remarks>
    /// Uma recusa não encerra o pagamento: a preferência continua aceitando novas tentativas até o prazo.
    /// </remarks>
    /// <param name="preferenceId">Identificador da preferência, devolvido na criação do pagamento (<c>fake-pref-...</c>).</param>
    /// <response code="200">Tentativa recusada simulada e webhook processado.</response>
    /// <response code="404">Preferência desconhecida pelo gateway fake.</response>
    [HttpPost("{preferenceId}/recusar")]
    [ProducesResponseType(typeof(SimulacaoDePagamentoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Recusar(string preferenceId, CancellationToken cancellationToken) =>
        SimularAsync(() => _gateway.RecusarPreferencia(preferenceId), cancellationToken);

    private async Task<IActionResult> SimularAsync(
        Func<Application.DTOs.PagamentoGatewayDto> registrarNoGateway,
        CancellationToken cancellationToken)
    {
        var pagamentoDoGateway = registrarNoGateway();
        var requestId = Guid.NewGuid().ToString();
        var assinatura = AssinaturaWebhookMercadoPago.GerarCabecalho(
            _opcoes.WebhookSecret!, pagamentoDoGateway.Id, requestId, _timeProvider.GetUtcNow());

        await _mediator.Send(new ProcessarWebhookPagamentoCommand(pagamentoDoGateway.Id, requestId, assinatura), cancellationToken);

        return Ok(new SimulacaoDePagamentoResponse(pagamentoDoGateway.Id, pagamentoDoGateway.Status, requestId));
    }
}

/// <summary>Resultado da simulação de um pagamento no gateway fake.</summary>
/// <param name="GatewayPaymentId">Identificador do pagamento criado no gateway fake.</param>
/// <param name="Status">Status do pagamento no gateway (<c>approved</c> ou <c>rejected</c>).</param>
/// <param name="RequestId">Valor usado em <c>x-request-id</c> no webhook disparado.</param>
public record SimulacaoDePagamentoResponse(string GatewayPaymentId, string Status, string RequestId);
