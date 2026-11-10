using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OficinaMecanica.Billing.Application.Commands.CancelarOrcamento;
using OficinaMecanica.Billing.Application.Commands.CancelarPagamento;
using OficinaMecanica.Billing.Application.Commands.EstornarPagamento;
using OficinaMecanica.Billing.Application.Commands.GerarOrcamento;
using OficinaMecanica.Billing.Application.Commands.ProcessarWebhookPagamento;
using OficinaMecanica.Billing.Application.Commands.SolicitarPagamento;
using OficinaMecanica.Billing.Application.DTOs;
using OficinaMecanica.Billing.Application.Interfaces;
using OficinaMecanica.Billing.Application.Queries.ListarOrcamentos;
using OficinaMecanica.Billing.Application.Queries.ObterOrcamentoPorOrdemDeServico;
using OficinaMecanica.Billing.Application.Queries.ObterPagamentoPorOrdemDeServico;
using OficinaMecanica.Billing.Domain.Interfaces;
using OficinaMecanica.Contracts.Events;

namespace OficinaMecanica.Billing.Application.Tests;

public class DependencyInjectionTests
{
    private readonly Mock<IIntegrationEventPublisher> _publisherMock = new();
    private readonly Mock<IUnitOfWork> _unitOfWorkMock = new();
    private readonly GravadorDeChamadas _gravador;

    public DependencyInjectionTests()
    {
        _gravador = new GravadorDeChamadas(_publisherMock, _unitOfWorkMock);
    }

    private ServiceProvider CriarProvedor(TimeProvider? relogio = null)
    {
        var services = new ServiceCollection();
        if (relogio is not null)
            services.AddSingleton(relogio);

        services.AddApplication();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(new Mock<IOrcamentoRepository>().Object);
        services.AddSingleton(new Mock<IPagamentoRepository>().Object);
        services.AddSingleton(new Mock<IWebhookRecebidoRepository>().Object);
        services.AddSingleton(new Mock<ICancelamentoPrevioRepository>().Object);
        services.AddSingleton(new Mock<IPagamentoGateway>().Object);
        services.AddSingleton(new Mock<IAssinaturaWebhookValidator>().Object);
        services.AddSingleton(new Mock<IMetricasDoBilling>().Object);
        services.AddSingleton(_publisherMock.Object);
        services.AddSingleton(_unitOfWorkMock.Object);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddApplication_DeveRegistrarOHandlerDeCadaCommandEQuery()
    {
        using var provedor = CriarProvedor();

        var esperados = new Dictionary<Type, Type>
        {
            [typeof(IRequestHandler<GerarOrcamentoCommand>)] = typeof(GerarOrcamentoCommandHandler),
            [typeof(IRequestHandler<SolicitarPagamentoCommand>)] = typeof(SolicitarPagamentoCommandHandler),
            [typeof(IRequestHandler<CancelarOrcamentoCommand>)] = typeof(CancelarOrcamentoCommandHandler),
            [typeof(IRequestHandler<CancelarPagamentoCommand>)] = typeof(CancelarPagamentoCommandHandler),
            [typeof(IRequestHandler<EstornarPagamentoCommand>)] = typeof(EstornarPagamentoCommandHandler),
            [typeof(IRequestHandler<ProcessarWebhookPagamentoCommand>)] = typeof(ProcessarWebhookPagamentoCommandHandler),
            [typeof(IRequestHandler<ObterOrcamentoPorOrdemDeServicoQuery, OrcamentoDto?>)] = typeof(ObterOrcamentoPorOrdemDeServicoQueryHandler),
            [typeof(IRequestHandler<ObterPagamentoPorOrdemDeServicoQuery, PagamentoDto?>)] = typeof(ObterPagamentoPorOrdemDeServicoQueryHandler),
            [typeof(IRequestHandler<ListarOrcamentosQuery, PaginacaoResultDto<OrcamentoDto>>)] = typeof(ListarOrcamentosQueryHandler)
        };

        foreach (var (servico, handler) in esperados)
            provedor.GetRequiredService(servico).Should().BeOfType(handler);
    }

    [Fact]
    public void AddApplication_DeveRegistrarOsValidatorsDosCommandsComInput()
    {
        using var provedor = CriarProvedor();

        provedor.GetRequiredService<IValidator<GerarOrcamentoCommand>>().Should().BeOfType<GerarOrcamentoCommandValidator>();
        provedor.GetRequiredService<IValidator<SolicitarPagamentoCommand>>().Should().BeOfType<SolicitarPagamentoCommandValidator>();
        provedor.GetRequiredService<IValidator<CancelarOrcamentoCommand>>().Should().BeOfType<CancelarOrcamentoCommandValidator>();
        provedor.GetRequiredService<IValidator<CancelarPagamentoCommand>>().Should().BeOfType<CancelarPagamentoCommandValidator>();
        provedor.GetRequiredService<IValidator<EstornarPagamentoCommand>>().Should().BeOfType<EstornarPagamentoCommandValidator>();
        provedor.GetRequiredService<IValidator<ProcessarWebhookPagamentoCommand>>().Should().BeOfType<ProcessarWebhookPagamentoCommandValidator>();
        provedor.GetRequiredService<IValidator<ListarOrcamentosQuery>>().Should().BeOfType<ListarOrcamentosQueryValidator>();
    }

    [Fact]
    public async Task AddApplication_ComandoInvalido_DeveSerBarradoPeloValidationBehaviorAntesDoHandler()
    {
        using var provedor = CriarProvedor();
        var mediator = provedor.GetRequiredService<IMediator>();

        var act = () => mediator.Send(new GerarOrcamentoCommandBuilder().DaOrdemDeServico(Guid.Empty).Build());

        await act.Should().ThrowAsync<ValidationException>();
        _gravador.Ordem.Should().BeEmpty();
    }

    [Fact]
    public async Task AddApplication_ComandoValido_DeveChegarAoHandlerEPublicarAntesDeSalvar()
    {
        using var provedor = CriarProvedor();
        var mediator = provedor.GetRequiredService<IMediator>();

        await mediator.Send(new GerarOrcamentoCommandBuilder().Build());

        _gravador.Publicadas<OrcamentoGerado>().Should().ContainSingle();
        _gravador.Ordem.Should().Equal("Publish", "Save");
    }

    [Fact]
    public void AddApplication_SemTimeProviderRegistrado_DeveUsarORelogioDoSistema()
    {
        using var provedor = CriarProvedor();

        provedor.GetRequiredService<TimeProvider>().Should().BeSameAs(TimeProvider.System);
    }

    [Fact]
    public void AddApplication_ComTimeProviderJaRegistrado_NaoDeveSubstituirOExistente()
    {
        var relogio = new RelogioFixo();
        using var provedor = CriarProvedor(relogio);

        provedor.GetRequiredService<TimeProvider>().Should().BeSameAs(relogio);
    }
}
