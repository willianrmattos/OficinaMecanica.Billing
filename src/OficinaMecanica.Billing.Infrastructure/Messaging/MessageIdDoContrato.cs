using System.Linq.Expressions;
using System.Reflection;

namespace OficinaMecanica.Billing.Infrastructure.Messaging;

// Lê a propriedade MessageId de um contrato sem reflexão por chamada: o delegate é compilado uma única vez por tipo
// (a classe estática genérica é o cache). Contrato sem MessageId do tipo Guid, ou com Guid.Empty, devolve null e o
// MassTransit gera o id do envelope sozinho.
internal static class MessageIdDoContrato<T> where T : class
{
    private static readonly Func<T, Guid>? Acessor = CriarAcessor();

    public static Guid? Obter(T mensagem)
    {
        var id = Acessor?.Invoke(mensagem);
        return id is { } valor && valor != Guid.Empty ? valor : null;
    }

    private static Func<T, Guid>? CriarAcessor()
    {
        var propriedade = typeof(T).GetProperty("MessageId", BindingFlags.Public | BindingFlags.Instance);
        if (propriedade is null || propriedade.PropertyType != typeof(Guid) || !propriedade.CanRead)
            return null;

        var parametro = Expression.Parameter(typeof(T), "mensagem");
        return Expression.Lambda<Func<T, Guid>>(Expression.Property(parametro, propriedade), parametro).Compile();
    }
}
