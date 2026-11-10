using OficinaMecanica.Billing.Application.Commands.CancelarOrcamento;
using OficinaMecanica.Billing.Application.Commands.CancelarPagamento;
using OficinaMecanica.Billing.Application.Commands.EstornarPagamento;
using OficinaMecanica.Billing.Application.Commands.GerarOrcamento;
using OficinaMecanica.Billing.Application.Commands.SolicitarPagamento;
using OficinaMecanica.Contracts.Commands;

namespace OficinaMecanica.Billing.Application.Mensagens;

// Mapeamento contrato -> command MediatR, o caminho inverso de MensagensDeIntegracao. Fica na Application (e
// não nos consumers) para os consumers continuarem sem regra e o mapeamento ser testável sem broker.
public static class ComandosDeMensagem
{
    public static GerarOrcamentoCommand De(GerarOrcamento mensagem) =>
        new(
            mensagem.OrdemDeServicoId, mensagem.Numero, mensagem.ValorTotal, mensagem.ValidoAte, mensagem.Laudo,
            mensagem.Servicos, mensagem.Pecas);

    public static SolicitarPagamentoCommand De(SolicitarPagamento mensagem) =>
        new(mensagem.OrdemDeServicoId, mensagem.OrcamentoId, mensagem.ExpiraEm);

    public static CancelarOrcamentoCommand De(CancelarOrcamento mensagem) =>
        new(mensagem.OrdemDeServicoId, mensagem.OrcamentoId, mensagem.Motivo);

    public static CancelarPagamentoCommand De(CancelarPagamento mensagem) =>
        new(mensagem.OrdemDeServicoId, mensagem.PagamentoId, mensagem.Motivo);

    public static EstornarPagamentoCommand De(EstornarPagamento mensagem) =>
        new(mensagem.OrdemDeServicoId, mensagem.PagamentoId, mensagem.Motivo);
}
