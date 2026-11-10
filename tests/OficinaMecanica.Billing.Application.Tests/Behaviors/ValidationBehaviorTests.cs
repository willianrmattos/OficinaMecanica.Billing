using FluentAssertions;
using FluentValidation;
using MediatR;
using OficinaMecanica.Billing.Application.Behaviors;

namespace OficinaMecanica.Billing.Application.Tests.Behaviors;

public class ValidationBehaviorTests
{
    private record PedidoDeTeste(string Nome, int Quantidade) : IRequest<string>;

    private record PedidoSemRetorno(string Nome) : IRequest;

    private class NomeDoPedidoSemRetornoValidator : AbstractValidator<PedidoSemRetorno>
    {
        public NomeDoPedidoSemRetornoValidator()
        {
            RuleFor(x => x.Nome).NotEmpty().WithMessage("O nome é obrigatório.");
        }
    }

    private class NomeObrigatorioValidator : AbstractValidator<PedidoDeTeste>
    {
        public NomeObrigatorioValidator()
        {
            RuleFor(x => x.Nome).NotEmpty().WithMessage("O nome é obrigatório.");
        }
    }

    private class QuantidadePositivaValidator : AbstractValidator<PedidoDeTeste>
    {
        public QuantidadePositivaValidator()
        {
            RuleFor(x => x.Quantidade).GreaterThan(0).WithMessage("A quantidade deve ser maior que zero.");
        }
    }

    private int _chamadasDoProximo;

    private RequestHandlerDelegate<string> Proximo => () =>
    {
        _chamadasDoProximo++;
        return Task.FromResult("ok");
    };

    [Fact]
    public async Task Handle_SemValidadores_DeveChamarOProximo()
    {
        var behavior = new ValidationBehavior<PedidoDeTeste, string>(Array.Empty<IValidator<PedidoDeTeste>>());

        var resposta = await behavior.Handle(new PedidoDeTeste("", 0), Proximo, CancellationToken.None);

        resposta.Should().Be("ok");
        _chamadasDoProximo.Should().Be(1);
    }

    [Fact]
    public async Task Handle_PedidoValido_DeveChamarOProximo()
    {
        var behavior = new ValidationBehavior<PedidoDeTeste, string>(new IValidator<PedidoDeTeste>[] { new NomeObrigatorioValidator() });

        var resposta = await behavior.Handle(new PedidoDeTeste("Troca de óleo", 1), Proximo, CancellationToken.None);

        resposta.Should().Be("ok");
        _chamadasDoProximo.Should().Be(1);
    }

    [Fact]
    public async Task Handle_PedidoInvalido_DeveLancarValidationExceptionSemChamarOProximo()
    {
        var behavior = new ValidationBehavior<PedidoDeTeste, string>(new IValidator<PedidoDeTeste>[] { new NomeObrigatorioValidator() });

        var act = () => behavior.Handle(new PedidoDeTeste("", 1), Proximo, CancellationToken.None);

        var excecao = await act.Should().ThrowAsync<ValidationException>();
        excecao.Which.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Be("O nome é obrigatório.");
        _chamadasDoProximo.Should().Be(0);
    }

    [Fact]
    public async Task Handle_VariosValidadoresComFalhas_DeveAgruparTodosOsErros()
    {
        var behavior = new ValidationBehavior<PedidoDeTeste, string>(new IValidator<PedidoDeTeste>[]
        {
            new NomeObrigatorioValidator(),
            new QuantidadePositivaValidator()
        });

        var act = () => behavior.Handle(new PedidoDeTeste("", 0), Proximo, CancellationToken.None);

        var excecao = await act.Should().ThrowAsync<ValidationException>();
        excecao.Which.Errors.Select(e => e.PropertyName).Should().BeEquivalentTo(new[] { "Nome", "Quantidade" });
    }

    [Fact]
    public async Task Handle_PedidoSemRetornoInvalido_DeveLancarValidationExceptionSemChamarOProximo()
    {
        var behavior = new ValidationBehavior<PedidoSemRetorno, Unit>(new IValidator<PedidoSemRetorno>[] { new NomeDoPedidoSemRetornoValidator() });
        var chamadas = 0;

        var act = () => behavior.Handle(new PedidoSemRetorno(""), () => { chamadas++; return Task.FromResult(Unit.Value); }, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        chamadas.Should().Be(0);
    }
}
