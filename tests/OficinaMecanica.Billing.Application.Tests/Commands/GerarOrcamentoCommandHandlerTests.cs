using FluentAssertions;
using OficinaMecanica.Billing.Application.Commands.GerarOrcamento;
using OficinaMecanica.Billing.Application.Interfaces;
using OficinaMecanica.Billing.Domain.Entities;
using OficinaMecanica.Billing.Domain.Enums;
using OficinaMecanica.Billing.Domain.Interfaces;
using OficinaMecanica.Contracts.Common;
using OficinaMecanica.Contracts.Events;

namespace OficinaMecanica.Billing.Application.Tests.Commands;

public class GerarOrcamentoCommandHandlerTests
{
    private readonly Mock<IOrcamentoRepository> _orcamentoRepositoryMock = new();
    private readonly Mock<ICancelamentoPrevioRepository> _cancelamentoPrevioRepositoryMock = new();
    private readonly Mock<IIntegrationEventPublisher> _publisherMock = new();
    private readonly Mock<IUnitOfWork> _unitOfWorkMock = new();
    private readonly RelogioFixo _relogio = new();
    private readonly GravadorDeChamadas _gravador;
    private readonly GerarOrcamentoCommandHandler _handler;

    public GerarOrcamentoCommandHandlerTests()
    {
        _gravador = new GravadorDeChamadas(_publisherMock, _unitOfWorkMock);
        _handler = new GerarOrcamentoCommandHandler(
            _orcamentoRepositoryMock.Object, _cancelamentoPrevioRepositoryMock.Object,
            _publisherMock.Object, _unitOfWorkMock.Object, _relogio);
    }

    [Fact]
    public async Task Handle_DadosValidos_DeveCriarOrcamentoEPublicarOrcamentoGerado()
    {
        var command = new GerarOrcamentoCommandBuilder().ComValorTotal(192.30m).Build();
        Orcamento? adicionado = null;
        _orcamentoRepositoryMock
            .Setup(r => r.AdicionarAsync(It.IsAny<Orcamento>(), It.IsAny<CancellationToken>()))
            .Callback<Orcamento, CancellationToken>((o, _) => adicionado = o);

        await _handler.Handle(command, CancellationToken.None);

        adicionado.Should().NotBeNull();
        adicionado!.OrdemDeServicoId.Should().Be(command.OrdemDeServicoId);
        adicionado.Numero.Should().Be(command.Numero);
        adicionado.Status.Should().Be(StatusOrcamento.Gerado);
        adicionado.ValorTotal.Should().Be(192.30m);
        adicionado.Itens.Should().HaveCount(2);
        adicionado.Itens.Select(i => i.Tipo).Should().BeEquivalentTo(new[] { TipoItemOrcamento.Servico, TipoItemOrcamento.Peca });

        var evento = _gravador.Publicadas<OrcamentoGerado>().Should().ContainSingle().Subject;
        evento.OrdemDeServicoId.Should().Be(command.OrdemDeServicoId);
        evento.OrcamentoId.Should().Be(adicionado.Id);
        evento.ValorTotal.Should().Be(192.30m);
        evento.ValidoAte.Should().Be(command.ValidoAte);
        evento.OcorridoEm.Should().Be(_relogio.GetUtcNow());
        evento.MessageId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task Handle_DadosValidos_DevePublicarAntesDeSalvar()
    {
        var command = new GerarOrcamentoCommandBuilder().Build();

        await _handler.Handle(command, CancellationToken.None);

        _gravador.Ordem.Should().Equal("Publish", "Save");
    }

    [Fact]
    public async Task Handle_OrdemDeServicoJaTemOrcamento_DeveReenviarOMesmoOrcamentoGeradoSemCriarOutro()
    {
        var existente = new OrcamentoBuilder().Gerado();
        var command = new GerarOrcamentoCommandBuilder().DaOrdemDeServico(existente.OrdemDeServicoId).Build();
        _orcamentoRepositoryMock
            .Setup(r => r.ObterPorOrdemDeServicoIdAsync(existente.OrdemDeServicoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existente);

        await _handler.Handle(command, CancellationToken.None);

        _orcamentoRepositoryMock.Verify(r => r.AdicionarAsync(It.IsAny<Orcamento>(), It.IsAny<CancellationToken>()), Times.Never());
        var evento = _gravador.Publicadas<OrcamentoGerado>().Should().ContainSingle().Subject;
        evento.OrcamentoId.Should().Be(existente.Id);
        evento.ValorTotal.Should().Be(existente.ValorTotal);
        evento.ValidoAte.Should().Be(existente.ValidoAte);
        _gravador.Ordem.Should().Equal("Publish", "Save");
    }

    [Fact]
    public async Task Handle_ReentregaDoMesmoComando_DevePublicarOrcamentoGeradoComOMesmoMessageId()
    {
        var command = new GerarOrcamentoCommandBuilder().Build();
        Orcamento? criado = null;
        _orcamentoRepositoryMock
            .Setup(r => r.AdicionarAsync(It.IsAny<Orcamento>(), It.IsAny<CancellationToken>()))
            .Callback<Orcamento, CancellationToken>((o, _) => criado = o);
        await _handler.Handle(command, CancellationToken.None);
        _orcamentoRepositoryMock
            .Setup(r => r.ObterPorOrdemDeServicoIdAsync(command.OrdemDeServicoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(criado);

        await _handler.Handle(command, CancellationToken.None);

        var eventos = _gravador.Publicadas<OrcamentoGerado>();
        eventos.Should().HaveCount(2);
        eventos[1].MessageId.Should().Be(eventos[0].MessageId);
    }

    [Fact]
    public async Task Handle_CancelamentoPrevioRegistrado_NaoDeveCriarNadaEDevePublicarOrcamentoNaoGerado()
    {
        var command = new GerarOrcamentoCommandBuilder().Build();
        _cancelamentoPrevioRepositoryMock
            .Setup(r => r.ObterAsync(command.OrdemDeServicoId, TipoCancelamentoPrevio.Orcamento, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CancelamentoPrevioBuilder().DaOrdemDeServico(command.OrdemDeServicoId).Build());

        await _handler.Handle(command, CancellationToken.None);

        _orcamentoRepositoryMock.Verify(r => r.AdicionarAsync(It.IsAny<Orcamento>(), It.IsAny<CancellationToken>()), Times.Never());
        _gravador.Publicadas<OrcamentoGerado>().Should().BeEmpty();
        var evento = _gravador.Publicadas<OrcamentoNaoGerado>().Should().ContainSingle().Subject;
        evento.OrdemDeServicoId.Should().Be(command.OrdemDeServicoId);
        evento.Motivo.Should().Be("Cancelado antes da geração do orçamento.");
        _gravador.Ordem.Should().Equal("Publish", "Save");
    }

    [Fact]
    public async Task Handle_ReentregaComCancelamentoPrevio_DevePublicarOrcamentoNaoGeradoComOMesmoMessageId()
    {
        var command = new GerarOrcamentoCommandBuilder().Build();
        _cancelamentoPrevioRepositoryMock
            .Setup(r => r.ObterAsync(command.OrdemDeServicoId, TipoCancelamentoPrevio.Orcamento, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CancelamentoPrevioBuilder().DaOrdemDeServico(command.OrdemDeServicoId).Build());

        await _handler.Handle(command, CancellationToken.None);
        await _handler.Handle(command, CancellationToken.None);

        var eventos = _gravador.Publicadas<OrcamentoNaoGerado>();
        eventos.Should().HaveCount(2);
        eventos[1].MessageId.Should().Be(eventos[0].MessageId);
    }

    [Fact]
    public async Task Handle_OrcamentoJaExisteMesmoComCancelamentoPrevio_DeveReenviarOrcamentoGerado()
    {
        var existente = new OrcamentoBuilder().Gerado();
        var command = new GerarOrcamentoCommandBuilder().DaOrdemDeServico(existente.OrdemDeServicoId).Build();
        _orcamentoRepositoryMock
            .Setup(r => r.ObterPorOrdemDeServicoIdAsync(existente.OrdemDeServicoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existente);
        _cancelamentoPrevioRepositoryMock
            .Setup(r => r.ObterAsync(existente.OrdemDeServicoId, TipoCancelamentoPrevio.Orcamento, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CancelamentoPrevioBuilder().DaOrdemDeServico(existente.OrdemDeServicoId).Build());

        await _handler.Handle(command, CancellationToken.None);

        _gravador.Publicadas<OrcamentoGerado>().Should().ContainSingle();
        _gravador.Publicadas<OrcamentoNaoGerado>().Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ValorTotalDivergenteDosItens_DevePublicarOrcamentoNaoGeradoSemLancar()
    {
        var command = new GerarOrcamentoCommandBuilder().ComValorTotal(1m).Build();

        var act = () => _handler.Handle(command, CancellationToken.None);

        await act.Should().NotThrowAsync();
        _orcamentoRepositoryMock.Verify(r => r.AdicionarAsync(It.IsAny<Orcamento>(), It.IsAny<CancellationToken>()), Times.Never());
        _gravador.Publicadas<OrcamentoGerado>().Should().BeEmpty();
        var evento = _gravador.Publicadas<OrcamentoNaoGerado>().Should().ContainSingle().Subject;
        evento.OrdemDeServicoId.Should().Be(command.OrdemDeServicoId);
        evento.Motivo.Should().Contain("valor total");
        evento.OcorridoEm.Should().Be(_relogio.GetUtcNow());
        _gravador.Ordem.Should().Equal("Publish", "Save");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task Handle_ValorTotalZeradoOuNegativo_DevePublicarOrcamentoNaoGeradoSemLancar(int valorTotal)
    {
        var command = new GerarOrcamentoCommandBuilder()
            .ComServicos(new ItemDeServicoMensagem(Guid.NewGuid(), "Cortesia", 0m, 1))
            .ComPecas()
            .ComValorTotal(valorTotal)
            .Build();

        var act = () => _handler.Handle(command, CancellationToken.None);

        await act.Should().NotThrowAsync();
        _orcamentoRepositoryMock.Verify(r => r.AdicionarAsync(It.IsAny<Orcamento>(), It.IsAny<CancellationToken>()), Times.Never());
        _gravador.Publicadas<OrcamentoNaoGerado>().Should().ContainSingle().Which.Motivo.Should().Contain("maior que zero");
        _gravador.Ordem.Should().Equal("Publish", "Save");
    }

    [Fact]
    public async Task Handle_ValorTotalComMaisDeDuasCasasDecimais_DevePublicarOrcamentoNaoGeradoSemLancar()
    {
        var command = new GerarOrcamentoCommandBuilder()
            .ComServicos(new ItemDeServicoMensagem(Guid.NewGuid(), "Alinhamento", 100m, 1))
            .ComPecas()
            .ComValorTotal(100.005m)
            .Build();

        await _handler.Handle(command, CancellationToken.None);

        _orcamentoRepositoryMock.Verify(r => r.AdicionarAsync(It.IsAny<Orcamento>(), It.IsAny<CancellationToken>()), Times.Never());
        _gravador.Publicadas<OrcamentoNaoGerado>().Should().ContainSingle().Which.Motivo.Should().Contain("duas casas decimais");
    }

    [Fact]
    public async Task Handle_PrecoDeItemComMaisDeDuasCasasDecimais_DevePublicarOrcamentoNaoGeradoSemLancar()
    {
        var command = new GerarOrcamentoCommandBuilder()
            .ComServicos(new ItemDeServicoMensagem(Guid.NewGuid(), "Alinhamento", 33.333m, 3))
            .ComPecas()
            .Build();

        await _handler.Handle(command, CancellationToken.None);

        _gravador.Publicadas<OrcamentoNaoGerado>().Should().ContainSingle().Which.Motivo.Should().Contain("preço unitário");
    }

    [Fact]
    public async Task Handle_ValoresNosMaximosDoTipo_DevePublicarOrcamentoNaoGeradoEmVezDeEstourar()
    {
        var command = new GerarOrcamentoCommandBuilder()
            .ComServicos(new ItemDeServicoMensagem(Guid.NewGuid(), "Item absurdo", decimal.MaxValue, int.MaxValue))
            .ComPecas()
            .ComValorTotal(decimal.MaxValue)
            .Build();

        var act = () => _handler.Handle(command, CancellationToken.None);

        await act.Should().NotThrowAsync();
        _orcamentoRepositoryMock.Verify(r => r.AdicionarAsync(It.IsAny<Orcamento>(), It.IsAny<CancellationToken>()), Times.Never());
        _gravador.Publicadas<OrcamentoNaoGerado>().Should().ContainSingle().Which.Motivo.Should().Contain("limite");
        _gravador.Ordem.Should().Equal("Publish", "Save");
    }

    [Fact]
    public async Task Handle_ValorTotalAcimaDoTeto_DevePublicarOrcamentoNaoGeradoSemLancar()
    {
        var command = new GerarOrcamentoCommandBuilder()
            .ComServicos(
                new ItemDeServicoMensagem(Guid.NewGuid(), "Motor completo", 1_000_000m, 1),
                new ItemDeServicoMensagem(Guid.NewGuid(), "Taxa", 0.01m, 1))
            .ComPecas()
            .Build();

        await _handler.Handle(command, CancellationToken.None);

        _gravador.Publicadas<OrcamentoNaoGerado>().Should().ContainSingle().Which.Motivo.Should().Contain("valor total");
    }

    [Fact]
    public async Task Handle_QuantidadeAcimaDoTeto_DevePublicarOrcamentoNaoGeradoSemLancar()
    {
        var command = new GerarOrcamentoCommandBuilder()
            .ComServicos(new ItemDeServicoMensagem(Guid.NewGuid(), "Parafuso", 1m, 10_001))
            .ComPecas()
            .Build();

        await _handler.Handle(command, CancellationToken.None);

        _gravador.Publicadas<OrcamentoNaoGerado>().Should().ContainSingle().Which.Motivo.Should().Contain("quantidade");
    }

    [Fact]
    public async Task Handle_ValidadeNoPassado_DevePublicarOrcamentoNaoGeradoComOMotivo()
    {
        var command = new GerarOrcamentoCommandBuilder().ValidoAte(_relogio.GetUtcNow().AddDays(-1)).Build();

        await _handler.Handle(command, CancellationToken.None);

        _gravador.Publicadas<OrcamentoNaoGerado>().Should().ContainSingle().Which.Motivo.Should().Contain("validade");
    }

    [Fact]
    public async Task Handle_SemItens_DevePublicarOrcamentoNaoGeradoComOMotivo()
    {
        var command = new GerarOrcamentoCommandBuilder().SemItens().Build();

        await _handler.Handle(command, CancellationToken.None);

        _gravador.Publicadas<OrcamentoNaoGerado>().Should().ContainSingle().Which.Motivo.Should().Contain("pelo menos um item");
    }

    [Fact]
    public async Task Handle_ItemComQuantidadeInvalida_DevePublicarOrcamentoNaoGeradoComOMotivo()
    {
        var command = new GerarOrcamentoCommandBuilder()
            .ComServicos(new ItemDeServicoMensagem(Guid.NewGuid(), "Alinhamento", 80m, 0))
            .ComPecas()
            .Build();

        await _handler.Handle(command, CancellationToken.None);

        _gravador.Publicadas<OrcamentoNaoGerado>().Should().ContainSingle().Which.Motivo.Should().Contain("quantidade");
    }

    [Fact]
    public async Task Handle_NumeroDaOrdemVazio_DevePublicarOrcamentoNaoGeradoComOMotivo()
    {
        var command = new GerarOrcamentoCommandBuilder().ComNumero("").Build();

        await _handler.Handle(command, CancellationToken.None);

        _gravador.Publicadas<OrcamentoNaoGerado>().Should().ContainSingle().Which.Motivo.Should().Contain("número");
    }

    [Fact]
    public async Task Handle_ReentregaDeEntradaInvalida_DevePublicarOrcamentoNaoGeradoComOMesmoMessageId()
    {
        var command = new GerarOrcamentoCommandBuilder().ComValorTotal(1m).Build();

        await _handler.Handle(command, CancellationToken.None);
        await _handler.Handle(command, CancellationToken.None);

        var eventos = _gravador.Publicadas<OrcamentoNaoGerado>();
        eventos.Should().HaveCount(2);
        eventos[1].MessageId.Should().Be(eventos[0].MessageId);
    }

    [Fact]
    public async Task Handle_ItensDePecaEServico_DeveGuardarOSnapshotDeCadaItem()
    {
        var command = new GerarOrcamentoCommandBuilder()
            .ComServicos(new ItemDeServicoMensagem(Guid.NewGuid(), "Troca de óleo", 120.50m, 1))
            .ComPecas(new ItemDePecaMensagem(Guid.NewGuid(), "Filtro de óleo", 35.90m, 2))
            .Build();
        Orcamento? adicionado = null;
        _orcamentoRepositoryMock
            .Setup(r => r.AdicionarAsync(It.IsAny<Orcamento>(), It.IsAny<CancellationToken>()))
            .Callback<Orcamento, CancellationToken>((o, _) => adicionado = o);

        await _handler.Handle(command, CancellationToken.None);

        adicionado!.Itens.Should().BeEquivalentTo(new[]
        {
            new { Tipo = TipoItemOrcamento.Servico, Nome = "Troca de óleo", PrecoUnitario = 120.50m, Quantidade = 1, Subtotal = 120.50m },
            new { Tipo = TipoItemOrcamento.Peca, Nome = "Filtro de óleo", PrecoUnitario = 35.90m, Quantidade = 2, Subtotal = 71.80m }
        });
    }
}
