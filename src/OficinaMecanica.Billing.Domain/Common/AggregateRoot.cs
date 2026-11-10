namespace OficinaMecanica.Billing.Domain.Common;

public abstract class AggregateRoot : Entity
{
    public uint Version { get; protected set; }

    public void IncrementVersion() => Version++;
}
