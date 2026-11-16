namespace SmartSpare.SharedKernel.UnitTests;

public sealed class EntityTests
{
    [Fact]
    public void SameTypeAndId_AreEqual()
    {
        var id = Guid.NewGuid();
        var a = new Machine(id);
        var b = new Machine(id);

        a.ShouldBe(b);
        (a == b).ShouldBeTrue();
        a.GetHashCode().ShouldBe(b.GetHashCode());
    }

    [Fact]
    public void DifferentIds_AreNotEqual() =>
        (new Machine(Guid.NewGuid()) != new Machine(Guid.NewGuid())).ShouldBeTrue();

    [Fact]
    public void DifferentTypes_WithSameId_AreNotEqual()
    {
        var id = Guid.NewGuid();
        Entity<Guid> machine = new Machine(id);
        Entity<Guid> warehouse = new Warehouse(id);

        machine.Equals(warehouse).ShouldBeFalse();
    }

    [Fact]
    public void AggregateRoot_CollectsAndClearsDomainEvents()
    {
        var warehouse = new Warehouse(Guid.NewGuid());

        warehouse.Open();
        warehouse.Open();

        warehouse.DomainEvents.Count.ShouldBe(2);
        warehouse.DomainEvents.ShouldAllBe(e => e is WarehouseOpened);

        warehouse.ClearDomainEvents();

        warehouse.DomainEvents.ShouldBeEmpty();
    }

    private sealed class Machine(Guid id) : Entity<Guid>(id);

    private sealed class Warehouse(Guid id) : AggregateRoot<Guid>(id)
    {
        public void Open() => Raise(new WarehouseOpened(Id, DateTimeOffset.UtcNow));
    }

    private sealed record WarehouseOpened(Guid WarehouseId, DateTimeOffset OccurredAt) : IDomainEvent;
}
