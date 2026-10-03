using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Vantage.Domain;
using Vantage.Infrastructure.Embedding;

namespace Vantage.Tests;

public class RulesTests
{
    [Theory]
    [InlineData("FIN-PNL", 12, "FIN-PNL-12-default")]
    [InlineData("  OPS  ", 7, "OPS-7-default")]
    public void Default_group_name_is_code_id_default(string code, int id, string expected) =>
        Assert.Equal(expected, Rules.DefaultGroupName(code, id));

    [Fact]
    public void Default_group_name_never_exceeds_40_characters()
    {
        var name = Rules.DefaultGroupName(new string('A', 20), 1234567);
        Assert.True(name.Length <= Rules.GroupNameMax);
        Assert.EndsWith("-1234567-default", name);
    }

    [Theory]
    [InlineData("FIN-PNL", true)]
    [InlineData("fin_pnl2", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("TOO-LONG-CODE-123456789", false)]
    public void Dashboard_code_validation(string code, bool valid) => Assert.Equal(valid, Rules.IsValidDashboardCode(code));

    [Theory]
    [InlineData("finance", true)]
    [InlineData("Q3report", true)]
    [InlineData("toolongtag1", false)]
    [InlineData("p&l", false)]
    public void Tag_validation(string tag, bool valid) => Assert.Equal(valid, Rules.IsValidTag(tag));

    [Theory]
    [InlineData("someone@rrd.com", true)]
    [InlineData("Someone@RRD.COM", true)]
    [InlineData("someone@acmeclient.com", false)]
    [InlineData("someone@notrrd.com", false)]
    public void Internal_email_detection(string email, bool internalUser) => Assert.Equal(internalUser, Rules.IsInternalEmail(email));
}

public class RlsIdentityTests
{
    private static readonly Guid Dataset = Guid.NewGuid();

    [Fact]
    public void No_identity_when_the_model_has_no_rls_even_if_the_group_has_a_role()
    {
        Assert.Empty(RlsIdentityBuilder.Build(false, false, "a@rrd.com", "Finance_All", Dataset));
    }

    [Fact]
    public void Identity_carries_email_role_and_dataset_when_the_model_has_roles()
    {
        var identity = Assert.Single(RlsIdentityBuilder.Build(true, true, "a@rrd.com", " Finance_All ", Dataset));
        Assert.Equal("a@rrd.com", identity.Username);
        Assert.Equal(["Finance_All"], identity.Roles);
        Assert.Equal([Dataset.ToString()], identity.Datasets);
    }

    [Fact]
    public void Several_roles_can_be_listed_with_commas()
    {
        var identity = Assert.Single(RlsIdentityBuilder.Build(true, true, "a@rrd.com", "Region_North, Region_South", Dataset));
        Assert.Equal(["Region_North", "Region_South"], identity.Roles);
    }

    [Fact]
    public void Model_with_roles_but_group_without_one_is_refused_clearly()
    {
        var ex = Assert.Throws<EmbedException>(() => RlsIdentityBuilder.Build(true, true, "a@rrd.com", null, Dataset));
        Assert.Equal("rls-missing", ex.Code);
    }
}

public class TableauTokenTests
{
    [Fact]
    public void Connected_app_jwt_has_the_claims_tableau_expects()
    {
        var now = new DateTimeOffset(2026, 10, 3, 8, 0, 0, TimeSpan.Zero);
        const string secret = "0123456789abcdef0123456789abcdef0123456789abcdef";
        var (token, expires) = TableauTokenService.Sign("client-1", "secret-id-1", secret, "emily.carter@rrd.com", now);

        Assert.Equal(now.AddMinutes(5), expires);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal("secret-id-1", jwt.Header.Kid);
        Assert.Equal("client-1", jwt.Header["iss"]);
        Assert.Equal("HS256", jwt.Header.Alg);
        Assert.Equal("client-1", jwt.Issuer);
        Assert.Equal("emily.carter@rrd.com", jwt.Subject);
        Assert.Contains("tableau", jwt.Audiences);
        Assert.Contains(jwt.Claims, c => c.Type == "scp" && c.Value == "tableau:views:embed");

        new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
        {
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
            ValidIssuer = "client-1", ValidAudience = "tableau", ValidateLifetime = false,
        }, out _);
    }
}
