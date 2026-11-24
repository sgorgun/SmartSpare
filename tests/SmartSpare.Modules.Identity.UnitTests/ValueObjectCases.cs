namespace SmartSpare.Modules.Identity;

/// <summary>
/// Accept/reject cases for the Identity value objects. The unit tests check them against the value objects;
/// the integration tests check the same cases against the real CHECK constraints of <c>iam.app_user</c>,
/// so the two rule sets cannot drift apart (STAGE_3_PLAN section 9.1).
/// </summary>
public static class ValueObjectCases
{
    public static TheoryData<string, bool> UserNames => new()
    {
        { "tomas.radzevicius", true },
        { "abc", true },
        { "a_b-c.d", true },
        { "admin.smartspare", true },
        { new string('a', 50), true },
        { "Tomas.Radzevicius", true },      // accepted and stored lower-case (the CHECK sees the canonical form)
        { "ab", false },                    // too short
        { new string('a', 51), false },     // too long
        { "tomas radzevicius", false },     // space
        { "tomas@dealer", false },
        { "tomas.radzevičius", false },     // non-ASCII letter
        { string.Empty, false },
    };

    public static TheoryData<string, bool> EmailAddresses => new()
    {
        { "tomas.radzevicius@dealer.example", true },
        { "a@b.c", true },
        { "a.b@c.d.e", true },
        { "Tomas@Dealer.Example", true },
        { "tomas", false },
        { "tomas@dealer", false },          // no dot after the @
        { "@dealer.example", false },
        { "tomas@.example", false },
        { "tomas@dealer.", false },
        { "to mas@dealer.example", false }, // whitespace
        { "tomas@@dealer.example", false },
        { string.Empty, false },
    };
}
