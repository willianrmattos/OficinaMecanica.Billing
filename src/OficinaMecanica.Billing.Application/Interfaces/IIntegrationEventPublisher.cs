namespace OficinaMecanica.Billing.Application.Interfaces;

// Optei por expor Publish (evento, vários consumidores) e Send (comando, uma fila) na mesma porta: os dois
// seguem a mesma regra e só chegam ao broker junto com o SaveChanges do handler, via outbox, então ambos
// devem ser chamados antes do SaveChangesAsync.
public interface IIntegrationEventPublisher
{
    Task PublishAsync<T>(T mensagem, CancellationToken cancellationToken = default) where T : class;
    Task SendAsync<T>(string fila, T mensagem, CancellationToken cancellationToken = default) where T : class;
}
