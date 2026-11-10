using System.Collections.Concurrent;
using MassTransit;
using OficinaMecanica.Contracts.Events;

namespace OficinaMecanica.Billing.Integration.Tests.Infra;

public sealed record MensagemRecebida(Type Tipo, object Mensagem, Guid? MessageId);

// Guarda tudo o que o Billing publicou e que chegou à fila do "OS Service" de teste, na ordem de chegada.
public sealed class ColetorDeMensagens
{
    private readonly ConcurrentQueue<MensagemRecebida> _recebidas = new();

    public IReadOnlyList<MensagemRecebida> Todas => _recebidas.ToArray();

    public IReadOnlyList<T> De<T>() where T : class =>
        _recebidas.Where(m => m.Tipo == typeof(T)).Select(m => (T)m.Mensagem).ToList();

    public IReadOnlyList<T> De<T>(Guid ordemDeServicoId) where T : class
    {
        var propriedade = typeof(T).GetProperty("OrdemDeServicoId")!;
        return De<T>().Where(m => (Guid)propriedade.GetValue(m)! == ordemDeServicoId).ToList();
    }

    public IReadOnlyList<Guid?> MessageIdsDe<T>() where T : class =>
        _recebidas.Where(m => m.Tipo == typeof(T)).Select(m => m.MessageId).ToList();

    public void Escutar<T>(IReceiveEndpointConfigurator endpoint) where T : class =>
        endpoint.Handler<T>(contexto =>
        {
            _recebidas.Enqueue(new MensagemRecebida(typeof(T), contexto.Message, contexto.MessageId));
            return Task.CompletedTask;
        });

    public void EscutarTodosOsEventosDoBilling(IReceiveEndpointConfigurator endpoint)
    {
        Escutar<OrcamentoGerado>(endpoint);
        Escutar<OrcamentoNaoGerado>(endpoint);
        Escutar<PagamentoSolicitado>(endpoint);
        Escutar<PagamentoNaoSolicitado>(endpoint);
        Escutar<PagamentoAprovado>(endpoint);
        Escutar<PagamentoCancelado>(endpoint);
        Escutar<PagamentoEstornado>(endpoint);
        Escutar<EstornoFalhou>(endpoint);
    }
}
