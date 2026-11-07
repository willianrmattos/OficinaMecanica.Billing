using FluentAssertions;
using OficinaMecanica.Billing.Application.Queries.ListarPagamentosParaReconciliacao;
using OficinaMecanica.Billing.Domain.Interfaces;

namespace OficinaMecanica.Billing.Application.Tests.Queries;

public class ListarPagamentosParaReconciliacaoQueryHandlerTests
{
    [Fact]
    public async Task Handle_DeveRepassarOLimiteAoRepositorioEDevolverOsIds()
    {
        var ids = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var repositorioMock = new Mock<IPagamentoRepository>();
        repositorioMock
            .Setup(r => r.ListarIdsParaReconciliacaoAsync(25, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ids);
        var handler = new ListarPagamentosParaReconciliacaoQueryHandler(repositorioMock.Object);

        var resultado = await handler.Handle(new ListarPagamentosParaReconciliacaoQuery(25), CancellationToken.None);

        resultado.Should().Equal(ids);
    }
}
