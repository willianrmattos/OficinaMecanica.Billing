using FluentValidation;
using MediatR;

namespace OficinaMecanica.Billing.Application.Behaviors;

// Optei por restringir TRequest a notnull (a mesma restrição de IPipelineBehavior) em vez de IRequest<TResponse>:
// os commands sem retorno implementam IRequest, que não é IRequest<Unit>, e com a restrição mais estreita
// o MediatR simplesmente não fecharia este behavior para eles e a validação nunca rodaria.
public class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators)
    {
        _validators = validators;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (!_validators.Any()) return await next();

        // Um contexto por validador: o contexto acumula as falhas, e compartilhá-lo duplicaria os erros
        // do primeiro validador no resultado do segundo.
        var validationResults = await Task.WhenAll(
            _validators.Select(v => v.ValidateAsync(new ValidationContext<TRequest>(request), cancellationToken))
        );

        var failures = validationResults
            .SelectMany(r => r.Errors)
            .Where(f => f != null)
            .ToList();

        if (failures.Count != 0)
            throw new ValidationException(failures);

        return await next();
    }
}
