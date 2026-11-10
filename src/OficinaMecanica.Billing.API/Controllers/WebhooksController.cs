using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using OficinaMecanica.Billing.API.Models;
using OficinaMecanica.Billing.Application.Commands.ProcessarWebhookPagamento;

namespace OficinaMecanica.Billing.API.Controllers;

/// <summary>Recepção das notificações do Mercado Pago.</summary>
[ApiController]
[Route("webhooks")]
[AllowAnonymous]
[Produces("application/json")]
public class WebhooksController : ControllerBase
{
    private const string TopicoDePagamento = "payment";

    private readonly IMediator _mediator;

    public WebhooksController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>Recebe a notificação de um pagamento do Mercado Pago.</summary>
    /// <remarks>
    /// Rota anônima: quem autentica a chamada é a assinatura <c>x-signature</c> (HMAC-SHA256 do manifest
    /// <c>id:&lt;data.id&gt;;request-id:&lt;x-request-id&gt;;ts:&lt;ts&gt;;</c> com o segredo do webhook). O corpo nunca é
    /// confiável: o estado real do pagamento é sempre consultado no gateway antes de qualquer ação. Notificações de
    /// outros tópicos (diferentes de <c>payment</c>) são confirmadas e ignoradas.
    /// </remarks>
    /// <param name="corpo">Corpo da notificação (<c>type</c>, <c>action</c> e <c>data.id</c>).</param>
    /// <param name="dataIdDaQuery">Parâmetro <c>data.id</c> da URL, o valor que o Mercado Pago assina; na ausência vale o do corpo.</param>
    /// <param name="tipoDaQuery">Parâmetro <c>type</c> da URL, usado quando o corpo não traz o tópico.</param>
    /// <param name="xSignature">Cabeçalho <c>x-signature</c> no formato <c>ts=...,v1=...</c>.</param>
    /// <param name="xRequestId">Cabeçalho <c>x-request-id</c>, único por entrega.</param>
    /// <response code="200">Notificação processada, repetida ou de um tópico ignorado.</response>
    /// <response code="400">Identificador do pagamento ou do pedido ausente.</response>
    /// <response code="401">Assinatura ausente, inválida ou fora da tolerância de tempo.</response>
    /// <response code="503">O gateway ainda não enxerga o pagamento notificado; o Mercado Pago deve reentregar.</response>
    [HttpPost("mercadopago")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ReceberMercadoPago(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] NotificacaoMercadoPagoRequest? corpo,
        [FromQuery(Name = "data.id")] string? dataIdDaQuery,
        [FromQuery(Name = "type")] string? tipoDaQuery,
        [FromHeader(Name = "x-signature")] string? xSignature,
        [FromHeader(Name = "x-request-id")] string? xRequestId,
        CancellationToken cancellationToken)
    {
        var tipo = corpo?.Type ?? tipoDaQuery;
        if (!string.Equals(tipo, TopicoDePagamento, StringComparison.OrdinalIgnoreCase))
            return Ok();

        var dataId = !string.IsNullOrWhiteSpace(dataIdDaQuery) ? dataIdDaQuery : corpo?.Data?.Id;

        await _mediator.Send(
            new ProcessarWebhookPagamentoCommand(dataId ?? string.Empty, xRequestId ?? string.Empty, xSignature),
            cancellationToken);

        return Ok();
    }
}
