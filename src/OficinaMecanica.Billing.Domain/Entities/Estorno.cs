using OficinaMecanica.Billing.Domain.Common;
using OficinaMecanica.Billing.Domain.Enums;

namespace OficinaMecanica.Billing.Domain.Entities;

public class Estorno : Entity
{
    public string? GatewayRefundId { get; private set; }
    public int Tentativas { get; private set; }
    public string? Erro { get; private set; }
    public StatusEstorno Status { get; private set; } = StatusEstorno.Pendente;

    // Preenchido quando o reconciliador esgotou as tentativas automáticas e passou o caso para o staff; é o que
    // faz o aviso (log e métrica) sair uma vez por abandono e não a cada ciclo. Um novo estorno (Reabrir) limpa a marca.
    public DateTimeOffset? AbandonadoEm { get; private set; }

    internal Estorno() { } // criado só pelo Pagamento; também serve ao EF Core

    internal void Concluir(string gatewayRefundId)
    {
        GatewayRefundId = gatewayRefundId;
        Erro = null;
        Status = StatusEstorno.Concluido;
    }

    internal void RegistrarFalha(string erro)
    {
        Tentativas++;
        Erro = erro;
        Status = StatusEstorno.Falhou;
    }

    internal void MarcarComoAbandonado(DateTimeOffset em) => AbandonadoEm ??= em;

    internal void Reabrir()
    {
        Status = StatusEstorno.Pendente;
        AbandonadoEm = null;
    }
}
