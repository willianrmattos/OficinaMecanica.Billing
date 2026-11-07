using MassTransit;
using MediatR;
using OficinaMecanica.Billing.Application.Mensagens;
using OficinaMecanica.Contracts.Commands;

namespace OficinaMecanica.Billing.Infrastructure.Messaging.Consumers;

// Os consumers só traduzem o contrato no command MediatR: toda regra, validação e publicação de resposta fica no
// handler, que roda dentro da transação (e do outbox) que o MassTransit abre para a mensagem. A única coisa que
// fazem além de traduzir é pedir a exclusividade da ordem de serviço antes do handler ler qualquer coisa.

public sealed class GerarOrcamentoConsumer : IConsumer<GerarOrcamento>
{
    private readonly IMediator _mediator;
    private readonly IBloqueioPorOrdemDeServico _bloqueio;

    public GerarOrcamentoConsumer(IMediator mediator, IBloqueioPorOrdemDeServico bloqueio)
    {
        _mediator = mediator;
        _bloqueio = bloqueio;
    }

    public async Task Consume(ConsumeContext<GerarOrcamento> context)
    {
        await _bloqueio.AdquirirAsync(context.Message.OrdemDeServicoId, context.CancellationToken);
        await _mediator.Send(ComandosDeMensagem.De(context.Message), context.CancellationToken);
    }
}

public sealed class SolicitarPagamentoConsumer : IConsumer<SolicitarPagamento>
{
    private readonly IMediator _mediator;
    private readonly IBloqueioPorOrdemDeServico _bloqueio;

    public SolicitarPagamentoConsumer(IMediator mediator, IBloqueioPorOrdemDeServico bloqueio)
    {
        _mediator = mediator;
        _bloqueio = bloqueio;
    }

    public async Task Consume(ConsumeContext<SolicitarPagamento> context)
    {
        await _bloqueio.AdquirirAsync(context.Message.OrdemDeServicoId, context.CancellationToken);
        await _mediator.Send(ComandosDeMensagem.De(context.Message), context.CancellationToken);
    }
}

public sealed class CancelarOrcamentoConsumer : IConsumer<CancelarOrcamento>
{
    private readonly IMediator _mediator;
    private readonly IBloqueioPorOrdemDeServico _bloqueio;

    public CancelarOrcamentoConsumer(IMediator mediator, IBloqueioPorOrdemDeServico bloqueio)
    {
        _mediator = mediator;
        _bloqueio = bloqueio;
    }

    public async Task Consume(ConsumeContext<CancelarOrcamento> context)
    {
        await _bloqueio.AdquirirAsync(context.Message.OrdemDeServicoId, context.CancellationToken);
        await _mediator.Send(ComandosDeMensagem.De(context.Message), context.CancellationToken);
    }
}

public sealed class CancelarPagamentoConsumer : IConsumer<CancelarPagamento>
{
    private readonly IMediator _mediator;
    private readonly IBloqueioPorOrdemDeServico _bloqueio;

    public CancelarPagamentoConsumer(IMediator mediator, IBloqueioPorOrdemDeServico bloqueio)
    {
        _mediator = mediator;
        _bloqueio = bloqueio;
    }

    public async Task Consume(ConsumeContext<CancelarPagamento> context)
    {
        await _bloqueio.AdquirirAsync(context.Message.OrdemDeServicoId, context.CancellationToken);
        await _mediator.Send(ComandosDeMensagem.De(context.Message), context.CancellationToken);
    }
}

public sealed class EstornarPagamentoConsumer : IConsumer<EstornarPagamento>
{
    private readonly IMediator _mediator;
    private readonly IBloqueioPorOrdemDeServico _bloqueio;

    public EstornarPagamentoConsumer(IMediator mediator, IBloqueioPorOrdemDeServico bloqueio)
    {
        _mediator = mediator;
        _bloqueio = bloqueio;
    }

    public async Task Consume(ConsumeContext<EstornarPagamento> context)
    {
        await _bloqueio.AdquirirAsync(context.Message.OrdemDeServicoId, context.CancellationToken);
        await _mediator.Send(ComandosDeMensagem.De(context.Message), context.CancellationToken);
    }
}
