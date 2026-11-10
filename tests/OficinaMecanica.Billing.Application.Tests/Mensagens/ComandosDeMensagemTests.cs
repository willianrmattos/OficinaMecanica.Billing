using FluentAssertions;
using OficinaMecanica.Billing.Application.Mensagens;
using OficinaMecanica.Contracts.Commands;
using OficinaMecanica.Contracts.Common;

namespace OficinaMecanica.Billing.Application.Tests.Mensagens;

public class ComandosDeMensagemTests
{
    private static readonly DateTimeOffset Instante = RelogioFixo.InstanteBase;

    [Fact]
    public void De_GerarOrcamento_DeveCopiarTodosOsCampos()
    {
        var servicos = new[] { new ItemDeServicoMensagem(Guid.NewGuid(), "Troca de óleo", 120.5m, 1) };
        var pecas = new[] { new ItemDePecaMensagem(Guid.NewGuid(), "Filtro", 35.9m, 2) };
        var mensagem = new GerarOrcamento(Guid.NewGuid(), Guid.NewGuid(), Instante, "OS-1", 192.3m, Instante.AddDays(3), "Laudo", servicos, pecas);

        var comando = ComandosDeMensagem.De(mensagem);

        comando.OrdemDeServicoId.Should().Be(mensagem.OrdemDeServicoId);
        comando.Numero.Should().Be("OS-1");
        comando.ValorTotal.Should().Be(192.3m);
        comando.ValidoAte.Should().Be(mensagem.ValidoAte);
        comando.Laudo.Should().Be("Laudo");
        comando.Servicos.Should().Equal(servicos);
        comando.Pecas.Should().Equal(pecas);
    }

    [Fact]
    public void De_SolicitarPagamento_DeveCopiarTodosOsCampos()
    {
        var mensagem = new SolicitarPagamento(Guid.NewGuid(), Guid.NewGuid(), Instante, Guid.NewGuid(), Instante.AddDays(2));

        var comando = ComandosDeMensagem.De(mensagem);

        comando.OrdemDeServicoId.Should().Be(mensagem.OrdemDeServicoId);
        comando.OrcamentoId.Should().Be(mensagem.OrcamentoId);
        comando.ExpiraEm.Should().Be(mensagem.ExpiraEm);
    }

    [Fact]
    public void De_CancelarOrcamento_DeveCopiarOrcamentoOpcionalEMotivo()
    {
        var mensagem = new CancelarOrcamento(Guid.NewGuid(), Guid.NewGuid(), Instante, null, "Cliente recusou");

        var comando = ComandosDeMensagem.De(mensagem);

        comando.OrdemDeServicoId.Should().Be(mensagem.OrdemDeServicoId);
        comando.OrcamentoId.Should().BeNull();
        comando.Motivo.Should().Be("Cliente recusou");
    }

    [Fact]
    public void De_CancelarPagamento_DeveCopiarPagamentoOpcionalEMotivo()
    {
        var pagamentoId = Guid.NewGuid();
        var mensagem = new CancelarPagamento(Guid.NewGuid(), Guid.NewGuid(), Instante, pagamentoId, "Prazo expirado");

        var comando = ComandosDeMensagem.De(mensagem);

        comando.OrdemDeServicoId.Should().Be(mensagem.OrdemDeServicoId);
        comando.PagamentoId.Should().Be(pagamentoId);
        comando.Motivo.Should().Be("Prazo expirado");
    }

    [Fact]
    public void De_EstornarPagamento_DeveCopiarOrdemPagamentoEMotivo()
    {
        var mensagem = new EstornarPagamento(Guid.NewGuid(), Guid.NewGuid(), Instante, Guid.NewGuid(), "Execução inviável");

        var comando = ComandosDeMensagem.De(mensagem);

        comando.OrdemDeServicoId.Should().Be(mensagem.OrdemDeServicoId);
        comando.PagamentoId.Should().Be(mensagem.PagamentoId);
        comando.Motivo.Should().Be("Execução inviável");
    }
}
