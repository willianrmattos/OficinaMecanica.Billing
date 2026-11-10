using FluentAssertions;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Exceptions;

namespace OficinaMecanica.Billing.Domain.Tests.Entities;

public class CancelamentoPrevioTests
{
    [Theory]
    [InlineData(TipoCancelamentoPrevio.Orcamento)]
    [InlineData(TipoCancelamentoPrevio.Pagamento)]
    public void Construtor_ComDadosValidos_DeveRegistrarOCancelamento(TipoCancelamentoPrevio tipo)
    {
        var ordemId = Guid.NewGuid();

        var cancelamento = new CancelamentoPrevioBuilder()
            .DaOrdemDeServico(ordemId)
            .DoTipo(tipo)
            .ComMotivo("Cliente recusou")
            .RegistradoEm(RelogioFixo.InstanteBase)
            .Build();

        cancelamento.OrdemDeServicoId.Should().Be(ordemId);
        cancelamento.Tipo.Should().Be(tipo);
        cancelamento.Motivo.Should().Be("Cliente recusou");
        cancelamento.RegistradoEm.Should().Be(RelogioFixo.InstanteBase);
    }

    [Fact]
    public void Construtor_SemOrdemDeServico_DeveLancarExcecao()
    {
        var act = () => new CancelamentoPrevioBuilder().DaOrdemDeServico(Guid.Empty).Build();

        act.Should().Throw<DomainException>().WithMessage("*ordem de serviço*obrigatória*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Construtor_SemMotivo_DeveLancarExcecao(string motivo)
    {
        var act = () => new CancelamentoPrevioBuilder().ComMotivo(motivo).Build();

        act.Should().Throw<DomainException>().WithMessage("*motivo*obrigatório*");
    }
}
