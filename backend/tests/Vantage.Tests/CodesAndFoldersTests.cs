using Vantage.Domain;
using Vantage.Infrastructure.Services;
using Vantage.Infrastructure.Storage;
using Xunit;

namespace Vantage.Tests;

/// <summary>C60/C61: generated dashboard codes and the upload folders. Pure rules, no database.</summary>
public class CodesAndFoldersTests
{
    [Theory]
    [InlineData("Sales Performance Overview", "SPOAEV")]
    [InlineData("Finance", "FINANC")]
    [InlineData("HR", "HR0000")]
    [InlineData("  Q3 -- Revenue (EMEA)  ", "QRE3EM")]
    public void Code_is_six_characters_made_from_the_name(string name, string expected)
    {
        var code = DashboardCodes.Generate(name, _ => false);
        Assert.Equal(expected, code);
        Assert.Equal(DashboardCodes.Length, code.Length);
        Assert.True(Rules.IsValidDashboardCode(code));
    }

    [Fact]
    public void A_taken_code_gets_a_counter_and_stays_six_characters()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FINANC" };
        var second = DashboardCodes.Generate("Finance", taken.Contains);
        Assert.Equal("FINAN2", second);
        taken.Add(second);
        var third = DashboardCodes.Generate("Finance", taken.Contains);
        Assert.Equal("FINAN3", third);
        Assert.All(new[] { second, third }, c => Assert.Equal(6, c.Length));
    }

    [Fact]
    public void Folders_are_tidied_and_unsafe_ones_refused()
    {
        Assert.Equal("powerbi/uploads", StorageFolders.Clean("/powerbi/uploads/ "));
        foreach (var bad in new[] { "", "   ", "../x", "a/../b", "C:/files", "a b", "/", new string('a', 101) })
            Assert.Throws<RuleException>(() => StorageFolders.Clean(bad));
    }

    [Fact]
    public void Upload_key_carries_the_folder_and_a_timestamp_but_not_a_changed_extension()
    {
        var key = StorageFolders.Key("powerbi", 7, 3, @"C:\temp\Sales Report.pbix", new DateTime(2026, 10, 7, 14, 5, 9, DateTimeKind.Utc));
        Assert.Equal("powerbi/7/v3/Sales Report_20261007-140509.pbix", key);
    }
}
