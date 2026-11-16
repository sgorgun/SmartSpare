namespace SmartSpare.SharedKernel.UnitTests;

public sealed class ValueObjectTests
{
    [Fact]
    public void SameComponents_AreEqual()
    {
        var a = new Money(10.5m, "EUR");
        var b = new Money(10.5m, "EUR");

        a.ShouldBe(b);
        (a == b).ShouldBeTrue();
        a.GetHashCode().ShouldBe(b.GetHashCode());
    }

    [Fact]
    public void DifferentComponents_AreNotEqual()
    {
        var a = new Money(10.5m, "EUR");
        var b = new Money(10.5m, "USD");

        a.ShouldNotBe(b);
        (a != b).ShouldBeTrue();
    }

    [Fact]
    public void DifferentTypes_WithSameComponents_AreNotEqual()
    {
        ValueObject money = new Money(1m, "EUR");
        ValueObject other = new OtherMoney(1m, "EUR");

        money.Equals(other).ShouldBeFalse();
    }

    [Fact]
    public void ComparisonWithNull_IsFalse()
    {
        var a = new Money(1m, "EUR");

        a.Equals(null).ShouldBeFalse();
        (a == null).ShouldBeFalse();
    }

    private sealed class Money(decimal amount, string currency) : ValueObject
    {
        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return amount;
            yield return currency;
        }
    }

    private sealed class OtherMoney(decimal amount, string currency) : ValueObject
    {
        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return amount;
            yield return currency;
        }
    }
}
