using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OficinaMecanica.Billing.Application.DTOs;
using OficinaMecanica.Billing.Application.Queries.ListarOrcamentos;
using OficinaMecanica.Billing.Application.Queries.ObterOrcamentoPorOrdemDeServico;
using OficinaMecanica.Billing.Domain.Enums;

namespace OficinaMecanica.Billing.API.Controllers;

/// <summary>Consulta de orçamentos gerados pelo fluxo da ordem de serviço.</summary>
[ApiController]
[Route("api/orcamentos")]
[Authorize]
[Produces("application/json")]
public class OrcamentosController : ControllerBase
{
    private readonly IMediator _mediator;

    public OrcamentosController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>Obtém o orçamento de uma ordem de serviço.</summary>
    /// <param name="ordemDeServicoId">Identificador da ordem de serviço (existe no máximo um orçamento por ordem).</param>
    /// <response code="200">Orçamento encontrado, com os itens em snapshot.</response>
    /// <response code="404">A ordem de serviço ainda não tem orçamento.</response>
    [HttpGet("ordem-de-servico/{ordemDeServicoId:guid}")]
    [ProducesResponseType(typeof(OrcamentoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ObterPorOrdemDeServico(Guid ordemDeServicoId, CancellationToken cancellationToken)
    {
        var resultado = await _mediator.Send(new ObterOrcamentoPorOrdemDeServicoQuery(ordemDeServicoId), cancellationToken);
        return resultado is not null ? Ok(resultado) : NotFound();
    }

    /// <summary>Lista os orçamentos de forma paginada, do mais recente para o mais antigo.</summary>
    /// <param name="pagina">Número da página, a partir de 1.</param>
    /// <param name="tamanhoPagina">Quantidade de itens por página.</param>
    /// <param name="status">Filtro opcional por status do orçamento.</param>
    /// <response code="200">Página de orçamentos.</response>
    /// <response code="400">Parâmetros de paginação inválidos.</response>
    [HttpGet]
    [ProducesResponseType(typeof(PaginacaoResultDto<OrcamentoDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Listar(
        [FromQuery] int pagina = 1,
        [FromQuery] int tamanhoPagina = 10,
        [FromQuery] StatusOrcamento? status = null,
        CancellationToken cancellationToken = default)
    {
        var resultado = await _mediator.Send(new ListarOrcamentosQuery(pagina, tamanhoPagina, status), cancellationToken);
        return Ok(resultado);
    }
}
