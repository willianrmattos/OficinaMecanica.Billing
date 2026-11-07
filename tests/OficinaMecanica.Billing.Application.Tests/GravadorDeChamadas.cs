using OficinaMecanica.Billing.Application.Interfaces;
using OficinaMecanica.Billing.Domain.Interfaces;

namespace OficinaMecanica.Billing.Application.Tests;

// Registra, na ordem em que acontecem, as chamadas ao publisher (Publish e Send) e ao unit of work: é o que
// prova que toda mensagem de integração sai antes do SaveChanges que a leva ao outbox.
internal class GravadorDeChamadas
{
    private readonly List<string> _ordem = new();
    private readonly List<object> _mensagens = new();
    private readonly List<object> _publicadas = new();
    private readonly List<(string Fila, object Mensagem)> _envios = new();

    public IReadOnlyList<string> Ordem => _ordem;
    public IReadOnlyList<object> Mensagens => _mensagens;
    public IReadOnlyList<(string Fila, object Mensagem)> Envios => _envios;

    public GravadorDeChamadas(Mock<IIntegrationEventPublisher> publisher, Mock<IUnitOfWork> unitOfWork)
    {
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<It.IsAnyType>(), It.IsAny<CancellationToken>()))
            .Callback(new InvocationAction(invocation =>
            {
                _ordem.Add("Publish");
                _mensagens.Add(invocation.Arguments[0]);
                _publicadas.Add(invocation.Arguments[0]);
            }))
            .Returns(Task.CompletedTask);

        publisher
            .Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<It.IsAnyType>(), It.IsAny<CancellationToken>()))
            .Callback(new InvocationAction(invocation =>
            {
                _ordem.Add("Send");
                _mensagens.Add(invocation.Arguments[1]);
                _envios.Add(((string)invocation.Arguments[0], invocation.Arguments[1]));
            }))
            .Returns(Task.CompletedTask);

        unitOfWork
            .Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => _ordem.Add("Save"))
            .ReturnsAsync(1);
    }

    public void Registrar(string chamada) => _ordem.Add(chamada);

    public IReadOnlyList<T> Publicadas<T>() => _publicadas.OfType<T>().ToList();

    public IReadOnlyList<T> Enviadas<T>() => _envios.Select(e => e.Mensagem).OfType<T>().ToList();
}
