using OficinaMecanica.Contracts.Commands;
using OficinaMecanica.Contracts.Common;
using OficinaMecanica.Contracts.Events;

namespace OficinaMecanica.Contracts.Tests;

// Valores fixos de proposito: o golden JSON so serve como trava de compatibilidade se a amostra
// que o gera for deterministica (nada de Guid.NewGuid() nem DateTime.Now aqui).
internal static class AmostrasDeContratos
{
    private static readonly Guid MensagemId = Id(1);
    private static readonly Guid OrdemId = Id(2);
    private static readonly Guid OrcamentoId = Id(3);
    private static readonly Guid PagamentoId = Id(4);
    private static readonly DateTimeOffset Ocorrido = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Prazo = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<ItemDeServicoMensagem> Servicos =
        new[] { new ItemDeServicoMensagem(Id(10), "Troca de óleo", 120.50m, 1) };

    private static readonly IReadOnlyList<ItemDePecaMensagem> Pecas =
        new[] { new ItemDePecaMensagem(Id(11), "Filtro de óleo", 35.90m, 2) };

    public static IReadOnlyDictionary<string, object> Todas { get; } = new Dictionary<string, object>
    {
        [nameof(GerarOrcamento)] = new GerarOrcamento(MensagemId, OrdemId, Ocorrido,
            "OS-20261107-ABC123", 192.30m, Prazo, "Troca de óleo e filtro recomendada", Servicos, Pecas),
        [nameof(SolicitarPagamento)] = new SolicitarPagamento(MensagemId, OrdemId, Ocorrido, OrcamentoId, Prazo),
        [nameof(CancelarOrcamento)] = new CancelarOrcamento(MensagemId, OrdemId, Ocorrido, OrcamentoId, "Cliente recusou"),
        [nameof(CancelarPagamento)] = new CancelarPagamento(MensagemId, OrdemId, Ocorrido, PagamentoId, "Prazo expirado"),
        [nameof(EstornarPagamento)] = new EstornarPagamento(MensagemId, OrdemId, Ocorrido, PagamentoId, "Execução inviável"),

        [nameof(OrcamentoGerado)] = new OrcamentoGerado(MensagemId, OrdemId, Ocorrido, OrcamentoId, 192.30m, Prazo),
        [nameof(OrcamentoNaoGerado)] = new OrcamentoNaoGerado(MensagemId, OrdemId, Ocorrido, "Valor total inválido"),
        [nameof(PagamentoSolicitado)] = new PagamentoSolicitado(MensagemId, OrdemId, Ocorrido, PagamentoId,
            "https://www.mercadopago.com.br/checkout/v1/redirect?pref_id=123", Prazo),
        [nameof(PagamentoNaoSolicitado)] = new PagamentoNaoSolicitado(MensagemId, OrdemId, Ocorrido, "Gateway indisponível"),
        [nameof(PagamentoAprovado)] = new PagamentoAprovado(MensagemId, OrdemId, Ocorrido, PagamentoId, "1234567890", 192.30m),
        [nameof(PagamentoCancelado)] = new PagamentoCancelado(MensagemId, OrdemId, Ocorrido, PagamentoId),
        [nameof(PagamentoEstornado)] = new PagamentoEstornado(MensagemId, OrdemId, Ocorrido, PagamentoId, "refund-987"),
        [nameof(EstornoFalhou)] = new EstornoFalhou(MensagemId, OrdemId, Ocorrido, PagamentoId, "Gateway recusou o estorno"),
    };

    // Variantes com os campos opcionais nulos e listas vazias: sao elas que pegam um consumidor
    // antigo quebrando com um campo ausente, o que o caminho feliz nunca mostra.
    public static IReadOnlyDictionary<string, object> ComNulos { get; } = new Dictionary<string, object>
    {
        [nameof(GerarOrcamento)] = new GerarOrcamento(MensagemId, OrdemId, Ocorrido,
            "OS-20261107-ABC123", 120.50m, Prazo, null, Servicos, Array.Empty<ItemDePecaMensagem>()),
        [nameof(CancelarOrcamento)] = new CancelarOrcamento(MensagemId, OrdemId, Ocorrido, null, "Cancelamento antes do orçamento"),
        [nameof(CancelarPagamento)] = new CancelarPagamento(MensagemId, OrdemId, Ocorrido, null, "Cancelamento antes do pagamento"),
        [nameof(PagamentoCancelado)] = new PagamentoCancelado(MensagemId, OrdemId, Ocorrido, null),
    };

    private static Guid Id(int n) => new($"00000000-0000-0000-0000-{n:D12}");
}
