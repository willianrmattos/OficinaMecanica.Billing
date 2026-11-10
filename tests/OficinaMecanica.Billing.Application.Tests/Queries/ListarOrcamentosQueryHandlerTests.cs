using FluentAssertions;
using OficinaMecanica.Billing.Application.Queries.ListarOrcamentos;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Interfaces;

namespace OficinaMecanica.Billing.Application.Tests.Queries;

public class ListarOrcamentosQueryHandlerTests
{
    private readonly Mock<IOrcamentoRepository> _orcamentoRepositoryMock = new();
    private readonly ListarOrcamentosQueryHandler _handler;

    public ListarOrcamentosQueryHandlerTests()
    {
        _handler = new ListarOrcamentosQueryHandler(_orcamentoRepositoryMock.Object);
    }

    [Fact]
    public async Task Handle_ComResultados_DeveRetornarPaginaComTotalETotalDePaginas()
    {
        var orcamentos = new[] { new OrcamentoBuilder().Gerado(), new OrcamentoBuilder().Aprovado() };
        _orcamentoRepositoryMock
            .Setup(r => r.ListarAsync(2, 2, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(orcamentos);
        _orcamentoRepositoryMock
            .Setup(r => r.ContarAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);

        var resultado = await _handler.Handle(new ListarOrcamentosQuery(2, 2), CancellationToken.None);

        resultado.Itens.Select(i => i.Id).Should().Equal(orcamentos.Select(o => o.Id));
        resultado.Total.Should().Be(5);
        resultado.Pagina.Should().Be(2);
        resultado.TamanhoPagina.Should().Be(2);
        resultado.TotalPaginas.Should().Be(3);
    }

    [Fact]
    public async Task Handle_ComFiltroDeStatus_DeveRepassarOFiltroAoListarEAoContar()
    {
        var cancelado = new OrcamentoBuilder().Cancelado();
        _orcamentoRepositoryMock
            .Setup(r => r.ListarAsync(1, 10, StatusOrcamento.Cancelado, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { cancelado });
        _orcamentoRepositoryMock
            .Setup(r => r.ContarAsync(StatusOrcamento.Cancelado, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var resultado = await _handler.Handle(new ListarOrcamentosQuery(FiltroStatus: StatusOrcamento.Cancelado), CancellationToken.None);

        resultado.Itens.Should().ContainSingle().Which.Status.Should().Be("Cancelado");
        resultado.TotalPaginas.Should().Be(1);
    }

    [Fact]
    public async Task Handle_SemResultados_DeveRetornarPaginaVaziaSemPaginas()
    {
        _orcamentoRepositoryMock
            .Setup(r => r.ListarAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<StatusOrcamento?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Orcamento>());

        var resultado = await _handler.Handle(new ListarOrcamentosQuery(), CancellationToken.None);

        resultado.Itens.Should().BeEmpty();
        resultado.Total.Should().Be(0);
        resultado.TotalPaginas.Should().Be(0);
    }

    [Fact]
    public void Query_SemParametros_DeveUsarPrimeiraPaginaDeDezItensSemFiltro()
    {
        var query = new ListarOrcamentosQuery();

        query.Pagina.Should().Be(1);
        query.TamanhoPagina.Should().Be(10);
        query.FiltroStatus.Should().BeNull();
    }
}
