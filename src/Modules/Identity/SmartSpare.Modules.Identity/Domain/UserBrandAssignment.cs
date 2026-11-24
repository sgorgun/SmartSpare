using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Domain;

/// <summary>A brand a user is responsible for (child entity of <see cref="AppUser"/>; the identifier is the brand id).</summary>
internal sealed class UserBrandAssignment : Entity<short>
{
    private UserBrandAssignment()
        : base(default)
    {
    }

    internal UserBrandAssignment(short brandId)
        : base(brandId)
    {
    }

    public short BrandId => Id;
}
