using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OficinaMecanica.Billing.API.Models;
using OficinaMecanica.Billing.Application.Commands.RefazerEstorno;
using OficinaMecanica.Billing.Application.DTOs;
using OficinaMecanica.Billing.Application.Queries.ObterPagamentoPorOrdemDeServico;

namespace OficinaMecanica.Billing.API.Controllers;

/// <summary>Consulta de pagamentos e ações do staff sobre estornos.</summary>
[ApiController]
[Route("api/pagamentos")]
[Authorize]
[Produces("application/json")]
public class PagamentosController : ControllerBase
{
    private readonly IMediator _mediator;

    public PagamentosController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>Obtém o pagamento de uma ordem de serviço, com as tentativas registradas no gateway e o estorno.</summary>
    /// <param name="ordemDeServicoId">Identificador da ordem de serviço (existe no máximo um pagamento por ordem).</param>
    /// <response code="200">Pagamento encontrado.</response>
    /// <response code="404">A ordem de serviço ainda não tem pagamento.</response>
    [HttpGet("ordem-de-servico/{ordemDeServicoId:guid}")]
    [ProducesResponseType(typeof(PagamentoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ObterPorOrdemDeServico(Guid ordemDeServicoId, CancellationToken cancellationToken)
    {
        var resultado = await _mediator.Send(new ObterPagamentoPorOrdemDeServicoQuery(ordemDeServicoId), cancellationToken);
        return resultado is not null ? Ok(resultado) : NotFound();
    }

    /// <summary>Refaz o estorno de um pagamento cujo estorno falhou.</summary>
    /// <remarks>
    /// Só vale para pagamento em <c>EstornoFalhou</c>. O estorno usa a mesma chave de idempotência das tentativas
    /// anteriores, então não há risco de devolver o valor duas vezes, e a saga é avisada do resultado.
    /// </remarks>
    /// <param name="id">Identificador do pagamento.</param>
    /// <param name="request">Motivo opcional do reprocessamento.</param>
    /// <response code="200">Estorno reprocessado; o corpo traz o estado atual do pagamento (<c>Estornado</c> ou <c>EstornoFalhou</c>).</response>
    /// <response code="400">O pagamento não está com estorno falho.</response>
    /// <response code="404">Pagamento não encontrado.</response>
    [HttpPost("{id:guid}/estornar")]
    [ProducesResponseType(typeof(PagamentoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Estornar(
        Guid id,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] RefazerEstornoRequest? request,
        CancellationToken cancellationToken)
    {
        var resultado = await _mediator.Send(new RefazerEstornoCommand(id, request?.Motivo), cancellationToken);
        return Ok(resultado);
    }
}
